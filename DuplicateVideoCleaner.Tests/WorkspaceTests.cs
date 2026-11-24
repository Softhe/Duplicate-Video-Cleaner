using DuplicateVideoCleaner;

// The scan → review → removal/export workflow without any UI, so it is covered independently of the views.
internal static class WorkspaceTests
{
    internal static IEnumerable<(string Name, Func<Task> Run)> Cases =>
    [
        ("workspace scan suggests removals and records activity", ScanSuggestsRemovals),
        ("workspace scan without groups keeps its context for the log", ScanWithoutGroups),
        ("workspace scan cancellation returns no partial results", ScanCancellation),
        ("workspace scan reports an unwritable diagnostics log", ScanLoggingFailure),
        ("workspace selection respects suggestions and protection", SelectionRules),
        ("workspace removal to Recovery reconciles the review", RemovalToRecovery),
        ("workspace removal is blocked when the cleanup log cannot be written", RemovalBlockedWithoutLog),
        ("workspace dry-run export reports success and failure", ExportOutcomes),
        ("activity log keeps the newest 200 entries", ActivityCapacity),
    ];

    private sealed class RecordingProgress : IScanProgress
    {
        internal List<string> Statuses { get; } = [];
        internal int MetadataStarts { get; private set; }
        public void Status(string text) => Statuses.Add(text);
        public void MetadataStarted(LatestMetadataProgress progress) => MetadataStarts++;
        public void MetadataFinished() { }
    }

    private static async Task ScanSuggestsRemovals() => await WithFolderAsync(async root =>
    {
        Write(root, "2026-08-02 10-00-00 first.mp4", 1);
        Write(root, "2026-08-02 10-00-30 second.mkv", 2);
        Write(root, "2026-08-02 12-00-00 alone.mp4", 3);
        var (activity, workspace, workflow) = Create(root);
        var progress = new RecordingProgress();
        var context = new ScanContext(root, 60, KeepMode.Oldest, false);
        workspace.Begin(context);
        var outcome = await workflow.RunAsync(context, progress, CancellationToken.None);
        workspace.Load(outcome);

        Equal(1, workspace.Groups.Count);
        Equal(2, workspace.FileCount);
        Equal(1, workspace.SelectedCount);
        True(workspace.IsSelected(Path.Combine(root, "2026-08-02 10-00-30 second.mkv")), "The newer file should be suggested for removal.");
        True(outcome.Status.StartsWith(context.Description), "Review status should describe the scan settings.");
        True(outcome.Status.Contains("Metadata unavailable"), "Missing ffprobe must be reported.");
        Equal(0, progress.MetadataStarts);
        True(progress.Statuses.Contains("Saving scan diagnostics…"), "Scan progress should report diagnostics.");
        Equal("Scan complete", activity.Entries[0].Title);
        True(File.ReadAllText(CleanupLog.PathFor(root)).Contains("SCAN | Folder="), "Scan diagnostics should reach the cleanup log.");
    });

    private static async Task ScanWithoutGroups() => await WithFolderAsync(async root =>
    {
        Write(root, "2026-08-02 10-00-00 first.mp4", 1);
        var (activity, workspace, workflow) = Create(root);
        var context = new ScanContext(root, 60, KeepMode.Oldest, false);
        var outcome = await workflow.RunAsync(context, new RecordingProgress(), CancellationToken.None);
        workspace.Load(outcome);
        False(workspace.HasResults, "A single file cannot form a group.");
        Equal("No potential duplicate groups found within 60 seconds.", outcome.Status);
        Equal(CleanupLog.PathFor(root), workspace.LogPath("ignored"));
        Equal(0, workspace.CreateDryRun().Rows.Count);
        Equal("Scan complete", activity.Entries[0].Title);
    });

