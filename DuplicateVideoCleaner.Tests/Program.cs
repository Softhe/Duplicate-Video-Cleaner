using DuplicateVideoCleaner;

if (MetadataRegressionTests.TryRunFixture(args, out var fixtureExit)) return fixtureExit;
if (args.Length > 0 && args[0] == "--measure-performance") return await PerformanceMeasurements.RunAsync(args);

var tests = new List<(string Name, Func<Task> Run)>
{
    ("filename timestamp", Sync(FilenameTimestamp)),
    ("creation time fallback", Sync(CreationTimeFallback)),
    ("exact threshold and outside", Sync(ExactThresholdAndOutside)),
    ("chained grouping", Sync(ChainedGrouping)),
    ("oldest and newest", Sync(OldestAndNewest)),
    ("extensions and recursion", Sync(ExtensionsAndRecursion)),
    ("settings defaults and corruption", Sync(SettingsDefaultsAndCorruption)),
    ("settings validation and persistence", Sync(SettingsValidationAndPersistence)),
    ("concurrent settings saves remain readable", ConcurrentSettingsSaves),
    ("metadata parsing", Sync(MetadataParsing)),
    ("long media duration display", Sync(LongMediaDuration)),
    ("confidence levels", Sync(ConfidenceLevels)),
    ("metadata cache invalidation key", Sync(CacheInvalidationKey)),
    ("dry run CSV and text", Sync(DryRunOutputs)),
    ("dry run destination failure", Sync(DryRunDestinationFailure)),
    ("dry run repeated exports preserve reports", Sync(DryRunRepeatedExports)),
    ("partial removal integration", PartialRemovalIntegration)
};
tests.AddRange(RemovalRegressionTests.Cases);
tests.Add(("scanner cancellation and snapshots", Sync(ScannerRegressionTests.CancellationAndSnapshots)));
tests.Add(("metadata real process cancellation and timeout", MetadataRegressionTests.ProcessesAndCancellation));

var failed = 0;
foreach (var test in tests)
{
    try { await test.Run(); Console.WriteLine($"PASS {test.Name}"); }
    catch (Exception ex) { failed++; Console.Error.WriteLine($"FAIL {test.Name}: {ex.Message}"); }
}
Console.WriteLine($"{tests.Count - failed}/{tests.Count} tests passed");
return failed == 0 ? 0 : 1;

static void FilenameTimestamp()
{
    var result = DuplicateScanner.GetEffectiveTimestamp("clip 2025-04-29 21-57-03.mp4", new DateTime(2000, 1, 1));
    Equal(new DateTime(2025, 4, 29, 21, 57, 3), result.Timestamp);
    Equal(TimestampSource.FileName, result.Source);
}

static void CreationTimeFallback()
{
    var fallback = new DateTime(2024, 3, 2, 1, 2, 3);
    var result = DuplicateScanner.GetEffectiveTimestamp("clip 2025-99-99 99-99-99.mkv", fallback);
    Equal(fallback, result.Timestamp);
    Equal(TimestampSource.CreationTime, result.Source);
}

static void ExactThresholdAndOutside() => WithFolder(root =>
{
    Touch(root, "2025-01-01 10-00-00 a.mp4");
    Touch(root, "2025-01-01 10-01-00 b.mkv");
    Touch(root, "2025-01-01 10-02-01 c.mp4");
    var groups = DuplicateScanner.Scan(root, 60, false, KeepMode.Oldest);
    Equal(1, groups.Count);
    Equal(2, groups[0].Files.Count);
});

static void ChainedGrouping() => WithFolder(root =>
{
    Touch(root, "2025-01-01 10-00-00 a.mp4");
    Touch(root, "2025-01-01 10-00-50 b.mp4");
    Touch(root, "2025-01-01 10-01-40 c.mp4");
    var groups = DuplicateScanner.Scan(root, 60, false, KeepMode.Oldest);
    Equal(3, groups.Single().Files.Count);
});

static void OldestAndNewest() => WithFolder(root =>
{
    Touch(root, "2025-01-01 10-00-00 old.mp4");
    Touch(root, "2025-01-01 10-00-05 new.mp4");
    var oldest = DuplicateScanner.Scan(root, 10, false, KeepMode.Oldest).Single();
    var newest = DuplicateScanner.Scan(root, 10, false, KeepMode.Newest).Single();
    Assert(oldest.SuggestedKeep.RelativePath.Contains("old"), "Oldest was not kept.");
    Assert(newest.SuggestedKeep.RelativePath.Contains("new"), "Newest was not kept.");
});

