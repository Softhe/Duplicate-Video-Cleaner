using DuplicateVideoCleaner;

internal static class RenamedFileTests
{
    internal static void ReconciliationDropsProtectedOnlyGroupsAfterSuccessfulRemoval()
    {
        var files = Enumerable.Range(0, 3).Select(i => new VideoCandidate(
            Path.Combine(Path.GetTempPath(), $"reconcile-{i}.mp4"), $"reconcile-{i}.mp4",
            new DateTime(2026, 1, 1), TimestampSource.FileName, 100, IsProtected: i < 2)).ToArray();
        var group = new DuplicateGroup(1, files, files[0]);
        var reviewed = files.ToDictionary(file => file.FullPath, file => new ReviewedVideo(file,
            new(TimeSpan.FromSeconds(10), 1920, 1080, MetadataProbeStatus.Available), DuplicateConfidence.High, file == files[0]));
        RenamedFilePolicy.Apply(group, reviewed);
        var session = new ReviewSession([group], new(StringComparer.OrdinalIgnoreCase),
            new(Path.GetTempPath(), 60, KeepMode.Oldest, false), reviewed);
        session.SetSelected(files[2], true);

        var failed = session.Reconcile(new RemovalResult(new HashSet<string>(), [new(files[2], "Fixture removal failure.")]));
        Check(failed.Groups.Count == 1 && failed.FileCount == 3 && failed.SelectedCount == 1,
            "A failed removal discarded the mixed group or its selection.");
        Check(failed.Groups[0].Files.Count(file => file.IsProtected) == 2 && failed.SelectedFiles().Single() == files[2],
            "A failed removal lost protected clips or the original.");

        var completed = failed.Reconcile(new RemovalResult(new HashSet<string> { files[2].FullPath }, []));
        Check(completed.Groups.Count == 0 && completed.FileCount == 0 && completed.SelectedCount == 0 &&
            completed.SelectedBytes == 0 && completed.SelectedFiles().Count == 0 && completed.CreateDryRun().Rows.Count == 0,
            "Successful removal left a protected-only cleanup group, indexed files, or stale selections.");
    }

    internal static void ScannerFindsProtectedCopiesAcrossDates()
    {
        var root = Path.Combine(Path.GetTempPath(), "dvc-renamed-" + Guid.NewGuid());
        Directory.CreateDirectory(root);
        try
        {
            File.WriteAllText(Path.Combine(root, "favorite.mp4"), "same-size");
            File.WriteAllText(Path.Combine(root, "another favorite.mkv"), "same-size");
            File.WriteAllText(Path.Combine(root, "Replay 2020-01-01 10-00-00.mp4"), "same-size");
            File.WriteAllText(Path.Combine(root, "Replay 2020-01-01 10-00-01.mp4"), "different size");
            foreach (var mode in Enum.GetValues<KeepMode>())
            {
                var group = DuplicateScanner.Scan(root, 60, false, mode).Single();
                Check(group.Files.Count == 4, "The size and time candidates were not merged.");
                Check(group.SuggestedKeep.IsProtected, "The selected date rule overrode rename protection.");
                Check(group.Files.Count(file => file.IsProtected) == 2, "Renamed clips were not identified.");
            }
            File.Delete(Path.Combine(root, "Replay 2020-01-01 10-00-00.mp4"));
            File.Delete(Path.Combine(root, "Replay 2020-01-01 10-00-01.mp4"));
            Check(DuplicateScanner.Scan(root, 60, false, KeepMode.Oldest).Count == 0,
                "Protected-only clips should be skipped from cleanup review.");
        }
        finally { Directory.Delete(root, true); }
    }

