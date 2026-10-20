using DuplicateVideoCleaner;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace DuplicateVideoCleaner.UiTests;

[TestClass]
[DoNotParallelize]
public sealed class RecoveryUiTests
{
    [TestMethod]
    public async Task CloseCancelsDiscoveryAndWaitsForItsCompletion()
    {
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var cancelled = false;
        var thread = new Thread(() =>
        {
            Exception? failure = null;
            using var dialog = new RecoveryDialog(Path.GetTempPath(), (_, _, token) =>
            {
                started.TrySetResult();
                token.WaitHandle.WaitOne(TimeSpan.FromSeconds(10));
                cancelled = token.IsCancellationRequested;
                token.ThrowIfCancellationRequested();
                return [];
            });
            dialog.Shown += async (_, _) =>
            {
                try { await started.Task; dialog.Close(); }
                catch (Exception ex) { failure = ex; dialog.Close(); }
            };
            Application.Run(dialog);
            if (failure is not null) completion.TrySetException(failure);
            else completion.TrySetResult();
        }) { IsBackground = true };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        await completion.Task.WaitAsync(TimeSpan.FromSeconds(20));
        Assert.IsTrue(cancelled, "Closing did not cancel in-progress recovery discovery.");
    }

    [TestMethod]
    public async Task RecoveryDialogListsAndRestoresTheSelectedFile()
    {
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            var root = Path.Combine(Path.GetTempPath(), "dvc-recovery-ui-" + Guid.NewGuid().ToString("N"));
            Exception? failure = null;
            Directory.CreateDirectory(root);
            try
            {
                var first = Path.Combine(root, "2026-01-01 10-00-00 first.mp4");
                var second = Path.Combine(root, "2026-01-01 10-00-01 second.mp4");
                File.WriteAllText(first, "first");
                File.WriteAllText(second, "recoverable");
                var candidate = DuplicateScanner.Scan(root, 60, false, KeepMode.Oldest).Single().Files.Single(file => file.FullPath == second);
                RecoveryStore.MoveToRecovery(candidate);
                var listings = 0;
                using var dialog = new RecoveryDialog(root, (path, diagnostic, token) =>
                {
                    var entries = RecoveryStore.List(path, diagnostic, token);
                    if (++listings > 1) diagnostic("Injected refresh warning.");
                    return entries;
                });
                dialog.Shown += async (_, _) =>
                {
                    try
                    {
                        await VerifyRestoreAsync(dialog, first, second);
                    }
                    catch (Exception ex) { failure = ex; }
                    finally { dialog.Close(); }
                };
                Application.Run(dialog);
                if (failure is not null) throw failure;
                completion.SetResult();
            }
            catch (Exception ex) { completion.TrySetException(ex); }
            finally { try { Directory.Delete(root, true); } catch { } }
        }) { IsBackground = true };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        await completion.Task.WaitAsync(TimeSpan.FromSeconds(30));
    }

    private static async Task VerifyRestoreAsync(RecoveryDialog dialog, string first, string second)
    {
        var list = dialog.Controls.OfType<TableLayoutPanel>().Single().Controls.OfType<ListView>().Single();
        await WaitUntil(() => list.Items.Count == 1);
        StringAssert.Contains(list.Items[0].Text, "second.mp4");
        Assert.IsFalse(File.Exists(second));
        list.Items[0].Selected = true;
        var restore = Descendants(dialog).OfType<Button>().Single(button => button.AccessibleName == "Restore recovery file");
        Assert.IsTrue(restore.Enabled);
        restore.PerformClick();
        await WaitUntil(() => File.Exists(second) && list.Items.Count == 0 && Descendants(dialog).OfType<Label>().Any(label => label.Text.StartsWith("File restored.")));
        Assert.IsTrue(Descendants(dialog).OfType<Label>().Any(label => label.Text.Contains("Injected refresh warning.")),
            "The successful restore hid the incomplete-refresh warning.");
        Assert.IsTrue(list.VirtualMode, "Recovery rows should be created on demand.");
        var inspectArchive = Descendants(dialog).OfType<CheckBox>().Single(control => control.AccessibleName == "Inspect archived recovery payloads");
        Assert.IsFalse(inspectArchive.Checked, "Ordinary discovery should skip archived history.");
        Assert.IsTrue(Descendants(dialog).OfType<Button>().Any(button => button.AccessibleName == "Archive completed recovery history"), "Explicit archival action is missing.");
        Assert.AreEqual("recoverable", File.ReadAllText(second));
        Assert.AreEqual("first", File.ReadAllText(first));
    }

    private static async Task WaitUntil(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (!condition())
        {
            if (DateTime.UtcNow >= deadline) Assert.Fail("Recovery UI did not reach the expected state.");
            await Task.Delay(20);
        }
    }

    private static IEnumerable<Control> Descendants(Control control)
    {
        foreach (Control child in control.Controls)
        {
            yield return child;
            foreach (var nested in Descendants(child)) yield return nested;
        }
    }
}
