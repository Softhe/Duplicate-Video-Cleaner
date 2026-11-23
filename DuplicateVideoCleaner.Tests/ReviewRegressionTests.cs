using DuplicateVideoCleaner;

// Regressions found in the 1.1.2 review: undated files, protected-group suggestions,
// filename dates, case-variant paths, selection order and directory pinning.
internal static class ReviewRegressionTests
{
    internal static IEnumerable<(string Name, Func<Task> Run)> Cases =>
    [
        ("undated clips are grouped and removable unless protection is on", Sync(UndatedClipsAreRemovableByDefault)),
        ("common camera and recorder filename dates are recognised", Sync(FileNameDateFormats)),
        ("protected clips do not block unrelated originals in their time window", Sync(ProtectedClipDoesNotBlockOriginals)),
        ("protected size matches keep originals in their own time windows", Sync(ProtectedMatchesKeepTimeWindows)),
        ("metadata unavailable keeps timestamp review for originals near protected clips", Sync(MetadataUnavailableKeepsTimestampReview)),
        ("reconciliation keeps per-window keepers in protected groups", Sync(ReconciliationKeepsWindowKeepers)),
        ("case-variant paths are reported instead of failing the scan", Sync(CaseVariantPaths)),
        ("selecting every original of a time window warns despite a protected clip", Sync(WindowWithoutSurvivorWarns)),
        ("selected files follow review group order", Sync(SelectedFilesFollowGroupOrder)),
        ("directory pins block deletion and rename of empty directories", Sync(DirectoryPinsBlockChanges)),
        ("scan and probe accept files another program is writing", Sync(ScanAcceptsActiveWriters))
    ];

    private static void UndatedClipsAreRemovableByDefault() => WithFolder(root =>
    {
        var start = new DateTime(2026, 3, 1, 12, 0, 0, DateTimeKind.Utc);
        foreach (var (name, offset) in new[] { ("GOPR0001.MP4", 0), ("GOPR0002.MP4", 20), ("DJI_0001.MP4", 7200) })
        {
            var path = Path.Combine(root, name);
            File.WriteAllText(path, name);
            File.SetCreationTimeUtc(path, start.AddSeconds(offset));
            File.SetLastWriteTimeUtc(path, start.AddSeconds(offset));
        }
        var group = DuplicateScanner.Scan(root, 60, false, KeepMode.Oldest).Single();
        Check(group.Files.Count == 2 && group.Files.All(file => !file.IsProtected), "Undated clips were protected or not grouped by default.");
        var reviewed = Review(group, new MediaMetadata(null, null, null, MetadataProbeStatus.Unavailable));
        RenamedFilePolicy.Apply(group, reviewed);
        Check(reviewed.Values.Count(video => video.SuggestRemoval) == 1 && reviewed[group.Files[1].FullPath].SuggestRemoval,
            "Default review did not suggest the newer undated duplicate.");
        Check(DuplicateScanner.Scan(root, 60, false, KeepMode.Oldest, protectUndated: true).Count == 0,
            "Opt-in protection did not skip protected-only groups.");
    });

    private static void FileNameDateFormats()
    {
        var expected = new DateTime(2026, 10, 15, 14, 30, 5);
        foreach (var name in new[]
                 {
                     "Replay 2026-10-15 14-30-05.mp4", "2026-10-15_14-30-05.mkv", "clip 2026-10-15 14.30.05.mp4",
                     "Desktop 2026.10.15 - 14.30.05.01.DVR.mp4", "VID_20261015_143005.mp4", "20261015_143005.mp4",
                     "PXL_20261015_143005123.mp4", "IMG-20261015-143005.mp4"
                 })
        {
            var result = DuplicateScanner.GetEffectiveTimestamp(name, new DateTime(2000, 1, 1));
            Check(result.Source == TimestampSource.FileName && result.Timestamp == expected, $"Filename date not recognised: {name} -> {result.Timestamp:O}");
        }
        foreach (var name in new[] { "GOPR0001.MP4", "DJI_0001.MP4", "clip 2026-13-15 14-30-05.mp4", "12345678_123456.mp4", "VID_20260230_120000.mp4", "x20261015_1430059.mp4",
                     // Arabic-Indic and full-width digits fall back to file times instead of failing the scan.
                     "٢٠٢٦-١٠-١٥ ١٤-٣٠-٠٥.mp4",
                     "２０２６１０１５_１４３００５.mp4" })
            Check(DuplicateScanner.GetEffectiveTimestamp(name, new DateTime(2000, 1, 1)).Source != TimestampSource.FileName, $"Invalid filename date accepted: {name}");
    }

