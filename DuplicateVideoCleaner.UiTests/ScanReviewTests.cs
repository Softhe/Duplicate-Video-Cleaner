using DuplicateVideoCleaner.ViewModels;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using static DuplicateVideoCleaner.UiTests.WpfTestHost;

namespace DuplicateVideoCleaner.UiTests;

// Scan → Review behaviour through the view models the WPF views bind to.
[TestClass]
[DoNotParallelize]
public sealed class ScanReviewTests
{
    private static readonly Dictionary<string, int> ProbeProcesses = new(StringComparer.OrdinalIgnoreCase);
    public TestContext TestContext { get; set; } = null!;

    [TestMethod]
    public void RenamedProtectionReachesBulkSelectionRowsAndExport()
    {
        RunWithWorkspace(async (main, root) =>
        {
            File.WriteAllText(Path.Combine(root, "favorite.mp4"), "one");
            File.WriteAllText(Path.Combine(root, "another favorite.mp4"), "one");
            File.WriteAllText(Path.Combine(root, "2026-01-01 10-00-02 unmatched.mp4"), "longer");
            main.Scan.ProtectUndated = true;
            await main.Scan.ScanAsync();
            void AssertSelection()
            {
                var context = main.Workspace.CreateDryRun();
                Assert.AreEqual(5, context.Rows.Count);
                Assert.AreEqual(2, context.Rows.Count(row => row.Video.Candidate.IsProtected));
                Assert.AreEqual(2, context.Rows.Count(row => row.Selected));
                Assert.IsTrue(context.Rows.Where(row => row.Selected).All(row => !row.Video.Candidate.IsProtected && row.Video.MatchesProtectedCopy));
                Assert.IsTrue(context.Rows.Single(row => row.Video.SuggestedKeep).Video.Candidate.IsProtected);
                StringAssert.Contains(DryRunExporter.BuildCsv(context), "Protected clip (no recording date in its name)");
                StringAssert.Contains(DryRunExporter.BuildText(context), "No metadata match to a protected clip and no other original in its time window; kept");
                StringAssert.Contains(DryRunExporter.BuildText(context), "protecting clips without a recording date in the name");
            }
            AssertSelection();
            main.Review.SelectAllCommand.Execute(null);
            AssertSelection();
            main.Review.SelectSuggestedCommand.Execute(null);
            AssertSelection();
            var group = main.Review.Groups.Single();
            Assert.AreSame(group, main.Review.SelectedGroup, "The first group should be shown after a scan.");
            foreach (var name in new[] { "favorite.mp4", "another favorite.mp4", "2026-01-01 10-00-02 unmatched.mp4" })
            {
                var row = group.Files.Single(file => file.RelativePath == name);
                Assert.IsFalse(row.CanSelect, $"{name} must not be selectable.");
                row.IsSelected = true;
                Assert.IsFalse(row.IsSelected, $"{name} was selected despite protection.");
            }
            Assert.AreEqual("Protected", group.Files.Single(file => file.RelativePath == "favorite.mp4").Role);
            group.ClearCommand.Execute(null);
            Assert.AreEqual(0, main.Workspace.CreateDryRun().Rows.Count(row => row.Selected));
            Assert.AreEqual("Nothing selected", main.Review.SelectionTotal);
            group.SelectSuggestedCommand.Execute(null);
            AssertSelection();
        }, useProbeFixture: true);
    }

    [TestMethod]
    public void ProtectionWithoutMetadataKeepsProtectedClipsAndTimestampReview()
    {
        RunWithWorkspace(async (main, root) =>
        {
            File.WriteAllText(Path.Combine(root, "favorite.mp4"), "one");
            main.Scan.ProtectUndated = true;
            await main.Scan.ScanAsync();
            var rows = main.Workspace.CreateDryRun().Rows;
            Assert.AreEqual(3, rows.Count);
            // Without metadata no copy can be confirmed, but the two dated originals are
            // still reviewed by timestamp among themselves; the protected clip is kept.
            Assert.AreEqual("2026-01-01 10-00-01 second.mp4", rows.Single(row => row.Video.SuggestRemoval).Video.Candidate.RelativePath);
            main.Review.SelectAllCommand.Execute(null);
            Assert.IsFalse(main.Workspace.CreateDryRun().Rows.Any(row => row.Selected && row.Video.Candidate.IsProtected));
            StringAssert.Contains(main.Review.Status, "Metadata unavailable", "Missing metadata must be visible in Review.");
        });
    }