static void ExtensionsAndRecursion() => WithFolder(root =>
{
    Touch(root, "2025-01-01 10-00-00 a.MP4");
    Touch(root, "2025-01-01 10-00-01 ignored.avi");
    var sub = Directory.CreateDirectory(Path.Combine(root, "sub")).FullName;
    Touch(sub, "2025-01-01 10-00-02 b.MkV");
    Equal(0, DuplicateScanner.Scan(root, 10, false, KeepMode.Oldest).Count);
    Equal(1, DuplicateScanner.Scan(root, 10, true, KeepMode.Oldest).Count);
});

static void SettingsDefaultsAndCorruption() => WithFolder(root =>
{
    var path = Path.Combine(root, "settings.json");
    File.WriteAllText(path, "not-json");
    var settings = new SettingsService(path).Load();
    Equal(60, settings.ThresholdSeconds);
    Equal(KeepMode.Oldest, settings.KeepMode);
});

static void SettingsValidationAndPersistence() => WithFolder(root =>
{
    var path = Path.Combine(root, "settings.json");
    var service = new SettingsService(path);
    service.Save(new AppSettings { LastFolder = "X", ThresholdSeconds = 0, WindowWidth = 1, WindowHeight = 1, KeepMode = KeepMode.Newest });
    var loaded = service.Load();
    Equal(1, loaded.ThresholdSeconds);
    Equal(920, loaded.WindowWidth);
    Equal(600, loaded.WindowHeight);
    Equal(KeepMode.Newest, loaded.KeepMode);
    Assert(!File.Exists(path + ".tmp"), "Atomic settings temporary file remained.");
});

static void MetadataParsing()
{
    var metadata = MetadataProbe.Parse("""{"streams":[{"width":1920,"height":1080}],"format":{"duration":"12.345"}}""");
    Equal(MetadataProbeStatus.Available, metadata.Status);
    Equal(1920, metadata.Width!.Value);
    Equal(1080, metadata.Height!.Value);
    Equal(12345L, ConfidenceEvaluator.NormalizeMilliseconds(metadata.Duration!.Value));
    Equal(MetadataProbeStatus.Failed, MetadataProbe.Parse("{}").Status);
}

static void LongMediaDuration()
{
    var metadata = new MediaMetadata(TimeSpan.FromHours(25) + TimeSpan.FromMilliseconds(123), 640, 480, MetadataProbeStatus.Available);
    Equal("25:00:00.123", metadata.DurationText);
    Equal("Unavailable", new MediaMetadata(null, null, null, MetadataProbeStatus.Unavailable).DurationText);
}

static async Task ConcurrentSettingsSaves()
{
    var root = Path.Combine(Path.GetTempPath(), "dvc-settings-" + Guid.NewGuid().ToString("N"));
    Directory.CreateDirectory(root);
    try
    {
        var path = Path.Combine(root, "settings.json");
        await Task.WhenAll(Enumerable.Range(1, 20).Select(index => Task.Run(() =>
            new SettingsService(path).Save(new AppSettings { ThresholdSeconds = index }))));
        using var document = System.Text.Json.JsonDocument.Parse(File.ReadAllText(path));
        Assert(document.RootElement.GetProperty("ThresholdSeconds").GetInt32() is >= 1 and <= 20, "Concurrent saves corrupted settings.");
        Assert(!Directory.EnumerateFiles(root, "*.tmp").Any(), "Concurrent save left temporary files.");
    }
    finally { Directory.Delete(root, true); }
}

static void ConfidenceLevels()
{
    var root = "C:\\test";
    var reference = Candidate(Path.Combine(root, "a.mp4"), 100);
    var same = Candidate(Path.Combine(root, "b.mp4"), 100);
    var differentSize = Candidate(Path.Combine(root, "c.mp4"), 101);
    var exact = new MediaMetadata(TimeSpan.FromSeconds(5), 1920, 1080, MetadataProbeStatus.Available);
    var otherResolution = new MediaMetadata(TimeSpan.FromSeconds(5), 1280, 720, MetadataProbeStatus.Available);
    Equal(DuplicateConfidence.High, ConfidenceEvaluator.Evaluate(same, exact, reference, exact));
    Equal(DuplicateConfidence.Medium, ConfidenceEvaluator.Evaluate(differentSize, exact, reference, exact));
    Equal(DuplicateConfidence.Low, ConfidenceEvaluator.Evaluate(differentSize, otherResolution, reference, exact));
    Equal(DuplicateConfidence.Low, ConfidenceEvaluator.Evaluate(same, new MediaMetadata(null, null, null, MetadataProbeStatus.Unavailable), reference, exact));
}

