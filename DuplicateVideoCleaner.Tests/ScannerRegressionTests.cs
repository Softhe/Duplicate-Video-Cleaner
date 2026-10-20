using DuplicateVideoCleaner;

internal static class ScannerRegressionTests
{
    internal static void CancellationAndSnapshots()
    {
        var root = Path.Combine(Path.GetTempPath(), "dvc-scan-" + Guid.NewGuid());
        Directory.CreateDirectory(root);
        try
        {
            File.WriteAllText(Path.Combine(root, "2025-01-01 10-00-00 a.mp4"), "a");
            File.WriteAllText(Path.Combine(root, "2025-01-01 10-00-01 b.mp4"), "b");
            using var canceled = new CancellationTokenSource();
            canceled.Cancel();
            try { DuplicateScanner.ScanDetailed(root, 60, true, KeepMode.Oldest, canceled.Token); throw new Exception("Canceled scan succeeded."); }
            catch (OperationCanceledException) { }
            var result = DuplicateScanner.ScanDetailed(root, 60, true, KeepMode.Oldest);
            if (result.Groups.Single().Files.Any(f => f.CreationTimeUtc is null || f.LastWriteTimeUtc is null))
                throw new Exception("Scan did not capture file timestamps.");
            var recovery = Directory.CreateDirectory(Path.Combine(root, ".duplicatevideocleaner-recovery")).FullName;
            File.WriteAllText(Path.Combine(recovery, "2025-02-01 10-00-00 recovered.mp4"), "a");
            File.WriteAllText(Path.Combine(recovery, "2025-02-01 10-00-01 recovered.mp4"), "b");
            if (DuplicateScanner.ScanDetailed(root, 60, true, KeepMode.Oldest).Groups.Count != 1)
                throw new Exception("Recovery storage was included in recursive scan results.");
            Directory.Delete(recovery, true);
            var link = Path.Combine(root, "loop");
            // Junctions need no symbolic-link privilege on Windows.
            var process = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("cmd.exe")
            {
                Arguments = $"/c mklink /J \"{link}\" \"{root}\"", UseShellExecute = false, CreateNoWindow = true,
                RedirectStandardOutput = true, RedirectStandardError = true
            })!;
            process.WaitForExit();
            if (process.ExitCode != 0) throw new Exception("Could not create junction fixture.");
            var linked = DuplicateScanner.ScanDetailed(root, 60, true, KeepMode.Oldest);
            if (!linked.Diagnostics.Any(d => d.Path == link)) throw new Exception("Junction was not reported.");
            var linkedRoot = DuplicateScanner.ScanDetailed(link, 60, true, KeepMode.Oldest);
            if (linkedRoot.Groups.Count != 0 || !linkedRoot.Diagnostics.Any(d => d.Path == link))
                throw new Exception("Reparse root was traversed.");
            Directory.Delete(link);
            IEnumerable<string> PartiallyDenied(string directory)
            {
                foreach (var entry in Directory.EnumerateFiles(directory)) yield return entry;
                throw new UnauthorizedAccessException("Fixture denied enumeration after files.");
            }
            var partial = DuplicateScanner.ScanDetailedCore(root, 60, true, KeepMode.Oldest,
                CancellationToken.None, PartiallyDenied);
            if (partial.Groups.Count != 1 || !partial.Diagnostics.Any(d => d.Path == root))
                throw new Exception("Partial enumeration results or access diagnostic were lost.");
            IEnumerable<string> RemovedEntry(string directory)
            {
                yield return Path.Combine(directory, "removed.mp4");
                foreach (var entry in Directory.EnumerateFiles(directory)) yield return entry;
            }
            var removed = DuplicateScanner.ScanDetailedCore(root, 60, true, KeepMode.Oldest,
                CancellationToken.None, RemovedEntry);
            if (removed.Groups.Count != 1 || !removed.Diagnostics.Any(d => d.Path.EndsWith("removed.mp4")))
                throw new Exception("Removed entry prevented scan continuation.");
            var queued = Path.Combine(root, "queued");
            Directory.CreateDirectory(queued);
            IEnumerable<string> ReplacedDirectory(string directory)
            {
                if (directory != root) throw new Exception("Replaced directory was traversed.");
                yield return queued;
                Directory.Delete(queued);
                var replacement = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("cmd.exe")
                {
                    Arguments = $"/c mklink /J \"{queued}\" \"{root}\"", UseShellExecute = false,
                    CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true
                })!;
                replacement.WaitForExit();
                if (replacement.ExitCode != 0) throw new Exception("Could not replace queued directory.");
            }
            var replaced = DuplicateScanner.ScanDetailedCore(root, 60, true, KeepMode.Oldest,
                CancellationToken.None, ReplacedDirectory);
            if (!replaced.Diagnostics.Any(d => d.Path == queued)) throw new Exception("Queued reparse directory was not reported.");
            Directory.Delete(queued);
        }
        finally
        {
            foreach (var name in new[] { "loop", "queued" })
            {
                var path = Path.Combine(root, name);
                if (Directory.Exists(path)) Directory.Delete(path);
            }
            Directory.Delete(root, true);
        }
    }
}