    [TestMethod]
    public void UndatedClipsAreReviewedByDefaultAndTheSettingPersists()
    {
        RunWithWorkspace(async (main, root) =>
        {
            foreach (var path in Directory.EnumerateFiles(root, "*.mp4")) File.Delete(path);
            foreach (var name in new[] { "GOPR0001.MP4", "GOPR0002.MP4" }) File.WriteAllText(Path.Combine(root, name), name);
            Assert.IsFalse(main.Scan.ProtectUndated, "Protection must be opt-in.");
            await main.Scan.ScanAsync();
            var rows = main.Workspace.CreateDryRun().Rows;
            Assert.AreEqual(2, rows.Count);
            Assert.IsFalse(rows.Any(row => row.Video.Candidate.IsProtected));
            Assert.AreEqual(1, rows.Count(row => row.Selected), "The undated duplicate was not suggested for removal.");
            main.Scan.ProtectUndated = true;
            await main.Scan.ScanAsync();
            await WaitUntil(() => new SettingsService(Path.Combine(root, "settings.json")).Load().ProtectUndatedClips, "The protection setting was not saved.");
            var saved = new SettingsService(Path.Combine(root, "settings.json")).Load();
            Assert.AreEqual(root, saved.RecentFolders.First(), "A scanned folder should become the most recent folder.");
            Assert.AreEqual(ThemePreference.Dark, saved.Theme);
        });
    }

    [TestMethod]
    public void MetadataSuccessReachesReviewConfidenceAndExport()
    {
        TestContext.WriteLine(string.IsNullOrEmpty(Environment.GetEnvironmentVariable("DVC_TEST_FFPROBE"))
            ? "Using subprocess metadata fixture." : "Using real ffprobe and video fixture.");
        RunWithWorkspace(async (main, root) =>
        {
            await main.Scan.ScanAsync();
            var context = main.Workspace.CreateDryRun();
            Assert.AreEqual(2, context.Rows.Count);
            foreach (var row in context.Rows)
            {
                Assert.AreEqual(MetadataProbeStatus.Available, row.Video.Metadata.Status);
                Assert.IsTrue(row.Video.Metadata.Width > 0 && row.Video.Metadata.Height > 0);
                Assert.IsTrue(row.Video.Metadata.Duration > TimeSpan.Zero);
                Assert.AreEqual(DuplicateConfidence.High, row.Video.Confidence);
            }
            var group = main.Review.SelectedGroup!;
            Assert.AreEqual("High", group.ConfidenceKey);
            var candidate = group.Files.Single(file => file.Role == "Remove");
            Assert.IsTrue(candidate.Attributes.All(attribute => attribute.IsMatch), "Matching metadata should be marked as a match.");
            Assert.IsTrue(group.Files.Single(file => file.Role == "Keep").Attributes.All(attribute => attribute.Matches is null), "The keeper is the reference.");
            var result = DryRunExporter.Export(root, context);
            Assert.AreEqual(0, result.Errors.Count);
            StringAssert.Contains(File.ReadAllText(result.CsvPath!), ",High,");
        }, useProbeFixture: true, useRealMediaIfProvided: true);
    }

