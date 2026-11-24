using DuplicateVideoCleaner;

// Thumbnail extraction through the sandbox (with this test executable standing in for ffmpeg)
// and the on-disk thumbnail cache.
internal static class ThumbnailTests
{
    internal static IEnumerable<(string Name, Func<Task> Run)> Cases =>
    [
        ("thumbnail extraction returns one exact raw frame from the sandbox", ExtractsExactFrame),
        ("thumbnail extraction rejects short, oversized and stalled output", RejectsBadOutput),
        ("thumbnail extraction skips a file changed since the scan", SkipsChangedFile),
        ("thumbnail extraction honours cancellation", HonoursCancellation),
        ("thumbnail extraction in progress does not block removal", ExtractionDoesNotBlockRemoval),
        ("thumbnail extraction without ffmpeg returns nothing", MissingTool),
        ("thumbnail seek position stays inside short clips", SeekPosition),
        ("thumbnail cache round trip, invalidation and trimming", CacheRoundTrip),
    ];

    private static string Fixture => Path.Combine(AppContext.BaseDirectory, "DuplicateVideoCleaner.Tests.exe");

    private static async Task ExtractsExactFrame() => await WithFilesAsync(async (root, file) =>
    {
        await using var extractor = new ThumbnailExtractor(Fixture);
        var frame = await extractor.ExtractAsync(file, TimeSpan.FromSeconds(30), CancellationToken.None);
        True(frame is not null, "The fixture frame was not returned.");
        Equal(ThumbnailExtractor.Width, frame!.Width);
        Equal(ThumbnailExtractor.FrameBytes, frame.Pixels.Length);
        True(frame.Pixels.All(value => value == 0x7F), "The frame bytes were altered.");
        var second = await extractor.ExtractAsync(file, null, CancellationToken.None);
        True(second is not null, "The reused sandbox did not produce a second frame.");
    }, "ordinary media");

    private static async Task RejectsBadOutput()
    {
        foreach (var content in new[] { "short media", "flood media", "wait media" })
            await WithFilesAsync(async (root, file) =>
            {
                await using var extractor = new ThumbnailExtractor(Fixture, TimeSpan.FromSeconds(2));
                var frame = await extractor.ExtractAsync(file, null, CancellationToken.None);
                True(frame is null, $"'{content}' should not produce a thumbnail.");
            }, content);
    }

    private static async Task SkipsChangedFile() => await WithFilesAsync(async (root, file) =>
    {
        File.AppendAllText(file.FullPath, " edited after the scan");
        await using var extractor = new ThumbnailExtractor(Fixture);
        True(await extractor.ExtractAsync(file, null, CancellationToken.None) is null, "A changed file must not get a thumbnail.");
    }, "ordinary media");

    private static async Task HonoursCancellation() => await WithFilesAsync(async (root, file) =>
    {
        await using var extractor = new ThumbnailExtractor(Fixture);
        using var cancellation = new CancellationTokenSource(TimeSpan.FromMilliseconds(500));
        try
        {
            await extractor.ExtractAsync(file, null, cancellation.Token);
            throw new InvalidOperationException("Cancellation was ignored.");
        }
        catch (OperationCanceledException) { }
    }, "wait media");

    // A frame being decoded holds the file open; moving it to Recovery or deleting it must still work.
    private static async Task ExtractionDoesNotBlockRemoval()
    {
        foreach (var permanent in new[] { false, true })
            await WithFilesAsync(async (root, file) =>
            {
                await using var extractor = new ThumbnailExtractor(Fixture, TimeSpan.FromSeconds(20));
                using var cancellation = new CancellationTokenSource();
                var extraction = extractor.ExtractAsync(file, null, cancellation.Token);
                var deadline = DateTime.UtcNow.AddSeconds(10);
                while (!IsOpenElsewhere(file.FullPath) && DateTime.UtcNow < deadline) await Task.Delay(50);
                True(IsOpenElsewhere(file.FullPath), "The extraction did not hold the file open.");
                var result = await FileRemovalService.RemoveAsync([file], permanent, Path.Combine(root, "DuplicateCleanupLog.txt"));
                True(result.Failures.Count == 0, $"Removal failed during extraction: {string.Join("; ", result.Failures.Select(failure => failure.Error))}");
                // A permanent delete stays pending until the extraction's handle closes; the app
                // cancels in-flight extraction when a removal starts.
                cancellation.Cancel();
                try { await extraction; } catch (OperationCanceledException) { }
                True(!File.Exists(file.FullPath), $"The file was not removed ({(permanent ? "permanent" : "Recovery")}).");
            }, "wait media");
    }

