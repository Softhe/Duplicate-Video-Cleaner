using System.Text.Json;
using FlaUI.Core;
using FlaUI.Core.AutomationElements;
using FlaUI.Core.Definitions;
using FlaUI.Core.Input;
using FlaUI.Core.Tools;
using FlaUI.Core.WindowsAPI;
using FlaUI.UIA3;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using AutomationApplication = FlaUI.Core.Application;

namespace DuplicateVideoCleaner.UiTests;

[TestClass]
[DoNotParallelize]
public sealed class InstalledVideoWorkflowTests
{
    [TestMethod]
    [Timeout(120000)]
    public void BundledVideoMetadataReachesReviewAndExport()
    {
        var executable = Environment.GetEnvironmentVariable("DVC_TEST_APP_PATH");
        if (string.IsNullOrWhiteSpace(executable))
            Assert.Inconclusive("Exact-payload acceptance requires DVC_TEST_APP_PATH. Release qualification must run this test explicitly.");
        var root = Path.Combine(Path.GetTempPath(), "dvc-installed-video-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var originalSettings = Environment.GetEnvironmentVariable("DVC_SETTINGS_PATH");
        var originalProbe = Environment.GetEnvironmentVariable("FFPROBE_PATH");
        AutomationApplication? application = null;
        try
        {
            Assert.IsTrue(File.Exists(Path.Combine(Path.GetDirectoryName(executable)!, "tools", "ffprobe.exe")), "Candidate must include bundled ffprobe.");
            var fixture = Path.Combine(AppContext.BaseDirectory, "Fixtures", "one-second.mp4");
            foreach (var name in new[] { "2026-01-01 10-00-00 first.mp4", "2026-01-01 10-00-01 second.mp4" })
                File.Copy(fixture, Path.Combine(root, name));
            Environment.SetEnvironmentVariable("DVC_SETTINGS_PATH", Path.Combine(root, "settings.json"));
            Environment.SetEnvironmentVariable("FFPROBE_PATH", null);
            application = AutomationApplication.Launch(executable);
            using var automation = new UIA3Automation();
            var window = application.GetMainWindow(automation, TimeSpan.FromSeconds(15)) ?? throw new AssertFailedException("Candidate window did not appear.");
            AutomationElement Find(string name) => window.FindFirstDescendant(cf => cf.ByName(name)) ?? throw new AssertFailedException($"Missing control: {name}");
            Find("Video folder").AsTextBox().Text = root;
            Find("Scan videos").AsButton().Invoke();
            var expand = Retry.WhileNull(() => window.FindFirstDescendant(cf => cf.ByName("Expand group 1")), TimeSpan.FromSeconds(45)).Result;
            Assert.IsNotNull(expand, "Candidate did not produce a review group with native probing enabled.");
            expand.AsButton().Invoke();
            var metadata = window.FindAllDescendants().Where(element => element.Name?.StartsWith("Effective timestamp:", StringComparison.Ordinal) == true).ToArray();
            Assert.AreEqual(2, metadata.Length);
            foreach (var row in metadata)
            {
                StringAssert.Contains(row.Name, "Duration: 00:00:01.000");
                StringAssert.Contains(row.Name, "Resolution: 64×48");
                StringAssert.Contains(row.Name, "Confidence: High");
            }

            // Exercise the actual export action and standard Windows folder picker.
            Find("Export dry run").AsButton().Click();
            var picker = Retry.WhileNull(() => window.ModalWindows.FirstOrDefault(), TimeSpan.FromSeconds(10)).Result;
            Assert.IsNotNull(picker, "Export folder picker did not open.");
            picker.Focus();
            Keyboard.Press(VirtualKeyShort.CONTROL);
            try { Keyboard.Type(VirtualKeyShort.KEY_L); }
            finally { Keyboard.Release(VirtualKeyShort.CONTROL); }
            Keyboard.Type(root);
            Keyboard.Type(VirtualKeyShort.RETURN);
            var select = picker.FindFirstDescendant(cf => cf.ByControlType(ControlType.Button).And(cf.ByAutomationId("1")))
                ?? picker.FindFirstDescendant(cf => cf.ByControlType(ControlType.Button).And(cf.ByName("Select Folder")));
            Assert.IsNotNull(select, "Folder picker confirmation button did not appear.");
            select.AsButton().Invoke();
            var csv = Retry.WhileNull(() => Directory.EnumerateFiles(root, "DuplicateVideoDryRun-*.csv").FirstOrDefault(), TimeSpan.FromSeconds(15)).Result;
            Assert.IsNotNull(csv, "Actual export did not produce CSV.");
            Assert.IsTrue(Retry.WhileTrue(() => !Directory.EnumerateFiles(root, "DuplicateVideoDryRun-*.txt").Any(), TimeSpan.FromSeconds(10)).Success);
            var contents = File.ReadAllText(csv);
            Assert.AreEqual(3, contents.Split('\n', StringSplitOptions.RemoveEmptyEntries).Length);
            StringAssert.Contains(contents, ",High,");
            StringAssert.Contains(contents, "64×48");
            Assert.AreEqual(2, Directory.EnumerateFiles(root, "*.mp4").Count(), "Acceptance must preserve its source videos.");
            var evidence = Environment.GetEnvironmentVariable("DVC_TEST_VIDEO_EVIDENCE");
            if (!string.IsNullOrWhiteSpace(evidence))
                File.WriteAllText(evidence, JsonSerializer.Serialize(new { status = "passed", executable = Path.GetFullPath(executable), metadata = "64x48, 1 second, High", export = "CSV and text", sourceFilesPreserved = true }));
        }
        finally
        {
            if (application is not null)
            {
                try { application.Close(); }
                finally { if (!application.HasExited) application.Kill(); application.Dispose(); }
            }
            Environment.SetEnvironmentVariable("DVC_SETTINGS_PATH", originalSettings);
            Environment.SetEnvironmentVariable("FFPROBE_PATH", originalProbe);
            // The root is generated here and contains only this test's synthetic media.
            try { Directory.Delete(root, true); } catch (IOException) { }
        }
    }
}
