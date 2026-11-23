namespace DuplicateVideoCleaner;

internal sealed record RemovalOutcome(RemovalResult Result, bool Successful, string Status, string Message);

// The current review: scan context, groups, metadata and selection, plus the operations a view
// performs on them. Views own presentation only (dialogs, navigation, rendering).
internal sealed class ReviewWorkspace(ActivityLog activity)
{
    private readonly HashSet<string> _selected = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, ReviewedVideo> _reviewed = new(StringComparer.OrdinalIgnoreCase);
    private ReviewSession? _session;

    internal ActivityLog Activity => activity;
    internal IReadOnlyList<DuplicateGroup> Groups { get; private set; } = [];
    internal ScanContext? Context { get; private set; }
    internal IReadOnlyDictionary<string, ReviewedVideo> Reviewed => _reviewed;
    internal bool HasResults => Groups.Count > 0;

    // Rebuilt whenever the group list is replaced; the session indexes selection per group.
    internal ReviewSession Session => _session is not null && ReferenceEquals(_session.Groups, Groups)
        ? _session : _session = new ReviewSession(Groups, _selected, Context, _reviewed);

    internal int FileCount => Session.FileCount;
    internal int SelectedCount => Session.SelectedCount;
    internal long SelectedBytes => Session.SelectedBytes;

    // The cleanup log follows the scanned folder; before a scan, the folder the user chose.
    internal string LogPath(string chosenFolder) => Context?.LogPath ?? CleanupLog.PathFor(chosenFolder.Trim());

    // A scan is starting: drop the previous review but keep its context for the scan's log.
    internal void Begin(ScanContext context)
    {
        Clear();
        Context = context;
    }

    internal void Load(ScanOutcome outcome) =>
        Load(outcome.Context, outcome.Groups, outcome.Reviewed, outcome.SuggestedSelection);

    internal void Load(ScanContext context, IReadOnlyList<DuplicateGroup> groups,
        IReadOnlyDictionary<string, ReviewedVideo> reviewed, IEnumerable<string> selected)
    {
        Clear();
        Context = context;
        Groups = groups;
        foreach (var (path, video) in reviewed) _reviewed[path] = video;
        _selected.UnionWith(selected);
    }

    internal void Clear()
    {
        Groups = [];
        Context = null;
        _reviewed.Clear();
        _selected.Clear();
        _session = null;
    }

    internal bool IsSelected(string path) => _selected.Contains(path);
    internal void SetSelected(VideoCandidate file, bool selected) => Session.SetSelected(file, selected);
    internal (int Count, long Bytes) GroupSelection(int number) => Session.GroupSelection(number);

    internal void SelectSuggested()
    {
        foreach (var group in Groups)
            foreach (var file in group.Files) Session.SetSelected(file, _reviewed[file.FullPath].SuggestRemoval);
    }

    internal void SelectAll(bool selected)
    {
        foreach (var group in Groups)
            foreach (var file in group.Files) Session.SetSelected(file, selected);
    }

    // Suggested = the group's suggested removals only; otherwise clear the group.
    internal void SelectGroup(DuplicateGroup group, bool suggested)
    {
        foreach (var file in group.Files)
            Session.SetSelected(file, suggested && _reviewed[file.FullPath].SuggestRemoval);
    }

    internal List<VideoCandidate> SelectedFiles() => Session.SelectedFiles();
    internal int GroupsWithoutSurvivors() => Session.GroupsWithoutSurvivors();
    internal DryRunContext CreateDryRun() => Session.CreateDryRun();

    // Drops removed files and regroups; selection of the remaining files is kept.
    internal void Reconcile(RemovalResult result)
    {
        var session = Session.Reconcile(result);
        Groups = session.Groups;
        _reviewed.Clear();
        foreach (var (path, video) in session.Reviewed) _reviewed.Add(path, video);
        _session = session;
    }

    // False (with an activity entry) when the audit log cannot be written; removal must not start.
    internal bool TryStartRemoval(out string error)
    {
        if (Context is null) { error = "No completed scan is available."; return false; }
        if (CleanupLog.TryStartRemovalSession(Context.LogPath, out error)) return true;
        activity.Add("Removal blocked", "The cleanup log could not be written.", false);
        return false;
    }

    // Removes the given files (normally SelectedFiles()) and reconciles the review.
    // Exceptions propagate: the caller must clear the review and ask for a rescan.
    internal async Task<RemovalOutcome> RemoveAsync(IReadOnlyList<VideoCandidate> files, bool permanent)
    {
        var context = Context ?? throw new InvalidOperationException("No completed scan is available.");
        var result = await FileRemovalService.RemoveReviewedAsync(files.Select(file => _reviewed[file.FullPath]).ToArray(), permanent, context.LogPath);
        Reconcile(result);
        var successful = result.Failures.Count == 0 && result.AuditError is null;
        activity.Add(successful ? "Removal complete" : "Removal partially complete", $"Removed {result.RemovedPaths.Count}; failed {result.Failures.Count}. {result.AuditError}", successful);
        var status = $"Removed {result.RemovedPaths.Count} file(s); {result.Failures.Count} failed.";
        var message = successful
            ? $"Successfully removed {result.RemovedPaths.Count} file(s)."
            : $"Removed {result.RemovedPaths.Count} file(s); {result.Failures.Count} failed or skipped.\n{result.AuditError}\n" +
              string.Join(Environment.NewLine, result.Failures.Take(15).Select(failure => $"{failure.File.RelativePath}: {failure.Error}"));
        return new RemovalOutcome(result, successful, status, message);
    }

    // Returns the status line for the view. Failures are reported, not thrown.
    internal async Task<string> ExportDryRunAsync(string destination, CancellationToken cancellationToken,
        Func<string, DryRunContext, CancellationToken, ExportResult>? export = null)
    {
        var context = CreateDryRun();
        try
        {
            var result = await Task.Run(() => export is null
                ? DryRunExporter.Export(destination, context, cancellationToken: cancellationToken)
                : export(destination, context, cancellationToken));
            var status = result.Errors.Count == 0
                ? $"Dry run exported: {Path.GetFileName(result.CsvPath)} and {Path.GetFileName(result.TextPath)}"
                : $"Dry run partially exported. {string.Join("; ", result.Errors)}";
            if (cancellationToken.IsCancellationRequested)
                status = $"Export cancelled. Completed reports: {(result.CsvPath is null ? 0 : 1) + (result.TextPath is null ? 0 : 1)}. Completed reports were retained.";
            activity.Add(cancellationToken.IsCancellationRequested ? "Export cancelled" : result.Errors.Count == 0 ? "Dry run exported" : "Dry run partially exported",
                result.Errors.Count == 0 ? destination : string.Join("; ", result.Errors), result.Errors.Count == 0);
            return status;
        }
        catch (Exception ex)
        {
            activity.Add("Export failed", ex.Message, false);
            return $"Export failed: {ex.Message}";
        }
    }
}
