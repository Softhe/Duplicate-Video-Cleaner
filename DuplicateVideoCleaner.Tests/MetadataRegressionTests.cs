using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text.Json;
using DuplicateVideoCleaner;

internal static class MetadataRegressionTests
{
    internal static bool TryRunFixture(string[] args, out int exitCode)
    {
        exitCode = 0;
        if (args.Length == 3 && args[0] == "--abandon-sandbox")
        {
            using var sandbox = new WindowsProbeSandbox(args[1]);
            File.WriteAllText(args[2], sandbox.Root);
            Process.GetCurrentProcess().Kill();
            Thread.Sleep(Timeout.Infinite);
            return true;
        }
        if (args.Length > 0 && args[0] == "--sandbox-check")
        {
            var result = new Dictionary<string, bool>();
            result["selectedRead"] = File.ReadAllText(args[1]) == "selected fixture";
            result["privateReadDenied"] = Denied(() => File.ReadAllText(args[2]));
            result["privateWriteDenied"] = Denied(() => File.WriteAllText(args[2], "changed"));
            result["selectedWriteDenied"] = Denied(() => File.WriteAllText(args[1], "changed"));
            result["environmentClean"] = Environment.GetEnvironmentVariable("DVC_TEST_SECRET_SENTINEL") is null;
            try
            {
                using var socket = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
                socket.ConnectAsync(IPAddress.Loopback, int.Parse(args[3])).WaitAsync(TimeSpan.FromSeconds(3)).GetAwaiter().GetResult();
                result["networkDenied"] = false;
            }
            catch (SocketException ex) { result["networkDenied"] = ex.SocketErrorCode == SocketError.AccessDenied; }
            catch (TimeoutException) { result["networkDenied"] = true; }
            try
            {
                using var child = Process.Start(new ProcessStartInfo(Environment.ProcessPath!, "--sandbox-child") { UseShellExecute = false, CreateNoWindow = true });
                result["childDenied"] = child is null;
                if (child is not null) { try { child.Kill(); } catch { } }
            }
            catch (System.ComponentModel.Win32Exception) { result["childDenied"] = true; }
            Console.WriteLine(JsonSerializer.Serialize(result));
            return true;
        }
        if (args.Length > 0 && args[0] == "--sandbox-child") { Console.WriteLine("Unexpected child executed."); exitCode = 99; return true; }
        if (args.Length == 0 || args[0] != "-v") return false;
        var media = args[^1];
        if (!Path.IsPathFullyQualified(media) || args[^2] != "-i" ||
            !args.Contains("-protocol_whitelist") || args[Array.IndexOf(args, "-protocol_whitelist") + 1] != "file")
        {
            Console.Error.WriteLine("Unsafe probe arguments.");
            exitCode = 1;
            return true;
        }
        if (Path.GetFileName(media).StartsWith("flood"))
        {
            var chunk = new string('x', 4096);
            while (true) Console.Out.Write(chunk);
        }
        if (Path.GetFileName(media).StartsWith("wait")) Thread.Sleep(30000);
        Console.WriteLine("{\"streams\":[{\"width\":1920,\"height\":1080}],\"format\":{\"duration\":\"12.5\"}}");
        return true;
    }

    private static bool Denied(Action operation)
    {
        try { operation(); return false; }
        catch (UnauthorizedAccessException) { return true; }
    }

    private sealed class InlineProgress(Action<MetadataProgress> report) : IProgress<MetadataProgress>
    {
        public void Report(MetadataProgress value) => report(value);
    }

