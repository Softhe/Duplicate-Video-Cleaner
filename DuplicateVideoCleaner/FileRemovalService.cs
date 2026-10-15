using Microsoft.VisualBasic.FileIO;

namespace DuplicateVideoCleaner;

internal sealed record RemovalFailure(VideoCandidate File, string Error);
internal sealed record RemovalResult(IReadOnlySet<string> RemovedPaths, IReadOnlyList<RemovalFailure> Failures);

internal static class FileRemovalService
{
    internal static async Task<RemovalResult> RemoveAsync(IReadOnlyList<VideoCandidate> files, bool permanent, string logPath)
    {
        return await Task.Run(() =>
        {
            var removed = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var failures = new List<RemovalFailure>();
            foreach (var file in files)
            {
                try
                {
                    if (!File.Exists(file.FullPath)) throw new FileNotFoundException("The file no longer exists.", file.FullPath);
                    if (permanent) File.Delete(file.FullPath);
                    else FileSystem.DeleteFile(file.FullPath, UIOption.OnlyErrorDialogs, RecycleOption.SendToRecycleBin);
                    removed.Add(file.FullPath);
                    AppendLog(logPath, $"DELETE SUCCESS | Mode={(permanent ? "Permanent" : "RecycleBin")} | {file.FullPath}");
                }
                catch (Exception ex)
                {
                    failures.Add(new RemovalFailure(file, ex.Message));
                    AppendLog(logPath, $"DELETE FAILED | Mode={(permanent ? "Permanent" : "RecycleBin")} | {file.FullPath} | {ex.Message}");
                }
            }
            return new RemovalResult(removed, failures);
        });
    }

    private static void AppendLog(string path, string message) => File.AppendAllText(path, $"{DateTime.Now:yyyy-MM-dd HH:mm:ss} | {message}{Environment.NewLine}");
}
