using System.Diagnostics;
using System.Globalization;

namespace DuplicateVideoCleaner;

// One decoded frame: Width × Height pixels, 32-bit BGRA, top-down, no padding.
internal sealed record ThumbnailFrame(int Width, int Height, byte[] Pixels);

// Extracts one frame per video with ffmpeg inside the same AppContainer sandbox as ffprobe.
// ffmpeg reads the validated read-only handle through fd: and writes raw BGRA pixels of a fixed
// size to stdout. The app never parses media or image formats produced by the sandbox: it only
// accepts exactly Width × Height × 4 bytes.
internal sealed class ThumbnailExtractor : IAsyncDisposable
{
    internal const int Width = 320;
    internal const int Height = 180;
    internal const int FrameBytes = Width * Height * 4;
    private readonly string? _ffmpegPath;
    private readonly TimeSpan _timeout;
    private readonly SemaphoreSlim _sandboxLock = new(1, 1);
    private WindowsProbeSandbox? _sandbox;
    private bool _disposed;

    internal ThumbnailExtractor(string? ffmpegPath = null, TimeSpan? timeout = null)
    {
        _ffmpegPath = ffmpegPath ?? ResolveFfmpegPath();
        _timeout = timeout ?? TimeSpan.FromSeconds(15);
    }

    internal bool IsAvailable => !string.IsNullOrWhiteSpace(_ffmpegPath) && File.Exists(_ffmpegPath);

    internal IReadOnlyList<string> CleanupWarnings => _sandbox?.CleanupWarnings ?? [];

    // ffmpeg ships beside ffprobe in tools\. FFMPEG_PATH overrides it (tests, development);
    // otherwise an FFPROBE_PATH override implies a sibling ffmpeg.exe.
    internal static string? ResolveFfmpegPath()
    {
        if (Environment.GetEnvironmentVariable("FFMPEG_PATH") is { Length: > 0 } explicitPath) return explicitPath;
        if (Environment.GetEnvironmentVariable("FFPROBE_PATH") is { Length: > 0 } probe)
            return Path.Combine(Path.GetDirectoryName(Path.GetFullPath(probe)) ?? string.Empty, "ffmpeg.exe");
        return Path.Combine(AppContext.BaseDirectory, "tools", "ffmpeg.exe");
    }

    // A point a little into the clip: past black intro frames, but inside short clips.
    internal static TimeSpan SeekPosition(TimeSpan? duration) => duration is not { } length || length <= TimeSpan.FromSeconds(2)
        ? TimeSpan.Zero
        : TimeSpan.FromMilliseconds(Math.Min(length.TotalMilliseconds * 0.1, 5000));

    internal static IReadOnlyList<string> Arguments(TimeSpan seek) =>
    [
        "-v", "error", "-nostdin",
        "-ss", seek.TotalSeconds.ToString("0.###", CultureInfo.InvariantCulture),
        "-protocol_whitelist", "fd", "-i", "fd:",
        "-map", "0:v:0", "-an", "-sn", "-dn", "-frames:v", "1",
        "-vf", $"scale={Width}:{Height}:force_original_aspect_ratio=increase,crop={Width}:{Height},format=bgra",
        "-f", "rawvideo", "pipe:1"
    ];

    // True when the file still matches its scan snapshot. Cached frames are shown only then,
    // so an edited or replaced file never shows an old frame.
    internal static bool IsUnchanged(VideoCandidate file)
    {
        try
        {
            using var source = OpenValidated(file);
            return true;
        }
        catch (Exception) { return false; }
    }

    // Opened without write sharing: no other program can write the file while the frame is decoded,
    // and a file another program is still writing (a recording in progress) gets no thumbnail.
    private static FileStream OpenValidated(VideoCandidate file)
    {
        using var directories = FileSnapshot.PinDirectories(Path.GetDirectoryName(file.FullPath)!);
        var source = WindowsProbeSandbox.OpenReadOnlyMedia(file.FullPath, allowWriters: false, allowDelete: true);
        try
        {
            // Only the file Review shows: a replaced or edited file gets no frame.
            FileSnapshot.ValidateReadHandle(source.SafeFileHandle, file);
            return source;
        }
        catch { source.Dispose(); throw; }
    }