    private static async Task VerifyBatchSetupFailure(string executable, string root)
    {
        var tools = Directory.CreateDirectory(Path.Combine(root, "setup-failure-tools")).FullName;
        var executableBase = Path.GetFileNameWithoutExtension(executable);
        foreach (var path in Directory.EnumerateFiles(Path.GetDirectoryName(executable)!))
        {
            var name = Path.GetFileName(path);
            if (Path.GetExtension(path).Equals(".dll", StringComparison.OrdinalIgnoreCase) ||
                path.Equals(executable, StringComparison.OrdinalIgnoreCase) ||
                name.Equals(executableBase + ".deps.json", StringComparison.OrdinalIgnoreCase) ||
                name.Equals(executableBase + ".runtimeconfig.json", StringComparison.OrdinalIgnoreCase))
                File.Copy(path, Path.Combine(tools, name));
        }
        var probe = new MetadataProbe(Path.Combine(tools, Path.GetFileName(executable)));
        var warmPath = Path.Combine(root, "setup-cached.mp4");
        var pendingPath = Path.Combine(root, "setup-pending.mp4");
        File.WriteAllText(warmPath, "cached original fixture");
        File.WriteAllText(pendingPath, "pending original fixture");
        VideoCandidate Capture(string path)
        {
            using var source = WindowsProbeSandbox.OpenReadOnlyMedia(path);
            return FileSnapshot.CaptureReadHandle(source.SafeFileHandle, path);
        }
        var cached = Capture(warmPath);
        var pending = Capture(pendingPath);
        var before = await probe.ProbeManyAsync([cached]);
        if (before[warmPath].Status != MetadataProbeStatus.Available)
            throw new Exception("Setup-failure fixture could not warm its metadata cache: " + before[warmPath].Error);

        // An exclusively locked dependency makes the real tool-copy setup fail
        // before any parser starts, without changing the sandbox implementation.
        var lockedDependency = Path.Combine(tools, "locked-fixture.dll");
        File.WriteAllText(lockedDependency, "locked setup fixture");
        MetadataProgress? lastProgress = null;
        using (var locked = new FileStream(lockedDependency, FileMode.Open, FileAccess.Read, FileShare.None))
        {
            var result = await probe.ProbeManyAsync([cached, pending], progress: new InlineProgress(value => lastProgress = value));
            if (result.Count != 2 || result[warmPath] != before[warmPath])
                throw new Exception("Sandbox setup failure discarded validated cached metadata.");
            if (result[pendingPath].Status != MetadataProbeStatus.Failed || result[pendingPath].Error?.StartsWith("Metadata isolation failed: ") != true)
                throw new Exception("Sandbox setup failure did not return an explicit per-file isolation failure.");
            if (probe.LastBatchMetrics is not { CacheHits: 1, StagedBytes: 0, Setup: var setup, Staging: var staging, Parsing: var parsing } ||
                setup <= TimeSpan.Zero || staging != TimeSpan.Zero || parsing != TimeSpan.Zero)
                throw new Exception("Sandbox setup failure did not complete its metrics without staging or parsing.");
            if (lastProgress is not { Phase: "Complete", CompletedFiles: 2, TotalFiles: 2 })
                throw new Exception("Sandbox setup failure did not complete batch progress.");
            if (MetadataProbe.GetActiveProcessId(warmPath) is not null || MetadataProbe.GetActiveProcessId(pendingPath) is not null)
                throw new Exception("Sandbox setup failure left an active parser.");
            using var canceled = new CancellationTokenSource();
            canceled.Cancel();
            try { await probe.ProbeManyAsync([pending], canceled.Token); throw new Exception("Batch setup ignored cancellation."); }
            catch (OperationCanceledException) { }
        }
        using (var source = WindowsProbeSandbox.OpenReadOnlyMedia(warmPath)) FileSnapshot.ValidateReadHandle(source.SafeFileHandle, cached);
        using (var source = WindowsProbeSandbox.OpenReadOnlyMedia(pendingPath)) FileSnapshot.ValidateReadHandle(source.SafeFileHandle, pending);
        if (File.ReadAllText(warmPath) != "cached original fixture" || File.ReadAllText(pendingPath) != "pending original fixture")
            throw new Exception("Sandbox setup failure changed original media.");
    }