    private static async Task ScanCancellation() => await WithFolderAsync(async root =>
    {
        Write(root, "2026-08-02 10-00-00 first.mp4", 1);
        Write(root, "2026-08-02 10-00-30 second.mkv", 2);
        var (_, _, workflow) = Create(root);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        try
        {
            await workflow.RunAsync(new ScanContext(root, 60, KeepMode.Oldest, false), new RecordingProgress(), cancellation.Token);
            throw new InvalidOperationException("A cancelled scan returned results.");
        }
        catch (OperationCanceledException) { }
    });

    private static async Task ScanLoggingFailure() => await WithFolderAsync(async root =>
    {
        Write(root, "2026-08-02 10-00-00 first.mp4", 1);
        Write(root, "2026-08-02 10-00-30 second.mkv", 2);
        var (activity, _, workflow) = Create(root);
        var outcome = await workflow.RunAsync(new ScanContext(root, 60, KeepMode.Oldest, false), new RecordingProgress(),
            CancellationToken.None, (_, _, _) => "disk full");
        True(outcome.Status.Contains("Scan diagnostics could not be fully saved"), "The review status must mention the logging failure.");
        True(activity.Entries.Any(entry => entry.Title == "Scan logging failed" && entry.Detail == "disk full"), "Logging failure must be in Activity.");
        False(activity.Entries.First(entry => entry.Title == "Scan complete").Success, "A scan with a logging failure is not fully successful.");
    });

    private static async Task SelectionRules() => await WithFolderAsync(async root =>
    {
        Write(root, "2026-08-02 10-00-00 first.mp4", 1);
        Write(root, "2026-08-02 10-00-20 second.mp4", 2);
        Write(root, "2026-08-02 10-00-40 third.mp4", 3);
        var favourite = Write(root, "favourite.mp4", 1);
        var (_, workspace, workflow) = Create(root);
        var context = new ScanContext(root, 60, KeepMode.Oldest, false, ProtectUndated: true);
        workspace.Load(await workflow.RunAsync(context, new RecordingProgress(), CancellationToken.None));
        var group = workspace.Groups.Single();
        var protectedClip = group.Files.Single(file => file.FullPath == favourite);
        True(protectedClip.IsProtected, "An undated clip must be protected.");

        workspace.SelectAll(true);
        False(workspace.IsSelected(favourite), "Select all must skip protected clips.");
        workspace.SetSelected(protectedClip, true);
        False(workspace.IsSelected(favourite), "A protected clip must never be selectable.");
        var selectable = workspace.SelectedCount;

        workspace.SelectAll(false);
        Equal(0, workspace.SelectedCount);
        Equal(0L, workspace.SelectedBytes);
        workspace.SelectSuggested();
        var suggested = workspace.SelectedCount;
        True(suggested <= selectable, "Suggestions are a subset of what can be selected.");
        True(workspace.SelectedFiles().All(file => workspace.Reviewed[file.FullPath].SuggestRemoval), "Suggested selection must only contain suggestions.");

        workspace.SelectGroup(group, suggested: false);
        Equal((0, 0L), workspace.GroupSelection(group.Number));
        workspace.SelectGroup(group, suggested: true);
        Equal(suggested, workspace.GroupSelection(group.Number).Count);
    });

    private static async Task RemovalToRecovery() => await WithFolderAsync(async root =>
    {
        var keeper = Write(root, "2026-08-02 10-00-00 first.mp4", 1);
        var copy = Write(root, "2026-08-02 10-00-30 second.mkv", 2);
        var (activity, workspace, workflow) = Create(root);
        workspace.Load(await workflow.RunAsync(new ScanContext(root, 60, KeepMode.Oldest, false), new RecordingProgress(), CancellationToken.None));
        Equal(0, workspace.GroupsWithoutSurvivors());
        True(workspace.TryStartRemoval(out var error), error);
        var outcome = await workspace.RemoveAsync(workspace.SelectedFiles(), permanent: false);

        True(outcome.Successful, outcome.Message);
        Equal("Removed 1 file(s); 0 failed.", outcome.Status);
        True(outcome.Result.RemovedPaths.Contains(copy), "The selected copy should be removed.");
        False(File.Exists(copy), "The copy should have moved to Recovery.");
        True(File.Exists(keeper), "The keeper must stay in place.");
        False(workspace.HasResults, "A group with one remaining file is no longer a duplicate group.");
        Equal("Removal complete", activity.Entries[0].Title);
        Equal(1, RecoveryStore.List(root).Count);
    });

