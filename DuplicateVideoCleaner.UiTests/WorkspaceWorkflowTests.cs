using FlaUI.Core;
using FlaUI.Core.AutomationElements;
using FlaUI.Core.Definitions;
using FlaUI.Core.Input;
using FlaUI.Core.Tools;
using FlaUI.Core.WindowsAPI;
using FlaUI.UIA3;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Application = FlaUI.Core.Application;

namespace DuplicateVideoCleaner.UiTests;

[TestClass]
[DoNotParallelize]
public sealed class WorkspaceWorkflowTests
{
    private string _root = null!;
    private Application _application = null!;
    private UIA3Automation _automation = null!;
    private Window _window = null!;

    [TestInitialize]
    public void Initialize()
    {
        _root = Path.Combine(Path.GetTempPath(), "DuplicateVideoCleanerUiTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);
        File.WriteAllBytes(Path.Combine(_root, "2026-08-02 10-00-00 first.mp4"), [1]);
        File.WriteAllBytes(Path.Combine(_root, "2026-08-02 10-00-30 second.mkv"), [1, 2]);
        Environment.SetEnvironmentVariable("DVC_SETTINGS_PATH", Path.Combine(_root, "settings.json"));
        Environment.SetEnvironmentVariable("FFPROBE_PATH", Path.Combine(_root, "missing-ffprobe.exe"));
        var executable = Environment.GetEnvironmentVariable("DVC_TEST_APP_PATH") ??
            Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "DuplicateVideoCleaner", "bin", "Release", "net10.0-windows", "DuplicateVideoCleaner.exe"));
        _application = Application.Launch(executable);
        _automation = new UIA3Automation();
        _window = _application.GetMainWindow(_automation, TimeSpan.FromSeconds(10)) ?? throw new AssertFailedException("Main window did not open.");
    }

    [TestCleanup]
    public void Cleanup()
    {
        try { _application.Close(); } catch { try { _application.Kill(); } catch { } }
        _automation.Dispose();
        Environment.SetEnvironmentVariable("DVC_SETTINGS_PATH", null);
        Environment.SetEnvironmentVariable("FFPROBE_PATH", null);
        try { Directory.Delete(_root, true); } catch { }
    }

    [TestMethod]
    public void SidebarCardsBulkSelectionAndNavigationPreserveState()
    {
        Assert.IsNotNull(Find("Scan workspace"));
        Assert.IsNotNull(Find("Scan workspace, Control 1"));
        Assert.IsNotNull(Find("Activity workspace, Control 3"));
        CaptureEvidence("scan");
        EnterFolder(_root);
        Invoke("Scan videos");
        WaitFor("Review workspace");
        Assert.IsNotNull(WaitFor("Expand group 1"));
        Assert.IsNotNull(FindContaining("1 duplicate groups"));

        var group = FindContaining("Duplicate group 1; 2 files;");
        var summaryHeight = group.BoundingRectangle.Height;
        Assert.IsTrue(summaryHeight <= 130, $"Collapsed group wastes vertical space: {summaryHeight}px.");
        var selection = Find("Group 1: 1 files selected").BoundingRectangle;
        var details = Find("Expand group 1").BoundingRectangle;
        Assert.IsTrue(selection.Top < group.BoundingRectangle.Top + summaryHeight - 10,
            "Selection count is outside the compact summary.");
        Assert.IsTrue(details.Right <= group.BoundingRectangle.Right, "Group actions are clipped.");
        CaptureEvidence("review");

        Invoke("Expand group 1");
        Assert.IsNotNull(WaitFor("File: 2026-08-02 10-00-30 second.mkv"));
        Assert.IsNotNull(FindContaining("Duration: Unavailable; Resolution: Unavailable"));
        CaptureEvidence("review-details");
        Invoke("Collapse group 1");
        Assert.AreEqual(summaryHeight, group.BoundingRectangle.Height, "Collapsing did not restore the compact row height.");
        Invoke("Expand group 1");

        Invoke("Clear all selections");
        WaitFor("Nothing selected");
        Invoke("Select all files");
        WaitFor("2 files selected, 3 B");
        Invoke("Select all suggested removals");
        WaitFor("1 files selected, 2 B");

        ControlShortcut(VirtualKeyShort.KEY_3);
        WaitFor("Activity workspace");
        Assert.IsNotNull(FindContaining("Scan complete"));
        CaptureEvidence("activity");
        ControlShortcut(VirtualKeyShort.KEY_2);
        WaitFor("Review workspace");
        Assert.IsNotNull(Find("1 files selected, 2 B"));

        Invoke("Rescan videos");
        WaitFor("Review workspace");
        Invoke("Clear results");
        WaitFor("Scan workspace");
        WaitFor("Scan status: Results cleared. Choose a folder or adjust the rules for another scan.");
    }

    [TestMethod]
    public void LongScanContextKeepsReviewActionsInsideTheWindow()
    {
        var source = Directory.CreateDirectory(Path.Combine(_root,
            "a-long-video-source-folder-to-exercise-the-review-context-layout")).FullName;
        foreach (var file in Directory.EnumerateFiles(_root, "*.mp4").Concat(Directory.EnumerateFiles(_root, "*.mkv")))
            File.Move(file, Path.Combine(source, Path.GetFileName(file)));
        EnterFolder(source);
        Invoke("Scan videos");
        WaitFor("Review workspace");
        WaitFor("Expand group 1");
        var window = _window.BoundingRectangle;
        foreach (var name in new[] { "Rescan videos", "Clear results", "Select suggested removals in group 1",
            "Clear selections in group 1", "Expand group 1" })
        {
            var control = Find(name).BoundingRectangle;
            Assert.IsTrue(control.Width > 0 && control.Left >= window.Left && control.Right <= window.Right &&
                control.Top >= window.Top && control.Bottom <= window.Bottom,
                $"Review action '{name}' is clipped outside the window: {control}; window {window}.");
        }
        CaptureEvidence("review-long-context");
        Invoke("Expand group 1");
        var metadata = FindContaining("Effective timestamp:").BoundingRectangle;
        Assert.IsTrue(metadata.Right <= window.Right, "Expanded metadata is clipped outside the window.");
    }

    [TestMethod]
    public void MultipleGroupsKeepReadableSummariesAndAccessibleActions()
    {
        for (var day = 3; day <= 7; day++)
        {
            File.WriteAllBytes(Path.Combine(_root, $"2026-08-{day:00} 10-00-00 replay.mp4"), [1]);
            File.WriteAllBytes(Path.Combine(_root, $"2026-08-{day:00} 10-00-30 replay.mkv"), [1, 2]);
        }
        EnterFolder(_root);
        Invoke("Scan videos");
        WaitFor("Review workspace");
        WaitFor("Expand group 6");
        for (var number = 1; number <= 6; number++)
        {
            var group = FindContaining($"Duplicate group {number}; 2 files;").BoundingRectangle;
            var button = Find($"Expand group {number}").BoundingRectangle;
            Assert.IsTrue(group.Height <= 130, "Group summary is no longer compact.");
            Assert.IsTrue(button.Left >= group.Left && button.Right <= group.Right, "Group actions overflow the summary.");
        }
        CaptureEvidence("review-six-groups");
    }

    [TestMethod]
    public void ConfirmationDefaultsToCancelAndExplainsBothModes()
    {
        EnterFolder(_root);
        Invoke("Scan videos");
        WaitFor("Review workspace");
        Find("Move selected files to Recovery").AsButton().Click();
        var dialog = WaitForWindow("Confirm deletion");
        Assert.IsNotNull(dialog.FindFirstDescendant(cf => cf.ByName("Move selected files to Recovery?")));
        Assert.IsNotNull(dialog.FindFirstDescendant(cf => cf.ByName("Cancel")));
        CaptureEvidence("confirmation", dialog);
        Keyboard.Press(VirtualKeyShort.ESCAPE);
        WaitForWindowClosed("Confirm deletion");

        FindControl("Deletion method", ControlType.ComboBox).Focus();
        Keyboard.Type(VirtualKeyShort.DOWN);
        WaitFor("Delete selected files permanently");
        Find("Delete selected files permanently").AsButton().Click();
        dialog = WaitForWindow("Confirm deletion");
        Assert.IsNotNull(dialog.FindFirstDescendant(cf => cf.ByName("Permanently delete selected files?")));
        Assert.IsNotNull(dialog.FindFirstDescendant(cf => cf.ByName("This action cannot be undone. The files will not go to Recovery.")));
        dialog.FindFirstDescendant(cf => cf.ByName("Cancel"))!.AsButton().Invoke();
        WaitForWindowClosed("Confirm deletion");
        Assert.IsTrue(File.Exists(Path.Combine(_root, "2026-08-02 10-00-30 second.mkv")), "A confirmation-only test removed a fixture.");
    }

    [TestMethod]
    public void CompletedScanDescriptionSurvivesEditingSource()
    {
        EnterFolder(_root);
        Invoke("Scan videos");
        WaitFor("Review workspace");
        Assert.IsNotNull(FindContaining($"{_root} · 60s · Keep Oldest · Current folder only"));
        ControlShortcut(VirtualKeyShort.KEY_1);
        EnterFolder(Path.Combine(_root, "different-source"));
        ControlShortcut(VirtualKeyShort.KEY_2);
        WaitFor("Review workspace");
        Assert.IsNotNull(FindContaining($"{_root} · 60s · Keep Oldest · Current folder only"));
    }

    [TestMethod]
    public void EmptyAndFailedScansRemainInScanWorkspace()
    {
        var empty = Directory.CreateDirectory(Path.Combine(_root, "empty")).FullName;
        EnterFolder(empty);
        Invoke("Scan videos");
        WaitFor("Scan status: No potential duplicate groups found within 60 seconds.");
        Assert.IsNotNull(Find("Scan workspace"));

        EnterFolder(Path.Combine(_root, "missing"));
        Invoke("Scan videos");
        WaitFor("Scan status: Select an existing folder before scanning.");
        ControlShortcut(VirtualKeyShort.KEY_3);
        WaitFor("Activity workspace");
        Assert.IsNotNull(FindContaining("Scan failed"));
    }

    private AutomationElement Find(string name) => _window.FindFirstDescendant(cf => cf.ByName(name)) ?? throw new AssertFailedException($"Element '{name}' was not found.");
    private void CaptureEvidence(string name, Window? window = null)
    {
        var directory = Environment.GetEnvironmentVariable("DVC_UI_EVIDENCE_DIR");
        if (string.IsNullOrWhiteSpace(directory)) return;
        Directory.CreateDirectory(directory);
        (window ?? _window).CaptureToFile(Path.Combine(directory, name + ".png"));
    }
    private AutomationElement FindContaining(string text) => _window.FindAllDescendants().FirstOrDefault(element => element.Name?.Contains(text, StringComparison.Ordinal) == true)
        ?? throw new AssertFailedException($"No element contained '{text}'. Visible names: {string.Join(" | ", _window.FindAllDescendants().Select(element => element.Name).Where(value => !string.IsNullOrWhiteSpace(value)))}");
    private void EnterFolder(string path)
    {
        var field = FindControl("Video folder", ControlType.Edit).AsTextBox();
        field.Focus();
        field.Text = path;
        Assert.AreEqual(path, field.Text, "Folder entry did not update.");
    }
    private AutomationElement FindControl(string name, ControlType type) => _window.FindFirstDescendant(cf => cf.ByName(name).And(cf.ByControlType(type)))
        ?? throw new AssertFailedException($"{type} '{name}' was not found.");
    private void Invoke(string name) => Find(name).AsButton().Invoke();
    private void ControlShortcut(VirtualKeyShort key)
    {
        _window.Focus();
        Keyboard.Press(VirtualKeyShort.CONTROL);
        Keyboard.Type(key);
        Keyboard.Release(VirtualKeyShort.CONTROL);
    }
    private AutomationElement WaitFor(string name) => Retry.WhileNull(() => _window.FindFirstDescendant(cf => cf.ByName(name)), TimeSpan.FromSeconds(10)).Result
        ?? throw new AssertFailedException($"Element '{name}' did not appear. Visible names: {string.Join(" | ", _window.FindAllDescendants().Select(element => element.Name).Where(value => !string.IsNullOrWhiteSpace(value)))}");
    private Window WaitForWindow(string title) => Retry.WhileNull(
        () => _automation.GetDesktop().FindFirstDescendant(cf => cf.ByName(title).And(cf.ByControlType(ControlType.Window)))?.AsWindow(), TimeSpan.FromSeconds(10)).Result
        ?? throw new AssertFailedException($"Window '{title}' did not appear.");
    private void WaitForWindowClosed(string title)
    {
        var result = Retry.WhileTrue(() => _automation.GetDesktop().FindFirstDescendant(cf => cf.ByName(title).And(cf.ByControlType(ControlType.Window))) is not null, TimeSpan.FromSeconds(10));
        if (!result.Success) throw new AssertFailedException($"Window '{title}' did not close.");
    }
}