    // Null when the frame cannot be produced (missing tool, changed or busy file, decode error,
    // timeout). Throws only OperationCanceledException.
    internal async Task<ThumbnailFrame?> ExtractAsync(VideoCandidate file, TimeSpan? duration, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!IsAvailable || _disposed) return null;
        FileStream source;
        try { source = OpenValidated(file); }
        catch (Exception) { return null; }
        using var input = source;
        var sandbox = await GetSandboxAsync(cancellationToken).ConfigureAwait(false);
        if (sandbox is null) return null;

        SandboxedProcess? isolated = null;
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        try
        {
            isolated = sandbox.Start(Arguments(SeekPosition(duration)), input.SafeFileHandle);
            timeout.CancelAfter(_timeout);
            var pixels = ReadExactAsync(isolated.StandardOutput.BaseStream, FrameBytes, timeout);
            var errors = DrainAsync(isolated.StandardError, timeout.Token);
            await Task.WhenAll(isolated.Process.WaitForExitAsync(timeout.Token), pixels, errors).ConfigureAwait(false);
            var frame = await pixels.ConfigureAwait(false);
            return isolated.Process.ExitCode == 0 && frame is not null ? new ThumbnailFrame(Width, Height, frame) : null;
        }
        catch (OperationCanceledException)
        {
            if (isolated is not null) await StopAsync(isolated.Process).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            return null;
        }
        catch (Exception)
        {
            timeout.Cancel();
            if (isolated is not null) await StopAsync(isolated.Process).ConfigureAwait(false);
            return null;
        }
        finally { isolated?.Dispose(); }
    }

    // Exactly `length` bytes followed by end of stream, or null.
    private static async Task<byte[]?> ReadExactAsync(Stream stream, int length, CancellationTokenSource cancellation)
    {
        var buffer = new byte[length];
        var read = 0;
        while (read < length)
        {
            var count = await stream.ReadAsync(buffer.AsMemory(read), cancellation.Token).ConfigureAwait(false);
            if (count == 0) return null;
            read += count;
        }
        var extra = new byte[1];
        if (await stream.ReadAsync(extra, cancellation.Token).ConfigureAwait(false) != 0)
        {
            cancellation.Cancel();
            throw new InvalidDataException("Thumbnail output exceeded the expected frame size.");
        }
        return buffer;
    }

    private static async Task DrainAsync(StreamReader reader, CancellationToken token)
    {
        var buffer = new char[4096];
        var total = 0;
        while (await reader.ReadAsync(buffer.AsMemory(), token).ConfigureAwait(false) is var count and > 0)
            if ((total += count) > 64 * 1024) throw new InvalidDataException("Thumbnail diagnostics exceeded the safety limit.");
    }

    private static async Task StopAsync(Process process)
    {
        try
        {
            if (!process.HasExited) process.Kill(true);
            await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception or TimeoutException) { }
    }

    // One sandbox is reused for a run of thumbnails and released by ReleaseSandboxAsync.
    private async Task<WindowsProbeSandbox?> GetSandboxAsync(CancellationToken cancellationToken)
    {
        await _sandboxLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_disposed) return null;
            if (_sandbox is null)
            {
                try { _sandbox = await Task.Run(() => new WindowsProbeSandbox(_ffmpegPath!, cancellationToken), cancellationToken).ConfigureAwait(false); }
                catch (OperationCanceledException) { throw; }
                catch (Exception) { return null; }
            }
            return _sandbox;
        }
        finally { _sandboxLock.Release(); }
    }

    // Removes the staged tools and AppContainer profile; the next extraction creates a new one.
    internal async Task ReleaseSandboxAsync()
    {
        await _sandboxLock.WaitAsync().ConfigureAwait(false);
        try
        {
            if (_sandbox is { } sandbox)
            {
                _sandbox = null;
                await Task.Run(sandbox.Dispose).ConfigureAwait(false);
            }
        }
        finally { _sandboxLock.Release(); }
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;
        _disposed = true;
        await ReleaseSandboxAsync().ConfigureAwait(false);
    }
}
