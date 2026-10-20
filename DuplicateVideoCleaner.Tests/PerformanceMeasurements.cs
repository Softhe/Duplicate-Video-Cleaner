using System.Buffers.Binary;
using System.Diagnostics;
using System.Text.Json;
using DuplicateVideoCleaner;

internal static class PerformanceMeasurements
{
    internal static async Task<int> RunAsync(string[] args)
    {
        if (args.Length != 4) throw new ArgumentException("Usage: --measure-performance <ffprobe.exe> <synthetic.mp4> <output.json>");
        var root = Path.Combine(Path.GetTempPath(), "dvc-performance-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            // Valid MP4 free atoms make staging measurable without user recordings.
            const int padding = 8 * 1024 * 1024;
            for (var index = 0; index < 4; index++)
            {
                var path = Path.Combine(root, $"2026-01-01 10-00-{index:00} synthetic.mp4");
                File.Copy(args[2], path);
                using var file = new FileStream(path, FileMode.Append, FileAccess.Write);
                var header = new byte[8];
                BinaryPrimitives.WriteInt32BigEndian(header, padding + header.Length);
                "free"u8.CopyTo(header.AsSpan(4));
                file.Write(header);
                file.SetLength(file.Position + padding);
            }
            var enumeration = Stopwatch.StartNew();
            var candidates = DuplicateScanner.Scan(root, 60, false, KeepMode.Oldest).Single().Files;
            enumeration.Stop();
            var probe = new MetadataProbe(Path.GetFullPath(args[1]));
            var cold = await probe.ProbeManyAsync(candidates);
            if (cold.Values.Any(value => value.Status != MetadataProbeStatus.Available))
                throw new InvalidOperationException("Synthetic native video benchmark failed: " + string.Join("; ", cold.Values.Select(value => value.Error)));
            var coldMetrics = probe.LastBatchMetrics;
            var warm = await probe.ProbeManyAsync(candidates);
            var warmMetrics = probe.LastBatchMetrics;
            if (warm.Values.Any(value => value.Status != MetadataProbeStatus.Available) || warmMetrics is null ||
                warmMetrics.CacheHits != candidates.Count || warmMetrics.Setup != TimeSpan.Zero || warmMetrics.StagedBytes != 0)
                throw new InvalidOperationException("Warm metadata benchmark did not avoid staging and sandbox setup.");

            const int count = 10000, operations = 1000;
            var files = Enumerable.Range(0, count).Select(index => new VideoCandidate(Path.Combine(root, $"selection-{index}.mp4"),
                $"selection-{index}.mp4", DateTime.UnixEpoch.AddSeconds(index), TimestampSource.FileName, index + 1)).ToArray();
            var initial = files.Where((_, index) => index % 2 == 0).Select(file => file.FullPath).ToArray();
            var legacy = new HashSet<string>(initial, StringComparer.OrdinalIgnoreCase);
            long legacyTotal = 0;
            var legacyClock = Stopwatch.StartNew();
            for (var index = 0; index < operations; index++)
            {
                var file = files[index * 7919 % count];
                if (index % 2 == 0) legacy.Add(file.FullPath); else legacy.Remove(file.FullPath);
                legacyTotal = files.Where(file => legacy.Contains(file.FullPath)).Sum(file => file.Size);
            }
            legacyClock.Stop();
            var selection = new HashSet<string>(initial, StringComparer.OrdinalIgnoreCase);
            var session = new ReviewSession([new DuplicateGroup(1, files, files[0])], selection, null,
                new Dictionary<string, ReviewedVideo>(StringComparer.OrdinalIgnoreCase));
            var indexedClock = Stopwatch.StartNew();
            for (var index = 0; index < operations; index++) session.SetSelected(files[index * 7919 % count], index % 2 == 0);
            indexedClock.Stop();
            if (session.SelectedBytes != legacyTotal || session.SelectedCount != legacy.Count)
                throw new InvalidOperationException("Indexed and legacy selection aggregates differ.");
            var record = new
            {
                checkedAtUtc = DateTimeOffset.UtcNow,
                workload = "Synthetic: four 1-second 64x48 videos padded with 8 MiB free atoms; 10,000 in-memory candidates, 1,000 toggles. No user media.",
                runtime = Environment.Version.ToString(),
                enumerationMs = enumeration.Elapsed.TotalMilliseconds,
                cold = coldMetrics,
                warm = warmMetrics,
                selection = new { files = count, operations, legacyMs = legacyClock.Elapsed.TotalMilliseconds,
                    indexedMs = indexedClock.Elapsed.TotalMilliseconds, selectedCount = session.SelectedCount, selectedBytes = session.SelectedBytes }
            };
            var output = Path.GetFullPath(args[3]);
            Directory.CreateDirectory(Path.GetDirectoryName(output)!);
            File.WriteAllText(output, JsonSerializer.Serialize(record, new JsonSerializerOptions { WriteIndented = true }));
            Console.WriteLine($"Synthetic benchmark saved: {output}");
            Console.WriteLine($"Selection totals, legacy {legacyClock.Elapsed.TotalMilliseconds:F2} ms; indexed {indexedClock.Elapsed.TotalMilliseconds:F2} ms.");
            Console.WriteLine($"Metadata cache hits: cold {coldMetrics?.CacheHits}, warm {warmMetrics.CacheHits}; warm staged bytes {warmMetrics.StagedBytes}.");
            return 0;
        }
        finally
        {
            // This exact generated directory contains only this measurement's fixtures.
            Directory.Delete(root, true);
        }
    }
}
