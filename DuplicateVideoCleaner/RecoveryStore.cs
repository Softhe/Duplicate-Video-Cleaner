using System.Text.Json;

namespace DuplicateVideoCleaner;

internal sealed record RecoveryEntry(string OriginalPath, string StoredPath, string FileIdentity,
    DateTimeOffset RemovedAt, string ManifestPath, long Size, DateTime CreationTimeUtc, DateTime LastWriteTimeUtc);

internal static partial class RecoveryStore
{
    internal const string DirectoryName = ".DuplicateVideoCleaner-Recovery";

    internal static void MoveToRecovery(VideoCandidate candidate, Action<RecoveryEntry>? beforeMove = null)
    {
        var original = Path.GetFullPath(candidate.FullPath);
        var parent = Path.GetDirectoryName(original)!;
        using var sourceDirectories = FileSnapshot.PinDirectories(parent);
        using var file = FileSnapshot.OpenValidated(candidate);
        var recovery = Path.Combine(parent, DirectoryName);
        Directory.CreateDirectory(recovery);
        using var recoveryDirectories = FileSnapshot.PinDirectories(recovery);
        var entryDirectory = Path.Combine(recovery, Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(entryDirectory);
        using var entryDirectories = FileSnapshot.PinDirectories(entryDirectory);
        var entry = new RecoveryEntry(original, Path.Combine(entryDirectory, "payload.dvc"), candidate.FileIdentity!,
            DateTimeOffset.UtcNow, Path.Combine(entryDirectory, "intent.json"), candidate.Size,
            candidate.CreationTimeUtc!.Value, candidate.LastWriteTimeUtc!.Value);
        // Persist the original location before the object-bound move. A crash leaves
        // either an untouched source or a payload with its durable restoration intent.
        using var intent = new FileStream(entry.ManifestPath, FileMode.CreateNew, FileAccess.Write, FileShare.Read);
        JsonSerializer.Serialize(intent, entry);
        intent.Flush(flushToDisk: true);
        beforeMove?.Invoke(entry);
        FileSnapshot.Rename(file, entryDirectories.Last, "payload.dvc");
    }

    internal static IReadOnlyList<RecoveryEntry> List(string root, Action<string>? diagnostic = null,
        CancellationToken cancellationToken = default, Action<string>? beforeReadChild = null, bool includeArchivedPayloads = false)
    {
        var entries = new List<RecoveryEntry>();
        var pending = new Stack<string>();
        var fullRoot = Path.GetFullPath(root);
        cancellationToken.ThrowIfCancellationRequested();
        using var rootPin = FileSnapshot.PinDirectories(fullRoot);
        pending.Push(fullRoot);
        while (pending.TryPop(out var directory))
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                using var pinned = FileSnapshot.PinDirectories(directory);
                foreach (var child in Directory.EnumerateDirectories(directory))
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    try
                    {
                        beforeReadChild?.Invoke(child);
                        cancellationToken.ThrowIfCancellationRequested();
                        if ((File.GetAttributes(child) & FileAttributes.ReparsePoint) != 0) continue;
                        if (!string.Equals(Path.GetFileName(child), DirectoryName, StringComparison.OrdinalIgnoreCase))
                        {
                            pending.Push(child);
                            continue;
                        }
                        using var recovery = FileSnapshot.PinDirectories(child);
                        foreach (var entryDirectory in Directory.EnumerateDirectories(child))
                        {
                            cancellationToken.ThrowIfCancellationRequested();
                            try
                            {
                                if ((File.GetAttributes(entryDirectory) & FileAttributes.ReparsePoint) != 0) continue;
                                if (!string.Equals(Path.GetFileName(entryDirectory), HistoryDirectoryName, StringComparison.OrdinalIgnoreCase))
                                { ReadOne(entryDirectory); continue; }
                                if (!includeArchivedPayloads) continue;
                                using var archivePin = FileSnapshot.PinDirectories(entryDirectory);
                                foreach (var archivedEntry in Directory.EnumerateDirectories(entryDirectory)) ReadOne(archivedEntry);
                            }
                            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or ArgumentException)
                            { diagnostic?.Invoke($"Recovery entry could not be read: {entryDirectory}: {ex.Message}"); }
                        }
                    }
                    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                    { diagnostic?.Invoke($"Recovery folder could not be read: {child}: {ex.Message}"); }
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            { diagnostic?.Invoke($"Recovery folder could not be read: {directory}: {ex.Message}"); }
        }
        void ReadOne(string entryDirectory)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                using var entryPin = FileSnapshot.PinDirectories(entryDirectory);
                if (!File.Exists(Path.Combine(entryDirectory, "payload.dvc"))) return;
                entries.Add(ReadEntry(Path.Combine(entryDirectory, "intent.json")));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or ArgumentException)
            { diagnostic?.Invoke($"Recovery entry could not be read: {entryDirectory}: {ex.Message}"); }
        }

        cancellationToken.ThrowIfCancellationRequested();
        return entries.OrderByDescending(entry => entry.RemovedAt).ToArray();
    }

    internal static string? Restore(RecoveryEntry entry)
    {
        using var storedDirectories = FileSnapshot.PinDirectories(Path.GetDirectoryName(entry.ManifestPath)!);
        var current = ReadEntry(entry.ManifestPath);
        if (current != entry) throw new IOException("The recovery entry changed. Refresh the recovery list.");
        using var originalDirectories = FileSnapshot.PinDirectories(Path.GetDirectoryName(current.OriginalPath)!);
        var candidate = new VideoCandidate(current.StoredPath, Path.GetFileName(current.StoredPath),
            current.LastWriteTimeUtc, TimestampSource.CreationTime, current.Size,
            current.CreationTimeUtc, current.LastWriteTimeUtc, current.FileIdentity);
        using var file = FileSnapshot.OpenValidated(candidate);
        using var journal = OpenJournal(current, "RESTORE ATTEMPT");
        try { FileSnapshot.Rename(file, originalDirectories.Last, Path.GetFileName(current.OriginalPath)); }
        catch { TryRecordFailure(journal, "RESTORE FAILED"); throw; }
        // Keep the small durable intent as a record. It is ignored once payload is absent.
        return FinishJournal(journal, "RESTORE SUCCESS");
    }

    internal static string? Purge(RecoveryEntry entry)
    {
        using var storedDirectories = FileSnapshot.PinDirectories(Path.GetDirectoryName(entry.ManifestPath)!);
        var current = ReadEntry(entry.ManifestPath);
        if (current != entry) throw new IOException("The recovery entry changed. Refresh the recovery list.");
        var candidate = new VideoCandidate(current.StoredPath, Path.GetFileName(current.StoredPath),
            current.LastWriteTimeUtc, TimestampSource.CreationTime, current.Size,
            current.CreationTimeUtc, current.LastWriteTimeUtc, current.FileIdentity);
        using var journal = OpenJournal(current, "PURGE ATTEMPT");
        try { FileSnapshot.DeleteValidated(candidate); }
        catch { TryRecordFailure(journal, "PURGE FAILED"); throw; }
        return FinishJournal(journal, "PURGE SUCCESS");
    }

    private static FileStream OpenJournal(RecoveryEntry entry, string action)
    {
        var path = Path.Combine(Path.GetDirectoryName(entry.ManifestPath)!, "actions.log");
        var stream = FileSnapshot.OpenAppendLog(path);
        try { WriteJournal(stream, action); return stream; }
        catch { stream.Dispose(); throw; }
    }

    private static string? FinishJournal(FileStream stream, string action)
    {
        try { WriteJournal(stream, action); return null; }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        { return $"The recovery action completed, but its completion audit could not be written: {ex.Message}"; }
    }

    private static void TryRecordFailure(FileStream stream, string action)
    {
        // Preserve the mutation error even if its best-effort outcome cannot be persisted.
        try { WriteJournal(stream, action); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }

    private static void WriteJournal(FileStream stream, string action)
    {
        var bytes = System.Text.Encoding.UTF8.GetBytes($"{DateTimeOffset.UtcNow:O} | {action}\n");
        stream.Write(bytes);
        stream.Flush(flushToDisk: true);
    }

    private static RecoveryEntry ReadEntry(string manifest)
    {
        using var stream = new FileStream(manifest, FileMode.Open, FileAccess.Read, FileShare.Read);
        if (stream.Length > 64 * 1024 || (File.GetAttributes(manifest) & FileAttributes.ReparsePoint) != 0)
            throw new IOException("The recovery manifest is invalid.");
        var entry = JsonSerializer.Deserialize<RecoveryEntry>(stream) ?? throw new IOException("The recovery manifest is empty.");
        var directory = Path.GetDirectoryName(Path.GetFullPath(manifest))!;
        var recovery = Path.GetDirectoryName(directory)!;
        var archived = string.Equals(Path.GetFileName(recovery), HistoryDirectoryName, StringComparison.OrdinalIgnoreCase);
        if (archived) recovery = Path.GetDirectoryName(recovery)!;
        var source = Path.GetDirectoryName(recovery)!;
        var originalDirectory = archived ? Path.Combine(recovery, Path.GetFileName(directory)) : directory;
        if (!Guid.TryParseExact(Path.GetFileName(directory), "N", out _) ||
            !string.Equals(Path.GetFileName(recovery), DirectoryName, StringComparison.OrdinalIgnoreCase) ||
            !Path.IsPathFullyQualified(entry.OriginalPath) ||
            !string.Equals(Path.GetDirectoryName(Path.GetFullPath(entry.OriginalPath)), source, StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(Path.GetFullPath(entry.StoredPath), Path.Combine(originalDirectory, "payload.dvc"), StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(Path.GetFullPath(entry.ManifestPath), Path.Combine(originalDirectory, "intent.json"), StringComparison.OrdinalIgnoreCase) ||
            string.IsNullOrWhiteSpace(entry.FileIdentity) || entry.Size < 0)
            throw new IOException("The recovery manifest contains an invalid location or snapshot.");
        return archived ? entry with { ManifestPath = Path.GetFullPath(manifest), StoredPath = Path.Combine(directory, "payload.dvc") } : entry;
    }
}