    // Two OBS recordings a second apart, plus a renamed clip in the same window that matches neither.
    private static void ProtectedClipDoesNotBlockOriginals()
    {
        var files = new[]
        {
            Candidate("renamed favourite.mp4", 0, 500, isProtected: true),
            Candidate("Replay 2026-01-01 10-00-01.mp4", 1, 100),
            Candidate("Replay 2026-01-01 10-00-02.mp4", 2, 101)
        };
        var group = new DuplicateGroup(1, files, files[0]);
        var reviewed = Review(group, Metadata(10));
        reviewed[files[0].FullPath] = reviewed[files[0].FullPath] with { Metadata = Metadata(99) };
        RenamedFilePolicy.Apply(group, reviewed, KeepMode.Oldest);
        Check(!reviewed[files[0].FullPath].CanRemove, "The protected clip became removable.");
        Check(reviewed[files[1].FullPath].SuggestedKeep && reviewed[files[1].FullPath].CanRemove, "The first original was not the window keeper.");
        Check(reviewed[files[2].FullPath].SuggestRemoval && reviewed[files[2].FullPath].OriginalKeeper == files[1],
            "The second recording was blocked by an unrelated protected clip.");
        Check(reviewed[files[2].FullPath].RetentionText.Contains(files[1].RelativePath), "Retention did not name the kept original.");

        // A lone unmatched original is still kept and cannot be selected.
        var lone = new DuplicateGroup(1, files[..2], files[0]);
        var loneReview = Review(lone, Metadata(10));
        loneReview[files[0].FullPath] = loneReview[files[0].FullPath] with { Metadata = Metadata(99) };
        RenamedFilePolicy.Apply(lone, loneReview);
        Check(!loneReview[files[1].FullPath].CanRemove && !loneReview[files[1].FullPath].SuggestRemoval,
            "A lone unmatched original next to a protected clip became removable.");
    }

    // A protected clip whose size equals files in two different time windows joins them,
    // but originals from one window are never suggested against a keeper in the other.
    private static void ProtectedMatchesKeepTimeWindows() => WithFolder(root =>
    {
        void Write(string name, string content, DateTime? created = null)
        {
            var path = Path.Combine(root, name);
            File.WriteAllText(path, content);
            if (created is { } time) { File.SetCreationTimeUtc(path, time); File.SetLastWriteTimeUtc(path, time); }
        }
        Write("favourite.mp4", "same-size", new DateTime(2020, 6, 1, 0, 0, 0, DateTimeKind.Utc));
        Write("Replay 2026-01-01 10-00-00.mp4", "same-size");
        Write("Replay 2026-01-01 10-00-05.mp4", "other one!");
        Write("Replay 2026-02-01 10-00-00.mp4", "same-size");
        Write("Replay 2026-02-01 10-00-05.mp4", "other two!!");
        var group = DuplicateScanner.Scan(root, 60, false, KeepMode.Oldest, protectUndated: true).Single();
        Check(group.Files.Count == 5 && group.SuggestedKeep.IsProtected, "Size matches across dates were not joined around the protected clip.");
        Check(group.Files.Where(file => !file.IsProtected).Select(file => file.TimeCluster).Distinct().Count() == 2,
            "Originals lost their time windows when groups were joined.");
        var reviewed = Review(group, Metadata(10));
        foreach (var file in group.Files.Where(file => file.Size != group.SuggestedKeep.Size))
            reviewed[file.FullPath] = reviewed[file.FullPath] with { Metadata = Metadata(20) };
        RenamedFilePolicy.Apply(group, reviewed, KeepMode.Oldest);
        VideoCandidate Named(string name) => group.Files.Single(file => file.RelativePath == name);
        Check(reviewed[Named("Replay 2026-01-01 10-00-00.mp4").FullPath].MatchesProtectedCopy &&
              reviewed[Named("Replay 2026-02-01 10-00-00.mp4").FullPath].MatchesProtectedCopy, "Protected copies were not matched.");
        // Each window has one unmatched original left, so both are kept.
        Check(!reviewed[Named("Replay 2026-01-01 10-00-05.mp4").FullPath].CanRemove &&
              !reviewed[Named("Replay 2026-02-01 10-00-05.mp4").FullPath].CanRemove,
            "An unmatched original was offered for removal against a file from another date.");
    });