    // True while another handle denies exclusive access to the file.
    private static bool IsOpenElsewhere(string path)
    {
        try { using var exclusive = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.None); return false; }
        catch (IOException) { return true; }
    }

    private static async Task MissingTool() => await WithFilesAsync(async (root, file) =>
    {
        await using var extractor = new ThumbnailExtractor(Path.Combine(root, "missing-ffmpeg.exe"));
        True(!extractor.IsAvailable, "A missing ffmpeg must be reported as unavailable.");
        True(await extractor.ExtractAsync(file, null, CancellationToken.None) is null, "No thumbnail without ffmpeg.");
    }, "ordinary media");

    private static Task SeekPosition()
    {
        Equal(TimeSpan.Zero, ThumbnailExtractor.SeekPosition(null));
        Equal(TimeSpan.Zero, ThumbnailExtractor.SeekPosition(TimeSpan.FromSeconds(1)));
        Equal(TimeSpan.FromSeconds(1), ThumbnailExtractor.SeekPosition(TimeSpan.FromSeconds(10)));
        Equal(TimeSpan.FromSeconds(5), ThumbnailExtractor.SeekPosition(TimeSpan.FromHours(2)));
        var arguments = ThumbnailExtractor.Arguments(TimeSpan.FromSeconds(1.5));
        True(arguments.Contains("1.5"), "The seek position must be invariant-culture seconds.");
        True(arguments[^1] == "pipe:1" && arguments.Contains("fd:"), "Media must come from the handle and the frame go to stdout.");
        return Task.CompletedTask;
    }

    private static Task CacheRoundTrip() => WithFilesAsync((root, file) =>
    {
        var cache = new ThumbnailCache(Path.Combine(root, "cache"), maxBytes: 3000);
        var key = ThumbnailCache.KeyFor(file);
        True(ThumbnailCache.CanCache(file), "A scanned file has a full snapshot.");
        True(cache.TryGet(key) is null, "An empty cache returned a frame.");
        var pixels = new byte[ThumbnailExtractor.FrameBytes];
        for (var index = 0; index < pixels.Length; index++) pixels[index] = (byte)(index % 251);
        cache.Store(key, new ThumbnailFrame(ThumbnailExtractor.Width, ThumbnailExtractor.Height, pixels));
        var loaded = cache.TryGet(key);
        True(loaded is not null && loaded.Pixels.AsSpan().SequenceEqual(pixels), "The cached frame changed.");

        var edited = file with { Size = file.Size + 1 };
        True(ThumbnailCache.KeyFor(edited) != key, "A changed file must not reuse a cached frame.");
        True(ThumbnailCache.KeyFor(file with { LastWriteTimeUtc = file.LastWriteTimeUtc!.Value.AddSeconds(1) }) != key, "A rewritten file must not reuse a cached frame.");

        var entry = Directory.GetFiles(Path.Combine(root, "cache"), "*.thumb").Single();
        File.WriteAllBytes(entry, [1, 2, 3]);
        True(cache.TryGet(key) is null, "A damaged entry must be ignored.");

        for (var index = 0; index < 5; index++)
            cache.Store($"KEY{index}", new ThumbnailFrame(ThumbnailExtractor.Width, ThumbnailExtractor.Height, pixels));
        cache.Trim();
        var total = Directory.GetFiles(Path.Combine(root, "cache"), "*.thumb").Sum(path => new FileInfo(path).Length);
        True(total <= 3000, $"The cache exceeded its size limit: {total} bytes.");
        return Task.CompletedTask;
    }, "ordinary media");

    private static async Task WithFilesAsync(Func<string, VideoCandidate, Task> test, string content)
    {
        var root = Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), "dvc-thumbnail-" + Guid.NewGuid().ToString("N"))).FullName;
        try
        {
            File.WriteAllText(Path.Combine(root, "2026-01-01 10-00-00 first.mp4"), content);
            File.WriteAllText(Path.Combine(root, "2026-01-01 10-00-01 second.mp4"), content + " copy");
            var file = DuplicateScanner.Scan(root, 60, false, KeepMode.Oldest).Single().Files[0];
            await test(root, file);
        }
        finally { try { Directory.Delete(root, true); } catch { } }
    }

    private static void True(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
    private static void Equal<T>(T expected, T actual) => True(EqualityComparer<T>.Default.Equals(expected, actual), $"Expected {expected}, got {actual}.");
}
