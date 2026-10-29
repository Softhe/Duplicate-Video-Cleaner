namespace DuplicateVideoCleaner;

internal sealed record RemovalFailure(VideoCandidate File, string Error);
internal sealed record RemovalResult(IReadOnlySet<string> RemovedPaths, IReadOnlyList<RemovalFailure> Failures, string? AuditError = null);

internal static class FileRemovalService
{
    internal static Task<RemovalResult> RemoveReviewedAsync(IReadOnlyList<ReviewedVideo> videos, bool permanent, string logPath) =>
        RemoveReviewedAsync(videos, permanent, logPath, DeleteFile, AppendLog);

    internal static Task<RemovalResult> RemoveReviewedAsync(IReadOnlyList<ReviewedVideo> videos, bool permanent, string logPath,
        Action<VideoCandidate, bool> deleteFile, Action<string, string> appendLog) =>
        RemoveAsync(videos.Select(video => video.Candidate).ToArray(), permanent, logPath, deleteFile, appendLog,
            videos.Where(video => video.RequiresProtectedMatch).ToDictionary(video => video.Candidate.FullPath,
                video => video.ProtectedMatches ?? Array.Empty<VideoCandidate>(), StringComparer.OrdinalIgnoreCase));

    internal static Task<RemovalResult> RemoveAsync(IReadOnlyList<VideoCandidate> files, bool permanent, string logPath) =>
        RemoveAsync(files, permanent, logPath, DeleteFile, AppendLog);

    internal static Task<RemovalResult> RemoveAsync(IReadOnlyList<VideoCandidate> files, bool permanent, string logPath,
        Action<VideoCandidate, bool> deleteFile, Action<string, string> appendLog,
        IReadOnlyDictionary<string, IReadOnlyList<VideoCandidate>>? protectedMatches = null)
    {
        var selected = files.ToArray();
        return Task.Run(() =>
        {
            var removed = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var failures = new List<RemovalFailure>();
            string? auditError = null;
            for (var index = 0; index < selected.Length; index++)
            {
                var file = selected[index];
                if (!TryLog($"DELETE ATTEMPT | {Describe(file)}"))
                {
                    AddSkipped(index);
                    break;
                }
                Exception? deletionError = null;
                try
                {
                    ValidateSnapshot(file);
                    using var survivor = protectedMatches is not null && protectedMatches.TryGetValue(file.FullPath, out var matches)
                        ? OpenProtectedSurvivor(matches) : null;
                    deleteFile(file, permanent);
                    removed.Add(file.FullPath);
                }
                catch (Exception ex)
                {
                    deletionError = ex;
                    failures.Add(new RemovalFailure(file, ex.Message));
                }
                // Logging must not turn a completed deletion into a removal failure.
                var outcome = deletionError == null
                    ? $"DELETE SUCCESS | {Describe(file)}"
                    : $"DELETE FAILED | {Describe(file)} | {deletionError.Message}";
                if (!TryLog(outcome))
                {
                    AddSkipped(index + 1);
                    break;
                }
            }
            return new RemovalResult(removed, failures, auditError);

            string Describe(VideoCandidate file) => $"Mode={(permanent ? "Permanent" : "Recovery")} | {file.FullPath}";
            bool TryLog(string message)
            {
                try { appendLog(logPath, message); return true; }
                catch (Exception ex)
                {
                    auditError = $"Cleanup stopped because the audit log could not be written: {ex.Message}";
                    return false;
                }
            }
            void AddSkipped(int first)
            {
                for (var remaining = first; remaining < selected.Length; remaining++)
                    failures.Add(new RemovalFailure(selected[remaining], "Not removed because the audit log failed. Scan again before retrying."));
            }
        });
    }

    private static ProtectedSurvivor OpenProtectedSurvivor(IReadOnlyList<VideoCandidate> matches)
    {
        foreach (var match in matches)
        {
            if (!match.IsProtected) continue;
            try { return new ProtectedSurvivor(match); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        }
        throw new IOException("No unchanged matching protected clip is available. The original was kept. Scan again before retrying.");
    }

    private sealed class ProtectedSurvivor : IDisposable
    {
        private readonly FileSnapshot.PinnedDirectories _directories;
        private readonly FileStream _source;

        internal ProtectedSurvivor(VideoCandidate file)
        {
            _directories = FileSnapshot.PinDirectories(Path.GetDirectoryName(Path.GetFullPath(file.FullPath))!);
            try
            {
                _source = WindowsProbeSandbox.OpenReadOnlyMedia(file.FullPath);
                try { FileSnapshot.ValidateReadHandle(_source.SafeFileHandle, file); }
                catch { _source.Dispose(); throw; }
            }
            catch { _directories.Dispose(); throw; }
        }

        public void Dispose()
        {
            _source.Dispose();
            _directories.Dispose();
        }
    }

    private static void ValidateSnapshot(VideoCandidate file)
    {
        if (file.IsProtected)
            throw new IOException("Renamed clips are protected and cannot be removed.");
        if (file.CreationTimeUtc == null || file.LastWriteTimeUtc == null || file.FileIdentity == null)
            throw new IOException("The file has no scan snapshot. Scan again before removing it.");
        var current = new FileInfo(file.FullPath);
        current.Refresh();
        if (!current.Exists) throw new FileNotFoundException("The file no longer exists.", file.FullPath);
        if ((current.Attributes & FileAttributes.ReparsePoint) != 0)
            throw new IOException("The file is a symbolic link or reparse point and cannot be removed.");
        if (current.Length != file.Size || current.CreationTimeUtc != file.CreationTimeUtc || current.LastWriteTimeUtc != file.LastWriteTimeUtc ||
            FileSnapshot.GetIdentity(file.FullPath) != file.FileIdentity)
            throw new IOException("The file changed since the scan. Scan again before removing it.");
    }

    private static void DeleteFile(VideoCandidate file, bool permanent)
    {
        if (permanent) FileSnapshot.DeleteValidated(file);
        else RecoveryStore.MoveToRecovery(file);
    }

    private static void AppendLog(string path, string message)
    {
        using var directories = FileSnapshot.PinDirectories(Path.GetDirectoryName(Path.GetFullPath(path))!);
        using var stream = FileSnapshot.OpenAppendLog(path);
        using var writer = new StreamWriter(stream);
        writer.WriteLine($"{DateTimeOffset.Now:O} | {message.Replace("\r", "\\r").Replace("\n", "\\n")}");
        writer.Flush();
        stream.Flush(flushToDisk: true);
    }
}