    private static async Task RemovalBlockedWithoutLog() => await WithFolderAsync(async root =>
    {
        Write(root, "2026-08-02 10-00-00 first.mp4", 1);
        var copy = Write(root, "2026-08-02 10-00-30 second.mkv", 2);
        var (activity, workspace, workflow) = Create(root);
        workspace.Load(await workflow.RunAsync(new ScanContext(root, 60, KeepMode.Oldest, false), new RecordingProgress(),
            CancellationToken.None, (_, _, _) => null));
        Directory.CreateDirectory(CleanupLog.PathFor(root));
        False(workspace.TryStartRemoval(out var error), "Removal must not start without an audit log.");
        True(error.Length > 0, "The failure reason should be reported.");
        Equal("Removal blocked", activity.Entries[0].Title);
        True(File.Exists(copy), "Nothing may be removed.");
    });

    private static async Task ExportOutcomes() => await WithFolderAsync(async root =>
    {
        Write(root, "2026-08-02 10-00-00 first.mp4", 1);
        Write(root, "2026-08-02 10-00-30 second.mkv", 2);
        var (activity, workspace, workflow) = Create(root);
        workspace.Load(await workflow.RunAsync(new ScanContext(root, 60, KeepMode.Oldest, false), new RecordingProgress(), CancellationToken.None));
        var reports = Directory.CreateDirectory(Path.Combine(root, "reports")).FullName;

        var status = await workspace.ExportDryRunAsync(reports, CancellationToken.None);
        True(status.StartsWith("Dry run exported: "), status);
        Equal("Dry run exported", activity.Entries[0].Title);
        Equal(2, Directory.GetFiles(reports).Length);

        status = await workspace.ExportDryRunAsync(reports, CancellationToken.None, (_, _, _) => throw new IOException("destination offline"));
        Equal("Export failed: destination offline", status);
        Equal("Export failed", activity.Entries[0].Title);
        False(activity.Entries[0].Success, "A failed export is not a success.");
    });

    private static Task ActivityCapacity()
    {
        var log = new ActivityLog();
        var evictions = 0;
        log.Added += (_, evicted) => { if (evicted is not null) evictions++; };
        for (var index = 0; index < ActivityLog.Capacity + 5; index++) log.Add($"entry {index}", "", true);
        Equal(ActivityLog.Capacity, log.Entries.Count);
        Equal(5, evictions);
        Equal($"entry {ActivityLog.Capacity + 4}", log.Entries[0].Title);
        Equal("entry 5", log.Entries[^1].Title);
        return Task.CompletedTask;
    }

    private static (ActivityLog Activity, ReviewWorkspace Workspace, ScanWorkflow Workflow) Create(string root)
    {
        var activity = new ActivityLog();
        // A missing ffprobe keeps these tests independent of the bundled parser.
        var probe = new MetadataProbe(Path.Combine(root, "missing-ffprobe.exe"));
        return (activity, new ReviewWorkspace(activity), new ScanWorkflow(probe, activity));
    }

    private static string Write(string root, string name, int length)
    {
        var path = Path.Combine(root, name);
        File.WriteAllBytes(path, Enumerable.Repeat((byte)7, length).ToArray());
        return path;
    }

    private static async Task WithFolderAsync(Func<string, Task> test)
    {
        var root = Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), "dvc-workspace-" + Guid.NewGuid().ToString("N"))).FullName;
        try { await test(root); }
        finally { try { Directory.Delete(root, true); } catch { } }
    }

    private static void True(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
    private static void False(bool condition, string message) => True(!condition, message);
    private static void Equal<T>(T expected, T actual) =>
        True(EqualityComparer<T>.Default.Equals(expected, actual), $"Expected {expected}, got {actual}.");
}