static void CacheInvalidationKey() => WithFolder(root =>
{
    var path = Path.Combine(root, "video.mp4");
    File.WriteAllBytes(path, [1]);
    var first = MetadataProbe.CreateCacheKey(new FileInfo(path));
    File.AppendAllText(path, "changed");
    File.SetLastWriteTimeUtc(path, DateTime.UtcNow.AddSeconds(1));
    var second = MetadataProbe.CreateCacheKey(new FileInfo(path));
    Assert(first != second, "Cache key did not change after file modification.");
});

static void DryRunOutputs() => WithFolder(root =>
{
    var candidate = Candidate(Path.Combine(root, "a,\"quoted\".mp4"), 123);
    var reviewed = new ReviewedVideo(candidate, new MediaMetadata(TimeSpan.FromSeconds(1), 640, 480, MetadataProbeStatus.Available), DuplicateConfidence.High, false);
    var context = new DryRunContext(root, 60, KeepMode.Oldest, false, [new DryRunRow(1, reviewed, true)]);
    var csv = DryRunExporter.BuildCsv(context);
    Assert(csv.Contains("\"" + candidate.FullPath.Replace("\"", "\"\"") + "\""), "CSV path was not escaped.");
    Assert(DryRunExporter.BuildText(context).Contains("selected size: 123 B"), "Text summary is incomplete.");
    var result = DryRunExporter.Export(root, context, new DateTimeOffset(2026, 8, 2, 1, 2, 3, TimeSpan.Zero));
    Assert(result.Errors.Count == 0 && File.Exists(result.CsvPath) && File.Exists(result.TextPath), "Export files were not created.");
    Assert(!Directory.EnumerateFiles(root, "*.tmp").Any(), "Atomic export temporary file remained.");
});

static void DryRunDestinationFailure() => WithFolder(root =>
{
    var destination = Path.Combine(root, "not-a-folder");
    File.WriteAllText(destination, "occupied");
    var result = DryRunExporter.Export(destination, new DryRunContext(root, 60, KeepMode.Oldest, false, []));
    Assert(result.Errors.Count > 0 && result.CsvPath is null && result.TextPath is null,
        "An invalid destination must return an export error without throwing.");
});

static void DryRunRepeatedExports() => WithFolder(root =>
{
    var context = new DryRunContext(root, 60, KeepMode.Oldest, false, []);
    var now = new DateTimeOffset(2026, 1, 1, 1, 1, 1, TimeSpan.Zero);
    var first = DryRunExporter.Export(root, context, now);
    var second = DryRunExporter.Export(root, context, now);
    Assert(first.Errors.Count == 0 && second.Errors.Count == 0, "Repeated export failed.");
    Assert(first.CsvPath != second.CsvPath && first.TextPath != second.TextPath, "Repeated export overwrote a prior report.");
    Assert(File.Exists(first.CsvPath) && File.Exists(first.TextPath), "Earlier reports disappeared.");
});

static async Task PartialRemovalIntegration()
{
    var root = Path.Combine(Path.GetTempPath(), "DuplicateVideoCleanerRemovalTests", Guid.NewGuid().ToString("N"));
    Directory.CreateDirectory(root);
    try
    {
        var existingPath = Path.Combine(root, "existing.mp4");
        File.WriteAllBytes(existingPath, [1, 2, 3]);
        var missingPath = Path.Combine(root, "missing.mp4");
        File.WriteAllBytes(Path.Combine(root, "other.mp4"), [1, 2, 3]);
        var scanned = DuplicateScanner.Scan(root, 60, false, KeepMode.Oldest).Single().Files.Single(file => file.FullPath == existingPath);
        var result = await FileRemovalService.RemoveAsync([scanned, Candidate(missingPath, 3)], true, Path.Combine(root, "log.txt"));
        Equal(1, result.RemovedPaths.Count);
        Equal(1, result.Failures.Count);
        Assert(!File.Exists(existingPath), "Existing fixture was not removed.");
    }
    finally { Directory.Delete(root, true); }
}

static void Touch(string root, string name) => File.WriteAllBytes(Path.Combine(root, name), [1, 2, 3]);
static VideoCandidate Candidate(string path, long size) => new(path, Path.GetFileName(path), new DateTime(2026, 1, 1), TimestampSource.FileName, size);
static Func<Task> Sync(Action action) => () => { action(); return Task.CompletedTask; };
static void WithFolder(Action<string> test)
{
    var root = Path.Combine(Path.GetTempPath(), "DuplicateVideoCleanerTests", Guid.NewGuid().ToString("N"));
    Directory.CreateDirectory(root);
    try { test(root); }
    finally { Directory.Delete(root, true); }
}
static void Equal<T>(T expected, T actual) where T : notnull => Assert(EqualityComparer<T>.Default.Equals(expected, actual), $"Expected {expected}, got {actual}.");
static void Assert(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
