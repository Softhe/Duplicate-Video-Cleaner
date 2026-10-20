using System.Diagnostics;
using DuplicateVideoCleaner;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace DuplicateVideoCleaner.UiTests;

[TestClass]
public sealed class ReviewSessionTests
{
    public TestContext TestContext { get; set; } = null!;

    [TestMethod]
    public void IndexedSelectionMatchesTraversalAndUsesCanonicalSize()
    {
        var root = Path.GetTempPath();
        var files = Enumerable.Range(0, 100_000).Select(index => new VideoCandidate(
            Path.Combine(root, $"session-{index}.mp4"), $"session-{index}.mp4", new DateTime(2026, 1, 1), TimestampSource.FileName, index + 1)).ToArray();
        IReadOnlyList<DuplicateGroup> groups = [new(1, files, files[0])];
        var selected = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var session = new ReviewSession(groups, selected, new(Path.GetTempPath(), 60, KeepMode.Oldest, false), new Dictionary<string, ReviewedVideo>());
        session.SetSelected(files[1] with { Size = 9999 }, true);
        Assert.AreEqual(2L, session.SelectedBytes);
        session.SetSelected(files[1], true);
        Assert.AreEqual((1, 2L), session.GroupSelection(1));
        session.SetSelected(files[1], false);
        Assert.AreEqual((0, 0L), session.GroupSelection(1));

        const int iterations = 500;
        long baselineBytes = 0;
        var timer = Stopwatch.StartNew();
        for (var index = 0; index < iterations; index++)
        {
            selected.Add(files[index].FullPath);
            baselineBytes = groups.SelectMany(group => group.Files).Where(file => selected.Contains(file.FullPath)).Sum(file => file.Size);
        }
        var traversal = timer.Elapsed;
        selected.Clear();
        session = new ReviewSession(groups, selected, null, new Dictionary<string, ReviewedVideo>());
        timer.Restart();
        for (var index = 0; index < iterations; index++) session.SetSelected(files[index], true);
        var indexed = timer.Elapsed;
        Assert.AreEqual(baselineBytes, session.SelectedBytes);
        Assert.AreEqual(iterations, session.SelectedCount);
        Assert.AreEqual((iterations, baselineBytes), session.GroupSelection(1));
        Assert.AreEqual(iterations, session.SelectedFiles().Count);
        TestContext.WriteLine($"Synthetic selection summary: {files.Length} files, {iterations} toggles; legacy traversal {traversal.TotalMilliseconds:0.000} ms; indexed updates {indexed.TotalMilliseconds:0.000} ms. Excludes rendering and one-time index construction.");
    }
}
