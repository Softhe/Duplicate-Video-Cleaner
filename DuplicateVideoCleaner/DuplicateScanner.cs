using System.Globalization;
using System.Text.RegularExpressions;

namespace DuplicateVideoCleaner;

public enum TimestampSource { FileName, CreationTime }
public enum KeepMode { Oldest, Newest }

public sealed record VideoCandidate(
    string FullPath,
    string RelativePath,
    DateTime Timestamp,
    TimestampSource TimestampSource,
    long Size,
    DateTime? CreationTimeUtc = null,
    DateTime? LastWriteTimeUtc = null,
    string? FileIdentity = null);

public sealed record DuplicateGroup(int Number, IReadOnlyList<VideoCandidate> Files, VideoCandidate SuggestedKeep);

public sealed record ScanDiagnostic(string Path, string Message);
public sealed record ScanResult(IReadOnlyList<DuplicateGroup> Groups, IReadOnlyList<ScanDiagnostic> Diagnostics);

public static partial class DuplicateScanner
{
    public static IReadOnlyList<DuplicateGroup> Scan(
        string rootPath,
        int thresholdSeconds,
        bool includeSubfolders,
        KeepMode keepMode,
        CancellationToken cancellationToken = default) =>
        ScanDetailed(rootPath, thresholdSeconds, includeSubfolders, keepMode, cancellationToken).Groups;

    public static ScanResult ScanDetailed(string rootPath, int thresholdSeconds, bool includeSubfolders,
        KeepMode keepMode, CancellationToken cancellationToken = default) =>
        ScanDetailedCore(rootPath, thresholdSeconds, includeSubfolders, keepMode, cancellationToken,
            path => Directory.EnumerateFileSystemEntries(path));

    internal static ScanResult ScanDetailedCore(string rootPath, int thresholdSeconds, bool includeSubfolders,
        KeepMode keepMode, CancellationToken cancellationToken, Func<string, IEnumerable<string>> enumerateEntries)
    {
        if (string.IsNullOrWhiteSpace(rootPath)) throw new ArgumentException("A folder is required.", nameof(rootPath));
        if (thresholdSeconds <= 0) throw new ArgumentOutOfRangeException(nameof(thresholdSeconds), "Threshold must be positive.");

        cancellationToken.ThrowIfCancellationRequested();
        rootPath = Path.GetFullPath(rootPath);
        var diagnostics = new List<ScanDiagnostic>();
        var candidates = new List<VideoCandidate>();
        var pending = new Stack<string>();
        pending.Push(rootPath);
        while (pending.TryPop(out var directory))
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                var directoryAttributes = File.GetAttributes(directory);
                if ((directoryAttributes & FileAttributes.ReparsePoint) != 0)
                {
                    diagnostics.Add(new(directory, "Skipped a reparse point to avoid following links."));
                    continue;
                }
                if ((directoryAttributes & FileAttributes.Directory) == 0)
                {
                    diagnostics.Add(new(directory, "Skipped path because it is no longer a directory."));
                    continue;
                }
                using var entries = enumerateEntries(directory).GetEnumerator();
                while (true)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    if (!entries.MoveNext()) break;
                    var path = entries.Current;
                    try
                    {
                        var attributes = File.GetAttributes(path);
                        if ((attributes & FileAttributes.ReparsePoint) != 0)
                        {
                            diagnostics.Add(new(path, "Skipped a reparse point to avoid following links."));
                            continue;
                        }
                        if ((attributes & FileAttributes.Directory) != 0)
                        {
                            if (Path.GetFileName(path).Equals(".DuplicateVideoCleaner-Recovery", StringComparison.OrdinalIgnoreCase)) continue;
                            if (includeSubfolders) pending.Push(path);
                        }
                        else if (IsSupportedVideo(path)) candidates.Add(CreateCandidate(rootPath, path));
                    }
                    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                    {
                        diagnostics.Add(new(path, $"Skipped entry: {ex.Message}"));
                    }
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                diagnostics.Add(new(directory, $"Skipped directory: {ex.Message}"));
            }
        }
        candidates.Sort((left, right) =>
        {
            var comparison = left.Timestamp.CompareTo(right.Timestamp);
            return comparison != 0 ? comparison : StringComparer.OrdinalIgnoreCase.Compare(left.FullPath, right.FullPath);
        });
        cancellationToken.ThrowIfCancellationRequested();

        var result = new List<DuplicateGroup>();
        var current = new List<VideoCandidate>();

        foreach (var candidate in candidates)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (current.Count == 0 || (candidate.Timestamp - current[^1].Timestamp).TotalSeconds <= thresholdSeconds)
            {
                current.Add(candidate);
                continue;
            }

            AddGroupIfDuplicate(result, current, keepMode);
            current = [candidate];
        }

        AddGroupIfDuplicate(result, current, keepMode);
        return new ScanResult(result, diagnostics);
    }

    public static (DateTime Timestamp, TimestampSource Source) GetEffectiveTimestamp(string fileName, DateTime creationTime)
    {
        var match = FileTimestampRegex().Match(fileName);
        if (match.Success && DateTime.TryParseExact(
                match.Value,
                "yyyy-MM-dd HH-mm-ss",
                CultureInfo.InvariantCulture,
                DateTimeStyles.None,
                out var timestamp))
        {
            return (timestamp, TimestampSource.FileName);
        }

        return (creationTime, TimestampSource.CreationTime);
    }

    private static bool IsSupportedVideo(string path) =>
        Path.GetExtension(path).Equals(".mp4", StringComparison.OrdinalIgnoreCase) ||
        Path.GetExtension(path).Equals(".mkv", StringComparison.OrdinalIgnoreCase);

    private static VideoCandidate CreateCandidate(string rootPath, string path)
    {
        var fullPath = Path.GetFullPath(path);
        using var directories = FileSnapshot.PinDirectories(Path.GetDirectoryName(fullPath)!);
        using var source = WindowsProbeSandbox.OpenReadOnlyMedia(fullPath);
        var snapshot = FileSnapshot.CaptureReadHandle(source.SafeFileHandle, fullPath);
        var effective = GetEffectiveTimestamp(Path.GetFileName(fullPath), snapshot.CreationTimeUtc!.Value.ToLocalTime());
        return snapshot with
        {
            RelativePath = Path.GetRelativePath(rootPath, fullPath),
            Timestamp = effective.Timestamp,
            TimestampSource = effective.Source
        };
    }

    private static void AddGroupIfDuplicate(List<DuplicateGroup> groups, List<VideoCandidate> files, KeepMode keepMode)
    {
        if (files.Count < 2) return;
        var keep = keepMode == KeepMode.Oldest ? files[0] : files[^1];
        groups.Add(new DuplicateGroup(groups.Count + 1, files.ToArray(), keep));
    }

    [GeneratedRegex(@"\d{4}-\d{2}-\d{2} \d{2}-\d{2}-\d{2}", RegexOptions.CultureInvariant)]
    private static partial Regex FileTimestampRegex();
}
