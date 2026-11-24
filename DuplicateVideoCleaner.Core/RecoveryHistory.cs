using System.ComponentModel;
using System.Globalization;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace DuplicateVideoCleaner;

internal sealed record RecoveryHistoryResult(int Archived, int Preserved, IReadOnlyList<string> Warnings);

internal static partial class RecoveryStore
{
    internal const string HistoryDirectoryName = ".completed-history";

    // Explicit opt-in archival preserves original audit bytes and never removes media.
    internal static RecoveryHistoryResult ArchiveCompletedHistory(string root, DateTimeOffset completedBefore,
        CancellationToken cancellationToken = default, Action<string>? beforeArchive = null, Action<string>? afterArchive = null)
    {
        if (completedBefore > DateTimeOffset.UtcNow.AddDays(-1))
            throw new ArgumentOutOfRangeException(nameof(completedBefore), "History must be at least one day old.");
        var pending = new Stack<string>();
        pending.Push(Path.GetFullPath(root));
        var archived = 0;
        var preserved = 0;
        var warnings = new List<string>();
        while (pending.TryPop(out var directory))
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                using var parent = FileSnapshot.PinDirectories(directory);
                foreach (var child in Directory.EnumerateDirectories(directory))
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    try
                    {
                        if ((File.GetAttributes(child) & FileAttributes.ReparsePoint) != 0) continue;
                        if (!string.Equals(Path.GetFileName(child), DirectoryName, StringComparison.OrdinalIgnoreCase))
                        { pending.Push(child); continue; }
                        using var store = FileSnapshot.PinDirectories(child);
                        foreach (var entryPath in Directory.EnumerateDirectories(child))
                        {
                            cancellationToken.ThrowIfCancellationRequested();
                            if (string.Equals(Path.GetFileName(entryPath), HistoryDirectoryName, StringComparison.OrdinalIgnoreCase)) continue;
                            try
                            {
                                using var entryHandle = OpenHistoryObject(entryPath, true);
                                var children = Directory.EnumerateFileSystemEntries(entryPath).Select(Path.GetFileName).ToArray();
                                if (children.Length != 2 || !children.Contains("intent.json", StringComparer.OrdinalIgnoreCase) ||
                                    !children.Contains("actions.log", StringComparer.OrdinalIgnoreCase)) { preserved++; continue; }
                                using var manifest = OpenHistoryObject(Path.Combine(entryPath, "intent.json"), false);
                                using var journal = OpenHistoryObject(Path.Combine(entryPath, "actions.log"), false);
                                // Validate metadata while denying writers; Windows requires closing child handles before renaming the pinned directory.
                                var entry = ReadEntry(Path.Combine(entryPath, "intent.json"));
                                using var journalStream = new FileStream(journal, FileAccess.Read);
                                if (journalStream.Length > 1024 * 1024) { preserved++; continue; }
                                using var reader = new StreamReader(journalStream);
                                var lines = reader.ReadToEnd().Split('\n', StringSplitOptions.RemoveEmptyEntries);
                                var last = lines.LastOrDefault()?.TrimEnd('\r');
                                var separator = last?.IndexOf(" | ", StringComparison.Ordinal) ?? -1;
                                if (separator < 0 || !DateTimeOffset.TryParseExact(last![..separator], "O", CultureInfo.InvariantCulture,
                                        DateTimeStyles.None, out var completed) || completed >= completedBefore || completed < entry.RemovedAt ||
                                    last[(separator + 3)..] is not ("RESTORE SUCCESS" or "PURGE SUCCESS")) { preserved++; continue; }
                                var archive = Path.Combine(child, HistoryDirectoryName);
                                Directory.CreateDirectory(archive);
                                using var destination = FileSnapshot.PinDirectories(archive);
                                reader.Dispose();
                                journalStream.Dispose();
                                manifest.Dispose();
                                beforeArchive?.Invoke(entryPath);
                                if (!ContainsOnlyHistory(entryPath)) { preserved++; continue; }
                                FileSnapshot.Rename(entryHandle, destination.Last, Path.GetFileName(entryPath));
                                var movedPath = Path.Combine(archive, Path.GetFileName(entryPath));
                                afterArchive?.Invoke(movedPath);
                                if (!ContainsOnlyHistory(movedPath))
                                {
                                    FileSnapshot.Rename(entryHandle, store.Last, Path.GetFileName(entryPath));
                                    preserved++;
                                    warnings.Add($"History changed during archival and was restored: {entryPath}");
                                    continue;
                                }
                                archived++;
                            }
                            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Text.Json.JsonException or ArgumentException)
                            { preserved++; warnings.Add($"History requires inspection at {entryPath} or its .completed-history location: {ex.Message}"); }
                        }
                    }
                    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                    { warnings.Add($"History location could not be read: {child}: {ex.Message}"); }
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            { warnings.Add($"History location could not be read: {directory}: {ex.Message}"); }
        }
        return new(archived, preserved, warnings);
    }

    private static bool ContainsOnlyHistory(string path)
    {
        var names = Directory.EnumerateFileSystemEntries(path).Select(Path.GetFileName).ToArray();
        return names.Length == 2 && names.Contains("intent.json", StringComparer.OrdinalIgnoreCase) &&
            names.Contains("actions.log", StringComparer.OrdinalIgnoreCase);
    }

    private static SafeFileHandle OpenHistoryObject(string path, bool directory)
    {
        var full = Path.GetFullPath(path);
        var extended = full.StartsWith(@"\\?\", StringComparison.Ordinal) ? full :
            full.StartsWith(@"\\", StringComparison.Ordinal) ? @"\\?\UNC\" + full[2..] : @"\\?\" + full;
        var handle = OpenHistoryHandle(extended, directory ? 0x80010080u : 0x80000080u, 1, IntPtr.Zero, 3,
            directory ? 0x02200000u : 0x00200000u, IntPtr.Zero);
        try
        {
            if (handle.IsInvalid || !ReadHistoryInfo(handle, out var info))
                throw new IOException("History object could not be opened safely.", new Win32Exception(Marshal.GetLastWin32Error()));
            if ((info.Attributes & (uint)FileAttributes.ReparsePoint) != 0 ||
                ((info.Attributes & (uint)FileAttributes.Directory) != 0) != directory || (!directory && info.Links != 1))
                throw new IOException("History must contain regular metadata with no reparse points or additional hard links.");
            return handle;
        }
        catch { handle.Dispose(); throw; }
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true, EntryPoint = "CreateFileW")]
    private static extern SafeFileHandle OpenHistoryHandle(string path, uint access, uint share, IntPtr security, uint creation, uint flags, IntPtr template);
    [DllImport("kernel32.dll", SetLastError = true, EntryPoint = "GetFileInformationByHandle")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ReadHistoryInfo(SafeFileHandle file, out HistoryInfo info);
    [StructLayout(LayoutKind.Sequential)]
    private struct HistoryInfo
    {
        public uint Attributes;
        public System.Runtime.InteropServices.ComTypes.FILETIME Creation, Access, Write;
        public uint Volume, SizeHigh, SizeLow, Links, IndexHigh, IndexLow;
    }
}
