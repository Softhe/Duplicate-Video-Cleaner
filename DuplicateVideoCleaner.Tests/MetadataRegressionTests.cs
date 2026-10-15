using System.Diagnostics;
using DuplicateVideoCleaner;

internal static class MetadataRegressionTests
{
    internal static bool TryRunFixture(string[] args, out int exitCode)
    {
        exitCode = 0;
        if (args.Length == 0 || args[0] != "-v") return false;
        var media = args[^1];
        File.WriteAllText(media + ".pid", Environment.ProcessId.ToString());
        if (Path.GetFileName(media).StartsWith("wait")) Thread.Sleep(30000);
        Console.WriteLine("{\"streams\":[{\"width\":1920,\"height\":1080}],\"format\":{\"duration\":\"12.5\"}}");
        return true;
    }

    internal static async Task ProcessesAndCancellation()
    {
        var root = Path.Combine(Path.GetTempPath(), "dvc-probe-" + Guid.NewGuid());
        Directory.CreateDirectory(root);
        try
        {
            using (var canceled = new CancellationTokenSource())
            {
                canceled.Cancel();
                try { await new MetadataProbe("missing.exe").ProbeAsync("missing.mp4", canceled.Token); throw new Exception("Unavailable probe ignored cancellation."); }
                catch (OperationCanceledException) { }
            }
            var executable = Path.Combine(AppContext.BaseDirectory, "DuplicateVideoCleaner.Tests.exe");
            var success = Path.Combine(root, "success.mp4");
            File.WriteAllText(success, "fixture");
            var metadata = await new MetadataProbe(executable).ProbeAsync(success);
            if (metadata.Status != MetadataProbeStatus.Available || metadata.Width != 1920)
                throw new Exception("Real subprocess metadata was not parsed.");
            var wait = Path.Combine(root, "wait-timeout.mp4");
            File.WriteAllText(wait, "fixture");
            var timeout = await new MetadataProbe(executable, TimeSpan.FromSeconds(1)).ProbeAsync(wait);
            if (timeout.Status != MetadataProbeStatus.TimedOut) throw new Exception("Timeout was not reported.");
            AssertExited(wait);
            wait = Path.Combine(root, "wait-cancel.mp4");
            File.WriteAllText(wait, "fixture");
            using var cancellation = new CancellationTokenSource();
            var task = new MetadataProbe(executable).ProbeAsync(wait, cancellation.Token);
            var timer = Stopwatch.StartNew();
            while (!File.Exists(wait + ".pid") && timer.Elapsed < TimeSpan.FromSeconds(5)) await Task.Delay(20);
            cancellation.Cancel();
            try { await task; throw new Exception("Cancellation was swallowed."); }
            catch (OperationCanceledException) { }
            AssertExited(wait);
            try { await new MetadataProbe("missing.exe").ProbeAsync(success, cancellation.Token); throw new Exception("Unavailable probe ignored cancellation."); }
            catch (OperationCanceledException) { }
        }
        finally { Directory.Delete(root, true); }
    }

    private static void AssertExited(string path)
    {
        if (!File.Exists(path + ".pid")) throw new Exception("Fixture did not start.");
        var pid = int.Parse(File.ReadAllText(path + ".pid"));
        try
        {
            using var process = Process.GetProcessById(pid);
            if (!process.HasExited) throw new Exception("Canceled or timed-out ffprobe is still running.");
        }
        catch (ArgumentException) { }
    }
}
