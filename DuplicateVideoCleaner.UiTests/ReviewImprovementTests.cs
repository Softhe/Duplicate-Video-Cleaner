using DuplicateVideoCleaner;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace DuplicateVideoCleaner.UiTests;

[TestClass]
[DoNotParallelize]
public sealed class ReviewImprovementTests
{
    [TestMethod]
    public void ExportWorkerKeepsUiResponsiveAndCancelRestoresReview()
    {
        ExerciseExportCancellation(closeWindow: false);
    }

    [TestMethod]
    public void ClosingDuringExportCancelsWorkerAndDefersDisposal()
    {
        ExerciseExportCancellation(closeWindow: true);
    }

    private static void ExerciseExportCancellation(bool closeWindow)
    {
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            var root = Path.Combine(Path.GetTempPath(), "DvcExportUiTest", Guid.NewGuid().ToString("N"));
            var priorSettings = Environment.GetEnvironmentVariable("DVC_SETTINGS_PATH");
            var priorProbe = Environment.GetEnvironmentVariable("FFPROBE_PATH");
            Exception? failure = null;
            try
            {
                Directory.CreateDirectory(root);
                File.WriteAllText(Path.Combine(root, "2026-01-01 10-00-00 first.mp4"), "one");
                File.WriteAllText(Path.Combine(root, "2026-01-01 10-00-01 second.mp4"), "two");
                Environment.SetEnvironmentVariable("DVC_SETTINGS_PATH", Path.Combine(root, "settings.json"));
                Environment.SetEnvironmentVariable("FFPROBE_PATH", Path.Combine(root, "missing.exe"));
                using var form = new MainForm();
                async Task ExerciseAsync()
                {
                    try
                    {
                        Descendants(form).OfType<TextBox>().Single(control => control.AccessibleName == "Video folder").Text = root;
                        await form.ScanAsync();
                        using var entered = new ManualResetEventSlim();
                        using var release = new ManualResetEventSlim();
                        var workerThread = 0;
                        var uiThread = Environment.CurrentManagedThreadId;
                        var exportTask = form.ExportDryRunToAsync(root, (destination, context, token) =>
                        {
                            workerThread = Environment.CurrentManagedThreadId;
                            entered.Set();
                            if (!token.WaitHandle.WaitOne(TimeSpan.FromSeconds(10))) throw new TimeoutException("Export cancellation was not delivered.");
                            if (!release.Wait(TimeSpan.FromSeconds(10))) throw new TimeoutException("UI did not release cancelled export.");
                            return DryRunExporter.Export(destination, context, cancellationToken: token);
                        });
                        var heartbeat = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                        using var timer = new System.Windows.Forms.Timer { Interval = 20 };
                        timer.Tick += (_, _) => { if (entered.IsSet) heartbeat.TrySetResult(); };
                        timer.Start();
                        await heartbeat.Task.WaitAsync(TimeSpan.FromSeconds(5));
                        Assert.AreNotEqual(uiThread, workerThread, "Export writer ran on the UI thread.");
                        Assert.IsFalse(exportTask.IsCompleted, "Blocked writer did not keep export active.");
                        var cancel = Descendants(form).OfType<Button>().Single(button => button.AccessibleName == "Cancel export");
                        Assert.IsTrue(cancel.Visible && cancel.Enabled);
                        if (closeWindow)
                        {
                            form.Close();
                            Assert.IsFalse(form.IsDisposed, "Window was disposed while its export worker was active.");
                        }
                        else cancel.PerformClick();
                        release.Set();
                        await exportTask.WaitAsync(TimeSpan.FromSeconds(5));
                        Assert.AreEqual(0, Directory.GetFiles(root, "DuplicateVideoDryRun-*").Length, "Cancelled export left a partial report or temporary file.");
                        if (!closeWindow)
                        {
                            Assert.IsFalse(cancel.Visible);
                            Assert.IsTrue(Descendants(form).OfType<Button>().Single(button => button.AccessibleName == "Export dry run").Enabled);
                            Assert.IsTrue(Descendants(form).OfType<Label>().Any(label => label.Text.StartsWith("Export cancelled.")));
                        }
                    }
                    catch (Exception ex) { failure = ex; }
                    finally { if (!form.IsDisposed) form.Close(); }
                }
                form.Shown += async (_, _) => await ExerciseAsync();
                Application.Run(form);
            }
            catch (Exception ex) { failure = ex; }
            finally
            {
                Environment.SetEnvironmentVariable("DVC_SETTINGS_PATH", priorSettings);
                Environment.SetEnvironmentVariable("FFPROBE_PATH", priorProbe);
                try { Directory.Delete(root, true); } catch (IOException) { }
            }
            if (failure is null) completion.SetResult(); else completion.SetException(failure);
        }) { IsBackground = true };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        completion.Task.WaitAsync(TimeSpan.FromSeconds(30)).GetAwaiter().GetResult();
    }

    [TestMethod]
    public void WindowPlacementAcceptsNegativeMonitorsAndRequiresReachableTitleBar()
    {
        Rectangle[] screens = [new(-1920, 0, 1920, 1080), new(0, -1080, 1920, 1080)];
        Assert.IsTrue(MainForm.HasReachableTitleBar(new(-1800, 50, 1200, 790), screens));
        Assert.IsTrue(MainForm.HasReachableTitleBar(new(100, -1000, 1200, 790), screens));
        Assert.IsFalse(MainForm.HasReachableTitleBar(new(-4000, 50, 1200, 790), screens));
        Assert.IsFalse(MainForm.HasReachableTitleBar(new(100, -1095, 1200, 790), screens));
    }

    [TestMethod]
    public void BulkSelectionRefreshesAccessibleStateAndConfirmationListsEveryPath()
    {
        RunSta(() =>
        {
            var files = Enumerable.Range(0, 20).Select(index => new VideoCandidate(
                Path.Combine(Path.GetTempPath(), $"video-{index}.mp4"), $"video-{index}.mp4", DateTime.Now, TimestampSource.FileName, 1)).ToArray();
            var reviewed = new ReviewedVideo(files[0], new(null, null, null, MetadataProbeStatus.Unavailable), DuplicateConfidence.Low, true);
            using var row = new ReviewedFileRow(1, reviewed, files[0], true, (_, _) => { });
            row.SetSelected(false);
            StringAssert.Contains(row.AccessibleName!, "Not selected");
            row.SetSelected(true);
            StringAssert.Contains(row.AccessibleName!, "Selected for removal");
            using var dialog = new DeletionConfirmationDialog(files, false, "20 B", 1);
            var preview = Descendants(dialog).OfType<ListBox>().Single();
            Assert.AreEqual(files.Length, preview.Items.Count);
            Assert.IsTrue(preview.TabStop);
            Assert.AreEqual(files[^1].FullPath, preview.Items[^1]);
            Assert.IsTrue(Descendants(dialog).OfType<Label>().Any(label => label.Text.Contains("No file will remain")));
        });
    }

    [TestMethod]
    public void DiagnosticLoggingCancelsBetweenEntriesAndReleasesLog()
    {
        var root = Path.Combine(Path.GetTempPath(), "DvcDiagnosticTest", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var log = Path.Combine(root, "log.txt");
        using var cancellation = new CancellationTokenSource();
        IEnumerable<string> Messages()
        {
            yield return "First\nentry";
            cancellation.Cancel();
            yield return "Should not be logged";
        }
        try
        {
            Assert.ThrowsException<OperationCanceledException>(() => MainForm.AppendScanDiagnostics(log, Messages(), cancellation.Token));
            var content = File.ReadAllText(log);
            StringAssert.Contains(content, "First\\nentry");
            Assert.IsFalse(content.Contains("Should not be logged"));
            using var exclusive = new FileStream(log, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
        }
        finally { Directory.Delete(root, true); }
    }

    [TestMethod]
    public void CancelledExportLeavesNoPartialReportOrTemporaryFile()
    {
        var root = Path.Combine(Path.GetTempPath(), "DvcExportTest", Guid.NewGuid().ToString("N"));
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        try
        {
            var result = DryRunExporter.Export(root, new(root, 60, KeepMode.Oldest, false, []), cancellationToken: cancellation.Token);
            Assert.IsNull(result.CsvPath);
            Assert.IsNull(result.TextPath);
            Assert.AreEqual(2, result.Errors.Count);
            Assert.AreEqual(0, Directory.GetFiles(root).Length);
        }
        finally { Directory.Delete(root, true); }
    }

    private static IEnumerable<Control> Descendants(Control parent) => parent.Controls.Cast<Control>().SelectMany(child => Descendants(child).Prepend(child));

    private static void RunSta(Action test)
    {
        Exception? failure = null;
        var thread = new Thread(() => { try { test(); } catch (Exception ex) { failure = ex; } });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        Assert.IsTrue(thread.Join(TimeSpan.FromSeconds(15)));
        if (failure is not null) throw failure;
    }
}