    internal static void SelectionRequiresMatchToAnyProtectedClip()
    {
        var files = Enumerable.Range(0, 7).Select(i => new VideoCandidate(
            Path.Combine(Path.GetTempPath(), $"protected-test-{i}.mp4"), $"protected-test-{i}.mp4",
            new DateTime(2026, 1, 1).AddSeconds(i), TimestampSource.FileName, i == 0 ? 101 : i == 5 ? 99 : 100,
            IsProtected: i < 2)).ToArray();
        var group = new DuplicateGroup(1, files, files[0]);
        MediaMetadata Metadata(int seconds, int width = 1920) => new(TimeSpan.FromSeconds(seconds), width, 1080, MetadataProbeStatus.Available);
        var metadata = new[] { Metadata(10), Metadata(20), Metadata(20), Metadata(30), Metadata(20, 1280), Metadata(20), new MediaMetadata(null, null, null, MetadataProbeStatus.Unavailable) };
        var reviewed = files.Select((file, i) => new ReviewedVideo(file, metadata[i], DuplicateConfidence.High, i == 0))
            .ToDictionary(video => video.Candidate.FullPath, StringComparer.OrdinalIgnoreCase);
        RenamedFilePolicy.Apply(group, reviewed);
        Check(reviewed[files[2].FullPath].SuggestRemoval, "A match to the second protected clip was missed.");
        Check(reviewed[files[2].FullPath].ProtectedReference == files[1] &&
            reviewed[files[2].FullPath].Confidence == DuplicateConfidence.High,
            "The comparison did not use the actual protected match.");
        Check(reviewed.Values.Count(video => video.SuggestRemoval) == 1, "Unmatched or protected clips were suggested.");

        var selected = files.Select(file => file.FullPath).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var session = new ReviewSession([group], selected, new(Path.GetTempPath(), 60, KeepMode.Oldest, false), reviewed);
        Check(session.SelectedCount == 1 && session.SelectedFiles()[0] == files[2], "Stale selections bypassed protection.");
        foreach (var file in files) session.SetSelected(file, true);
        session.SetSelected(files[0] with { IsProtected = false }, true);
        Check(session.SelectedCount == 1, "Select all or an altered candidate bypassed protection.");
        var csv = DryRunExporter.BuildCsv(session.CreateDryRun());
        Check(csv.Contains("Protected renamed clip") && csv.Contains("No metadata match to a protected clip; kept"),
            "Export did not explain protected and unmatched clips.");
        Check(csv.Contains("ProtectedMatchPath") && csv.Contains(files[1].FullPath), "Export omitted the protected match path.");

        var rebuilt = session.Reconcile(new RemovalResult(new HashSet<string> { files[2].FullPath }, []));
        Check(rebuilt.SelectedCount == 0 && rebuilt.Groups.Single().SuggestedKeep.IsProtected,
            "Reconciliation lost protection or retained a removed selection.");
    }

    internal static async Task ProtectedSurvivorValidation()
    {
        foreach (var permanent in new[] { false, true })
        foreach (var scenario in new[] { "missing", "replaced", "edited", "alternative", "unchanged" })
        {
            var root = Path.Combine(Path.GetTempPath(), "dvc-survivor-" + Guid.NewGuid());
            Directory.CreateDirectory(root);
            try
            {
                var favorite = Capture("favorite.mp4", true);
                var alternative = Capture("another favorite.mp4", true);
                var original = Capture("Replay 2026-01-01 10-00-00.mp4", false);
                var metadata = new MediaMetadata(TimeSpan.FromSeconds(10), 1920, 1080, MetadataProbeStatus.Available);
                var group = new DuplicateGroup(1, [favorite, alternative, original], favorite);
                var reviewed = group.Files.ToDictionary(file => file.FullPath,
                    file => new ReviewedVideo(file, metadata, DuplicateConfidence.High, file == favorite));
                RenamedFilePolicy.Apply(group, reviewed);
                if (scenario is "missing" or "alternative") File.Delete(favorite.FullPath);
                if (scenario == "replaced")
                {
                    File.Move(favorite.FullPath, favorite.FullPath + ".old");
                    File.WriteAllText(favorite.FullPath, "one");
                    File.SetCreationTimeUtc(favorite.FullPath, favorite.CreationTimeUtc!.Value);
                    File.SetLastWriteTimeUtc(favorite.FullPath, favorite.LastWriteTimeUtc!.Value);
                }
                if (scenario == "edited")
                {
                    File.WriteAllText(favorite.FullPath, "two");
                    File.SetLastWriteTimeUtc(favorite.FullPath, favorite.LastWriteTimeUtc!.Value.AddMinutes(1));
                }
                if (scenario is not "alternative" and not "unchanged") File.Delete(alternative.FullPath);
                var result = await FileRemovalService.RemoveReviewedAsync([reviewed[original.FullPath]], permanent, Path.Combine(root, "audit.txt"));
                var allowed = scenario is "alternative" or "unchanged";
                Check(result.RemovedPaths.Contains(original.FullPath) == allowed && File.Exists(original.FullPath) != allowed,
                    $"Survivor validation failed for {scenario}, permanent={permanent}.");
                Check(result.Failures.Count == (allowed ? 0 : 1), "Survivor failure was not reported.");
                if (!allowed) Check(result.Failures[0].Error.Contains("Scan again"), "Failure did not explain the required rescan.");

                VideoCandidate Capture(string name, bool protect)
                {
                    var path = Path.Combine(root, name);
                    File.WriteAllText(path, "one");
                    using var source = WindowsProbeSandbox.OpenReadOnlyMedia(path);
                    return FileSnapshot.CaptureReadHandle(source.SafeFileHandle, path) with { IsProtected = protect };
                }
            }
            finally { Directory.Delete(root, true); }
        }
    }

