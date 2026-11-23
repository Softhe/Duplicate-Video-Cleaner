using System.Diagnostics;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace DuplicateVideoCleaner.UiTests;

[TestClass]
public sealed class CleanupLogTests
{
    [TestMethod]
    public void ScanLogEscapesLinesAndRejectsHardLinkedDestinations()
    {
        var root = Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), "dvc-form-log-" + Guid.NewGuid().ToString("N"))).FullName;
        try
        {
            var log = Path.Combine(root, "scan.log");
            CleanupLog.Append(log, "filename\r\nFORGED RECORD");
            Assert.AreEqual(1, File.ReadAllLines(log).Length);
            StringAssert.Contains(File.ReadAllText(log), @"filename\r\nFORGED RECORD");
            var target = Path.Combine(root, "protected.txt");
            File.WriteAllText(target, "preserve original content");
            var alias = Path.Combine(root, "linked.log");
            using var process = Process.Start(new ProcessStartInfo("cmd.exe")
            {
                Arguments = $"/c mklink /H \"{alias}\" \"{target}\"", UseShellExecute = false, CreateNoWindow = true,
                RedirectStandardOutput = true, RedirectStandardError = true
            })!;
            process.WaitForExit();
            Assert.AreEqual(0, process.ExitCode, "Could not create hard-link log fixture.");
            Assert.ThrowsException<IOException>(() => CleanupLog.Append(alias, "unsafe write"), "A hard-linked log must be rejected.");
            Assert.AreEqual("preserve original content", File.ReadAllText(target));
        }
        finally { Directory.Delete(root, true); }
    }

    [TestMethod]
    public void DiagnosticLoggingCancelsBetweenEntriesAndReleasesLog()
    {
        var root = Path.Combine(Path.GetTempPath(), "DvcDiagnosticTest", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var log = Path.Combine(root, "log.txt");
        using var cancellation = new CancellationTokenSource();
        IEnumerable<string> Messages()
        {
            yield return "First\nentry";
            cancellation.Cancel();
            yield return "Should not be logged";
        }
        try
        {
            Assert.ThrowsException<OperationCanceledException>(() => CleanupLog.AppendScanDiagnostics(log, Messages(), cancellation.Token));
            var content = File.ReadAllText(log);
            StringAssert.Contains(content, @"First\nentry");
            Assert.IsFalse(content.Contains("Should not be logged"));
            using var exclusive = new FileStream(log, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
        }
        finally { Directory.Delete(root, true); }
    }

    [TestMethod]
    public void CancelledExportLeavesNoPartialReportOrTemporaryFile()
    {
        var root = Path.Combine(Path.GetTempPath(), "DvcExportTest", Guid.NewGuid().ToString("N"));
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        try
        {
            var result = DryRunExporter.Export(root, new(root, 60, KeepMode.Oldest, false, []), cancellationToken: cancellation.Token);
            Assert.IsNull(result.CsvPath);
            Assert.IsNull(result.TextPath);
            Assert.AreEqual(2, result.Errors.Count);
            Assert.AreEqual(0, Directory.GetFiles(root).Length);
        }
        finally { Directory.Delete(root, true); }
    }
}