    private static void MetadataUnavailableKeepsTimestampReview()
    {
        var files = new[]
        {
            Candidate("renamed.mp4", 0, 100, isProtected: true),
            Candidate("Replay 2026-01-01 10-00-01.mp4", 1, 100),
            Candidate("Replay 2026-01-01 10-00-02.mp4", 2, 100)
        };
        var group = new DuplicateGroup(1, files, files[0]);
        var reviewed = Review(group, new MediaMetadata(null, null, null, MetadataProbeStatus.Unavailable, "ffprobe was not found"));
        RenamedFilePolicy.Apply(group, reviewed);
        Check(reviewed.Values.Count(video => video.SuggestRemoval) == 1 && reviewed[files[2].FullPath].SuggestRemoval,
            "Without metadata, originals near a protected clip lost timestamp review.");
    }

    private static void ReconciliationKeepsWindowKeepers()
    {
        var files = new[]
        {
            Candidate("renamed.mp4", 0, 500, isProtected: true),
            Candidate("Replay 2026-01-01 10-00-01.mp4", 1, 100),
            Candidate("Replay 2026-01-01 10-00-02.mp4", 2, 101),
            Candidate("Replay 2026-01-01 10-00-03.mp4", 3, 102)
        };
        var group = new DuplicateGroup(1, files, files[0]);
        var reviewed = Review(group, Metadata(10));
        reviewed[files[0].FullPath] = reviewed[files[0].FullPath] with { Metadata = Metadata(99) };
        RenamedFilePolicy.Apply(group, reviewed);
        var session = new ReviewSession([group], new(StringComparer.OrdinalIgnoreCase), new(Path.GetTempPath(), 60, KeepMode.Oldest, false, true), reviewed);
        // The user overrides the suggestion and removes the window keeper.
        var rebuilt = session.Reconcile(new RemovalResult(new HashSet<string>(StringComparer.OrdinalIgnoreCase) { files[1].FullPath }, []));
        var after = rebuilt.Reviewed;
        Check(rebuilt.Groups.Count == 1 && after[files[2].FullPath].SuggestedKeep && after[files[3].FullPath].SuggestRemoval,
            "Reconciliation did not choose a new keeper for the time window.");
    }

    private static void CaseVariantPaths() => WithFolder(root =>
    {
        File.WriteAllText(Path.Combine(root, "2026-01-01 10-00-00 a.mp4"), "a");
        File.WriteAllText(Path.Combine(root, "2026-01-01 10-00-01 b.mp4"), "b");
        // Case-sensitive folders can contain both spellings; simulate that enumeration.
        IEnumerable<string> Entries(string directory)
        {
            foreach (var entry in Directory.EnumerateFileSystemEntries(directory)) yield return entry;
            yield return Path.Combine(directory, "2026-01-01 10-00-00 A.MP4");
        }
        var result = DuplicateScanner.ScanDetailedCore(root, 60, false, KeepMode.Oldest, CancellationToken.None, Entries);
        Check(result.Groups.Single().Files.Count == 2 && result.Diagnostics.Any(d => d.Message.Contains("letter case")),
            "A case-variant path failed the scan or was not reported.");
    });

    private static void WindowWithoutSurvivorWarns()
    {
        var files = new[]
        {
            Candidate("renamed favourite.mp4", 0, 500, isProtected: true),
            Candidate("Replay 2026-01-01 10-00-01.mp4", 1, 100),
            Candidate("Replay 2026-01-01 10-00-02.mp4", 2, 101)
        };
        var group = new DuplicateGroup(1, files, files[0]);
        var reviewed = Review(group, Metadata(10));
        reviewed[files[0].FullPath] = reviewed[files[0].FullPath] with { Metadata = Metadata(99) };
        RenamedFilePolicy.Apply(group, reviewed);
        var selection = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { files[2].FullPath };
        var session = new ReviewSession([group], selection, null, reviewed);
        Check(session.GroupsWithoutSurvivors() == 0, "The window keeper survives, but a warning was raised.");
        session.SetSelected(files[1], true);
        Check(session.SelectedCount == 2 && session.GroupsWithoutSurvivors() == 1,
            "Selecting every original of a time window was hidden by an unrelated protected clip.");
    }

