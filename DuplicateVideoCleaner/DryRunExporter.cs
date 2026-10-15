using System.Globalization;
using System.Text;

namespace DuplicateVideoCleaner;

internal sealed record DryRunRow(int Group, ReviewedVideo Video, bool Selected);
internal sealed record DryRunContext(string Root, int ThresholdSeconds, KeepMode KeepMode, bool IncludeSubfolders, IReadOnlyList<DryRunRow> Rows);
internal sealed record ExportResult(string? CsvPath, string? TextPath, IReadOnlyList<string> Errors);

internal static class DryRunExporter
{
    internal static ExportResult Export(string destination, DryRunContext context, DateTimeOffset? now = null)
    {
        try { Directory.CreateDirectory(destination); }
        catch (Exception ex) { return new ExportResult(null, null, [$"Destination: {ex.Message}"]); }
        var stamp = (now ?? DateTimeOffset.Now).ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture) + "-" + Guid.NewGuid().ToString("N");
        var csvPath = Path.Combine(destination, $"DuplicateVideoDryRun-{stamp}.csv");
        var textPath = Path.Combine(destination, $"DuplicateVideoDryRun-{stamp}.txt");
        var errors = new List<string>();
        string? successfulCsv = null, successfulText = null;
        try { AtomicWrite(csvPath, BuildCsv(context)); successfulCsv = csvPath; } catch (Exception ex) { errors.Add($"CSV: {ex.Message}"); }
        try { AtomicWrite(textPath, BuildText(context)); successfulText = textPath; } catch (Exception ex) { errors.Add($"Text: {ex.Message}"); }
        return new ExportResult(successfulCsv, successfulText, errors);
    }

    internal static string BuildCsv(DryRunContext context)
    {
        var builder = new StringBuilder();
        builder.AppendLine("Group,SelectedAction,SuggestedAction,Path,EffectiveTimestamp,TimestampSource,Duration,Resolution,Bytes,FormattedSize,Confidence,KeepRule,ThresholdSeconds,Scope");
        foreach (var row in context.Rows)
        {
            var video = row.Video;
            var values = new[]
            {
                row.Group.ToString(CultureInfo.InvariantCulture), row.Selected ? "Remove" : "Keep", video.SuggestedKeep ? "Keep" : "Remove",
                video.Candidate.FullPath, video.Candidate.Timestamp.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture), video.Candidate.TimestampSource.ToString(),
                video.Metadata.DurationText, video.Metadata.ResolutionText, video.Candidate.Size.ToString(CultureInfo.InvariantCulture), MainForm.FormatSize(video.Candidate.Size),
                video.Confidence.ToString(), context.KeepMode.ToString(), context.ThresholdSeconds.ToString(CultureInfo.InvariantCulture), context.IncludeSubfolders ? "Recursive" : "Current folder"
            };
            builder.AppendLine(string.Join(',', values.Select(EscapeCsv)));
        }
        return builder.ToString();
    }

    internal static string BuildText(DryRunContext context)
    {
        var selected = context.Rows.Where(row => row.Selected).ToList();
        var builder = new StringBuilder();
        builder.AppendLine("Duplicate Video Cleaner — Dry Run");
        builder.AppendLine($"Folder: {context.Root}");
        builder.AppendLine($"Settings: {context.ThresholdSeconds}s threshold; keep {context.KeepMode}; {(context.IncludeSubfolders ? "including subfolders" : "current folder only")}");
        builder.AppendLine($"Groups: {context.Rows.Select(row => row.Group).Distinct().Count()}; files: {context.Rows.Count}; selected: {selected.Count}; estimated reclaimable: {MainForm.FormatSize(selected.Sum(row => row.Video.Candidate.Size))}");
        foreach (var confidence in Enum.GetValues<DuplicateConfidence>()) builder.AppendLine($"{confidence} confidence: {context.Rows.Count(row => row.Video.Confidence == confidence)}");
        builder.AppendLine();
        foreach (var group in context.Rows.GroupBy(row => row.Group))
        {
            builder.AppendLine($"Group {group.Key}");
            foreach (var row in group) builder.AppendLine($"  {(row.Selected ? "REMOVE" : "KEEP"),-6} [{row.Video.Confidence}] {row.Video.Candidate.RelativePath}");
        }
        return builder.ToString();
    }

    internal static string EscapeCsv(string value) => value.IndexOfAny([',', '"', '\r', '\n']) >= 0 ? $"\"{value.Replace("\"", "\"\"")}\"" : value;

    private static void AtomicWrite(string path, string contents)
    {
        var temporary = path + ".tmp";
        try
        {
            File.WriteAllText(temporary, contents, new UTF8Encoding(false));
            File.Move(temporary, path, false);
        }
        finally
        {
            try { File.Delete(temporary); } catch { }
        }
    }
}
