using System.Diagnostics;
using DuplicateVideoCleaner.ViewModels;
using DuplicateVideoCleaner.Views;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using static DuplicateVideoCleaner.UiTests.WpfTestHost;

namespace DuplicateVideoCleaner.UiTests;

// Group and file rows of the Review page, and large-library behaviour.
[TestClass]
[DoNotParallelize]
public sealed class ReviewModelTests
{
    public TestContext TestContext { get; set; } = null!;

    [TestMethod]
    public void ProtectedComparisonNamesTheActualMatchAndUsesItsSize()
    {
        Run(() =>
        {
            VideoCandidate File(string name, long size, bool protect) => new(Path.Combine(Path.GetTempPath(), name), name,
                new DateTime(2026, 1, 1, 10, 0, 0), TimestampSource.FileName, size, IsProtected: protect);
            var keeper = File("first favorite.mp4", 200, true);
            var match = File("second favorite.mp4", 100, true);
            var original = File("Replay 2026-01-01 10-00-00.mp4", 100, false);
            var group = new DuplicateGroup(1, [keeper, match, original], keeper);
            var reviewed = group.Files.ToDictionary(file => file.FullPath, file => new ReviewedVideo(file,
                new(TimeSpan.FromSeconds(file == keeper ? 10 : 20), 1920, 1080, MetadataProbeStatus.Available),
                DuplicateConfidence.Low, file == keeper), StringComparer.OrdinalIgnoreCase);
            RenamedFilePolicy.Apply(group, reviewed);
            var main = CreateMain(out _);
            main.Workspace.Load(new ScanContext(Path.GetTempPath(), 60, KeepMode.Oldest, false, true), [group], reviewed, []);
            main.Review.Reload();
            var row = main.Review.SelectedGroup!.Files.Single(file => file.RelativePath == original.RelativePath);
            Assert.AreEqual("Remove", row.Role);
            StringAssert.Contains(row.Note!, "second favorite.mp4", "The note must name the protected clip that is the actual match.");
            Assert.IsTrue(row.Attributes.All(attribute => attribute.IsMatch), "The match's size (not the keeper's) is the reference.");
            StringAssert.Contains(row.Attributes[2].Text, "exact size");
            StringAssert.Contains(row.RoleNote, "its match");
            StringAssert.Contains(DryRunExporter.BuildCsv(new(Path.GetTempPath(), 60, KeepMode.Oldest, false,
                [new(1, reviewed[original.FullPath], true)])), match.FullPath);
            return Task.CompletedTask;
        });
    }

    [TestMethod]
    public void GroupHeaderUsesWeakestSuggestionAndHandlesNoSuggestions()
    {
        Run(() =>
        {
            var files = Enumerable.Range(0, 3).Select(i => new VideoCandidate(Path.Combine(Path.GetTempPath(), $"badge-{i}.mp4"),
                $"badge-{i}.mp4", new DateTime(2026, 1, 1, 10, 0, i), TimestampSource.FileName, 1)).ToArray();
            var group = new DuplicateGroup(1, files, files[0]);
            var reviewed = files.ToDictionary(file => file.FullPath, file => new ReviewedVideo(file,
                new(null, null, null, MetadataProbeStatus.Unavailable), file == files[2] ? DuplicateConfidence.Low : DuplicateConfidence.High, file == files[0]),
                StringComparer.OrdinalIgnoreCase);
            var main = CreateMain(out _);
            GroupItemViewModel Header()
            {
                main.Workspace.Load(new ScanContext(Path.GetTempPath(), 60, KeepMode.Oldest, false), [group], reviewed, []);
                main.Review.Reload();
                return main.Review.Groups.Single();
            }
            Assert.AreEqual("Low", Header().ConfidenceKey);
            reviewed[files[2].FullPath] = reviewed[files[2].FullPath] with { Confidence = DuplicateConfidence.High };
            Assert.AreEqual("High confidence", Header().ConfidenceTitle);
            foreach (var file in files) reviewed[file.FullPath] = reviewed[file.FullPath] with { RequiresProtectedMatch = true };
            var none = Header();
            Assert.AreEqual("None", none.ConfidenceKey);
            Assert.AreEqual("No removal suggested", none.ConfidenceText);
            Assert.AreEqual("—", none.SizeText);
            Assert.IsTrue(none.Files.All(file => !file.CanSelect));
            return Task.CompletedTask;
        });
    }

