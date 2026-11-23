using DuplicateVideoCleaner;

// Runs against extra volumes named in DVC_TEST_VOLUMES (for example "V:\;W:\" for
// FAT32 and exFAT test disks). Without the variable the checks are reported as skipped.
internal static class VolumeRegressionTests
{
    internal static IReadOnlyList<string> Volumes =>
        (Environment.GetEnvironmentVariable("DVC_TEST_VOLUMES") ?? string.Empty)
        .Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    internal static async Task RecoveryRoundTripOnTestVolumes()
    {
        if (Volumes.Count == 0) { Console.WriteLine("SKIP recovery on extra volumes: DVC_TEST_VOLUMES is not set"); return; }
        foreach (var volume in Volumes)
        {
            var fileSystem = new DriveInfo(volume).DriveFormat;
            var root = Path.Combine(volume, "dvc-volume-" + Guid.NewGuid().ToString("N"));
            var nested = Directory.CreateDirectory(Path.Combine(root, "nested")).FullName;
            try
            {
                File.WriteAllText(Path.Combine(nested, "2026-01-01 10-00-00 keep.mp4"), "keep");
                File.WriteAllText(Path.Combine(nested, "2026-01-01 10-00-01 remove.mp4"), "remove");
                File.WriteAllText(Path.Combine(nested, "2026-01-01 10-00-02 purge.mp4"), "purge");
                var group = DuplicateScanner.Scan(root, 60, true, KeepMode.Oldest).Single();
                var remove = group.Files.Single(file => file.FullPath.EndsWith("remove.mp4"));
                var purge = group.Files.Single(file => file.FullPath.EndsWith("purge.mp4"));
                var result = await FileRemovalService.RemoveAsync([remove, purge], false, Path.Combine(root, "DuplicateCleanupLog.txt"));
                Check(result.Failures.Count == 0 && !File.Exists(remove.FullPath) && !File.Exists(purge.FullPath),
                    $"{fileSystem}: move to Recovery failed: {string.Join("; ", result.Failures.Select(f => f.Error))}");
                var entries = RecoveryStore.List(root, message => throw new Exception($"{fileSystem}: {message}"));
                Check(entries.Count == 2, $"{fileSystem}: Recovery listed {entries.Count} entries.");
                var restoreWarning = RecoveryStore.Restore(entries.Single(entry => entry.OriginalPath == remove.FullPath));
                Check(restoreWarning is null && File.ReadAllText(remove.FullPath) == "remove", $"{fileSystem}: restore failed. {restoreWarning}");
                var purgeWarning = RecoveryStore.Purge(entries.Single(entry => entry.OriginalPath == purge.FullPath));
                Check(purgeWarning is null && RecoveryStore.List(root).Count == 0, $"{fileSystem}: purge failed. {purgeWarning}");
                // A different payload with the same name, size and exact timestamps must still be
                // rejected by restore and purge: the identity recorded after the move must match.
                var again = DuplicateScanner.Scan(root, 60, true, KeepMode.Oldest).Single().Files.Single(file => file.FullPath == remove.FullPath);
                RecoveryStore.MoveToRecovery(again);
                var stored = RecoveryStore.List(root).Single();
                var replacement = Path.Combine(Path.GetDirectoryName(stored.StoredPath)!, "replacement.tmp");
                File.WriteAllText(replacement, "REMOVE");
                File.Delete(stored.StoredPath);
                File.Move(replacement, stored.StoredPath);
                File.SetCreationTimeUtc(stored.StoredPath, stored.CreationTimeUtc);
                File.SetLastWriteTimeUtc(stored.StoredPath, stored.LastWriteTimeUtc);
                Check(new FileInfo(stored.StoredPath).Length == stored.Size && File.GetCreationTimeUtc(stored.StoredPath) == stored.CreationTimeUtc &&
                      File.GetLastWriteTimeUtc(stored.StoredPath) == stored.LastWriteTimeUtc, $"{fileSystem}: the replacement fixture differs in size or time.");
                try { RecoveryStore.Restore(stored); throw new Exception($"{fileSystem}: a substituted payload was restored."); }
                catch (IOException ex) when (ex.Message.Contains("changed")) { }
                try { RecoveryStore.Purge(stored); throw new Exception($"{fileSystem}: a substituted payload was purged."); }
                catch (IOException ex) when (ex.Message.Contains("changed")) { }
                Check(File.Exists(stored.StoredPath) && !File.Exists(remove.FullPath), $"{fileSystem}: a rejected payload was moved or deleted.");
                Console.WriteLine($"PASS recovery round trip on {fileSystem} ({volume})");
            }
            finally { Directory.Delete(root, true); }
        }
    }

