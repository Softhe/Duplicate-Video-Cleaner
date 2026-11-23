using DuplicateVideoCleaner;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace DuplicateVideoCleaner.UiTests;

[TestClass]
public sealed class SettingsQueueTests
{
    [TestMethod]
    public async Task BlockedWriterCoalescesSnapshotsAndFlushesLatest()
    {
        using var entered = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        var saves = new List<AppSettings>();
        var caller = Environment.CurrentManagedThreadId;
        var worker = caller;
        var queue = new SettingsSaveQueue(snapshot =>
        {
            worker = Environment.CurrentManagedThreadId;
            lock (saves) saves.Add(snapshot);
            if (snapshot.ThresholdSeconds == 1)
            {
                entered.Set();
                if (!release.Wait(TimeSpan.FromSeconds(10))) throw new TimeoutException();
            }
        });
        queue.Enqueue(new() { ThresholdSeconds = 1 });
        Assert.IsTrue(entered.Wait(TimeSpan.FromSeconds(5)));
        for (var index = 2; index <= 500; index++) queue.Enqueue(new() { ThresholdSeconds = index });
        var flush = queue.FlushAsync();
        Assert.IsFalse(flush.IsCompleted);
        release.Set();
        await flush.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.AreNotEqual(caller, worker);
        Assert.AreEqual(2, saves.Count);
        Assert.AreEqual(500, saves[^1].ThresholdSeconds);
        Assert.IsNull(queue.LastError);
        queue.Enqueue(new() { ThresholdSeconds = 501 });
        await queue.FlushAsync();
        Assert.AreEqual(501, saves[^1].ThresholdSeconds);
    }

    [TestMethod]
    public async Task FailedSaveDoesNotPoisonFollowingSnapshot()
    {
        var calls = 0;
        var queue = new SettingsSaveQueue(_ => { if (Interlocked.Increment(ref calls) == 1) throw new IOException("fixture contention"); });
        queue.Enqueue(new());
        await queue.FlushAsync();
        Assert.IsInstanceOfType<IOException>(queue.LastError);
        queue.Enqueue(new() { ThresholdSeconds = 2 });
        await queue.FlushAsync();
        Assert.AreEqual(2, calls);
        Assert.IsNull(queue.LastError);
    }
}