    [TestMethod]
    public void TenThousandResultsBuildRowsLazilyAndKeepSelectionsGlobal()
    {
        Run(() =>
        {
            var groups = CreateGroups(5000, 2);
            var main = CreateMain(out _);
            var timer = Stopwatch.StartNew();
            Populate(main, groups);
            timer.Stop();
            TestContext.WriteLine($"10,000 results initial review: {timer.ElapsedMilliseconds} ms.");
            Assert.IsTrue(timer.Elapsed < TimeSpan.FromSeconds(5), "Initial review took over five seconds.");
            Assert.AreEqual(5000, main.Review.Groups.Count);
            Assert.AreEqual(0, main.Review.Groups.Count(group => group.FilesBuilt), "File rows are built only when a group is shown.");
            Assert.AreEqual(2, main.Review.SelectedGroup!.Files.Count);
            Assert.AreEqual(1, main.Review.Groups.Count(group => group.FilesBuilt), "File rows should only exist for the shown group.");
            StringAssert.StartsWith(main.Review.Groups[0].AutomationName, "Duplicate group 1;", "List order differs from group order.");
            main.Review.SelectAllCommand.Execute(null);
            Assert.AreEqual(10000, main.Workspace.CreateDryRun().Rows.Count(row => row.Selected));
            main.Review.SelectedGroup = main.Review.Groups[25];
            main.Review.SelectedGroup.ClearCommand.Execute(null);
            Assert.AreEqual(9998, main.Workspace.CreateDryRun().Rows.Count(row => row.Selected));
            main.Review.SelectedGroup = main.Review.Groups[0];
            Assert.AreEqual(9998, main.Workspace.CreateDryRun().Rows.Count(row => row.Selected), "Changing groups lost selection state.");
            Assert.AreEqual("9998 files · 9,76 KB".Replace(",", System.Globalization.CultureInfo.CurrentCulture.NumberFormat.NumberDecimalSeparator), main.Review.SelectionTotal);
            main.Review.SelectSuggestedCommand.Execute(null);
            var context = main.Workspace.CreateDryRun();
            Assert.AreEqual(5000, context.Rows.Count(row => row.Selected));
            StringAssert.Contains(DryRunExporter.BuildCsv(context), "group-5000-file-1.mp4");
            main.Workspace.Reconcile(new RemovalResult(new HashSet<string>(StringComparer.OrdinalIgnoreCase) { groups[^1].Files[1].FullPath }, []));
            main.Review.Reload();
            Assert.AreEqual(9998, main.Workspace.CreateDryRun().Rows.Count, "Removal reconciliation lost or retained unrelated groups.");
            Assert.AreEqual(4999, main.Review.Groups.Count);
            return Task.CompletedTask;
        });
    }

    [TestMethod]
    public void TenThousandFileGroupSelectsOneRowWithoutTouchingOthers()
    {
        Run(() =>
        {
            var main = CreateMain(out _);
            Populate(main, CreateGroups(1, 10000));
            var timer = Stopwatch.StartNew();
            var files = main.Review.SelectedGroup!.Files;
            timer.Stop();
            TestContext.WriteLine($"10,000 file rows: {timer.ElapsedMilliseconds} ms.");
            Assert.IsTrue(timer.Elapsed < TimeSpan.FromSeconds(3), "Building file rows took too long.");
            Assert.AreEqual(10000, files.Count);
            var target = files.Single(file => file.Name == "group-1-file-25.mp4");
            Assert.AreEqual("Group 1, select group-1-file-25.mp4 for removal", target.AutomationName);
            target.IsSelected = true;
            Assert.AreEqual(1, main.Workspace.CreateDryRun().Rows.Count(row => row.Selected));
            Assert.AreEqual("1 of 10000 selected · 1 B", main.Review.SelectedGroup.SelectionDetail);
            return Task.CompletedTask;
        });
    }