    // Uses the real ffprobe named in DVC_TEST_FFPROBE. The checked-in one-second MP4 has
    // its index (moov) after the media data; padding the media data to 300 MiB makes the
    // parser seek across the whole file through its read-only handle.
    internal static async Task RealProbeSeeksLargeFilesWithoutCopying()
    {
        var ffprobe = Environment.GetEnvironmentVariable("DVC_TEST_FFPROBE");
        if (string.IsNullOrWhiteSpace(ffprobe)) { Console.WriteLine("SKIP real ffprobe: DVC_TEST_FFPROBE is not set"); return; }
        var fixture = Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "DuplicateVideoCleaner.UiTests", "Fixtures", "one-second.mp4");
        var bytes = File.ReadAllBytes(fixture);
        var mdat = System.Text.Encoding.ASCII.GetString(bytes).IndexOf("mdat", StringComparison.Ordinal) - 4;
        var size = System.Buffers.Binary.BinaryPrimitives.ReadInt32BigEndian(bytes.AsSpan(mdat));
        Check(mdat > 0 && System.Text.Encoding.ASCII.GetString(bytes).IndexOf("moov", StringComparison.Ordinal) > mdat + size - 8,
            "The fixture no longer has its index after the media data.");
        var root = Path.Combine(Path.GetTempPath(), "dvc-real-probe-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            const int padding = 300 * 1024 * 1024;
            var path = Path.Combine(root, "2026-01-01 10-00-00 large.mp4");
            using (var file = new FileStream(path, FileMode.CreateNew, FileAccess.Write))
            {
                file.Write(bytes, 0, mdat);
                var header = new byte[4];
                System.Buffers.Binary.BinaryPrimitives.WriteInt32BigEndian(header, size + padding);
                file.Write(header);
                file.Write(bytes, mdat + 4, size - 4);
                file.SetLength(file.Length + padding);
                file.Position = file.Length;
                file.Write(bytes, mdat + size, bytes.Length - mdat - size);
            }
            VideoCandidate snapshot;
            using (var source = WindowsProbeSandbox.OpenReadOnlyMedia(path)) snapshot = FileSnapshot.CaptureReadHandle(source.SafeFileHandle, path);
            var probe = new MetadataProbe(ffprobe);
            var clock = System.Diagnostics.Stopwatch.StartNew();
            var result = (await probe.ProbeManyAsync([snapshot]))[path];
            clock.Stop();
            Check(result is { Status: MetadataProbeStatus.Available, Width: 64, Height: 48 } && result.Duration == TimeSpan.FromSeconds(1),
                $"Real ffprobe did not read the large file through its handle: {result.Status} {result.Error}");
            Console.WriteLine($"PASS real sandboxed ffprobe read a 300 MiB moov-at-end file in {clock.Elapsed.TotalMilliseconds:F0} ms (setup {probe.LastBatchMetrics!.Setup.TotalMilliseconds:F0} ms, parse {probe.LastBatchMetrics.Parsing.TotalMilliseconds:F0} ms)");
        }
        finally { Directory.Delete(root, true); }
    }

    private static void Check(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}
