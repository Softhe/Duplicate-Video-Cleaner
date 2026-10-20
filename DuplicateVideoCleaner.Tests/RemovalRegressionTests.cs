using DuplicateVideoCleaner;

internal static class RemovalRegressionTests
{
    internal static IEnumerable<(string Name, Func<Task> Run)> Cases =>
    [
        ("recovery history archive preserves unsafe and active entries", RecoveryHistoryArchive),
        ("unwritable audit prevents deletion", UnwritableAudit),
        ("recovery round trip preserves contents and original path", RecoveryRoundTrip),
        ("recovery restore never overwrites an existing file", RecoveryCollision),
        ("recovery purge validates stored snapshot", RecoveryPurge),
        ("recovery rejects a substituted stored file", RecoveryReplacement),
        ("recovery rejects a manifest with a tampered source parent", RecoveryTamperedParent),
        ("recovery discovery isolates failed sibling directories", RecoveryFailedSibling),
        ("recovery discovery supports cancellation and releases directory pins", RecoveryCancellation),
        ("recovery intent failure preserves source", RecoveryIntentFailure),
        ("recovery pins source and destination against replacement", RecoveryPinnedPaths),
        ("recovery supports long path round trip", RecoveryLongPath),
        ("recovery rejects reparse parent directories", RecoveryReparseParent),
        ("recovery list isolates corrupted entries", RecoveryCorruptedEntry),
        ("recovery audit failure preserves payload", RecoveryAuditFailure),
        ("removal rejects redirected audit log", RedirectedAuditLog),
        ("permanent removal rejects an active writer", ActiveWriter),
        ("permanent removal supports long paths", LongPathRemoval),
        ("handle removal rejects a replaced snapshot", HandleReplacement),
        ("CSV formula prefixes are neutralized", CsvFormulaPrefixes),
        ("modified scan candidate is preserved", ModifiedCandidate),
        ("same-size replacement is preserved", ReplacedCandidate),
        ("missing snapshot is preserved", MissingSnapshot),
        ("audit failure after deletion preserves outcome and stops batch", PostDeleteAuditFailure),
        ("audit failure after failed deletion returns both failures", FailedDeleteAuditFailure)
    ];

