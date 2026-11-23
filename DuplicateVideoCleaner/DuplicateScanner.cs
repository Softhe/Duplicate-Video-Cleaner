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
    bool IsProtected = false,
    // Files in one review group that came from the same time window share a cluster.
    // Groups only contain several clusters when protected size matches join them.
    int TimeCluster = 0);

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
        CancellationToken cancellationToken = default,
        bool protectUndated = false) =>
        ScanDetailed(rootPath, thresholdSeconds, includeSubfolders, keepMode, cancellationToken, protectUndated).Groups;

    public static ScanResult ScanDetailed(string rootPath, int thresholdSeconds, bool includeSubfolders,
        KeepMode keepMode, CancellationToken cancellationToken = default, bool protectUndated = false) =>
        ScanDetailedCore(rootPath, thresholdSeconds, includeSubfolders, keepMode, cancellationToken,
            path => Directory.EnumerateFileSystemEntries(path), protectUndated);

    internal static ScanResult ScanDetailedCore(string rootPath, int thresholdSeconds, bool includeSubfolders,
        KeepMode keepMode, CancellationToken cancellationToken, Func<string, IEnumerable<string>> enumerateEntries,
        bool protectUndated = false)
    {
        if (string.IsNullOrWhiteSpace(rootPath)) throw new ArgumentException("A folder is required.", nameof(rootPath));
        if (thresholdSeconds <= 0) throw new ArgumentOutOfRangeException(nameof(thresholdSeconds), "Threshold must be positive.");

        cancellationToken.ThrowIfCancellationRequested();
        rootPath = Path.GetFullPath(rootPath);
        var diagnostics = new List<ScanDiagnostic>();
        var candidates = new List<VideoCandidate>();
        // Review, selection and removal index files by case-insensitive path. Paths that
        // differ only by case (possible in case-sensitive folders) are reported, not merged.
        var seenPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var pending = new Stack<string>();
        pending.Push(rootPath);
        while (pending.TryPop(out var directory))
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                var directoryAttributes = File.GetAttributes(directory);
                // The chosen folder may itself be a link or mounted volume; only links
                // discovered inside the scanned tree are skipped.
                if (directory != rootPath && (directoryAttributes & FileAttributes.ReparsePoint) != 0 && FileSnapshot.IsLink(directory))
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
                        // Links are never followed. Data reparse points (cloud-sync folders) are ordinary
                        // folders; files must still be regular files to be opened safely.
                        if ((attributes & FileAttributes.ReparsePoint) != 0 && ((attributes & FileAttributes.Directory) == 0 || FileSnapshot.IsLink(path)))
                        {
                            diagnostics.Add(new(path, "Skipped a reparse point to avoid following links."));
                            continue;
                        }
                        if ((attributes & FileAttributes.Directory) != 0)
                        {
                            if (Path.GetFileName(path).Equals(RecoveryStore.DirectoryName, StringComparison.OrdinalIgnoreCase)) continue;
                            if (includeSubfolders) pending.Push(path);
                        }
                        else if (IsSupportedVideo(path))
                        {
                            var candidate = CreateCandidate(rootPath, path, protectUndated);
                            if (!seenPaths.Add(candidate.FullPath))
                            {
                                diagnostics.Add(new(path, "Skipped because another video path differs from it only by letter case."));
                                continue;
                            }
                            candidates.Add(candidate);
                        }
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

        // Time windows as index ranges over the sorted candidates.
        var windows = new List<(int Start, int Count)>();
        var start = 0;
        for (var index = 1; index <= candidates.Count; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (index < candidates.Count && (candidates[index].Timestamp - candidates[index - 1].Timestamp).TotalSeconds <= thresholdSeconds)
                continue;
            windows.Add((start, index - start));
            start = index;
        }
        for (var window = 0; window < windows.Count; window++)
            for (var index = windows[window].Start; index < windows[window].Start + windows[window].Count; index++)
                candidates[index] = candidates[index] with { TimeCluster = window + 1 };

        var result = new List<DuplicateGroup>();
        if (!candidates.Any(file => file.IsProtected))
        {
            foreach (var (first, count) in windows)
                AddGroupIfDuplicate(result, candidates.GetRange(first, count), keepMode);
            return new ScanResult(result, diagnostics);
        }
        return new ScanResult(IncludeProtectedMatches(windows, candidates, keepMode, cancellationToken), diagnostics);
    }

    public static (DateTime Timestamp, TimestampSource Source) GetEffectiveTimestamp(string fileName, DateTime creationTime, DateTime? lastWriteTime = null)
    {
        if (TryParseFileNameTimestamp(fileName, out var timestamp)) return (timestamp, TimestampSource.FileName);

        // A copy can reset creation time while preserving the original last-write time.
        if (lastWriteTime is { } modified && modified < creationTime)
            return (modified, TimestampSource.LastWriteTime);

        return (creationTime, TimestampSource.CreationTime);
    }

    // Recording dates written by common recorders and cameras:
    // OBS "2026-10-15 14-30-00", "2026-10-15_14-30-00", "2026-10-15 14.30.00",
    // NVIDIA ShadowPlay "2026.10.15 - 14.30.00.01", and phones/cameras
    // "VID_20261015_143000", "20261015_143000", "PXL_20261015_143000123".
    internal static bool TryParseFileNameTimestamp(string fileName, out DateTime timestamp)
    {
        foreach (var pattern in new[] { DashedTimestampRegex(), ShadowPlayTimestampRegex(), CompactTimestampRegex() })
        {
            for (var match = pattern.Match(fileName); match.Success; match = match.NextMatch())
            {
                if (TryCreate(match, out timestamp)) return true;
            }
        }
        timestamp = default;
        return false;

        static bool TryCreate(Match match, out DateTime value)
        {
            value = default;
            int Part(string name) => int.Parse(match.Groups[name].ValueSpan, provider: System.Globalization.CultureInfo.InvariantCulture);
            int year = Part("y"), month = Part("mo"), day = Part("d"), hour = Part("h"), minute = Part("mi"), second = Part("s");
            if (year < 1 || month is < 1 or > 12 || day < 1 || day > DateTime.DaysInMonth(year, month) ||
                hour > 23 || minute > 59 || second > 59)
                return false;
            value = new DateTime(year, month, day, hour, minute, second);
            return true;
        }
    }

    private static bool IsSupportedVideo(string path) =>
        Path.GetExtension(path).Equals(".mp4", StringComparison.OrdinalIgnoreCase) ||
        Path.GetExtension(path).Equals(".mkv", StringComparison.OrdinalIgnoreCase);

    private static VideoCandidate CreateCandidate(string rootPath, string path, bool protectUndated)
    {
        var fullPath = Path.GetFullPath(path);
        using var directories = FileSnapshot.PinDirectories(Path.GetDirectoryName(fullPath)!);
        // Allow other programs to keep writing; removal later rejects active writers.
        using var source = WindowsProbeSandbox.OpenReadOnlyMedia(fullPath, allowWriters: true);
        var snapshot = FileSnapshot.CaptureReadHandle(source.SafeFileHandle, fullPath);
        var effective = GetEffectiveTimestamp(Path.GetFileName(fullPath), snapshot.CreationTimeUtc!.Value.ToLocalTime(),
            snapshot.LastWriteTimeUtc!.Value.ToLocalTime());
        return snapshot with
        {
            RelativePath = Path.GetRelativePath(rootPath, fullPath),
            Timestamp = effective.Timestamp,
            TimestampSource = effective.Source,
            IsProtected = protectUndated && effective.Source != TimestampSource.FileName
        };
    }

    private static void AddGroupIfDuplicate(List<DuplicateGroup> groups, IReadOnlyList<VideoCandidate> files, KeepMode keepMode)
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

    private static IReadOnlyList<DuplicateGroup> IncludeProtectedMatches(IReadOnlyList<(int Start, int Count)> windows,
        IReadOnlyList<VideoCandidate> candidates, KeepMode keepMode, CancellationToken cancellationToken)
    {
        // Equal byte size is a cheap prerequisite for a metadata match. Include these
        // pairs even if a rename or transfer changed their effective timestamps. Each
        // file keeps its TimeCluster, so originals are only compared within their own window.
        var parents = Enumerable.Range(0, candidates.Count).ToArray();
        int Find(int i)
        {
            while (parents[i] != i) { parents[i] = parents[parents[i]]; i = parents[i]; }
            return i;
        }
        void Join(int left, int right) => parents[Find(left)] = Find(right);
        foreach (var (first, count) in windows)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (count < 2) continue;
            for (var index = first + 1; index < first + count; index++) Join(first, index);
        }
        foreach (var sameSize in Enumerable.Range(0, candidates.Count).GroupBy(index => candidates[index].Size))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var members = sameSize.ToArray();
            var protectedIndex = Array.FindIndex(members, index => candidates[index].IsProtected);
            if (protectedIndex < 0 || !members.Any(index => !candidates[index].IsProtected)) continue;
            foreach (var index in members) Join(members[protectedIndex], index);
        }
        var result = new List<DuplicateGroup>();
        foreach (var component in Enumerable.Range(0, candidates.Count).GroupBy(Find))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var files = component.Select(index => candidates[index]).ToList();
            if (files.All(file => file.IsProtected)) continue;
            AddGroupIfDuplicate(result, files, keepMode);
        }
        return result;
    }

    // [0-9], not \d: \d also matches non-ASCII decimal digits, which int.Parse rejects.
    [GeneratedRegex(@"(?<![0-9])(?<y>[0-9]{4})-(?<mo>[0-9]{2})-(?<d>[0-9]{2})[ _T](?<h>[0-9]{2})[-.](?<mi>[0-9]{2})[-.](?<s>[0-9]{2})(?![0-9])", RegexOptions.CultureInvariant)]
    private static partial Regex DashedTimestampRegex();

    [GeneratedRegex(@"(?<![0-9])(?<y>[0-9]{4})\.(?<mo>[0-9]{2})\.(?<d>[0-9]{2}) - (?<h>[0-9]{2})\.(?<mi>[0-9]{2})\.(?<s>[0-9]{2})(?![0-9])", RegexOptions.CultureInvariant)]
    private static partial Regex ShadowPlayTimestampRegex();

    [GeneratedRegex(@"(?<![0-9])(?<y>(?:19|20)[0-9]{2})(?<mo>[0-9]{2})(?<d>[0-9]{2})[_-](?<h>[0-9]{2})(?<mi>[0-9]{2})(?<s>[0-9]{2})(?:[0-9]{3})?(?![0-9])", RegexOptions.CultureInvariant)]
    private static partial Regex CompactTimestampRegex();
}
