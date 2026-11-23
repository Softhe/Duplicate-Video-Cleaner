using DuplicateVideoCleaner.ViewModels;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using static DuplicateVideoCleaner.UiTests.WpfTestHost;

namespace DuplicateVideoCleaner.UiTests;

// The Activity & Recovery page (formerly the Recovery dialog) and closing behaviour.
[TestClass]
[DoNotParallelize]
public sealed class ActivityRecoveryTests
{
    [TestMethod]
    public void ClosingCancelsDiscoveryAndWaitsForItsCompletion()
    {
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var cancelled = false;
        RunWithWorkspace(async (main, _) =>
        {
            var closed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            main.CloseRequested += () => closed.TrySetResult();
            main.Navigate(WorkspacePage.Activity);
            await started.Task.WaitAsync(TimeSpan.FromSeconds(10));
            Assert.IsTrue(main.ActivityPage.IsLoading);
            Assert.IsFalse(await main.RequestCloseAsync(), "The window must not close during discovery.");
            await closed.Task.WaitAsync(TimeSpan.FromSeconds(10));
            Assert.AreEqual("Recovery discovery cancelled.", main.ActivityPage.RecoveryStatus);
        }, recoveryList: (_, _, token, _) =>
        {
            started.TrySetResult();
            token.WaitHandle.WaitOne(TimeSpan.FromSeconds(10));
            cancelled = token.IsCancellationRequested;
            token.ThrowIfCancellationRequested();
            return [];
        });
        Assert.IsTrue(cancelled, "Closing did not cancel in-progress recovery discovery.");
    }

    [TestMethod]
    public void RecoveryListsAndRestoresTheSelectedFile()
    {
        var listings = 0;
        RunWithWorkspace(async (main, root) =>
        {
            var first = Path.Combine(root, "2026-01-01 10-00-00 first.mp4");
            var second = Path.Combine(root, "2026-01-01 10-00-01 second.mp4");
            File.WriteAllText(second, "recoverable");
            RecoveryStore.MoveToRecovery(DuplicateScanner.Scan(root, 60, false, KeepMode.Oldest).Single().Files.Single(file => file.FullPath == second));
            var page = main.ActivityPage;
            main.Navigate(WorkspacePage.Activity);
            await WaitUntil(() => page.Items.Count == 1 && !main.IsBusy, "Recovery did not list the moved file.");
            Assert.AreEqual("2026-01-01 10-00-01 second.mp4", page.Items[0].Name);
            Assert.AreEqual("Scanned folder", page.Items[0].Folder);
            Assert.IsFalse(File.Exists(second));
            Assert.IsFalse(page.RestoreCommand.CanExecute(null), "Restore needs a selection.");
            page.Items[0].IsSelected = true;
            Assert.IsTrue(page.RestoreCommand.CanExecute(null));
            await page.OperateAsync(permanent: false);
            Assert.IsTrue(File.Exists(second));
            Assert.AreEqual(0, page.Items.Count);
            StringAssert.StartsWith(page.RecoveryStatus, "File restored.");
            StringAssert.Contains(page.RecoveryStatus, "Injected refresh warning.", "The successful restore hid the incomplete-refresh warning.");
            Assert.AreEqual("Restored 1 file", page.Entries[0].Title);
            Assert.AreEqual("Success", page.Entries[0].Tone);
            Assert.IsFalse(page.InspectArchived, "Ordinary discovery should skip archived history.");
            Assert.IsTrue(page.ArchiveHistoryCommand.CanExecute(null), "Explicit archival action is missing.");
            Assert.AreEqual("recoverable", File.ReadAllText(second));
            Assert.AreEqual("one", File.ReadAllText(first));
        }, recoveryList: (path, diagnostic, token, archived) =>
        {
            var entries = RecoveryStore.List(path, diagnostic, token, includeArchivedPayloads: archived);
            if (++listings > 1) diagnostic("Injected refresh warning.");
            return entries;
        });
    }

