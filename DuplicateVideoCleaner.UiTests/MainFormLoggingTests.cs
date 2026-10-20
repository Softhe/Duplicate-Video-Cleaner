using System.Diagnostics;
using System.Reflection;
using DuplicateVideoCleaner;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace DuplicateVideoCleaner.UiTests;

[TestClass]
public sealed class MainFormLoggingTests
{
    [TestMethod]
    public void ScanLogEscapesLinesAndRejectsHardLinkedDestinations()
    {
        var root = Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), "dvc-form-log-" + Guid.NewGuid().ToString("N"))).FullName;
        try
        {
            var log = Path.Combine(root, "scan.log");
            Append(log, "filename\r\nFORGED RECORD");
            Assert.AreEqual(1, File.ReadAllLines(log).Length);
            StringAssert.Contains(File.ReadAllText(log), "filename\\r\\nFORGED RECORD");
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
            var failure = Assert.ThrowsException<TargetInvocationException>(() => Append(alias, "unsafe write"));
            Assert.IsInstanceOfType<IOException>(failure.InnerException);
            Assert.AreEqual("preserve original content", File.ReadAllText(target));
        }
        finally { Directory.Delete(root, true); }
    }

    private static void Append(string path, string message) => typeof(MainForm)
        .GetMethod("AppendLog", BindingFlags.Static | BindingFlags.NonPublic)!.Invoke(null, [path, message]);
}
