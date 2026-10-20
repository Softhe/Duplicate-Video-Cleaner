using System.Diagnostics;
using System.Reflection;
using DuplicateVideoCleaner;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace DuplicateVideoCleaner.UiTests;

[TestClass]
[DoNotParallelize]
public sealed class LargeLibraryTests
{
    public TestContext TestContext { get; set; } = null!;

    [TestMethod]
    public void TenThousandResultsKeepControlsBoundedAndSelectionsGlobal()
    {
        RunOnUiThread(form =>
        {
            var groups = CreateGroups(5000, 2);
            Populate(form, groups);
            var timer = Stopwatch.StartNew();
            Invoke(form, "BuildCards");
            Invoke(form, "Navigate", WorkspacePage.Review);
            Application.DoEvents();
            timer.Stop();
            TestContext.WriteLine($"10,000 results initial review: {timer.ElapsedMilliseconds} ms; controls: {Descendants(form).Count()}.");
            Assert.IsTrue(timer.Elapsed < TimeSpan.FromSeconds(5), "Initial review blocked the UI for over five seconds.");
            Assert.AreEqual(25, Descendants(form).OfType<DuplicateGroupCard>().Count());
            var cards = (List<DuplicateGroupCard>)typeof(MainForm).GetField("_cards", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(form)!;
            StringAssert.StartsWith(cards[0].AccessibleName!, "Duplicate group 1;", "Keyboard focus order differs from visible group order.");
            Assert.IsTrue(Descendants(form).Count() < 1000, "Review controls grew with the whole library.");
            Find<Button>(form, "Select all files").PerformClick();
            Assert.AreEqual(10000, form.BuildDryRunContext().Rows.Count(row => row.Selected));
            Find<Button>(form, "Next review page").PerformClick();
            Assert.IsTrue(Descendants(form).OfType<DuplicateGroupCard>().Any(card => card.AccessibleName!.StartsWith("Duplicate group 26;")));
            Find<Button>(form, "Clear selections in group 26").PerformClick();
            Assert.AreEqual(9998, form.BuildDryRunContext().Rows.Count(row => row.Selected));
            Find<Button>(form, "Previous review page").PerformClick();
            Assert.AreEqual(9998, form.BuildDryRunContext().Rows.Count(row => row.Selected), "Changing pages lost selection state.");
            Find<Button>(form, "Select all suggested removals").PerformClick();
            var context = form.BuildDryRunContext();
            Assert.AreEqual(5000, context.Rows.Count(row => row.Selected));
            StringAssert.Contains(DryRunExporter.BuildCsv(context), "group-5000-file-1.mp4");
            form.ReconcileRemoval(new RemovalResult(new HashSet<string>(StringComparer.OrdinalIgnoreCase) { groups[^1].Files[1].FullPath }, []));
            Assert.AreEqual(9998, form.BuildDryRunContext().Rows.Count, "Removal reconciliation lost or retained unrelated groups.");
            var heartbeat = new TaskCompletionSource();
            form.BeginInvoke(new Action(() => heartbeat.SetResult()));
            Application.DoEvents();
            Assert.IsTrue(heartbeat.Task.IsCompleted, "UI message processing stopped after paging and selection.");
        });
    }

    [TestMethod]
    public void LongGroupTimeSpanDoesNotWrapAtOneDay()
    {
        var span = TimeSpan.FromHours(49) + TimeSpan.FromMinutes(2) + TimeSpan.FromSeconds(3);
        var text = typeof(DuplicateGroupCard).GetMethod("FormatSpan", BindingFlags.Static | BindingFlags.NonPublic)!.Invoke(null, [span]);
        Assert.AreEqual("49:02:03", text);
    }

    [TestMethod]
    public void TenThousandFileGroupBuildsOnlyOneDetailPage()
    {
        RunOnUiThread(form =>
        {
            Populate(form, CreateGroups(1, 10000));
            Invoke(form, "BuildCards");
            Invoke(form, "Navigate", WorkspacePage.Review);
            var timer = Stopwatch.StartNew();
            Find<Button>(form, "Expand group 1").PerformClick();
            Application.DoEvents();
            timer.Stop();
            TestContext.WriteLine($"10,000 file group expansion: {timer.ElapsedMilliseconds} ms; detail rows: {Descendants(form).OfType<ReviewedFileRow>().Count()}.");
            Assert.IsTrue(timer.Elapsed < TimeSpan.FromSeconds(5), "Expansion blocked the UI for over five seconds.");
            Assert.AreEqual(25, Descendants(form).OfType<ReviewedFileRow>().Count());
            Find<Button>(form, "Next files in group 1").PerformClick();
            Assert.AreEqual(25, Descendants(form).OfType<ReviewedFileRow>().Count());
            Assert.IsTrue(Descendants(form).Any(control => control.AccessibleName == "File: group-1-file-25.mp4"));
            Find<CheckBox>(form, "Group 1, select group-1-file-25.mp4 for removal").Checked = true;
            Find<Button>(form, "Previous files in group 1").PerformClick();
            Assert.AreEqual(1, form.BuildDryRunContext().Rows.Count(row => row.Selected));
        });
    }

    [TestMethod]
    public void DetailPagingPreservesFocusAndMovesItToAnEnabledButtonAtBoundaries()
    {
        RunOnUiThread(form =>
        {
            Populate(form, CreateGroups(1, 51));
            Invoke(form, "BuildCards");
            Invoke(form, "Navigate", WorkspacePage.Review);
            Find<Button>(form, "Expand group 1").PerformClick();
            Application.DoEvents();
            var next = Find<Button>(form, "Next files in group 1");
            var previous = Find<Button>(form, "Previous files in group 1");
            Assert.IsTrue(next.Focus(), "The next-page button could not receive keyboard focus.");
            Assert.IsTrue(next.Focused);
            next.PerformClick();
            Application.DoEvents();
            Assert.AreSame(next, Find<Button>(form, "Next files in group 1"));
            Assert.IsTrue(next.Focused, "Paging to an intermediate page lost keyboard focus.");
            next.PerformClick();
            Application.DoEvents();
            Assert.IsFalse(next.Enabled);
            Assert.IsTrue(previous.Focused, "The last page must move focus to the enabled previous button.");
            Assert.IsTrue(Descendants(form).Any(control => control is Label && control.Text == "Files 51–51 of 51"));
            previous.PerformClick();
            Application.DoEvents();
            Assert.IsTrue(previous.Focused, "Paging backward to an intermediate page lost keyboard focus.");
            previous.PerformClick();
            Application.DoEvents();
            Assert.IsFalse(previous.Enabled);
            Assert.IsTrue(next.Focused, "The first page must move focus to the enabled next button.");
            Assert.AreSame(previous, Find<Button>(form, "Previous files in group 1"));
            Assert.IsTrue(Descendants(form).Any(control => control is Label && control.Text == "Files 1–25 of 51"));
        });
    }

    private static IReadOnlyList<DuplicateGroup> CreateGroups(int count, int filesPerGroup) => Enumerable.Range(1, count).Select(number =>
    {
        var files = Enumerable.Range(0, filesPerGroup).Select(index => new VideoCandidate(
            Path.Combine(Path.GetTempPath(), $"group-{number}-file-{index}.mp4"), $"group-{number}-file-{index}.mp4",
            new DateTime(2026, 1, 1).AddSeconds(index), TimestampSource.FileName, 1)).ToArray();
        return new DuplicateGroup(number, files, files[0]);
    }).ToArray();

    private static void Populate(MainForm form, IReadOnlyList<DuplicateGroup> groups)
    {
        typeof(MainForm).GetField("_groups", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(form, groups);
        typeof(MainForm).GetField("_scanContext", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(form, new ScanContext(Path.GetTempPath(), 60, KeepMode.Oldest, false));
        var reviewed = (Dictionary<string, ReviewedVideo>)typeof(MainForm).GetField("_reviewed", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(form)!;
        foreach (var group in groups)
            foreach (var file in group.Files)
                reviewed[file.FullPath] = new ReviewedVideo(file, new MediaMetadata(null, null, null, MetadataProbeStatus.Unavailable), DuplicateConfidence.Low, file == group.SuggestedKeep);
    }

    private static void Invoke(MainForm form, string method, params object[] arguments) =>
        typeof(MainForm).GetMethod(method, BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(form, arguments);
    private static IEnumerable<Control> Descendants(Control control) => control.Controls.Cast<Control>().SelectMany(child => new[] { child }.Concat(Descendants(child)));
    private static T Find<T>(Control form, string name) where T : Control => Descendants(form).OfType<T>().SingleOrDefault(control => control.AccessibleName == name)
        ?? throw new AssertFailedException($"Missing {typeof(T).Name}: {name}. Available: {string.Join("; ", Descendants(form).OfType<T>().Select(control => control.AccessibleName))}");

    private static void RunOnUiThread(Action<MainForm> test)
    {
        Exception? failure = null;
        var settings = Environment.GetEnvironmentVariable("DVC_SETTINGS_PATH");
        var path = Path.Combine(Path.GetTempPath(), "dvc-large-library-" + Guid.NewGuid().ToString("N"), "settings.json");
        var thread = new Thread(() =>
        {
            try
            {
                Environment.SetEnvironmentVariable("DVC_SETTINGS_PATH", path);
                using var form = new MainForm();
                form.Shown += (_, _) => { try { test(form); } catch (Exception ex) { failure = ex; } finally { form.Close(); } };
                Application.Run(form);
            }
            catch (Exception ex) { failure = ex; }
            finally { Environment.SetEnvironmentVariable("DVC_SETTINGS_PATH", settings); }
        }) { IsBackground = true };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        Assert.IsTrue(thread.Join(TimeSpan.FromSeconds(30)), "Large-library UI test did not finish.");
        if (failure is not null) System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(failure).Throw();
        var directory = Path.GetDirectoryName(path)!;
        if (Directory.Exists(directory)) Directory.Delete(directory, true);
    }
}