    [TestMethod]
    public void RecoveryRestoresAllSelectedFiles()
    {
        RunWithWorkspace(async (main, root) =>
        {
            foreach (var path in Directory.EnumerateFiles(root, "*.mp4")) File.Delete(path);
            var paths = Enumerable.Range(0, 4).Select(index => Path.Combine(root, $"2026-01-01 10-00-0{index} clip.mp4")).ToArray();
            foreach (var path in paths) File.WriteAllText(path, Path.GetFileName(path));
            foreach (var file in DuplicateScanner.Scan(root, 60, false, KeepMode.Oldest).Single().Files.Skip(1)) RecoveryStore.MoveToRecovery(file);
            var page = main.ActivityPage;
            main.Navigate(WorkspacePage.Activity);
            await WaitUntil(() => page.Items.Count == 3 && !main.IsBusy, "Recovery did not list every moved file.");
            Assert.AreEqual(false, page.AllSelected);
            page.ToggleAllCommand.Execute(null);
            Assert.AreEqual(3, page.SelectedCount, "Select all did not select every recovery file.");
            Assert.AreEqual(true, page.AllSelected);
            page.Items[0].IsSelected = false;
            Assert.IsNull(page.AllSelected, "A partial selection is indeterminate.");
            page.ToggleAllCommand.Execute(null);
            Assert.AreEqual(3, page.SelectedCount);
            await page.OperateAsync(permanent: false);
            Assert.AreEqual(0, page.Items.Count);
            StringAssert.StartsWith(page.RecoveryStatus, "Restored 3 of 3 files.");
            foreach (var path in paths) Assert.AreEqual(Path.GetFileName(path), File.ReadAllText(path));
        });
    }

    [TestMethod]
    public void PermanentDeletionAsksFirstAndCancelKeepsTheFile()
    {
        RunWithWorkspace(async (main, root) =>
        {
            var second = Path.Combine(root, "2026-01-01 10-00-01 second.mp4");
            RecoveryStore.MoveToRecovery(DuplicateScanner.Scan(root, 60, false, KeepMode.Oldest).Single().Files.Single(file => file.FullPath == second));
            var page = main.ActivityPage;
            main.Navigate(WorkspacePage.Activity);
            await WaitUntil(() => page.Items.Count == 1 && !main.IsBusy, "Recovery did not list the moved file.");
            page.Items[0].IsSelected = true;
            var purge = page.OperateAsync(permanent: true);
            await WaitUntil(() => main.Dialog is not null, "Permanent deletion did not ask for confirmation.");
            Assert.AreEqual("Permanently delete this file?", main.Dialog!.Title);
            Assert.AreEqual("Cancel", main.Dialog.CloseText);
            main.Dialog.CloseCommand.Execute(null);
            await purge;
            Assert.AreEqual(1, page.Items.Count, "Cancel must keep the Recovery file.");

            purge = page.OperateAsync(permanent: true);
            await WaitUntil(() => main.Dialog is not null, "Permanent deletion did not ask again.");
            main.Dialog!.PrimaryCommand.Execute(null);
            await purge;
            Assert.AreEqual(0, page.Items.Count);
            Assert.AreEqual("Caution", page.Entries[0].Tone);
            Assert.IsFalse(File.Exists(second));
        });
    }

    [TestMethod]
    public void ActivityMirrorsTheLogAndKeepsTheNewest200()
    {
        RunWithWorkspace((main, _) =>
        {
            Assert.AreEqual("Ready", main.ActivityPage.Entries.Single().Title);
            for (var index = 0; index < 210; index++) main.Activity.Add($"Entry {index}", "", index % 2 == 0);
            Assert.AreEqual(ActivityLog.Capacity, main.ActivityPage.Entries.Count);
            Assert.AreEqual("Entry 209", main.ActivityPage.Entries[0].Title);
            Assert.AreEqual("Caution", main.ActivityPage.Entries[0].Tone, "Failed entries are flagged.");
            Assert.AreEqual("Neutral", main.ActivityPage.Entries[1].Tone);
            return Task.CompletedTask;
        });
    }

    [TestMethod]
    public void ClosingWaitsForSettingsWithoutBlockingUi()
    {
        using var entered = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        RunWithWorkspace(async (main, _) =>
        {
            var closed = false;
            main.CloseRequested += () => closed = true;
            var close = main.RequestCloseAsync();
            await WaitUntil(() => entered.IsSet, "Settings were not written on close.", 5);
            Assert.IsFalse(closed, "The window closed before settings were written.");
            var heartbeat = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var pending = System.Windows.Threading.Dispatcher.CurrentDispatcher.BeginInvoke(() => heartbeat.SetResult());
            await heartbeat.Task.WaitAsync(TimeSpan.FromSeconds(5));
            release.Set();
            await close.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.IsTrue(closed);
        }, settingsWriter: _ =>
        {
            entered.Set();
            if (!release.Wait(TimeSpan.FromSeconds(10))) throw new TimeoutException("Settings writer was never released.");
        });
    }
}
