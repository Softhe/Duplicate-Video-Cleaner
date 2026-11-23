using System.Diagnostics;

namespace DuplicateVideoCleaner;

// What a view shows while a scan runs. Called on the caller's synchronization context.
internal interface IScanProgress
{
    void Status(string text);
    // The view polls progress.Latest (see LatestMetadataProgress) until MetadataFinished.
    void MetadataStarted(LatestMetadataProgress progress);
    void MetadataFinished();
}

// A completed scan. With no groups, Status is the scan-page message; otherwise it describes
// the review (scan settings plus any warnings).
internal sealed record ScanOutcome(
    ScanContext Context,
    IReadOnlyList<DuplicateGroup> Groups,
    IReadOnlyDictionary<string, ReviewedVideo> Reviewed,
    IReadOnlySet<string> SuggestedSelection,
    string Status);

// Scan → diagnostics → isolated metadata → confidence → protection policy → suggestions.
// Runs its continuations on the caller's context, so activity entries are added on the UI thread.
internal sealed class ScanWorkflow(MetadataProbe probe, ActivityLog activity)
{
    internal const int MaxSkippedEntries = 199;
    internal const int MaxMetadataWarnings = 20;

    internal static string FormatProgress(MetadataProgress update) =>
        $"{update.Phase}: {update.CompletedFiles}/{update.TotalFiles} files · {update.Elapsed.TotalSeconds:0.0}s";

    // Throws OperationCanceledException when cancelled; partial results are never returned.
    internal async Task<ScanOutcome> RunAsync(ScanContext context, IScanProgress progress, CancellationToken cancellationToken,
        Func<string, IEnumerable<string>, CancellationToken, string?>? diagnosticWriter = null)
    {
        var threshold = context.ThresholdSeconds;
        var keep = context.KeepMode;
        var scanClock = Stopwatch.StartNew();
        var scan = await Task.Run(() => DuplicateScanner.ScanDetailed(context.Root, threshold, context.IncludeSubfolders, keep, cancellationToken, context.ProtectUndated));
        var groups = scan.Groups;
        var enumerationTime = scanClock.Elapsed;
        cancellationToken.ThrowIfCancellationRequested();

        progress.Status("Saving scan diagnostics…");
        var messages = scan.Diagnostics.Select(diagnostic => $"SCAN SKIPPED | {diagnostic.Path} | {diagnostic.Message}")
            .Prepend($"SCAN | Folder={context.Root} | Threshold={threshold}s | Keep={keep} | Subfolders={context.IncludeSubfolders} | ProtectUndated={context.ProtectUndated} | Groups={groups.Count} | Files={groups.Sum(g => g.Files.Count)}");
        var loggingError = await Task.Run(() => (diagnosticWriter ?? CleanupLog.AppendScanDiagnostics)(context.LogPath, messages, cancellationToken));
        if (loggingError is not null) activity.Add("Scan logging failed", loggingError, false);
        foreach (var diagnostic in scan.Diagnostics.Take(MaxSkippedEntries))
            activity.Add("Scan entry skipped", $"{diagnostic.Path}: {diagnostic.Message}", false);
        if (scan.Diagnostics.Count > MaxSkippedEntries)
            activity.Add("Additional scan entries skipped", $"{scan.Diagnostics.Count - MaxSkippedEntries} additional entries were skipped; see the cleanup log for all diagnostics.", false);
        var skipped = scan.Diagnostics.Count == 0 ? string.Empty : $" {scan.Diagnostics.Count} entries were skipped; see Activity.";
        if (loggingError is not null) skipped += " Scan diagnostics could not be fully saved; see Activity.";

        var reviewed = new Dictionary<string, ReviewedVideo>(StringComparer.OrdinalIgnoreCase);
        var suggested = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (groups.Count == 0)
        {
            activity.Add("Scan complete", "No duplicate groups were found." + skipped, scan.Diagnostics.Count == 0 && loggingError is null);
            return new ScanOutcome(context, groups, reviewed, suggested, $"No potential duplicate groups found within {threshold} seconds.{skipped}");
        }

        progress.Status("Reading metadata with the isolated parser…");
        if (probe.IsAvailable)
        {
            var metadataProgress = new LatestMetadataProgress();
            progress.MetadataStarted(metadataProgress);
            IReadOnlyDictionary<string, MediaMetadata> metadata;
            try { metadata = await probe.ProbeManyAsync(groups.SelectMany(group => group.Files).ToArray(), cancellationToken, metadataProgress); }
            finally { progress.MetadataFinished(); }
            if (probe.LastBatchMetrics is { } metrics)
                activity.Add("Scan performance", $"Enumeration {enumerationTime.TotalSeconds:0.00}s; setup {metrics.Setup.TotalSeconds:0.00}s; parsing {metrics.Parsing.TotalSeconds:0.00}s; metadata elapsed {metrics.Elapsed.TotalSeconds:0.00}s; {metrics.CacheHits}/{metrics.Files} cached; {metrics.Parsed} parsed. Parsing time sums worker durations.", true);
            foreach (var group in groups)
            {
                var referenceMetadata = metadata[group.SuggestedKeep.FullPath];
                foreach (var file in group.Files)
                {
                    var fileMetadata = metadata[file.FullPath];
                    var confidence = ConfidenceEvaluator.Evaluate(file, fileMetadata, group.SuggestedKeep, referenceMetadata);
                    reviewed[file.FullPath] = new ReviewedVideo(file, fileMetadata, confidence, ReferenceEquals(file, group.SuggestedKeep));
                }
            }
        }
        else
        {
            foreach (var group in groups)
                foreach (var file in group.Files)
                    reviewed[file.FullPath] = new ReviewedVideo(file, new MediaMetadata(null, null, null, MetadataProbeStatus.Unavailable, "ffprobe was not found"), DuplicateConfidence.Low, ReferenceEquals(file, group.SuggestedKeep));
        }

        foreach (var group in groups) RenamedFilePolicy.Apply(group, reviewed, keep);
        var metadataWarnings = reviewed.Values.Where(video => !string.IsNullOrWhiteSpace(video.Metadata.Error) || video.Metadata.Status is MetadataProbeStatus.Failed or MetadataProbeStatus.TimedOut).ToArray();
        foreach (var warning in metadataWarnings.Take(MaxMetadataWarnings))
            activity.Add("Metadata needs attention", $"{warning.Candidate.RelativePath}: {warning.Metadata.Error ?? warning.Metadata.Status.ToString()}", false);
        if (metadataWarnings.Length > MaxMetadataWarnings)
            activity.Add("Additional metadata warnings", $"{metadataWarnings.Length - MaxMetadataWarnings} additional files need attention. Metadata confidence is shown in Review.", false);
        if (metadataWarnings.Length > 0) skipped += $" Metadata warnings affect {metadataWarnings.Length} file(s); see Activity and Review.";

        foreach (var group in groups)
            foreach (var file in group.Files.Where(file => reviewed[file.FullPath].SuggestRemoval)) suggested.Add(file.FullPath);
        cancellationToken.ThrowIfCancellationRequested();

        var status = context.Description + (probe.IsAvailable
            ? string.Empty : " · Metadata unavailable: ffprobe was not found. Time-based review remains available.") + skipped;
        activity.Add("Scan complete", $"Found {groups.Count} duplicate group(s) containing {groups.Sum(group => group.Files.Count)} files.{skipped}", scan.Diagnostics.Count == 0 && loggingError is null && metadataWarnings.Length == 0);
        return new ScanOutcome(context, groups, reviewed, suggested, status);
    }
}