    [TestMethod]
    public void ExportAndLogUseCompletedScanAfterAllInputsChange()
    {
        RunWithWorkspace(async (main, root) =>
        {
            await main.Scan.ScanAsync();
            main.Scan.Folder = Path.Combine(root, "other-source");
            main.Scan.ThresholdSeconds = 3600;
            main.Scan.KeepNewest = true;
            main.Scan.IncludeSubfolders = true;
            var context = main.Workspace.CreateDryRun();
            Assert.AreEqual(root, context.Root);
            Assert.AreEqual(60, context.ThresholdSeconds);
            Assert.AreEqual(KeepMode.Oldest, context.KeepMode);
            Assert.IsFalse(context.IncludeSubfolders);
            Assert.AreEqual(2, context.Rows.Count);
            Assert.AreEqual(Path.Combine(root, "DuplicateCleanupLog.txt"), main.Workspace.LogPath(main.ChosenFolder));
            StringAssert.Contains(main.Review.Summary, $"in {root} · 60 s window · keep oldest · this folder only");
            var export = DryRunExporter.Export(root, context);
            Assert.AreEqual(0, export.Errors.Count);
            var text = File.ReadAllText(export.TextPath!);
            StringAssert.Contains(text, $"Folder: {root}");
            StringAssert.Contains(text, "60s threshold; keep Oldest; current folder only");
            Assert.IsFalse(text.Contains("other-source"));
        });
    }

    [TestMethod]
    public void RemovalRebuildUsesNewestRuleAndUpdatesSuggestedKeep()
    {
        RunWithWorkspace(async (main, root) =>
        {
            var newest = Path.Combine(root, "2026-01-01 10-00-02 newest.mp4");
            File.WriteAllText(newest, "three");
            main.Scan.KeepNewest = true;
            await main.Scan.ScanAsync();
            main.Workspace.Reconcile(new RemovalResult(new HashSet<string>(StringComparer.OrdinalIgnoreCase) { newest }, []));
            main.Review.Reload();
            var context = main.Workspace.CreateDryRun();
            Assert.AreEqual(2, context.Rows.Count);
            var kept = context.Rows.Single(row => row.Video.SuggestedKeep);
            StringAssert.EndsWith(kept.Video.Candidate.FullPath, "second.mp4");
            Assert.IsFalse(context.Rows.Any(row => row.Video.Candidate.FullPath == newest));
            var keeperRow = main.Review.SelectedGroup!.Files.Single(file => file.Role == "Keep");
            Assert.AreEqual("newest in group · reference", keeperRow.RoleNote);
        });
    }

    [TestMethod]
    public void MoveSelectedConfirmsThenMovesToRecoveryAndUpdatesTheReview()
    {
        RunWithWorkspace(async (main, root) =>
        {
            await main.Scan.ScanAsync();
            Assert.AreEqual(WorkspacePage.Review, main.Page);
            var move = main.Review.RemoveSelectedAsync();
            await WaitUntil(() => main.Dialog is not null, "The confirmation dialog did not open.");
            var confirmation = main.Dialog!;
            Assert.AreEqual("Move 1 file to Recovery?", confirmation.Title);
            Assert.AreEqual("Cancel", confirmation.CloseText, "Cancel must be the default button.");
            Assert.IsTrue(confirmation.PrimaryIsDestructive);
            Assert.AreEqual(Path.Combine(root, "2026-01-01 10-00-01 second.mp4"), confirmation.ListItems.Single().Full);
            confirmation.CloseCommand.Execute(null);
            await move;
            Assert.IsTrue(File.Exists(Path.Combine(root, "2026-01-01 10-00-01 second.mp4")), "Cancel must not remove anything.");

            move = main.Review.RemoveSelectedAsync();
            await WaitUntil(() => main.Dialog is not null, "The confirmation dialog did not reopen.");
            main.Dialog!.PrimaryCommand.Execute(null);
            await move;
            Assert.IsFalse(File.Exists(Path.Combine(root, "2026-01-01 10-00-01 second.mp4")));
            Assert.IsTrue(File.Exists(Path.Combine(root, "2026-01-01 10-00-00 first.mp4")));
            Assert.IsFalse(main.Workspace.HasResults);
            Assert.AreEqual(WorkspacePage.Scan, main.Page, "With no groups left, the app returns to Scan.");
            await WaitUntil(() => main.Dialog?.Title == "Removal complete", "The result was not reported.");
            Assert.AreEqual("Removal complete", main.ActivityPage.Entries[0].Title);
        });
    }

