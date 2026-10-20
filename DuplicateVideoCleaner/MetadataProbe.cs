using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.Json;

namespace DuplicateVideoCleaner;

internal sealed class MetadataProbe
{
    private readonly string? _ffprobePath;
    private readonly TimeSpan _timeout;
    private readonly TimeSpan _stagingTimeout;
    internal MetadataBatchMetrics? LastBatchMetrics { get; private set; }
    private static readonly ConcurrentDictionary<string, int> ActiveProcesses = new(StringComparer.OrdinalIgnoreCase);
    internal static int? GetActiveProcessId(string path) => ActiveProcesses.TryGetValue(Path.GetFullPath(path), out var pid) ? pid : null;
    private readonly BoundedMetadataCache _cache = new(2048);

    internal MetadataProbe(string? ffprobePath = null, TimeSpan? timeout = null, TimeSpan? stagingTimeout = null)
    {
        _ffprobePath = ffprobePath ?? ResolveFfprobePath();
        _timeout = timeout ?? TimeSpan.FromSeconds(15);
        _stagingTimeout = stagingTimeout ?? TimeSpan.FromMinutes(30);
        if (_stagingTimeout <= TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(stagingTimeout));
    }

    internal bool IsAvailable => !string.IsNullOrWhiteSpace(_ffprobePath) && File.Exists(_ffprobePath);