    internal static async Task ProtectedSurvivorStaysPinnedDuringRemoval()
    {
        var root = Path.Combine(Path.GetTempPath(), "dvc-survivor-lock-" + Guid.NewGuid());
        var protectedDirectory = Directory.CreateDirectory(Path.Combine(root, "protected")).FullName;
        try
        {
            var path = Path.Combine(protectedDirectory, "favorite.mp4");
            File.WriteAllText(path, "one");
            VideoCandidate survivor;
            using (var source = WindowsProbeSandbox.OpenReadOnlyMedia(path))
                survivor = FileSnapshot.CaptureReadHandle(source.SafeFileHandle, path) with { IsProtected = true };
            var originalPath = Path.Combine(root, "Replay 2026-01-01 10-00-00.mp4");
            File.WriteAllText(originalPath, "one");
            VideoCandidate original;
            using (var source = WindowsProbeSandbox.OpenReadOnlyMedia(originalPath))
                original = FileSnapshot.CaptureReadHandle(source.SafeFileHandle, originalPath);
            var video = new ReviewedVideo(original, new(TimeSpan.FromSeconds(10), 1920, 1080, MetadataProbeStatus.Available),
                DuplicateConfidence.High, false, true, [survivor]);
            var called = false;
            var result = await FileRemovalService.RemoveReviewedAsync([video], true, Path.Combine(root, "audit.txt"), (_, _) =>
            {
                called = true;
                Denied(() => File.Delete(path));
                Denied(() => File.WriteAllText(path, "changed"));
                Denied(() => File.Move(path, path + ".moved"));
                Denied(() => Directory.Move(protectedDirectory, protectedDirectory + ".moved"));
            }, (_, _) => { });
            Check(called && result.Failures.Count == 0 && File.ReadAllText(path) == "one", "Survivor was not pinned during removal.");
            using var released = File.Open(path, FileMode.Open, FileAccess.Write, FileShare.None);
        }
        finally { Directory.Delete(root, true); }

        static void Denied(Action action)
        {
            try { action(); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return; }
            throw new InvalidOperationException("The protected survivor could be changed during removal.");
        }
    }

    internal static void GroupConfidenceUsesSuggestedRemovals()
    {
        var file = new VideoCandidate("fixture.mp4", "fixture.mp4", DateTime.Now, TimestampSource.FileName, 1);
        var high = new ReviewedVideo(file, new(null, null, null, MetadataProbeStatus.Unavailable), DuplicateConfidence.High, false);
        var low = high with { Confidence = DuplicateConfidence.Low };
        Check(ConfidenceEvaluator.ForSuggestedRemovals([high with { SuggestedKeep = true }, low]) == DuplicateConfidence.Low,
            "The keeper's self-comparison concealed a weak removal suggestion.");
        Check(ConfidenceEvaluator.ForSuggestedRemovals([high, low]) == DuplicateConfidence.Low, "The group did not show its weakest suggestion.");
        Check(ConfidenceEvaluator.ForSuggestedRemovals([high, high]) == DuplicateConfidence.High, "High suggestions did not stay High.");
        Check(ConfidenceEvaluator.ForSuggestedRemovals([high with { SuggestedKeep = true }]) is null, "An empty suggestion set showed confidence.");
    }

    internal static async Task RemovalRejectsProtectedClip()
    {
        var root = Path.Combine(Path.GetTempPath(), "dvc-protected-remove-" + Guid.NewGuid());
        Directory.CreateDirectory(root);
        try
        {
            var path = Path.Combine(root, "favorite.mp4");
            File.WriteAllText(path, "keep");
            using var source = WindowsProbeSandbox.OpenReadOnlyMedia(path);
            var file = FileSnapshot.CaptureReadHandle(source.SafeFileHandle, path) with { IsProtected = true };
            var called = false;
            var result = await FileRemovalService.RemoveAsync([file], true, Path.Combine(root, "audit.txt"),
                (_, _) => called = true, (_, _) => { });
            Check(!called && File.Exists(path) && result.Failures.Count == 1, "Removal did not enforce rename protection.");
        }
        finally { Directory.Delete(root, true); }
    }

    private static void Check(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}