    private static Task RecoveryHistoryArchive() => WithFiles((root, files) =>
    {
        foreach (var file in files) RecoveryStore.MoveToRecovery(file);
        var entries = RecoveryStore.List(root);
        var completed = entries[0];
        var active = entries[1];
        RecoveryStore.Restore(completed);
        var directory = Path.GetDirectoryName(completed.ManifestPath)!;
        var journal = Path.Combine(directory, "actions.log");
        var now = DateTimeOffset.UtcNow;
        // Keep chronological evidence internally consistent when simulating an old entry.
        var old = completed with { RemovedAt = now.AddDays(-60) };
        File.WriteAllText(completed.ManifestPath, System.Text.Json.JsonSerializer.Serialize(old));
        File.WriteAllText(journal, $"{now.AddDays(-40):O} | RESTORE SUCCESS\n");
        var result = RecoveryStore.ArchiveCompletedHistory(root, now.AddDays(-30), beforeArchive: path =>
            File.WriteAllText(Path.Combine(path, "payload.dvc"), "concurrently added"));
        Check(result.Archived == 0 && File.Exists(Path.Combine(directory, "payload.dvc")), "Concurrent payload was hidden by archival.");
        File.Delete(Path.Combine(directory, "payload.dvc"));
        result = RecoveryStore.ArchiveCompletedHistory(root, now.AddDays(-30), afterArchive: path =>
            File.WriteAllText(Path.Combine(path, "payload.dvc"), "late addition"));
        Check(result.Archived == 0 && Directory.Exists(directory) && File.Exists(Path.Combine(directory, "payload.dvc")),
            "A late payload addition was not rolled back into the active store.");
        File.Delete(Path.Combine(directory, "payload.dvc"));
        result = RecoveryStore.ArchiveCompletedHistory(root, now.AddDays(-30));
        Check(result.Archived == 1 && !Directory.Exists(directory), "Completed history was not archived: " + string.Join("; ", result.Warnings));
        var archived = Path.Combine(Path.GetDirectoryName(directory)!, RecoveryStore.HistoryDirectoryName, Path.GetFileName(directory));
        Check(File.Exists(Path.Combine(archived, "intent.json")) && File.Exists(Path.Combine(archived, "actions.log")), "History audit was lost.");
        Check(File.Exists(completed.OriginalPath) && File.Exists(active.StoredPath) && RecoveryStore.List(root).Count == 1,
            "Archival changed active or restored media.");
        // An unexpected payload appearing in an archive stays discoverable and restorable.
        File.Move(completed.OriginalPath, Path.Combine(archived, "payload.dvc"));
        var unexpected = RecoveryStore.List(root, includeArchivedPayloads: true).Single(entry => entry.ManifestPath.StartsWith(archived, StringComparison.OrdinalIgnoreCase));
        RecoveryStore.Restore(unexpected);
        Check(File.Exists(completed.OriginalPath), "An unexpected archived payload could not be restored.");
        var archiveRoot = Path.GetDirectoryName(archived)!;
        var redirectedArchive = Path.Combine(root, "redirected-history");
        Directory.Move(archiveRoot, redirectedArchive);
        using (var junction = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("cmd.exe")
        {
            Arguments = $"/c mklink /J \"{archiveRoot}\" \"{redirectedArchive}\"", UseShellExecute = false,
            CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true
        })!)
        {
            junction.WaitForExit();
            Check(junction.ExitCode == 0, "Could not create archived-history junction fixture: " + junction.StandardError.ReadToEnd());
        }
        var archiveWarnings = new List<string>();
        Check(RecoveryStore.List(root, archiveWarnings.Add, includeArchivedPayloads: true).Count == 1 && archiveWarnings.Count == 0,
            "Archive inspection traversed a reparse point or hid a healthy active sibling.");
        Directory.Delete(archiveRoot);
        Directory.Move(redirectedArchive, archiveRoot);
        // A missing payload is insufficient evidence of completion.
        RecoveryStore.Restore(active);
        var activeDirectory = Path.GetDirectoryName(active.ManifestPath)!;
        File.WriteAllText(Path.Combine(activeDirectory, "actions.log"), $"{now.AddDays(-40):O} | RESTORE ATTEMPT\n");
        result = RecoveryStore.ArchiveCompletedHistory(root, now.AddDays(-30));
        Check(result.Archived == 0 && Directory.Exists(activeDirectory), "An incomplete operation was archived.");
        File.WriteAllText(Path.Combine(activeDirectory, "actions.log"), $"{now.AddDays(-40):O} | RESTORE SUCCESS\n");
        File.WriteAllText(Path.Combine(activeDirectory, "unknown.txt"), "preserve");
        result = RecoveryStore.ArchiveCompletedHistory(root, now.AddDays(-30));
        Check(result.Archived == 0 && File.Exists(Path.Combine(activeDirectory, "unknown.txt")), "Unknown contents were archived.");
        File.Delete(Path.Combine(activeDirectory, "unknown.txt"));
        var outside = Path.Combine(root, "outside-audit.log");
        File.WriteAllText(outside, $"{now.AddDays(-40):O} | RESTORE SUCCESS\n");
        File.Delete(Path.Combine(activeDirectory, "actions.log"));
        File.CreateSymbolicLink(Path.Combine(activeDirectory, "actions.log"), outside);
        result = RecoveryStore.ArchiveCompletedHistory(root, now.AddDays(-30));
        Check(result.Archived == 0 && File.Exists(outside), "A redirected journal was archived.");
        File.Delete(Path.Combine(activeDirectory, "actions.log"));
        File.WriteAllText(Path.Combine(activeDirectory, "actions.log"), $"{now.AddDays(-40):O} | RESTORE SUCCESS\n");
        var extraLink = Path.Combine(root, "audit-hard-link.log");
        Check(CreateHistoryHardLink(extraLink, Path.Combine(activeDirectory, "actions.log"), IntPtr.Zero), "Could not create hard-link fixture.");
        result = RecoveryStore.ArchiveCompletedHistory(root, now.AddDays(-30));
        Check(result.Archived == 0 && result.Warnings.Any(warning => warning.Contains("hard links", StringComparison.Ordinal)),
            "A multiply linked audit was not rejected.");
        File.Delete(extraLink);
        File.WriteAllText(active.ManifestPath, "corrupt");
        result = RecoveryStore.ArchiveCompletedHistory(root, now.AddDays(-30));
        Check(result.Archived == 0 && result.Warnings.Count > 0, "Corrupt history was not preserved with a warning.");
        return Task.CompletedTask;
    });
    [System.Runtime.InteropServices.DllImport("kernel32.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode,
        SetLastError = true, EntryPoint = "CreateHardLinkW")]
    [return: System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.Bool)]
    private static extern bool CreateHistoryHardLink(string path, string existing, IntPtr security);

    private static Task RecoveryRoundTrip() => WithFiles(async (root, files) =>
    {
        var expected = File.ReadAllBytes(files[0].FullPath);
        var result = await FileRemovalService.RemoveAsync([files[0]], false, Path.Combine(root, "log.txt"));
        Check(result.Failures.Count == 0 && !File.Exists(files[0].FullPath), "Recovery move failed: " + string.Join("; ", result.Failures.Select(f => f.Error)));
        var entry = RecoveryStore.List(root).Single();
        Check(entry.OriginalPath == files[0].FullPath && File.Exists(entry.StoredPath) &&
            File.ReadAllBytes(entry.StoredPath).SequenceEqual(expected), "Recovery contents or original path were lost.");
        RecoveryStore.Restore(entry);
        Check(File.ReadAllBytes(files[0].FullPath).SequenceEqual(expected) && RecoveryStore.List(root).Count == 0,
            "Recovery did not restore the original bytes and location.");
    });

    private static Task RecoveryIntentFailure() => WithFiles((root, files) =>
    {
        ExpectIOException(() => RecoveryStore.MoveToRecovery(files[0], entry =>
        {
            Check(File.ReadAllText(entry.ManifestPath).Contains(files[0].FileIdentity!, StringComparison.Ordinal), "Original identity was not durably recorded before move.");
            throw new IOException("Injected failure after intent persistence.");
        }));
        Check(File.Exists(files[0].FullPath) && RecoveryStore.List(root).Count == 0, "A failed recovery intent removed the source or listed an incomplete entry.");
        return Task.CompletedTask;
    });

    private static Task RecoveryCorruptedEntry() => WithFiles((root, files) =>
    {
        foreach (var file in files) RecoveryStore.MoveToRecovery(file);
        var entries = RecoveryStore.List(root);
        File.WriteAllText(entries[0].ManifestPath, "invalid JSON");
        var diagnostics = new List<string>();
        var valid = RecoveryStore.List(root, diagnostics.Add);
        Check(valid.Count == 1 && diagnostics.Count == 1 && valid[0] == entries[1], "A corrupt recovery entry prevented access to a valid entry or was silently ignored.");
        RecoveryStore.Restore(valid[0]);
        return Task.CompletedTask;
    });

    private static Task RecoveryAuditFailure() => WithFiles((root, files) =>
    {
        RecoveryStore.MoveToRecovery(files[0]);
        var entry = RecoveryStore.List(root).Single();
        Directory.CreateDirectory(Path.Combine(Path.GetDirectoryName(entry.ManifestPath)!, "actions.log"));
        ExpectIOException(() => RecoveryStore.Restore(entry));
        ExpectIOException(() => RecoveryStore.Purge(entry));
        Check(File.Exists(entry.StoredPath) && !File.Exists(entry.OriginalPath), "Recovery mutated data without a durable action audit.");
        return Task.CompletedTask;
    });

    private static Task RedirectedAuditLog() => WithFiles(async (root, files) =>
    {
        var target = Path.Combine(root, "unrelated.txt");
        File.WriteAllText(target, "unchanged");
        var log = Path.Combine(root, "redirected.log");
        File.CreateSymbolicLink(log, target);
        var result = await FileRemovalService.RemoveAsync([files[0]], true, log);
        Check(result.RemovedPaths.Count == 0 && result.AuditError != null && File.Exists(files[0].FullPath) &&
            File.ReadAllText(target) == "unchanged", "Redirected audit log was followed or allowed removal.");
    });

    private static Task RecoveryPinnedPaths() => WithFiles((root, files) =>
    {
        RecoveryStore.MoveToRecovery(files[0], entry =>
        {
            ExpectIOException(() => File.Move(files[0].FullPath, files[0].FullPath + ".swapped"));
            var directory = Path.GetDirectoryName(entry.StoredPath)!;
            ExpectIOException(() => Directory.Move(directory, directory + "-swapped"));
            ExpectIOException(() => File.WriteAllBytes(files[0].FullPath, [9, 8, 7]));
        });
        var entry = RecoveryStore.List(root).Single();
        RecoveryStore.Restore(entry);
        Check(File.ReadAllBytes(files[0].FullPath).SequenceEqual(new byte[] { 1, 2, 3 }), "Recovery moved changed data.");
        return Task.CompletedTask;
    });

    private static Task RecoveryLongPath() => WithFiles((root, files) =>
    {
        var directory = root;
        while (directory.Length < 300) directory = Path.Combine(directory, "long-path-recovery-directory");
        Directory.CreateDirectory(directory);
        foreach (var file in files) File.Move(file.FullPath, Path.Combine(directory, file.RelativePath));
        var candidate = DuplicateScanner.Scan(directory, 60, false, KeepMode.Oldest).Single().Files[0];
        RecoveryStore.MoveToRecovery(candidate);
        RecoveryStore.Restore(RecoveryStore.List(root).Single());
        Check(File.Exists(candidate.FullPath), "Long-path recovery did not restore the source.");
        return Task.CompletedTask;
    });

    private static Task RecoveryReparseParent() => WithFiles((root, files) =>
    {
        var link = Path.Combine(root, "linked-source");
        Directory.CreateSymbolicLink(link, root);
        var throughLink = files[0] with { FullPath = Path.Combine(link, files[0].RelativePath) };
        try
        {
            ExpectIOException(() => RecoveryStore.MoveToRecovery(throughLink));
            ExpectIOException(() => FileSnapshot.DeleteValidated(throughLink));
            Check(File.Exists(files[0].FullPath), "A reparse parent allowed destructive removal.");
        }
        finally { Directory.Delete(link); }
        return Task.CompletedTask;
    });

    private static Task RecoveryCollision() => WithFiles((root, files) =>
    {
        RecoveryStore.MoveToRecovery(files[0]);
        var entry = RecoveryStore.List(root).Single();
        File.WriteAllText(entry.OriginalPath, "replacement");
        ExpectIOException(() => RecoveryStore.Restore(entry));
        Check(File.ReadAllText(entry.OriginalPath) == "replacement" && File.Exists(entry.StoredPath), "Restore overwrote an existing file or lost recovery data.");
        Check(File.ReadAllText(Path.Combine(Path.GetDirectoryName(entry.ManifestPath)!, "actions.log")).Contains("RESTORE FAILED"),
            "A failed restore left an attempt without a failure outcome.");
        return Task.CompletedTask;
    });

    private static Task RecoveryPurge() => WithFiles((root, files) =>
    {
        RecoveryStore.MoveToRecovery(files[0]);
        var entry = RecoveryStore.List(root).Single();
        RecoveryStore.Purge(entry);
        Check(!File.Exists(entry.StoredPath) && RecoveryStore.List(root).Count == 0 && File.Exists(files[1].FullPath), "Purge failed or affected an unrelated file.");
        return Task.CompletedTask;
    });

    private static Task RecoveryReplacement() => WithFiles((root, files) =>
    {
        RecoveryStore.MoveToRecovery(files[0]);
        var entry = RecoveryStore.List(root).Single();
        File.Move(entry.StoredPath, entry.StoredPath + ".original");
        File.WriteAllBytes(entry.StoredPath, [9, 8, 7]);
        File.SetCreationTimeUtc(entry.StoredPath, entry.CreationTimeUtc);
        File.SetLastWriteTimeUtc(entry.StoredPath, entry.LastWriteTimeUtc);
        ExpectIOException(() => RecoveryStore.Restore(entry));
        ExpectIOException(() => RecoveryStore.Purge(entry));
        Check(File.Exists(entry.StoredPath) && !File.Exists(entry.OriginalPath), "A substituted recovery file was restored or purged.");
        return Task.CompletedTask;
    });

    private static Task RecoveryTamperedParent() => WithFiles((root, files) =>
    {
        RecoveryStore.MoveToRecovery(files[0]);
        var entry = RecoveryStore.List(root).Single();
        var manifest = System.Text.Json.JsonSerializer.Serialize(entry with { OriginalPath = Path.Combine(root, "missing", "video.mp4") });
        File.WriteAllText(entry.ManifestPath, manifest);
        ExpectIOException(() => RecoveryStore.Restore(entry));
        Check(File.Exists(entry.StoredPath), "Failed restoration lost recovery data.");
        return Task.CompletedTask;
    });

    private static Task RecoveryFailedSibling() => WithFiles((root, files) =>
    {
        var failed = Directory.CreateDirectory(Path.Combine(root, "unreadable")).FullName;
        RecoveryStore.MoveToRecovery(files[0]);
        var warnings = new List<string>();
        var listed = RecoveryStore.List(root, warnings.Add, beforeReadChild: child =>
        {
            if (child == failed) throw new IOException("Injected child read failure.");
        });
        Check(listed.Count == 1 && warnings.Count == 1, "A failed sibling hid a healthy recovery store.");
        return Task.CompletedTask;
    });

    private static Task RecoveryCancellation() => WithFiles((root, files) =>
    {
        Directory.CreateDirectory(Path.Combine(root, "child"));
        using var cancellation = new CancellationTokenSource();
        var cancelled = false;
        try
        {
            RecoveryStore.List(root, cancellationToken: cancellation.Token,
                beforeReadChild: _ => cancellation.Cancel());
        }
        catch (OperationCanceledException) { cancelled = true; }
        Check(cancelled, "Discovery ignored cancellation between directory operations.");
        var renamed = root + "-renamed";
        Directory.Move(root, renamed);
        Directory.Move(renamed, root);
        return Task.CompletedTask;
    });

    private static void ExpectIOException(Action action)
    {
        try { action(); }
        catch (IOException) { return; }
        throw new InvalidOperationException("The unsafe operation was not rejected.");
    }

    private static Task LongPathRemoval() => WithFiles(async (root, _) =>
    {
        var directory = root;
        while (directory.Length < 300) directory = Path.Combine(directory, "nested-directory-for-long-path-regression");
        Directory.CreateDirectory(directory);
        File.WriteAllBytes(Path.Combine(directory, "2026-01-01 10-00-00 a.mp4"), [1, 2, 3]);
        File.WriteAllBytes(Path.Combine(directory, "2026-01-01 10-00-01 b.mp4"), [4, 5, 6]);
        var files = DuplicateScanner.Scan(directory, 60, false, KeepMode.Oldest).Single().Files;
        var result = await FileRemovalService.RemoveAsync([files[0]], true, Path.Combine(root, "log.txt"));
        Check(result.RemovedPaths.SetEquals([files[0].FullPath]) && result.Failures.Count == 0 &&
            !File.Exists(files[0].FullPath) && File.Exists(files[1].FullPath), "Long-path removal failed or affected the retained file.");
    });
    private static Task ActiveWriter() => WithFiles(async (root, files) =>
    {
        using var writer = new FileStream(files[0].FullPath, FileMode.Open, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete);
        var result = await FileRemovalService.RemoveAsync([files[0]], true, Path.Combine(root, "log.txt"));
        Check(result.RemovedPaths.Count == 0 && result.Failures.Count == 1 && File.Exists(files[0].FullPath),
            "A file with an active writer was removed.");
    });

    private static Task HandleReplacement() => WithFiles((root, files) =>
    {
        File.Move(files[0].FullPath, Path.Combine(root, "original.bak"));
        File.WriteAllBytes(files[0].FullPath, [9, 8, 7]);
        File.SetCreationTimeUtc(files[0].FullPath, files[0].CreationTimeUtc!.Value);
        File.SetLastWriteTimeUtc(files[0].FullPath, files[0].LastWriteTimeUtc!.Value);
        var rejected = false;
        try { FileSnapshot.DeleteValidated(files[0]); }
        catch (IOException) { rejected = true; }
        Check(rejected && File.Exists(files[0].FullPath), "Handle deletion removed a replacement with matching metadata.");
        return Task.CompletedTask;
    });

    private static Task CsvFormulaPrefixes()
    {
        foreach (var value in new[] { "=1+1", "+1+1", "-1+1", "@SUM(1)", "  =1", "\tvalue", "\rvalue", "\nvalue" })
            Check(DryRunExporter.EscapeCsv(value).TrimStart('"').StartsWith("'", StringComparison.Ordinal),
                "CSV formula prefix was not neutralized.");
        Check(DryRunExporter.EscapeCsv("C:\\video.mp4") == "C:\\video.mp4", "A safe path was changed.");
        Check(DryRunExporter.EscapeCsv("a,\"b") == "\"a,\"\"b\"", "CSV quote escaping changed.");
        return Task.CompletedTask;
    }
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