    internal async Task<IReadOnlyDictionary<string, MediaMetadata>> ProbeManyAsync(IEnumerable<VideoCandidate> files, CancellationToken cancellationToken = default, IProgress<MetadataProgress>? progress = null)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var candidates = files.ToArray();
        var clock = Stopwatch.StartNew();
        var results = new ConcurrentDictionary<string, MediaMetadata>(StringComparer.OrdinalIgnoreCase);
        var pending = new List<VideoCandidate>();
        var hits = 0;
        await Task.Run(() =>
        {
        foreach (var file in candidates)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                using var directories = FileSnapshot.PinDirectories(Path.GetDirectoryName(file.FullPath)!);
                using var source = WindowsProbeSandbox.OpenReadOnlyMedia(file.FullPath);
                FileSnapshot.ValidateReadHandle(source.SafeFileHandle, file);
                if (_cache.TryGetValue(SnapshotCacheKey(file), out var cached)) { results[file.FullPath] = cached; hits++; }
                else pending.Add(file);
            }
            catch (Exception ex) { results[file.FullPath] = new(null, null, null, MetadataProbeStatus.Failed, ex.Message); }
        }
        }, cancellationToken);
        long stagingTicks = 0, parsingTicks = 0, stagedBytes = 0, copiedBytes = 0;
        var completed = results.Count;
        var totalBytes = pending.Sum(file => file.Size);
        progress?.Report(new("Preparing", null, 0, totalBytes, completed, candidates.Length, clock.Elapsed));
        if (pending.Count == 0)
        {
            LastBatchMetrics = new(candidates.Length, hits, 0, TimeSpan.Zero, TimeSpan.Zero, TimeSpan.Zero, clock.Elapsed);
            progress?.Report(new("Complete", null, 0, 0, completed, candidates.Length, clock.Elapsed));
            return results;
        }
        if (!IsAvailable)
        {
            foreach (var file in pending) results[file.FullPath] = new(null, null, null, MetadataProbeStatus.Unavailable, "ffprobe was not found");
            LastBatchMetrics = new(candidates.Length, hits, 0, TimeSpan.Zero, TimeSpan.Zero, TimeSpan.Zero, clock.Elapsed);
            progress?.Report(new("Complete", null, 0, totalBytes, results.Count, candidates.Length, clock.Elapsed));
            return results;
        }
        var setupClock = Stopwatch.StartNew();
        WindowsProbeSandbox sandbox;
        try { sandbox = await Task.Run(() => new WindowsProbeSandbox(_ffprobePath!, cancellationToken), cancellationToken); }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            cancellationToken.ThrowIfCancellationRequested();
            foreach (var file in pending)
                results[file.FullPath] = new(null, null, null, MetadataProbeStatus.Failed, "Metadata isolation failed: " + ex.Message);
            LastBatchMetrics = new(candidates.Length, hits, 0, setupClock.Elapsed, TimeSpan.Zero, TimeSpan.Zero, clock.Elapsed);
            progress?.Report(new("Complete", null, 0, totalBytes, results.Count, candidates.Length, clock.Elapsed));
            return results;
        }
        var setup = setupClock.Elapsed;
        try
        {
            await Parallel.ForEachAsync(pending, new ParallelOptions
            {
                MaxDegreeOfParallelism = 2,
                CancellationToken = cancellationToken
            }, async (file, token) =>
            {
                results[file.FullPath] = await ProbeCoreAsync(file.FullPath, sandbox, token, file,
                    count => { var current = Interlocked.Add(ref copiedBytes, count); progress?.Report(new("Copying", file.FullPath, current, totalBytes, Volatile.Read(ref completed), candidates.Length, clock.Elapsed)); },
                    (stage, parser, bytes) => { Interlocked.Add(ref stagingTicks, stage.Ticks); Interlocked.Add(ref parsingTicks, parser.Ticks); Interlocked.Add(ref stagedBytes, bytes); });
                var done = Interlocked.Increment(ref completed);
                progress?.Report(new("Reading metadata", file.FullPath, Volatile.Read(ref copiedBytes), totalBytes, done, candidates.Length, clock.Elapsed));
            });
        }
        finally { await Task.Run(sandbox.Dispose); }
        if (sandbox.CleanupWarnings.Count > 0)
            foreach (var path in results.Keys) results[path] = WithCleanupWarnings(results[path], sandbox.CleanupWarnings);
        LastBatchMetrics = new(candidates.Length, hits, stagedBytes, setup, TimeSpan.FromTicks(stagingTicks), TimeSpan.FromTicks(parsingTicks), clock.Elapsed);
        progress?.Report(new("Complete", null, copiedBytes, totalBytes, completed, candidates.Length, clock.Elapsed));
        return results;
    }

    internal async Task<MediaMetadata> ProbeAsync(string path, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!IsAvailable) return new MediaMetadata(null, null, null, MetadataProbeStatus.Unavailable, "ffprobe was not found");
        try
        {
            var sandbox = await Task.Run(() => new WindowsProbeSandbox(_ffprobePath!, cancellationToken), cancellationToken);
            MediaMetadata result;
            try { result = await ProbeCoreAsync(path, sandbox, cancellationToken); }
            finally { await Task.Run(sandbox.Dispose); }
            return WithCleanupWarnings(result, sandbox.CleanupWarnings);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex) { return new MediaMetadata(null, null, null, MetadataProbeStatus.Failed, "Metadata isolation failed: " + ex.Message); }
    }

    internal static MediaMetadata WithCleanupWarnings(MediaMetadata result, IReadOnlyList<string> warnings) =>
        warnings.Count == 0 ? result : result with
        {
            Error = string.Join("; ", new[] { result.Error }.Where(error => !string.IsNullOrWhiteSpace(error)).Concat(warnings))
        };

    private async Task<MediaMetadata> ProbeCoreAsync(string path, WindowsProbeSandbox sandbox, CancellationToken cancellationToken, VideoCandidate? scanSnapshot = null, Action<int>? copied = null, Action<TimeSpan, TimeSpan, long>? measured = null)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!IsAvailable) return new MediaMetadata(null, null, null, MetadataProbeStatus.Unavailable, "ffprobe was not found");
        string key;
        VideoCandidate expected;
        try
        {
            path = Path.GetFullPath(path);
            using var directories = FileSnapshot.PinDirectories(Path.GetDirectoryName(path)!);
            using var source = WindowsProbeSandbox.OpenReadOnlyMedia(path);
            expected = scanSnapshot ?? FileSnapshot.CaptureReadHandle(source.SafeFileHandle, path);
            FileSnapshot.ValidateReadHandle(source.SafeFileHandle, expected);
            key = SnapshotCacheKey(expected);
        }
        catch (Exception ex) { return new MediaMetadata(null, null, null, MetadataProbeStatus.Failed, ex.Message); }
        if (_cache.TryGetValue(key, out var cached)) return cached;

        SandboxedProcess? isolated = null;
        Process? process = null;
        string? staged = null;
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var stagingClock = Stopwatch.StartNew();
        var parsingClock = new Stopwatch();
        var stagedSuccessfully = false;
        try
        {
            using (var stagingCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken))
            {
                stagingCancellation.CancelAfter(_stagingTimeout);
                staged = await sandbox.StageMediaAsync(path, stagingCancellation.Token, expected, copied);
            }
            stagedSuccessfully = true;
            stagingClock.Stop();
            parsingClock.Start();
            cancellationToken.ThrowIfCancellationRequested();
            isolated = sandbox.Start(["-v", "error", "-select_streams", "v:0", "-show_entries", "stream=width,height:format=duration", "-of", "json", "-protocol_whitelist", "file", "-i", staged]);
            process = isolated.Process;
            ActiveProcesses[path] = process.Id;
            timeout.CancelAfter(_timeout);
            var outputTask = ReadBoundedAsync(isolated.StandardOutput, 1024 * 1024, timeout);
            var errorTask = ReadBoundedAsync(isolated.StandardError, 64 * 1024, timeout);
            await Task.WhenAll(process.WaitForExitAsync(timeout.Token), outputTask, errorTask);
            var output = await outputTask;
            var error = await errorTask;
            var result = process.ExitCode == 0 ? Parse(output) : new MediaMetadata(null, null, null, MetadataProbeStatus.Failed, error.Trim());
            parsingClock.Stop();
            isolated.Dispose();
            isolated = null;
            var cleanupWarning = sandbox.RemoveMedia(staged);
            staged = null;
            _cache.Add(key, result);
            if (cleanupWarning is not null) result = result with { Error = string.IsNullOrWhiteSpace(result.Error) ? cleanupWarning : result.Error + "; " + cleanupWarning };
            return result;
        }
        catch (OperationCanceledException)
        {
            if (process is not null) await StopProcessAsync(process);
            cancellationToken.ThrowIfCancellationRequested();
            return new MediaMetadata(null, null, null, MetadataProbeStatus.TimedOut, stagedSuccessfully ? "ffprobe timed out" : "Metadata staging exceeded its separate time limit");
        }
        catch (Exception ex)
        {
            timeout.Cancel();
            if (process is not null) await StopProcessAsync(process);
            return new MediaMetadata(null, null, null, MetadataProbeStatus.Failed, ex.Message);
        }
        finally
        {
            stagingClock.Stop();
            parsingClock.Stop();
            measured?.Invoke(stagingClock.Elapsed, parsingClock.Elapsed, stagedSuccessfully ? expected.Size : 0);
            ActiveProcesses.TryRemove(path, out _);
            isolated?.Dispose();
            if (staged is not null) sandbox.RemoveMedia(staged);
        }
    }

    private static async Task<string> ReadBoundedAsync(StreamReader reader, int limit, CancellationTokenSource cancellation)
    {
        var result = new StringBuilder();
        var buffer = new char[4096];
        while (true)
        {
            var count = await reader.ReadAsync(buffer.AsMemory(), cancellation.Token);
            if (count == 0) return result.ToString();
            if (result.Length + count > limit)
            {
                cancellation.Cancel();
                throw new InvalidDataException("ffprobe output exceeded the safety limit.");
            }
            result.Append(buffer, 0, count);
        }
    }

    private static async Task StopProcessAsync(Process process)
    {
        try
        {
            if (!process.HasExited) process.Kill(true);
            await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(5));
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception or TimeoutException) { }
    }

    internal static MediaMetadata Parse(string json)
    {
        try
        {
            using var document = JsonDocument.Parse(json);
            var root = document.RootElement;
            int? width = null, height = null;
            if (root.TryGetProperty("streams", out var streams) && streams.GetArrayLength() > 0)
            {
                var stream = streams[0];
                if (stream.TryGetProperty("width", out var widthElement)) width = widthElement.GetInt32();
                if (stream.TryGetProperty("height", out var heightElement)) height = heightElement.GetInt32();
            }
            TimeSpan? duration = null;
            if (root.TryGetProperty("format", out var format) && format.TryGetProperty("duration", out var durationElement) &&
                double.TryParse(durationElement.GetString(), NumberStyles.Float, CultureInfo.InvariantCulture, out var seconds))
                duration = TimeSpan.FromMilliseconds(Math.Round(seconds * 1000, MidpointRounding.AwayFromZero));
            return duration >= TimeSpan.Zero && width > 0 && height > 0
                ? new MediaMetadata(duration, width, height, MetadataProbeStatus.Available)
                : new MediaMetadata(duration, width, height, MetadataProbeStatus.Failed, "Required metadata was missing");
        }
        catch (Exception ex) { return new MediaMetadata(null, null, null, MetadataProbeStatus.Failed, ex.Message); }
    }

    internal static string CreateCacheKey(FileInfo info)
    {
        using var directories = FileSnapshot.PinDirectories(info.DirectoryName!);
        using var source = WindowsProbeSandbox.OpenReadOnlyMedia(info.FullName);
        return SnapshotCacheKey(FileSnapshot.CaptureReadHandle(source.SafeFileHandle, info.FullName));
    }

    private static string SnapshotCacheKey(VideoCandidate snapshot) =>
        $"{snapshot.FullPath}|{snapshot.Size}|{snapshot.CreationTimeUtc!.Value.Ticks}|{snapshot.LastWriteTimeUtc!.Value.Ticks}|{snapshot.FileIdentity}";

    private static string? ResolveFfprobePath()
    {
        var overridePath = Environment.GetEnvironmentVariable("FFPROBE_PATH");
        if (!string.IsNullOrWhiteSpace(overridePath)) return overridePath;
        return Path.Combine(AppContext.BaseDirectory, "tools", "ffprobe.exe");
    }
}
