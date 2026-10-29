using System.Globalization;
using System.Text.RegularExpressions;

namespace DuplicateVideoCleaner;

public enum TimestampSource { FileName, CreationTime, LastWriteTime }
public enum KeepMode { Oldest, Newest }

public sealed record VideoCandidate(
    string FullPath,
    string RelativePath,
    DateTime Timestamp,
    TimestampSource TimestampSource,
    long Size,
    DateTime? CreationTimeUtc = null,
    DateTime? LastWriteTimeUtc = null,
    string? FileIdentity = null,
    bool IsProtected = false);

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
        return new ScanResult(IncludeProtectedMatches(result, candidates, keepMode, cancellationToken), diagnostics);
    }

    public static (DateTime Timestamp, TimestampSource Source) GetEffectiveTimestamp(string fileName, DateTime creationTime, DateTime? lastWriteTime = null)
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

        // A copy can reset creation time while preserving the original last-write time.
        if (lastWriteTime is { } modified && modified < creationTime)
            return (modified, TimestampSource.LastWriteTime);

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
        var effective = GetEffectiveTimestamp(Path.GetFileName(fullPath), snapshot.CreationTimeUtc!.Value.ToLocalTime(),
            snapshot.LastWriteTimeUtc!.Value.ToLocalTime());
        return snapshot with
        {
            RelativePath = Path.GetRelativePath(rootPath, fullPath),
            Timestamp = effective.Timestamp,
            TimestampSource = effective.Source,
            IsProtected = effective.Source != TimestampSource.FileName
        };
    }

    private static void AddGroupIfDuplicate(List<DuplicateGroup> groups, List<VideoCandidate> files, KeepMode keepMode)
    {
        if (files.Count < 2) return;
        var keep = ChooseKeeper(files, keepMode);
        groups.Add(new DuplicateGroup(groups.Count + 1, files.ToArray(), keep));
    }

    internal static VideoCandidate ChooseKeeper(IReadOnlyList<VideoCandidate> files, KeepMode keepMode)
    {
        var protectedFiles = files.Where(file => file.IsProtected).ToArray();
        IReadOnlyList<VideoCandidate> eligible = protectedFiles.Length > 0 ? protectedFiles : files;
        return keepMode == KeepMode.Oldest ? eligible[0] : eligible[^1];
    }

    private static IReadOnlyList<DuplicateGroup> IncludeProtectedMatches(IReadOnlyList<DuplicateGroup> timeGroups,
        IReadOnlyList<VideoCandidate> candidates, KeepMode keepMode, CancellationToken cancellationToken)
    {
        // Equal byte size is a cheap prerequisite for a metadata match. Include these
        // pairs even if a rename or transfer changed their effective timestamps.
        var index = candidates.Select((file, i) => (file.FullPath, i))
            .ToDictionary(pair => pair.FullPath, pair => pair.i, StringComparer.OrdinalIgnoreCase);
        var parents = Enumerable.Range(0, candidates.Count).ToArray();
        int Find(int i)
        {
            while (parents[i] != i) { parents[i] = parents[parents[i]]; i = parents[i]; }
            return i;
        }
        void Join(int left, int right) => parents[Find(left)] = Find(right);
        foreach (var group in timeGroups)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var first = index[group.Files[0].FullPath];
            foreach (var file in group.Files.Skip(1)) Join(first, index[file.FullPath]);
        }
        foreach (var sameSize in candidates.GroupBy(file => file.Size))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var protectedFile = sameSize.FirstOrDefault(file => file.IsProtected);
            if (protectedFile is null || !sameSize.Any(file => !file.IsProtected)) continue;
            var first = index[protectedFile.FullPath];
            foreach (var file in sameSize) Join(first, index[file.FullPath]);
        }
        var result = new List<DuplicateGroup>();
        foreach (var component in candidates.GroupBy(file => Find(index[file.FullPath])))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var files = component.ToList();
            if (files.All(file => file.IsProtected)) continue;
            AddGroupIfDuplicate(result, files, keepMode);
        }
        return result;
    }

    [GeneratedRegex(@"\d{4}-\d{2}-\d{2} \d{2}-\d{2}-\d{2}", RegexOptions.CultureInvariant)]
    private static partial Regex FileTimestampRegex();
}