    private static void SelectedFilesFollowGroupOrder()
    {
        var files = Enumerable.Range(0, 40).Select(index => Candidate($"Replay 2026-01-01 10-00-{index:00}.mp4", index, index + 1)).ToArray();
        var groups = Enumerable.Range(0, 10).Select(index => new DuplicateGroup(index + 1, files.Skip(index * 4).Take(4).ToArray(), files[index * 4])).ToArray();
        var selection = files.Reverse().Where((_, index) => index % 4 != 3).Select(file => file.FullPath).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var session = new ReviewSession(groups, selection, null, new Dictionary<string, ReviewedVideo>(StringComparer.OrdinalIgnoreCase));
        var selected = session.SelectedFiles();
        Check(selected.SequenceEqual(groups.SelectMany(group => group.Files).Where(file => selection.Contains(file.FullPath))),
            "Selected files did not follow review order.");
    }

    private static void DirectoryPinsBlockChanges() => WithFolder(root =>
    {
        var empty = Directory.CreateDirectory(Path.Combine(root, "empty", "inner")).FullName;
        using (FileSnapshot.PinDirectories(empty))
        {
            Expect(() => Directory.Delete(empty), "A pinned empty directory was deleted.");
            Expect(() => Directory.Move(empty, empty + "-moved"), "A pinned empty directory was renamed.");
            Expect(() => Directory.Move(Path.GetDirectoryName(empty)!, Path.GetDirectoryName(empty) + "-moved"), "A pinned parent was renamed.");
            File.WriteAllText(Path.Combine(empty, "allowed.txt"), "files can still be created");
            File.Delete(Path.Combine(empty, "allowed.txt"));
        }
        Directory.Move(empty, empty + "-moved");

        static void Expect(Action action, string message)
        {
            try { action(); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return; }
            throw new InvalidOperationException(message);
        }
    });

    private static void ScanAcceptsActiveWriters() => WithFolder(root =>
    {
        File.WriteAllText(Path.Combine(root, "2026-01-01 10-00-00 done.mp4"), "done");
        using var recording = new FileStream(Path.Combine(root, "2026-01-01 10-00-30 live.mp4"), FileMode.CreateNew, FileAccess.Write, FileShare.Read);
        recording.Write("partial"u8);
        recording.Flush();
        var result = DuplicateScanner.ScanDetailed(root, 60, false, KeepMode.Oldest);
        Check(result.Diagnostics.Count == 0 && result.Groups.Single().Files.Count == 2, "A file open for writing was skipped by the scan.");
    });

    private static Dictionary<string, ReviewedVideo> Review(DuplicateGroup group, MediaMetadata metadata) =>
        group.Files.ToDictionary(file => file.FullPath,
            file => new ReviewedVideo(file, metadata, DuplicateConfidence.High, ReferenceEquals(file, group.SuggestedKeep)),
            StringComparer.OrdinalIgnoreCase);

    private static MediaMetadata Metadata(int seconds) => new(TimeSpan.FromSeconds(seconds), 1920, 1080, MetadataProbeStatus.Available);

    private static VideoCandidate Candidate(string name, int second, long size, bool isProtected = false) =>
        new(Path.Combine(Path.GetTempPath(), "dvc-review", name), name, new DateTime(2026, 1, 1, 10, 0, 0).AddSeconds(second),
            isProtected ? TimestampSource.CreationTime : TimestampSource.FileName, size, IsProtected: isProtected, TimeCluster: 1);

    private static void WithFolder(Action<string> test)
    {
        var root = Path.Combine(Path.GetTempPath(), "dvc-review-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try { test(root); }
        finally { Directory.Delete(root, true); }
    }

    private static Func<Task> Sync(Action action) => () => { action(); return Task.CompletedTask; };

    private static void Check(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}