    [TestMethod]
    public void LongGroupTimeSpanDoesNotWrapAtOneDay()
    {
        var span = TimeSpan.FromHours(49) + TimeSpan.FromMinutes(2) + TimeSpan.FromSeconds(3);
        Assert.AreEqual("49:02:03", DisplayText.Duration(span));
        Assert.AreEqual("4 s", DisplayText.Gap(TimeSpan.FromSeconds(4)));
        Assert.AreEqual("1:25", DisplayText.Gap(TimeSpan.FromSeconds(85)));
    }

    [TestMethod]
    public void WindowPlacementAcceptsNegativeMonitorsAndRequiresReachableTitleBar()
    {
        PixelRect[] screens = [new(-1920, 0, 1920, 1080), new(0, -1080, 1920, 1080)];
        Assert.IsTrue(WindowPlacement.HasReachableTitleBar(new(-1800, 50, 1200, 790), screens));
        Assert.IsTrue(WindowPlacement.HasReachableTitleBar(new(100, -1000, 1200, 790), screens));
        Assert.IsFalse(WindowPlacement.HasReachableTitleBar(new(-4000, 50, 1200, 790), screens));
        Assert.IsFalse(WindowPlacement.HasReachableTitleBar(new(100, -1095, 1200, 790), screens));
    }

    [TestMethod]
    public void ThemePreferenceIsSavedAndDefaultsToDark()
    {
        Run(async () =>
        {
            var saves = new List<AppSettings>();
            var main = CreateMain(out _, settings => { lock (saves) saves.Add(settings); });
            Assert.AreEqual(ThemePreference.Dark, main.Theme);
            main.SetThemeCommand.Execute("Light");
            Assert.AreEqual(ThemePreference.Light, main.Theme);
            await WaitUntil(() => { lock (saves) return saves.Any(settings => settings.Theme == ThemePreference.Light); }, "The theme was not saved.");
        });
    }

    internal static MainViewModel CreateMain(out string settingsPath, Action<AppSettings>? writer = null)
    {
        settingsPath = Path.Combine(Path.GetTempPath(), "dvc-ui-settings-" + Guid.NewGuid().ToString("N") + ".json");
        return new MainViewModel(new SettingsService(settingsPath), writer ?? (_ => { }), new MetadataProbe(Path.Combine(Path.GetTempPath(), "missing-ffprobe.exe")));
    }

    private static IReadOnlyList<DuplicateGroup> CreateGroups(int count, int filesPerGroup) => Enumerable.Range(1, count).Select(number =>
    {
        var files = Enumerable.Range(0, filesPerGroup).Select(index => new VideoCandidate(
            Path.Combine(Path.GetTempPath(), $"group-{number}-file-{index}.mp4"), $"group-{number}-file-{index}.mp4",
            new DateTime(2026, 1, 1).AddSeconds(index), TimestampSource.FileName, 1)).ToArray();
        return new DuplicateGroup(number, files, files[0]);
    }).ToArray();

    private static void Populate(MainViewModel main, IReadOnlyList<DuplicateGroup> groups)
    {
        var reviewed = new Dictionary<string, ReviewedVideo>(StringComparer.OrdinalIgnoreCase);
        foreach (var group in groups)
            foreach (var file in group.Files)
                reviewed[file.FullPath] = new ReviewedVideo(file, new MediaMetadata(null, null, null, MetadataProbeStatus.Unavailable), DuplicateConfidence.Low, file == group.SuggestedKeep);
        main.Workspace.Load(new ScanContext(Path.GetTempPath(), 60, KeepMode.Oldest, false), groups, reviewed, []);
        main.Review.Reload();
        main.OnResultsChanged();
        main.Navigate(WorkspacePage.Review);
    }
}
