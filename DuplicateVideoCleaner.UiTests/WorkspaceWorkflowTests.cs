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

// End-to-end tests that drive the real app through UI Automation, as a screen reader or a user would.
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
        _window = _application.GetMainWindow(_automation, TimeSpan.FromSeconds(15)) ?? throw new AssertFailedException("Main window did not open.");
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
    public void NavigationBulkSelectionAndPagesPreserveState()
    {
        Assert.IsNotNull(Find("Scan workspace"));
        Assert.IsNotNull(Find("Scan workspace, Control 1"));
        Assert.IsNotNull(Find("Activity workspace, Control 3"));
        Assert.IsFalse(Find("Review workspace, Control 2").IsEnabled, "Review needs a completed scan.");
        CaptureEvidence("scan");
        EnterFolder(_root);
        Invoke("Scan videos");
        WaitFor("Review workspace");
        Assert.IsNotNull(WaitForContaining("Duplicate group 1; 2 files; 1 selected"));
        Assert.IsNotNull(WaitFor("File: 2026-08-02 10-00-30 second.mkv"));
        Assert.IsNotNull(FindContaining("Duration Unavailable · Resolution Unavailable"));
        Assert.IsNotNull(Find("Group 1, select 2026-08-02 10-00-30 second.mkv for removal"));
        Assert.IsNotNull(Find("1 files selected, 2 B"));
        CaptureEvidence("review");

        Invoke("Clear all selections");
        WaitFor("Nothing selected");
        Invoke("Select all files");
        WaitFor("2 files selected, 3 B");
        Invoke("Select all suggested removals");
        WaitFor("1 files selected, 2 B");

        ControlShortcut(VirtualKeyShort.KEY_3);
        WaitFor("Activity workspace");
        Assert.IsNotNull(WaitForContaining(": Scan complete."));
        CaptureEvidence("activity");
        ControlShortcut(VirtualKeyShort.KEY_2);
        WaitFor("Review workspace");
        Assert.IsNotNull(WaitFor("1 files selected, 2 B"), "Changing pages lost the selection.");

        Invoke("Rescan videos");
        WaitFor("Review workspace");
        ChooseFromMenu("More review actions", "Clear results");
        WaitFor("Scan workspace");
        WaitFor("Scan status: Results cleared. Choose a folder or adjust the rules for another scan.");
    }

    [TestMethod]
    public void LongScanContextKeepsReviewActionsInsideTheWindow()
    {
        var source = Directory.CreateDirectory(Path.Combine(_root,
            "a-long-video-source-folder-to-exercise-the-review-context-layout-and-make-it-wider-than-the-header")).FullName;
        foreach (var file in Directory.EnumerateFiles(_root, "*.mp4").Concat(Directory.EnumerateFiles(_root, "*.mkv")))
            File.Move(file, Path.Combine(source, Path.GetFileName(file)));
        EnterFolder(source);
        Invoke("Scan videos");
        WaitFor("Review workspace");
        WaitFor("Select suggested removals in group 1");
        var window = _window.BoundingRectangle;
        foreach (var name in new[] { "Rescan videos", "More review actions", "Select all files", "Select suggested removals in group 1",
            "Clear selections in group 1", "Move selected files to Recovery", "Export dry run", "Deletion method" })
        {
            var control = Find(name).BoundingRectangle;
            Assert.IsTrue(control.Width > 0 && control.Left >= window.Left && control.Right <= window.Right &&
                control.Top >= window.Top && control.Bottom <= window.Bottom,
                $"Review action '{name}' is clipped outside the window: {control}; window {window}.");
        }
        var details = Find("File: 2026-08-02 10-00-30 second.mkv").BoundingRectangle;
        Assert.IsTrue(details.Right <= window.Right, "File details are clipped outside the window.");
        CaptureEvidence("review-long-context");
    }

    [TestMethod]
    public void MultipleGroupsListEveryGroupAndShowTheSelectedOne()
    {
        for (var day = 3; day <= 7; day++)
        {
            File.WriteAllBytes(Path.Combine(_root, $"2026-08-{day:00} 10-00-00 replay.mp4"), [1]);
            File.WriteAllBytes(Path.Combine(_root, $"2026-08-{day:00} 10-00-30 replay.mkv"), [1, 2]);
        }
        EnterFolder(_root);
        Invoke("Scan videos");
        WaitFor("Review workspace");
        var list = WaitFor("Duplicate groups").BoundingRectangle;
        for (var number = 1; number <= 6; number++)
        {
            var group = WaitForContaining($"Duplicate group {number}; 2 files;").BoundingRectangle;
            Assert.IsTrue(group.Height is > 0 and <= 80, $"Group {number} row is not compact: {group.Height}px.");
            Assert.IsTrue(group.Left >= list.Left && group.Right <= list.Right, $"Group {number} overflows the list.");
        }
        FindContaining("Duplicate group 6; 2 files;").Patterns.SelectionItem.Pattern.Select();
        WaitFor("Duplicate group 6");
        WaitFor("File: 2026-08-07 10-00-30 replay.mkv");
        CaptureEvidence("review-six-groups");
    }

    [TestMethod]
    public void ConfirmationDefaultsToCancelAndExplainsBothModes()
    {
        EnterFolder(_root);
        Invoke("Scan videos");
        WaitFor("Review workspace");
        Find("Move selected files to Recovery").AsButton().Invoke();
        WaitFor("Move 1 file to Recovery?");
        Assert.IsNotNull(FindContaining("They'll be moved, not deleted."));
        Assert.IsTrue(Retry.WhileFalse(() => _automation.FocusedElement()?.Name == "Cancel", TimeSpan.FromSeconds(5)).Result,
            "Cancel must have keyboard focus when the confirmation opens.");
        Assert.IsFalse(Find("Move selected files to Recovery").IsEnabled, "The window behind the dialog must be inactive.");
        CaptureEvidence("confirmation");
        Keyboard.Type(VirtualKeyShort.ESCAPE);
        WaitUntilGone("Move 1 file to Recovery?");

        var method = FindControl("Deletion method", ControlType.ComboBox).AsComboBox();
        method.Expand();
        Retry.WhileNull(() => method.Items.FirstOrDefault(item => item.Name == "Permanent"), TimeSpan.FromSeconds(5)).Result!.Select();
        method.Collapse();
        WaitFor("Delete selected files permanently").AsButton().Invoke();
        WaitFor("Permanently delete 1 file?");
        Assert.IsNotNull(Find("This cannot be undone. The files will not go to Recovery."));
        // Even with focus moved to the destructive button, Enter goes to the default (Cancel) button.
        Find("Delete permanently").Focus();
        Assert.IsTrue(Retry.WhileFalse(() => _automation.FocusedElement()?.Name == "Delete permanently", TimeSpan.FromSeconds(5)).Result);
        Keyboard.Type(VirtualKeyShort.ENTER);
        WaitUntilGone("Permanently delete 1 file?");
        Assert.IsTrue(File.Exists(Path.Combine(_root, "2026-08-02 10-00-30 second.mkv")), "Enter must cancel, never confirm.");
    }

    [TestMethod]
    public void CompletedScanDescriptionSurvivesEditingSource()
    {
        EnterFolder(_root);
        Invoke("Scan videos");
        WaitFor("Review workspace");
        var description = $"in {_root} · 60 s window · keep oldest · this folder only";
        Assert.IsNotNull(WaitForContaining(description));
        ControlShortcut(VirtualKeyShort.KEY_1);
        WaitFor("Scan workspace");
        EnterFolder(Path.Combine(_root, "different-source"));
        ControlShortcut(VirtualKeyShort.KEY_2);
        WaitFor("Review workspace");
        Assert.IsNotNull(WaitForContaining(description));
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
        Assert.IsNotNull(WaitForContaining(": Scan failed."));
    }

    [TestMethod]
    public void ThemeCanBeSwitchedAndIsRemembered()
    {
        WaitFor("Settings and about").Patterns.Toggle.Pattern.Toggle();
        var light = Retry.WhileNull(() => _automation.GetDesktop().FindFirstDescendant(cf => cf.ByName("Light theme")), TimeSpan.FromSeconds(5)).Result
            ?? throw new AssertFailedException("The theme setting did not appear.");
        Assert.IsTrue(_automation.GetDesktop().FindFirstDescendant(cf => cf.ByName("Dark theme"))!.Patterns.SelectionItem.Pattern.IsSelected.Value,
            "Dark must be the default theme.");
        light.Patterns.SelectionItem.Pattern.Select();
        Keyboard.Type(VirtualKeyShort.ESCAPE);
        CaptureEvidence("light-theme");
        Assert.IsTrue(Retry.WhileFalse(() => File.Exists(Path.Combine(_root, "settings.json")) &&
            File.ReadAllText(Path.Combine(_root, "settings.json")).Contains("\"Theme\": 1"), TimeSpan.FromSeconds(5)).Result,
            "The theme choice was not saved.");
    }

    private AutomationElement Find(string name) => _window.FindFirstDescendant(cf => cf.ByName(name)) ?? throw new AssertFailedException($"Element '{name}' was not found. {VisibleNames()}");
    private string VisibleNames() => "Visible names: " + string.Join(" | ", _window.FindAllDescendants().Select(element => element.Name).Where(value => !string.IsNullOrWhiteSpace(value)).Distinct());
    private void CaptureEvidence(string name)
    {
        var directory = Environment.GetEnvironmentVariable("DVC_UI_EVIDENCE_DIR");
        if (string.IsNullOrWhiteSpace(directory)) return;
        Directory.CreateDirectory(directory);
        _window.CaptureToFile(Path.Combine(directory, name + ".png"));
    }
    private AutomationElement FindContaining(string text) => _window.FindAllDescendants().FirstOrDefault(element => element.Name?.Contains(text, StringComparison.Ordinal) == true)
        ?? throw new AssertFailedException($"No element contained '{text}'. {VisibleNames()}");
    private AutomationElement WaitForContaining(string text) => Retry.WhileNull(
        () => _window.FindAllDescendants().FirstOrDefault(element => element.Name?.Contains(text, StringComparison.Ordinal) == true), TimeSpan.FromSeconds(10)).Result
        ?? throw new AssertFailedException($"No element contained '{text}'. {VisibleNames()}");
    private void EnterFolder(string path)
    {
        var field = FindControl("Video folder", ControlType.ComboBox);
        field.Focus();
        field.Patterns.Value.Pattern.SetValue(path);
        Assert.AreEqual(path, field.Patterns.Value.Pattern.Value.Value, "Folder entry did not update.");
    }
    private AutomationElement FindControl(string name, ControlType type) => _window.FindFirstDescendant(cf => cf.ByName(name).And(cf.ByControlType(type)))
        ?? throw new AssertFailedException($"{type} '{name}' was not found.");
    private void Invoke(string name) => WaitFor(name).AsButton().Invoke();
    private void ChooseFromMenu(string button, string item)
    {
        Invoke(button);
        var menuItem = Retry.WhileNull(() => _automation.GetDesktop().FindFirstDescendant(cf => cf.ByName(item).And(cf.ByControlType(ControlType.MenuItem))),
            TimeSpan.FromSeconds(5)).Result ?? throw new AssertFailedException($"Menu item '{item}' did not appear.");
        menuItem.AsMenuItem().Invoke();
    }
    private void ControlShortcut(VirtualKeyShort key)
    {
        _window.Focus();
        Keyboard.Press(VirtualKeyShort.CONTROL);
        Keyboard.Type(key);
        Keyboard.Release(VirtualKeyShort.CONTROL);
    }
    private AutomationElement WaitFor(string name) => Retry.WhileNull(() => _window.FindFirstDescendant(cf => cf.ByName(name)), TimeSpan.FromSeconds(10)).Result
        ?? throw new AssertFailedException($"Element '{name}' did not appear. {VisibleNames()}");
    private void WaitUntilGone(string name)
    {
        if (!Retry.WhileTrue(() => _window.FindFirstDescendant(cf => cf.ByName(name)) is not null, TimeSpan.FromSeconds(10)).Success)
            throw new AssertFailedException($"'{name}' is still shown.");
    }
}
