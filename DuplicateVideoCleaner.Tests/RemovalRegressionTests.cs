using DuplicateVideoCleaner;

internal static class RemovalRegressionTests
{
    internal static IEnumerable<(string Name, Func<Task> Run)> Cases =>
    [
        ("unwritable audit prevents deletion", UnwritableAudit),
        ("modified scan candidate is preserved", ModifiedCandidate),
        ("same-size replacement is preserved", ReplacedCandidate),
        ("missing snapshot is preserved", MissingSnapshot),
        ("audit failure after deletion preserves outcome and stops batch", PostDeleteAuditFailure),
        ("audit failure after failed deletion returns both failures", FailedDeleteAuditFailure)
    ];

    private static Task UnwritableAudit() => WithFiles(async (root, files) =>
    {
        var result = await FileRemovalService.RemoveAsync(files, true, root);
        Check(result.RemovedPaths.Count == 0 && files.All(f => File.Exists(f.FullPath)), "An unaudited file was removed.");
        Check(result.Failures.Count == files.Count && result.AuditError != null, "Audit failure was not returned for the entire stopped batch.");
    });

    private static Task ModifiedCandidate() => WithFiles(async (root, files) =>
    {
        File.AppendAllText(files[0].FullPath, "changed");
        var result = await FileRemovalService.RemoveAsync([files[0]], true, Path.Combine(root, "log.txt"));
        Check(result.RemovedPaths.Count == 0 && result.Failures.Count == 1 && File.Exists(files[0].FullPath), "Changed file was removed.");
    });

    private static Task ReplacedCandidate() => WithFiles(async (root, files) =>
    {
        // Keep the original object alive so the filesystem cannot reuse its identity.
        File.Move(files[0].FullPath, Path.Combine(root, "original.bak"));
        File.WriteAllBytes(files[0].FullPath, [9, 8, 7]);
        File.SetCreationTimeUtc(files[0].FullPath, files[0].CreationTimeUtc!.Value);
        File.SetLastWriteTimeUtc(files[0].FullPath, files[0].LastWriteTimeUtc!.Value);
        var result = await FileRemovalService.RemoveAsync([files[0]], true, Path.Combine(root, "log.txt"));
        Check(result.RemovedPaths.Count == 0 && File.Exists(files[0].FullPath), "Replaced file was removed.");
    });

    private static Task MissingSnapshot() => WithFiles(async (root, files) =>
    {
        var file = files[0];
        var withoutSnapshot = new VideoCandidate(file.FullPath, file.RelativePath, file.Timestamp, file.TimestampSource, file.Size);
        var result = await FileRemovalService.RemoveAsync([withoutSnapshot], true, Path.Combine(root, "log.txt"));
        Check(result.RemovedPaths.Count == 0 && result.Failures.Count == 1 && File.Exists(file.FullPath), "A file without scan snapshot was removed.");
    });

    private static Task PostDeleteAuditFailure() => WithFiles(async (root, files) =>
    {
        var writes = 0;
        var result = await FileRemovalService.RemoveAsync(files, true, Path.Combine(root, "log.txt"),
            (file, _) => File.Delete(file.FullPath),
            (_, _) => { if (++writes == 2) throw new IOException("Injected audit failure."); });
        Check(result.RemovedPaths.SetEquals([files[0].FullPath]), "Successful deletion was misreported after audit failure.");
        Check(result.Failures.Count == 1 && result.Failures[0].File == files[1], "Stopped file was not reported as skipped.");
        Check(result.AuditError != null && !File.Exists(files[0].FullPath) && File.Exists(files[1].FullPath), "Batch continued after audit failure.");
    });

    private static Task FailedDeleteAuditFailure() => WithFiles(async (root, files) =>
    {
        var writes = 0;
        var result = await FileRemovalService.RemoveAsync(files, true, Path.Combine(root, "log.txt"),
            (_, _) => throw new IOException("Injected deletion failure."),
            (_, _) => { if (++writes == 2) throw new IOException("Injected audit failure."); });
        Check(result.RemovedPaths.Count == 0 && result.Failures.Count == 2, "Failed deletion or skipped file was lost.");
        Check(result.Failures[0].Error == "Injected deletion failure." && result.AuditError != null, "Audit error replaced the deletion error.");
        Check(files.All(file => File.Exists(file.FullPath)), "Batch removed a fixture despite the injected failure.");
    });

    private static async Task WithFiles(Func<string, IReadOnlyList<VideoCandidate>, Task> run)
    {
        var root = Path.Combine(Path.GetTempPath(), "DuplicateVideoCleanerRemovalRegression", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            File.WriteAllBytes(Path.Combine(root, "2026-01-01 10-00-00 a.mp4"), [1, 2, 3]);
            File.WriteAllBytes(Path.Combine(root, "2026-01-01 10-00-01 b.mp4"), [4, 5, 6]);
            await run(root, DuplicateScanner.Scan(root, 60, false, KeepMode.Oldest).Single().Files);
        }
        finally { Directory.Delete(root, true); }
    }

    private static void Check(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}
