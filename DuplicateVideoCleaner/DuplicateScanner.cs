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
    long Size);

public sealed record DuplicateGroup(int Number, IReadOnlyList<VideoCandidate> Files, VideoCandidate SuggestedKeep);

public static partial class DuplicateScanner
{
    public static IReadOnlyList<DuplicateGroup> Scan(
        string rootPath,
        int thresholdSeconds,
        bool includeSubfolders,
        KeepMode keepMode)
    {
        if (string.IsNullOrWhiteSpace(rootPath)) throw new ArgumentException("A folder is required.", nameof(rootPath));
        if (!Directory.Exists(rootPath)) throw new DirectoryNotFoundException($"Folder not found: {rootPath}");
        if (thresholdSeconds <= 0) throw new ArgumentOutOfRangeException(nameof(thresholdSeconds), "Threshold must be positive.");

        var option = includeSubfolders ? SearchOption.AllDirectories : SearchOption.TopDirectoryOnly;
        var candidates = Directory.EnumerateFiles(rootPath, "*", option)
            .Where(IsSupportedVideo)
            .Select(path => CreateCandidate(rootPath, path))
            .OrderBy(file => file.Timestamp)
            .ThenBy(file => file.FullPath, StringComparer.OrdinalIgnoreCase)
            .ToList();

        var result = new List<DuplicateGroup>();
        var current = new List<VideoCandidate>();

        foreach (var candidate in candidates)
        {
            if (current.Count == 0 || (candidate.Timestamp - current[^1].Timestamp).TotalSeconds <= thresholdSeconds)
            {
                current.Add(candidate);
                continue;
            }

            AddGroupIfDuplicate(result, current, keepMode);
            current = [candidate];
        }

        AddGroupIfDuplicate(result, current, keepMode);
        return result;
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
        var info = new FileInfo(path);
        var effective = GetEffectiveTimestamp(info.Name, info.CreationTime);
        return new VideoCandidate(
            info.FullName,
            Path.GetRelativePath(rootPath, info.FullName),
            effective.Timestamp,
            effective.Source,
            info.Length);
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
