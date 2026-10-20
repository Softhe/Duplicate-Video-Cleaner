using DuplicateVideoCleaner;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace DuplicateVideoCleaner.UiTests;

[TestClass]
[DoNotParallelize]
public sealed class ScanContextTests
{
    private static readonly Dictionary<string, int> ProbeProcesses = new(StringComparer.OrdinalIgnoreCase);
    public TestContext TestContext { get; set; } = null!;

    [TestMethod]
    public void MetadataSuccessReachesReviewConfidenceAndExport()
    {
        TestContext.WriteLine(string.IsNullOrEmpty(Environment.GetEnvironmentVariable("DVC_TEST_FFPROBE"))
            ? "Using subprocess metadata fixture." : "Using real ffprobe and video fixture.");
        RunOnUiThread(async (form, root) =>
        {
            Find<TextBox>(form, "Video folder").Text = root;
            await form.ScanAsync();
            var context = form.BuildDryRunContext();
            Assert.AreEqual(2, context.Rows.Count);
            foreach (var row in context.Rows)
            {
                Assert.AreEqual(MetadataProbeStatus.Available, row.Video.Metadata.Status);
                Assert.IsTrue(row.Video.Metadata.Width > 0 && row.Video.Metadata.Height > 0);
                Assert.IsTrue(row.Video.Metadata.Duration > TimeSpan.Zero);
                Assert.AreEqual(DuplicateConfidence.High, row.Video.Confidence);
            }
            var result = DryRunExporter.Export(root, context);
            Assert.AreEqual(0, result.Errors.Count);
            StringAssert.Contains(File.ReadAllText(result.CsvPath!), ",High,");
        }, useProbeFixture: true, useRealMediaIfProvided: true);
    }