    [TestMethod]
    public void ConfirmationWarnsWhenEveryCopyIsSelectedAndListsEveryPath()
    {
        RunWithWorkspace(async (main, root) =>
        {
            for (var index = 2; index < 20; index++) File.WriteAllText(Path.Combine(root, $"2026-01-01 10-00-{index:00} more.mp4"), $"{index}");
            await main.Scan.ScanAsync();
            main.Review.SelectAllCommand.Execute(null);
            var selected = main.Workspace.SelectedFiles();
            Assert.AreEqual(20, selected.Count);
            main.Review.PermanentDeletion = true;
            var dialog = main.Review.CreateRemovalConfirmation(selected);
            Assert.AreEqual("Permanently delete 20 files?", dialog.Title);
            StringAssert.Contains(dialog.Message, "cannot be undone");
            Assert.AreEqual(20, dialog.ListItems.Count);
            Assert.AreEqual(selected[^1].FullPath, dialog.ListItems[^1].Full);
            StringAssert.Contains(dialog.Warning!, "No file will remain");
            Assert.AreEqual("Delete selected files permanently", main.Review.MoveAutomationName);
            main.Review.PermanentDeletion = false;
        });
    }

    [TestMethod]
    public void CancelStopsMetadataAndDiscardsPartialReview()
    {
        RunWithWorkspace(async (main, root) =>
        {
            var wait = Path.Combine(root, "wait-2026-01-01 10-00-02 pending.mp4");
            File.WriteAllText(wait, "wait fixture");
            var scan = main.Scan.ScanAsync();
            await WaitForProbe(wait);
            Assert.IsTrue(main.Scan.IsScanning);
            Assert.IsTrue(main.Scan.CancelCommand.CanExecute(null));
            main.Scan.CancelCommand.Execute(null);
            await scan.WaitAsync(TimeSpan.FromSeconds(10));
            Assert.IsTrue(File.Exists(wait));
            Assert.IsFalse(main.Scan.IsScanning);
            Assert.ThrowsException<InvalidOperationException>(() => main.Workspace.CreateDryRun());
            Assert.AreEqual("Scan cancelled. No video files were changed.", main.Scan.Status);
            AssertProbeExited(wait);
        }, useProbeFixture: true);
    }

    [TestMethod]
    public void ClosingDuringMetadataCancelsBeforeTheWindowCloses()
    {
        RunWithWorkspace(async (main, root) =>
        {
            var wait = Path.Combine(root, "wait-2026-01-01 10-00-02 pending.mp4");
            File.WriteAllText(wait, "wait fixture");
            var closed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            main.CloseRequested += () => closed.TrySetResult();
            var scan = main.Scan.ScanAsync();
            await WaitForProbe(wait);
            Assert.IsFalse(await main.RequestCloseAsync(), "The window must not close while a scan runs.");
            await scan.WaitAsync(TimeSpan.FromSeconds(10));
            await closed.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.IsTrue(File.Exists(wait));
            AssertProbeExited(wait);
        }, useProbeFixture: true);
    }

    [TestMethod]
    public void DiagnosticWriterRunsOffUiThreadAndCancelRemainsResponsive()
    {
        RunWithWorkspace(async (main, root) =>
        {
            using var entered = new ManualResetEventSlim();
            var uiThread = Environment.CurrentManagedThreadId;
            var writerThread = uiThread;
            var scan = main.Scan.ScanAsync((path, messages, token) =>
            {
                writerThread = Environment.CurrentManagedThreadId;
                entered.Set();
                if (!token.WaitHandle.WaitOne(TimeSpan.FromSeconds(10))) throw new TimeoutException("Scan cancel did not reach diagnostics writer.");
                token.ThrowIfCancellationRequested();
                return null;
            });
            await WaitUntil(() => entered.IsSet, "The diagnostics writer did not start.", 5);
            Assert.AreNotEqual(uiThread, writerThread);
            Assert.IsFalse(scan.IsCompleted);
            main.Scan.CancelCommand.Execute(null);
            await scan.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.ThrowsException<InvalidOperationException>(() => main.Workspace.CreateDryRun());
            Assert.AreEqual(2, Directory.GetFiles(root, "*.mp4").Length);
        });
    }