    private static async Task VerifyAbandonedCleanup(string executable, string root)
    {
        var receipt = Path.Combine(root, "abandoned-root.txt");
        var start = new ProcessStartInfo(executable) { UseShellExecute = false, CreateNoWindow = true };
        start.ArgumentList.Add("--abandon-sandbox"); start.ArgumentList.Add(executable); start.ArgumentList.Add(receipt);
        using var crashed = Process.Start(start)!;
        await crashed.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(30));
        var abandoned = File.ReadAllText(receipt);
        if (!Directory.Exists(abandoned)) throw new Exception("Crash fixture left no staging directory.");
        AbandonedProbeCleanup.Run(onlyRoot: abandoned);
        if (!Directory.Exists(abandoned)) throw new Exception("Cleanup removed recent crashed staging before the retention deadline.");
        var unowned = Path.Combine(Path.GetTempPath(), "dvc-sandbox-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(unowned);
        try
        {
            var sentinel = Path.Combine(unowned, "unowned.txt");
            File.WriteAllText(sentinel, "keep");
            AbandonedProbeCleanup.Run(TimeSpan.Zero, unowned);
            if (File.ReadAllText(sentinel) != "keep") throw new Exception("Cleanup touched an unowned directory.");
            File.WriteAllText(Path.Combine(unowned, AbandonedProbeCleanup.MarkerName), "{}");
            AbandonedProbeCleanup.Run(TimeSpan.Zero, unowned);
            if (File.ReadAllText(sentinel) != "keep") throw new Exception("Cleanup accepted an invalid ownership marker.");
            File.Delete(Path.Combine(unowned, AbandonedProbeCleanup.MarkerName));
            using (AbandonedProbeCleanup.CreateMarker(unowned, Path.GetFileName(unowned)["dvc-sandbox-".Length..])) { }
            AbandonedProbeCleanup.Run(TimeSpan.Zero, unowned);
            if (File.ReadAllText(sentinel) != "keep") throw new Exception("Cleanup ignored a live process after the marker lease closed.");
        }
        finally { Directory.Delete(unowned, true); }
        var external = Directory.CreateDirectory(Path.Combine(root, "outside-cleanup")).FullName;
        var outsideSentinel = Path.Combine(external, "keep.txt");
        File.WriteAllText(outsideSentinel, "external content");
        var link = Path.Combine(abandoned, "unsafe-link");
        using (var junction = Process.Start(new ProcessStartInfo("cmd.exe")
        {
            Arguments = $"/c mklink /J \"{link}\" \"{external}\"", UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardOutput = true, RedirectStandardError = true
        })!)
        {
            await junction.WaitForExitAsync();
            if (junction.ExitCode != 0) throw new Exception("Could not create abandoned staging junction fixture.");
        }
        try
        {
            var warnings = AbandonedProbeCleanup.Run(TimeSpan.Zero, abandoned);
            if (!warnings.Any(message => message.Contains("reparse point"))) throw new Exception("Unsafe cleanup did not retain its diagnostic.");
            if (!Directory.Exists(abandoned) || File.ReadAllText(outsideSentinel) != "external content")
                throw new Exception("Cleanup followed a reparse point or discarded the owned marker.");
        }
        finally { Directory.Delete(link, false); }
        // Age zero is test-only; production retains recent crash directories for 24 hours.
        using (var live = new WindowsProbeSandbox(executable))
        {
            AbandonedProbeCleanup.Run(TimeSpan.Zero, live.Root);
            if (!Directory.Exists(live.Root)) throw new Exception("Cleanup touched a live owner.");
        }
        AbandonedProbeCleanup.Run(TimeSpan.Zero, abandoned);
        if (Directory.Exists(abandoned)) throw new Exception("Cleanup did not remove precisely owned crashed staging.");
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
            await VerifyIsolation(executable, root);
            await VerifyBatchSetupFailure(executable, root);
            var success = Path.Combine(root, "success.mp4");
            File.WriteAllText(success, "fixture");
            var metadata = await new MetadataProbe(executable).ProbeAsync(success);
            if (metadata.Status != MetadataProbeStatus.Available || metadata.Width != 1920)
                throw new Exception("Real subprocess metadata was not parsed: " + metadata.Error);
            var originalWrite = File.GetLastWriteTimeUtc(success);
            var originalCreation = File.GetCreationTimeUtc(success);
            var originalKey = MetadataProbe.CreateCacheKey(new FileInfo(success));
            VideoCandidate expected;
            using (var source = WindowsProbeSandbox.OpenReadOnlyMedia(success)) expected = FileSnapshot.CaptureReadHandle(source.SafeFileHandle, success);
            var batchProbe = new MetadataProbe(executable);
            var firstBatch = await batchProbe.ProbeManyAsync([expected]);
            if (batchProbe.LastBatchMetrics is not { CacheHits: 0, StagedBytes: > 0 }) throw new Exception("Cold metadata metrics were missing.");
            await batchProbe.ProbeManyAsync([expected]);
            if (batchProbe.LastBatchMetrics is not { CacheHits: 1, StagedBytes: 0, Setup: var setup } || setup != TimeSpan.Zero)
                throw new Exception("Warm metadata created a sandbox or staged media.");
            await VerifyAbandonedCleanup(executable, root);
            var large = Path.Combine(root, "staging-deadline.mp4");
            File.WriteAllBytes(large, new byte[3 * 1024 * 1024]);
            VideoCandidate largeSnapshot;
            using (var source = WindowsProbeSandbox.OpenReadOnlyMedia(large)) largeSnapshot = FileSnapshot.CaptureReadHandle(source.SafeFileHandle, large);
            var deadlineProbe = new MetadataProbe(executable, stagingTimeout: TimeSpan.FromMilliseconds(25));
            var timedStaging = await deadlineProbe.ProbeManyAsync([largeSnapshot], progress: new InlineProgress(value =>
            {
                if (value.Phase == "Copying") Thread.Sleep(75);
            }));
            if (timedStaging[large].Status != MetadataProbeStatus.TimedOut || !timedStaging[large].Error!.Contains("staging"))
                throw new Exception("Staging deadline did not report separately from parser timeout.");
            if (deadlineProbe.LastBatchMetrics!.Parsing != TimeSpan.Zero) throw new Exception("Parser ran after staging deadline.");
            if (firstBatch[success].Status != MetadataProbeStatus.Available)
                throw new Exception("The original scan snapshot could not be probed.");
            var replacement = Path.Combine(root, "replacement.mp4");
            File.WriteAllText(replacement, "fixture");
            File.Move(replacement, success, true);
            File.SetLastWriteTimeUtc(success, originalWrite);
            File.SetCreationTimeUtc(success, originalCreation);
            if (originalKey == MetadataProbe.CreateCacheKey(new FileInfo(success)))
                throw new Exception("A same-size replacement reused cached metadata.");
            var replacedBatch = await batchProbe.ProbeManyAsync([expected]);
            if (replacedBatch[success].Status != MetadataProbeStatus.Failed || replacedBatch[success].Error?.Contains("changed since the scan") != true)
                throw new Exception("Batch probing accepted metadata for a replaced scan candidate or reused its cached result.");
            string cleanupRoot;
            using (var sandbox = new WindowsProbeSandbox(executable))
            {
                cleanupRoot = sandbox.Root;
                var staged = await sandbox.StageMediaAsync(success, CancellationToken.None);
                using (var locked = new FileStream(staged, FileMode.Open, FileAccess.Read, FileShare.Read))
                {
                    if (sandbox.RemoveMedia(staged)?.Contains("cleanup deferred") != true)
                        throw new Exception("A locked staging file did not produce its cleanup warning.");
                    if (!File.Exists(staged)) throw new Exception("The locked cleanup fixture was not retained for owner cleanup.");
                }
            }
            if (Directory.Exists(cleanupRoot)) throw new Exception("Sandbox disposal did not remove deferred media.");
            using (var sandbox = new WindowsProbeSandbox(executable))
            {
                var ownedRoot = sandbox.Root;
                var staged = await sandbox.StageMediaAsync(success, CancellationToken.None);
                try
                {
                    using (var locked = new FileStream(staged, FileMode.Open, FileAccess.Read, FileShare.Read))
                    {
                        // A persistent external lock must report cleanup trouble without
                        // throwing away results obtained before owner disposal.
                        sandbox.Dispose();
                        if (!sandbox.CleanupWarnings.Any(warning => warning.Contains("could not be removed")))
                            throw new Exception("Owner disposal did not report the locked staging root.");
                        var retained = MetadataProbe.WithCleanupWarnings(metadata, sandbox.CleanupWarnings);
                        if (retained.Status != MetadataProbeStatus.Available || retained.Width != 1920 ||
                            retained.Error?.Contains("could not be removed") != true)
                            throw new Exception("Owner cleanup warnings did not preserve successful metadata and its diagnostic.");
                    }
                }
                finally
                {
                    // This exact generated sandbox root belongs to this fixture.
                    if (Directory.Exists(ownedRoot)) Directory.Delete(ownedRoot, true);
                }
            }
            var cache = new BoundedMetadataCache(2);
            cache.Add("first", metadata);
            cache.Add("second", metadata);
            cache.Add("SECOND", metadata);
            cache.Add("third", metadata);
            if (cache.TryGetValue("first", out _) || !cache.TryGetValue("second", out _) || !cache.TryGetValue("third", out _))
                throw new Exception("Bounded cache eviction discarded recent entries or counted an update as a new entry.");
            using (var sandbox = new WindowsProbeSandbox(executable))
            {
                try { await sandbox.StageMediaAsync(success, CancellationToken.None, expected); throw new Exception("Metadata staging accepted a replaced file snapshot."); }
                catch (IOException ex) when (ex.Message.Contains("changed since the scan")) { }
            }
            var privateTools = Directory.CreateDirectory(Path.Combine(root, "private-tools")).FullName;
            var copiedExecutable = Path.Combine(privateTools, Path.GetFileName(executable));
            File.Copy(executable, copiedExecutable);
            File.WriteAllText(Path.Combine(privateTools, "unrelated-credentials.json"), "private fixture");
            using (var sandbox = new WindowsProbeSandbox(copiedExecutable))
                if (File.Exists(Path.Combine(sandbox.Root, "tools", "unrelated-credentials.json"))) throw new Exception("Sandbox copied unrelated configuration JSON.");
            var flood = Path.Combine(root, "flood.mp4");
            File.WriteAllText(flood, "fixture");
            var floodResult = await new MetadataProbe(executable, TimeSpan.FromSeconds(5)).ProbeAsync(flood);
            if (floodResult.Status != MetadataProbeStatus.Failed || floodResult.Error?.Contains("safety limit") != true)
                throw new Exception("Excessive subprocess output was not rejected.");
            var wait = Path.Combine(root, "wait-timeout.mp4");
            File.WriteAllText(wait, "fixture");
            var timeout = await new MetadataProbe(executable, TimeSpan.FromSeconds(1)).ProbeAsync(wait);
            if (timeout.Status != MetadataProbeStatus.TimedOut) throw new Exception("Timeout was not reported.");
            wait = Path.Combine(root, "wait-cancel.mp4");
            File.WriteAllText(wait, "fixture");
            using var cancellation = new CancellationTokenSource();
            var task = new MetadataProbe(executable).ProbeAsync(wait, cancellation.Token);
            var timer = Stopwatch.StartNew();
            while (MetadataProbe.GetActiveProcessId(wait) is null && !task.IsCompleted && timer.Elapsed < TimeSpan.FromSeconds(5)) await Task.Delay(20);
            var childPid = MetadataProbe.GetActiveProcessId(wait) ?? throw new Exception("Isolated fixture did not start: " + (task.IsCompleted ? (await task).Error : "timeout"));
            cancellation.Cancel();
            try { await task; throw new Exception("Cancellation was swallowed."); }
            catch (OperationCanceledException) { }
            AssertExited(childPid);
            try { await new MetadataProbe("missing.exe").ProbeAsync(success, cancellation.Token); throw new Exception("Unavailable probe ignored cancellation."); }
            catch (OperationCanceledException) { }
        }
        finally { Directory.Delete(root, true); }
    }

    private static async Task VerifyIsolation(string executable, string root)
    {
        var selected = Path.Combine(root, "selected.mp4");
        var sentinel = Path.Combine(root, "private-sentinel.txt");
        File.WriteAllText(selected, "selected fixture");
        File.WriteAllText(sentinel, "private fixture");
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        using (var control = new TcpClient()) await control.ConnectAsync(IPAddress.Loopback, port);
        using (var accepted = await listener.AcceptTcpClientAsync()) { }
        var previous = Environment.GetEnvironmentVariable("DVC_TEST_SECRET_SENTINEL");
        Environment.SetEnvironmentVariable("DVC_TEST_SECRET_SENTINEL", "must not reach the parser");
        try
        {
            using var sandbox = new WindowsProbeSandbox(executable);
            var staged = await sandbox.StageMediaAsync(selected, CancellationToken.None);
            using var isolated = sandbox.Start(["--sandbox-check", staged, sentinel, port.ToString()]);
            if (!isolated.IsolationVerified) throw new Exception("AppContainer token and job were not verified before resume.");
            var output = isolated.StandardOutput.ReadToEndAsync();
            var error = isolated.StandardError.ReadToEndAsync();
            await isolated.Process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(10));
            var text = await output;
            if (isolated.Process.ExitCode != 0) throw new Exception("Sandbox adversarial fixture failed: " + await error + text);
            var checks = JsonSerializer.Deserialize<Dictionary<string, bool>>(text) ?? throw new Exception("Missing sandbox checks.");
            foreach (var expected in new[] { "selectedRead", "privateReadDenied", "privateWriteDenied", "selectedWriteDenied", "environmentClean", "networkDenied", "childDenied" })
                if (!checks.TryGetValue(expected, out var passed) || !passed) throw new Exception("Sandbox restriction failed: " + expected + "; " + text);
            if (listener.Pending()) throw new Exception("Sandbox TCP connection reached the host listener.");
            if (File.ReadAllText(sentinel) != "private fixture" || File.ReadAllText(selected) != "selected fixture") throw new Exception("Sandbox modified an original fixture.");
            Console.WriteLine("PASS live AppContainer selected read, denied private read/write, denied staged write, clean environment, denied TCP, denied child process");
        }
        finally { Environment.SetEnvironmentVariable("DVC_TEST_SECRET_SENTINEL", previous); }
    }

    private static void AssertExited(int pid)
    {
        try
        {
            using var process = Process.GetProcessById(pid);
            if (!process.HasExited) throw new Exception("Canceled or timed-out ffprobe is still running.");
        }
        catch (ArgumentException) { }
    }
}