    [TestMethod]
    public void ExportAndLogUseCompletedScanAfterAllInputsChange()
    {
        RunOnUiThread(async (form, root) =>
        {
            Find<TextBox>(form, "Video folder").Text = root;
            await form.ScanAsync();
            Find<TextBox>(form, "Video folder").Text = Path.Combine(root, "other-source");
            Find<NumericUpDown>(form, "Duplicate threshold in seconds").Value = 3600;
            Find<ComboBox>(form, "File to keep").SelectedIndex = 1;
            Find<CheckBox>(form, "Include subfolders").Checked = true;
            var context = form.BuildDryRunContext();
            Assert.AreEqual(root, context.Root);
            Assert.AreEqual(60, context.ThresholdSeconds);
            Assert.AreEqual(KeepMode.Oldest, context.KeepMode);
            Assert.IsFalse(context.IncludeSubfolders);
            Assert.AreEqual(2, context.Rows.Count);
            Assert.AreEqual(Path.Combine(root, "DuplicateCleanupLog.txt"), form.GetLogPath());
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
        RunOnUiThread(async (form, root) =>
        {
            var newest = Path.Combine(root, "2026-01-01 10-00-02 newest.mp4");
            File.WriteAllText(newest, "three");
            Find<TextBox>(form, "Video folder").Text = root;
            Find<ComboBox>(form, "File to keep").SelectedIndex = 1;
            await form.ScanAsync();
            form.ReconcileRemoval(new RemovalResult(new HashSet<string>(StringComparer.OrdinalIgnoreCase) { newest }, []));
            var context = form.BuildDryRunContext();
            Assert.AreEqual(2, context.Rows.Count);
            var kept = context.Rows.Single(row => row.Video.SuggestedKeep);
            StringAssert.EndsWith(kept.Video.Candidate.FullPath, "second.mp4");
            Assert.IsFalse(context.Rows.Any(row => row.Video.Candidate.FullPath == newest));
        });
    }

    [TestMethod]
    public void CancelButtonStopsMetadataAndDiscardsPartialReview()
    {
        RunOnUiThread(async (form, root) =>
        {
            var wait = Path.Combine(root, "wait-2026-01-01 10-00-02 pending.mp4");
            File.WriteAllText(wait, "fixture");
            Find<TextBox>(form, "Video folder").Text = root;
            var scan = form.ScanAsync();
            await WaitForProbe(wait);
            var cancel = Find<Button>(form, "Cancel scan");
            Assert.IsTrue(cancel.Visible && cancel.Enabled);
            cancel.PerformClick();
            await scan.WaitAsync(TimeSpan.FromSeconds(10));
            Assert.IsTrue(File.Exists(wait));
            Assert.IsFalse(cancel.Visible);
            Assert.ThrowsException<InvalidOperationException>(() => form.BuildDryRunContext());
            Assert.IsNotNull(Find<Label>(form, "Scan status: Scan cancelled. No video files were changed."));
            AssertProbeExited(wait);
        }, useProbeFixture: true);
    }

    [TestMethod]
    public void ClosingDuringMetadataCancelsBeforeTheWindowCloses()
    {
        RunOnUiThread(async (form, root) =>
        {
            var wait = Path.Combine(root, "wait-2026-01-01 10-00-02 pending.mp4");
            File.WriteAllText(wait, "fixture");
            Find<TextBox>(form, "Video folder").Text = root;
            var scan = form.ScanAsync();
            await WaitForProbe(wait);
            form.Close();
            await scan.WaitAsync(TimeSpan.FromSeconds(10));
            Assert.IsTrue(File.Exists(wait));
            AssertProbeExited(wait);
        }, useProbeFixture: true);
    }

    [TestMethod]
    public void DiagnosticWriterRunsOffUiThreadAndCancelRemainsResponsive()
    {
        RunOnUiThread(async (form, root) =>
        {
            Find<TextBox>(form, "Video folder").Text = root;
            using var entered = new ManualResetEventSlim();
            var uiThread = Environment.CurrentManagedThreadId;
            var writerThread = uiThread;
            var scan = form.ScanAsync((path, messages, token) =>
            {
                writerThread = Environment.CurrentManagedThreadId;
                entered.Set();
                if (!token.WaitHandle.WaitOne(TimeSpan.FromSeconds(10))) throw new TimeoutException("Scan cancel did not reach diagnostics writer.");
                token.ThrowIfCancellationRequested();
                return null;
            });
            var heartbeat = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            using var timer = new System.Windows.Forms.Timer { Interval = 20 };
            timer.Tick += (_, _) => { if (entered.IsSet) heartbeat.TrySetResult(); };
            timer.Start();
            await heartbeat.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.AreNotEqual(uiThread, writerThread);
            Assert.IsFalse(scan.IsCompleted);
            Find<Button>(form, "Cancel scan").PerformClick();
            await scan.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.ThrowsException<InvalidOperationException>(() => form.BuildDryRunContext());
            Assert.AreEqual(2, Directory.GetFiles(root, "*.mp4").Length);
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

    private static T Find<T>(Control parent, string name) where T : Control
    {
        foreach (Control child in parent.Controls)
        {
            if (child is T match && child.AccessibleName == name) return match;
            try { return Find<T>(child, name); } catch (KeyNotFoundException) { }
        }
        throw new KeyNotFoundException(name);
    }

    private static void RunOnUiThread(Func<MainForm, string, Task> test, bool useProbeFixture = false, bool useRealMediaIfProvided = false)
    {
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            var root = Path.Combine(Path.GetTempPath(), "dvc-ui-context-" + Guid.NewGuid().ToString("N"));
            var settings = Environment.GetEnvironmentVariable("DVC_SETTINGS_PATH");
            var probe = Environment.GetEnvironmentVariable("FFPROBE_PATH");
            Exception? failure = null;
            try
            {
                Directory.CreateDirectory(root);
                File.WriteAllText(Path.Combine(root, "2026-01-01 10-00-00 first.mp4"), "one");
                File.WriteAllText(Path.Combine(root, "2026-01-01 10-00-01 second.mp4"), "two");
                Environment.SetEnvironmentVariable("DVC_SETTINGS_PATH", Path.Combine(root, "settings.json"));
                var fixture = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "DuplicateVideoCleaner.Tests", "bin", "Release", "net10.0-windows", "DuplicateVideoCleaner.Tests.exe"));
                if (useRealMediaIfProvided && Environment.GetEnvironmentVariable("DVC_TEST_FFPROBE") is { Length: > 0 } realProbe)
                {
                    var media = Environment.GetEnvironmentVariable("DVC_TEST_MEDIA") ?? throw new InvalidOperationException("DVC_TEST_MEDIA is required with DVC_TEST_FFPROBE.");
                    fixture = realProbe;
                    foreach (var path in Directory.EnumerateFiles(root, "*.mp4")) File.Copy(media, path, true);
                }
                Environment.SetEnvironmentVariable("FFPROBE_PATH", useProbeFixture ? fixture : Path.Combine(root, "missing.exe"));
                using var form = new MainForm();
                form.Shown += async (_, _) =>
                {
                    try { await test(form, root); }
                    catch (Exception ex) { failure = ex; }
                    finally { form.Close(); }
                };
                Application.Run(form);
            }
            catch (Exception ex) { failure = ex; }
            finally
            {
                Environment.SetEnvironmentVariable("DVC_SETTINGS_PATH", settings);
                Environment.SetEnvironmentVariable("FFPROBE_PATH", probe);
                try { Directory.Delete(root, true); } catch { }
            }
            if (failure is null) completion.SetResult(); else completion.SetException(failure);
        }) { IsBackground = true };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        completion.Task.WaitAsync(TimeSpan.FromSeconds(30)).GetAwaiter().GetResult();
    }
}
