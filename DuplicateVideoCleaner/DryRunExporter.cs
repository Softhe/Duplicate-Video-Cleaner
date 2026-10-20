using System.Globalization;
using System.Text;

namespace DuplicateVideoCleaner;

internal sealed record DryRunRow(int Group, ReviewedVideo Video, bool Selected);
internal sealed record DryRunContext(string Root, int ThresholdSeconds, KeepMode KeepMode, bool IncludeSubfolders, IReadOnlyList<DryRunRow> Rows);
internal sealed record ExportResult(string? CsvPath, string? TextPath, IReadOnlyList<string> Errors);

internal static class DryRunExporter
{
    internal static ExportResult Export(string destination, DryRunContext context, DateTimeOffset? now = null, CancellationToken cancellationToken = default)
    {
        try { Directory.CreateDirectory(destination); }
        catch (Exception ex) { return new ExportResult(null, null, [$"Destination: {ex.Message}"]); }
        var stamp = (now ?? DateTimeOffset.Now).ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture) + "-" + Guid.NewGuid().ToString("N");
        var csvPath = Path.Combine(destination, $"DuplicateVideoDryRun-{stamp}.csv");
        var textPath = Path.Combine(destination, $"DuplicateVideoDryRun-{stamp}.txt");
        var errors = new List<string>();
        string? successfulCsv = null, successfulText = null;
        try { AtomicWrite(csvPath, writer => WriteCsv(writer, context, cancellationToken), cancellationToken); successfulCsv = csvPath; } catch (Exception ex) { errors.Add($"CSV: {ex.Message}"); }
        try { AtomicWrite(textPath, writer => WriteText(writer, context, cancellationToken), cancellationToken); successfulText = textPath; } catch (Exception ex) { errors.Add($"Text: {ex.Message}"); }
        return new ExportResult(successfulCsv, successfulText, errors);
    }

    internal static string BuildCsv(DryRunContext context)
    {
        using var writer = new StringWriter(CultureInfo.InvariantCulture);
        WriteCsv(writer, context, CancellationToken.None);
        return writer.ToString();
    }

    private static void WriteCsv(TextWriter builder, DryRunContext context, CancellationToken cancellationToken)
    {
        builder.WriteLine("Group,SelectedAction,SuggestedAction,Path,EffectiveTimestamp,TimestampSource,Duration,Resolution,Bytes,FormattedSize,Confidence,KeepRule,ThresholdSeconds,Scope");
        foreach (var row in context.Rows)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var video = row.Video;
            var values = new[]
            {
                row.Group.ToString(CultureInfo.InvariantCulture), row.Selected ? "Remove" : "Keep", video.SuggestedKeep ? "Keep" : "Remove",
                video.Candidate.FullPath, video.Candidate.Timestamp.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture), video.Candidate.TimestampSource.ToString(),
                video.Metadata.DurationText, video.Metadata.ResolutionText, video.Candidate.Size.ToString(CultureInfo.InvariantCulture), MainForm.FormatSize(video.Candidate.Size),
                video.Confidence.ToString(), context.KeepMode.ToString(), context.ThresholdSeconds.ToString(CultureInfo.InvariantCulture), context.IncludeSubfolders ? "Recursive" : "Current folder"
            };
            builder.WriteLine(string.Join(',', values.Select(EscapeCsv)));
        }
    }

    internal static string BuildText(DryRunContext context)
    {
        using var writer = new StringWriter(CultureInfo.InvariantCulture);
        WriteText(writer, context, CancellationToken.None);
        return writer.ToString();
    }

    private static void WriteText(TextWriter builder, DryRunContext context, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var selected = context.Rows.Where(row => row.Selected).ToList();
        builder.WriteLine("Duplicate Video Cleaner — Dry Run");
        builder.WriteLine($"Folder: {context.Root}");
        builder.WriteLine($"Settings: {context.ThresholdSeconds}s threshold; keep {context.KeepMode}; {(context.IncludeSubfolders ? "including subfolders" : "current folder only")}");
        builder.WriteLine($"Groups: {context.Rows.Select(row => row.Group).Distinct().Count()}; files: {context.Rows.Count}; selected: {selected.Count}; selected size: {MainForm.FormatSize(selected.Sum(row => row.Video.Candidate.Size))}");
        foreach (var confidence in Enum.GetValues<DuplicateConfidence>()) builder.WriteLine($"{confidence} confidence: {context.Rows.Count(row => row.Video.Confidence == confidence)}");
        builder.WriteLine();
        foreach (var group in context.Rows.GroupBy(row => row.Group))
        {
            cancellationToken.ThrowIfCancellationRequested();
            builder.WriteLine($"Group {group.Key}");
            foreach (var row in group)
            {
                cancellationToken.ThrowIfCancellationRequested();
                builder.WriteLine($"  {(row.Selected ? "REMOVE" : "KEEP"),-6} [{row.Video.Confidence}] {row.Video.Candidate.RelativePath}");
            }
        }
    }

    internal static string EscapeCsv(string value)
    {
        // Spreadsheet programs interpret formula prefixes even in quoted CSV cells.
        var first = value.AsSpan().TrimStart();
        if (!first.IsEmpty && (first[0] is '=' or '+' or '-' or '@' || value[0] is '\t' or '\r' or '\n'))
            value = "'" + value;
        return value.IndexOfAny([',', '"', '\r', '\n']) >= 0 ? $"\"{value.Replace("\"", "\"\"")}\"" : value;
    }
    private static void AtomicWrite(string path, Action<TextWriter> write, CancellationToken cancellationToken)
    {
        var temporary = path + ".tmp";
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            using (var writer = new StreamWriter(temporary, false, new UTF8Encoding(false))) write(writer);
            cancellationToken.ThrowIfCancellationRequested();
            File.Move(temporary, path, false);
        }
        finally
        {
            try { File.Delete(temporary); } catch { }
        }
    }
}
