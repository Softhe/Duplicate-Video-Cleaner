using System.ComponentModel;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace DuplicateVideoCleaner;

internal static class FileSnapshot
{
    internal static string GetIdentity(string path)
    {
        using var handle = File.OpenHandle(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        if (!GetFileInformationByHandle(handle, out var info))
            throw new IOException("The file identity could not be read.", new Win32Exception(Marshal.GetLastWin32Error()));
        return $"{info.VolumeSerialNumber:X8}:{info.FileIndexHigh:X8}{info.FileIndexLow:X8}";
    }

    internal static void DeleteValidated(VideoCandidate file)
    {
        using var directories = PinDirectories(Path.GetDirectoryName(Path.GetFullPath(file.FullPath))!);
        using var handle = OpenValidated(file);
        var disposition = new FileDispositionInformation { DeleteFile = true };
        if (!SetFileInformationByHandle(handle, 4, ref disposition, 1))
            throw new IOException("The validated file could not be removed.", new Win32Exception(Marshal.GetLastWin32Error()));
    }

    internal static SafeFileHandle OpenValidated(VideoCandidate file)
    {
        // Keep validation and deletion bound to one object, even if its path is replaced.
        const uint genericRead = 0x80000000, deleteAccess = 0x00010000;
        const uint shareRead = 1, openExisting = 3, openReparsePoint = 0x00200000;
        var path = Path.GetFullPath(file.FullPath);
        if (!path.StartsWith(@"\\?\", StringComparison.Ordinal))
            path = path.StartsWith(@"\\", StringComparison.Ordinal) ? @"\\?\UNC\" + path[2..] : @"\\?\" + path;
        var handle = CreateFile(path, genericRead | deleteAccess, shareRead,
            IntPtr.Zero, openExisting, openReparsePoint, IntPtr.Zero);
        try
        {
            if (handle.IsInvalid)
                throw new IOException("The file could not be opened safely for removal.", new Win32Exception(Marshal.GetLastWin32Error()));
            ValidateReadHandle(handle, file);
            return handle;
        }
        catch { handle.Dispose(); throw; }
    }

    internal static void ValidateReadHandle(SafeFileHandle handle, VideoCandidate expected)
    {
        var actual = CaptureReadHandle(handle, expected.FullPath);
        if (actual.FileIdentity != expected.FileIdentity || actual.Size != expected.Size ||
            actual.CreationTimeUtc != expected.CreationTimeUtc || actual.LastWriteTimeUtc != expected.LastWriteTimeUtc)
            throw new IOException("The file changed since the scan. Scan again before continuing.");
    }

    internal static VideoCandidate CaptureReadHandle(SafeFileHandle handle, string fullPath)
    {
        if (!GetFileInformationByHandle(handle, out var info))
            throw new IOException("The file snapshot could not be read.", new Win32Exception(Marshal.GetLastWin32Error()));
        if ((info.FileAttributes & (uint)(FileAttributes.ReparsePoint | FileAttributes.Directory)) != 0)
            throw new IOException("A metadata or removal source must be a regular file, not a reparse point.");
        var identity = $"{info.VolumeSerialNumber:X8}:{info.FileIndexHigh:X8}{info.FileIndexLow:X8}";
        var size = ((long)info.FileSizeHigh << 32) | info.FileSizeLow;
        static DateTime Utc(System.Runtime.InteropServices.ComTypes.FILETIME time) =>
            DateTime.FromFileTimeUtc(((long)time.dwHighDateTime << 32) | (uint)time.dwLowDateTime);
        var creation = Utc(info.CreationTime);
        return new VideoCandidate(Path.GetFullPath(fullPath), Path.GetFileName(fullPath), creation,
            TimestampSource.CreationTime, size, creation, Utc(info.LastWriteTime), identity);
    }

    internal sealed class PinnedDirectories : IDisposable
    {
        internal List<SafeFileHandle> Handles { get; } = [];
        internal SafeFileHandle Last => Handles[^1];
        public void Dispose() { for (var index = Handles.Count - 1; index >= 0; index--) Handles[index].Dispose(); }
    }

    internal static PinnedDirectories PinDirectories(string directory)
    {
        var pinned = new PinnedDirectories();
        try
        {
            var full = Path.GetFullPath(directory);
            var root = Path.GetPathRoot(full)!;
            var current = root;
            Pin(current);
            foreach (var part in full[root.Length..].Split(Path.DirectorySeparatorChar, StringSplitOptions.RemoveEmptyEntries))
            {
                current = Path.Combine(current, part);
                Pin(current);
            }
            return pinned;

            void Pin(string path)
            {
                var handle = CreateFile(ExtendedPath(path), 0x80, 1, IntPtr.Zero, 3, 0x02200000, IntPtr.Zero);
                if (handle.IsInvalid) { handle.Dispose(); throw new IOException("A parent directory could not be pinned safely.", new Win32Exception(Marshal.GetLastWin32Error())); }
                pinned.Handles.Add(handle);
                if (!GetFileInformationByHandle(handle, out var information) ||
                    (information.FileAttributes & (uint)FileAttributes.Directory) == 0 ||
                    (information.FileAttributes & (uint)FileAttributes.ReparsePoint) != 0)
                    throw new IOException("A parent directory is a symbolic link or reparse point. Removal and recovery are blocked.");
            }
        }
        catch { pinned.Dispose(); throw; }
    }

    internal static void Rename(SafeFileHandle file, SafeFileHandle destinationDirectory, string name)
    {
        if (name != Path.GetFileName(name) || string.IsNullOrWhiteSpace(name) || name is "." or ".." || name.Contains(':'))
            throw new IOException("Invalid recovery destination name.");
        var directoryPath = new System.Text.StringBuilder(32768);
        var pathLength = GetFinalPathNameByHandle(destinationDirectory, directoryPath, (uint)directoryPath.Capacity, 0);
        if (pathLength == 0 || pathLength >= directoryPath.Capacity)
            throw new IOException("The pinned recovery destination could not be resolved.", new Win32Exception(Marshal.GetLastWin32Error()));
        // Resolve from the destination handle while its complete directory chain
        // remains pinned. SetFileInformationByHandle expects a full Win32 path.
        var nameBytes = System.Text.Encoding.Unicode.GetBytes(Path.Combine(directoryPath.ToString(), name));
        var rootOffset = IntPtr.Size == 8 ? 8 : 4;
        var lengthOffset = rootOffset + IntPtr.Size;
        var nameOffset = lengthOffset + sizeof(uint);
        var size = nameOffset + nameBytes.Length + 2;
        var buffer = Marshal.AllocHGlobal(size);
        try
        {
            Marshal.Copy(new byte[size], 0, buffer, size);
            Marshal.WriteInt32(buffer, lengthOffset, nameBytes.Length);
            Marshal.Copy(nameBytes, 0, buffer + nameOffset, nameBytes.Length);
            if (!SetRenameInformation(file, 3, buffer, (uint)size))
                throw new IOException("The validated file could not be moved. The destination must be on the same volume and must not exist. " + new Win32Exception(Marshal.GetLastWin32Error()).Message);
        }
        finally { Marshal.FreeHGlobal(buffer); }
    }

    internal static FileStream OpenAppendLog(string path)
    {
        var handle = CreateFile(ExtendedPath(Path.GetFullPath(path)), 0x40000080, 1, IntPtr.Zero, 4, 0x00200000, IntPtr.Zero);
        try
        {
            if (handle.IsInvalid || !GetFileInformationByHandle(handle, out var information))
                throw new IOException("The audit log could not be opened safely.", new Win32Exception(Marshal.GetLastWin32Error()));
            if ((information.FileAttributes & (uint)(FileAttributes.Directory | FileAttributes.ReparsePoint)) != 0 || information.NumberOfLinks != 1)
                throw new IOException("The audit log must be a regular file with no additional hard links.");
            var stream = new FileStream(handle, FileAccess.Write, bufferSize: 1);
            stream.Seek(0, SeekOrigin.End);
            return stream;
        }
        catch { handle.Dispose(); throw; }
    }

    private static string ExtendedPath(string path) => path.StartsWith(@"\\?\", StringComparison.Ordinal) ? path :
        path.StartsWith(@"\\", StringComparison.Ordinal) ? @"\\?\UNC\" + path[2..] : @"\\?\" + path;

    [DllImport("kernel32.dll", SetLastError = true, EntryPoint = "SetFileInformationByHandle")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetRenameInformation(SafeFileHandle handle, int informationClass, IntPtr information, uint size);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern uint GetFinalPathNameByHandle(SafeFileHandle file, System.Text.StringBuilder path, uint length, uint flags);

    [StructLayout(LayoutKind.Sequential)]
    private struct FileDispositionInformation
    {
        [MarshalAs(UnmanagedType.U1)] public bool DeleteFile;
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true, EntryPoint = "CreateFileW")]
    private static extern SafeFileHandle CreateFile(string path, uint access, uint share, IntPtr security, uint creation, uint flags, IntPtr template);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetFileInformationByHandle(SafeFileHandle handle, int informationClass,
        ref FileDispositionInformation information, uint size);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetFileInformationByHandle(SafeFileHandle handle, out ByHandleFileInformation information);

    [StructLayout(LayoutKind.Sequential)]
    private struct ByHandleFileInformation
    {
        public uint FileAttributes;
        public System.Runtime.InteropServices.ComTypes.FILETIME CreationTime;
        public System.Runtime.InteropServices.ComTypes.FILETIME LastAccessTime;
        public System.Runtime.InteropServices.ComTypes.FILETIME LastWriteTime;
        public uint VolumeSerialNumber;
        public uint FileSizeHigh;
        public uint FileSizeLow;
        public uint NumberOfLinks;
        public uint FileIndexHigh;
        public uint FileIndexLow;
    }
}
