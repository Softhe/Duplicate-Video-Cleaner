using System.Text;

namespace DuplicateVideoCleaner;

// DuplicateCleanupLog.txt in the scanned folder. Every line is escaped so a file name
// cannot forge a record, and the log is opened through the same pinned, link-rejecting
// path as removal.
internal static class CleanupLog
{
    internal const string FileName = "DuplicateCleanupLog.txt";

    internal static string PathFor(string folder) => Path.Combine(folder, FileName);

    internal static void Append(string logPath, string message)
    {
        using var directories = FileSnapshot.PinDirectories(Path.GetDirectoryName(Path.GetFullPath(logPath))!);
        using var stream = FileSnapshot.OpenAppendLog(logPath);
        using var writer = new StreamWriter(stream, new UTF8Encoding(false), leaveOpen: true);
        writer.WriteLine(Line(message));
        writer.Flush();
        stream.Flush(true);
    }

    // Returns the error message instead of throwing, so a scan can finish and report it.
    internal static string? AppendScanDiagnostics(string logPath, IEnumerable<string> messages, CancellationToken cancellationToken)
    {
        try
        {
            using var directories = FileSnapshot.PinDirectories(Path.GetDirectoryName(Path.GetFullPath(logPath))!);
            using var stream = FileSnapshot.OpenAppendLog(logPath);
            using var writer = new StreamWriter(stream, new UTF8Encoding(false), leaveOpen: true);
            var count = 0;
            foreach (var message in messages)
            {
                cancellationToken.ThrowIfCancellationRequested();
                writer.WriteLine(Line(message));
                if (++count % 64 == 0) writer.Flush();
            }
            writer.Flush();
            stream.Flush(true);
            return null;
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex) { return ex.Message; }
    }

    // Removal never starts unless its audit trail can be written.
    internal static bool TryStartRemovalSession(string logPath, out string error)
    {
        try { Append(logPath, "DELETE SESSION START"); error = string.Empty; return true; }
        catch (Exception ex) { error = ex.Message; return false; }
    }

    private static string Line(string message) =>
        $"{DateTime.Now:yyyy-MM-dd HH:mm:ss} | {message.Replace("\r", "\\r").Replace("\n", "\\n")}";
}
