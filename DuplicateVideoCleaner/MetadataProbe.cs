using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using System.Text.Json;

namespace DuplicateVideoCleaner;

internal sealed class MetadataProbe
{
    private readonly string? _ffprobePath;
    private readonly TimeSpan _timeout;
    private readonly ConcurrentDictionary<string, MediaMetadata> _cache = new(StringComparer.OrdinalIgnoreCase);

    internal MetadataProbe(string? ffprobePath = null, TimeSpan? timeout = null)
    {
        _ffprobePath = ffprobePath ?? ResolveFfprobePath();
        _timeout = timeout ?? TimeSpan.FromSeconds(15);
    }

    internal bool IsAvailable => !string.IsNullOrWhiteSpace(_ffprobePath) && File.Exists(_ffprobePath);

    internal async Task<IReadOnlyDictionary<string, MediaMetadata>> ProbeManyAsync(IEnumerable<VideoCandidate> files, CancellationToken cancellationToken = default)
    {
        var results = new ConcurrentDictionary<string, MediaMetadata>(StringComparer.OrdinalIgnoreCase);
        using var gate = new SemaphoreSlim(2);
        await Task.WhenAll(files.Select(async file =>
        {
            await gate.WaitAsync(cancellationToken);
            try { results[file.FullPath] = await ProbeAsync(file.FullPath, cancellationToken); }
            finally { gate.Release(); }
        }));
        return results;
    }

    internal async Task<MediaMetadata> ProbeAsync(string path, CancellationToken cancellationToken = default)
    {
        if (!IsAvailable) return new MediaMetadata(null, null, null, MetadataProbeStatus.Unavailable, "ffprobe was not found");
        FileInfo info;
        try { info = new FileInfo(path); if (!info.Exists) throw new FileNotFoundException("Media file was not found.", path); }
        catch (Exception ex) { return new MediaMetadata(null, null, null, MetadataProbeStatus.Failed, ex.Message); }
        var key = CreateCacheKey(info);
        if (_cache.TryGetValue(key, out var cached)) return cached;

        using var process = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = _ffprobePath!,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            }
        };
        process.StartInfo.ArgumentList.Add("-v");
        process.StartInfo.ArgumentList.Add("error");
        process.StartInfo.ArgumentList.Add("-select_streams");
        process.StartInfo.ArgumentList.Add("v:0");
        process.StartInfo.ArgumentList.Add("-show_entries");
        process.StartInfo.ArgumentList.Add("stream=width,height:format=duration");
        process.StartInfo.ArgumentList.Add("-of");
        process.StartInfo.ArgumentList.Add("json");
        process.StartInfo.ArgumentList.Add(path);

        try
        {
            process.Start();
            var outputTask = process.StandardOutput.ReadToEndAsync(cancellationToken);
            var errorTask = process.StandardError.ReadToEndAsync(cancellationToken);
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(_timeout);
            await process.WaitForExitAsync(timeout.Token);
            var output = await outputTask;
            var error = await errorTask;
            var result = process.ExitCode == 0 ? Parse(output) : new MediaMetadata(null, null, null, MetadataProbeStatus.Failed, error.Trim());
            _cache[key] = result;
            return result;
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            try { process.Kill(true); } catch { }
            return new MediaMetadata(null, null, null, MetadataProbeStatus.TimedOut, "ffprobe timed out");
        }
        catch (Exception ex)
        {
            return new MediaMetadata(null, null, null, MetadataProbeStatus.Failed, ex.Message);
        }
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
            return duration is not null && width is not null && height is not null
                ? new MediaMetadata(duration, width, height, MetadataProbeStatus.Available)
                : new MediaMetadata(duration, width, height, MetadataProbeStatus.Failed, "Required metadata was missing");
        }
        catch (Exception ex) { return new MediaMetadata(null, null, null, MetadataProbeStatus.Failed, ex.Message); }
    }

    internal static string CreateCacheKey(FileInfo info) => $"{info.FullName}|{info.Length}|{info.LastWriteTimeUtc.Ticks}";

    private static string? ResolveFfprobePath()
    {
        var overridePath = Environment.GetEnvironmentVariable("FFPROBE_PATH");
        if (!string.IsNullOrWhiteSpace(overridePath)) return overridePath;
        return Path.Combine(AppContext.BaseDirectory, "tools", "ffprobe.exe");
    }
}