    [TestMethod]
    public void EmptyAndMissingFoldersStayOnScan()
    {
        RunWithWorkspace(async (main, root) =>
        {
            var empty = Directory.CreateDirectory(Path.Combine(root, "empty")).FullName;
            main.Scan.Folder = empty;
            await main.Scan.ScanAsync();
            Assert.AreEqual("No potential duplicate groups found within 60 seconds.", main.Scan.Status);
            Assert.AreEqual(WorkspacePage.Scan, main.Page);
            Assert.IsFalse(main.NavigateCommand.CanExecute("Review"), "Review needs results.");
            main.Scan.Folder = Path.Combine(root, "missing");
            await main.Scan.ScanAsync();
            Assert.AreEqual("Select an existing folder before scanning.", main.Scan.Status);
            Assert.AreEqual("Scan failed", main.ActivityPage.Entries[0].Title);
        });
    }

    [TestMethod]
    public void ExportWorkerKeepsUiResponsiveAndCancelRestoresReview() => ExerciseExportCancellation(closeWindow: false);

    [TestMethod]
    public void ClosingDuringExportCancelsWorkerAndDefersClosing() => ExerciseExportCancellation(closeWindow: true);

    private static void ExerciseExportCancellation(bool closeWindow)
    {
        RunWithWorkspace(async (main, root) =>
        {
            await main.Scan.ScanAsync();
            using var entered = new ManualResetEventSlim();
            using var release = new ManualResetEventSlim();
            var workerThread = 0;
            var uiThread = Environment.CurrentManagedThreadId;
            var closed = false;
            main.CloseRequested += () => closed = true;
            var exportTask = main.Review.ExportToAsync(root, (destination, context, token) =>
            {
                workerThread = Environment.CurrentManagedThreadId;
                entered.Set();
                if (!token.WaitHandle.WaitOne(TimeSpan.FromSeconds(10))) throw new TimeoutException("Export cancellation was not delivered.");
                if (!release.Wait(TimeSpan.FromSeconds(10))) throw new TimeoutException("UI did not release cancelled export.");
                return DryRunExporter.Export(destination, context, cancellationToken: token);
            });
            await WaitUntil(() => entered.IsSet, "The export worker did not start.", 5);
            Assert.AreNotEqual(uiThread, workerThread, "Export writer ran on the UI thread.");
            Assert.IsFalse(exportTask.IsCompleted, "Blocked writer did not keep export active.");
            Assert.IsTrue(main.Review.IsExporting);
            Assert.IsTrue(main.Review.CancelExportCommand.CanExecute(null));
            if (closeWindow)
            {
                Assert.IsFalse(await main.RequestCloseAsync(), "The window closed while its export worker was active.");
                Assert.IsFalse(closed);
            }
            else main.Review.CancelExportCommand.Execute(null);
            release.Set();
            await exportTask.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.AreEqual(0, Directory.GetFiles(root, "DuplicateVideoDryRun-*").Length, "Cancelled export left a partial report or temporary file.");
            if (closeWindow) Assert.IsTrue(closed, "The window did not close after the export stopped.");
            else
            {
                Assert.IsFalse(main.Review.IsExporting);
                Assert.IsTrue(main.Review.ExportCommand.CanExecute(null));
                StringAssert.StartsWith(main.Review.Status, "Export cancelled.");
            }
        });
    }

    private static async Task WaitForProbe(string path)
    {
        var timer = System.Diagnostics.Stopwatch.StartNew();
        while (MetadataProbe.GetActiveProcessId(path) is null && timer.Elapsed < TimeSpan.FromSeconds(10)) await Task.Delay(20);
        var pid = MetadataProbe.GetActiveProcessId(path);
        Assert.IsNotNull(pid, "Isolated metadata process did not start.");
        ProbeProcesses[path] = pid.Value;
    }

    private static void AssertProbeExited(string path)
    {
        Assert.IsTrue(ProbeProcesses.Remove(path, out var pid), "Isolated metadata process was not observed.");
        try
        {
            using var process = System.Diagnostics.Process.GetProcessById(pid);
            Assert.IsTrue(process.HasExited, "Cancellation left a metadata process running.");
        }
        catch (ArgumentException) { }
    }
}
