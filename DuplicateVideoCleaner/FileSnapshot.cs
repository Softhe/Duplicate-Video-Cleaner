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

    internal static void DeleteValidated(VideoCandidate file, bool movedSinceSnapshot = false)
    {
        using var directories = PinDirectories(Path.GetDirectoryName(Path.GetFullPath(file.FullPath))!);
        using var handle = OpenValidated(file, movedSinceSnapshot);
        var disposition = new FileDispositionInformation { DeleteFile = true };
        if (!SetFileInformationByHandle(handle, 4, ref disposition, 1))
            throw new IOException("The validated file could not be removed.", new Win32Exception(Marshal.GetLastWin32Error()));
    }

    // movedSinceSnapshot: the file was renamed into another folder after its snapshot
    // (Recovery). FAT-family volumes derive file IDs from directory entries, so the ID
    // changes on such a move; there the volume, size and timestamps must still match.
    internal static SafeFileHandle OpenValidated(VideoCandidate file, bool movedSinceSnapshot = false)
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
            ValidateReadHandle(handle, file, movedSinceSnapshot);
            return handle;
        }
        catch { handle.Dispose(); throw; }
    }

    internal static void ValidateReadHandle(SafeFileHandle handle, VideoCandidate expected, bool movedSinceSnapshot = false)
    {
        var actual = CaptureReadHandle(handle, expected.FullPath);
        var fatFamily = IsFatFamily(handle);
        var identityMatches = actual.FileIdentity == expected.FileIdentity ||
            movedSinceSnapshot && fatFamily && VolumeOf(actual.FileIdentity) == VolumeOf(expected.FileIdentity);
        if (!identityMatches || actual.Size != expected.Size ||
            !TimesMatch(actual.CreationTimeUtc, actual.LastWriteTimeUtc, expected.CreationTimeUtc, expected.LastWriteTimeUtc, fatFamily))
            throw new IOException("The file changed since the scan. Scan again before continuing.");
    }

    // FAT stores local times, which Windows converts with the current UTC offset: after a
    // daylight saving change both times read back shifted by the same whole hour.
    private static bool TimesMatch(DateTime? creation, DateTime? write, DateTime? expectedCreation, DateTime? expectedWrite, bool fatFamily)
    {
        if (creation == expectedCreation && write == expectedWrite) return true;
        if (!fatFamily || creation is not { } c || write is not { } w || expectedCreation is not { } ec || expectedWrite is not { } ew) return false;
        var shift = c - ec;
        return shift == w - ew && (shift == TimeSpan.FromHours(1) || shift == TimeSpan.FromHours(-1));
    }

    private static string? VolumeOf(string? identity) => identity is null ? null : identity.Split(':')[0];

    internal static bool IsFatFamily(SafeFileHandle handle)
    {
        var name = new System.Text.StringBuilder(64);
        if (!GetVolumeInformationByHandle(handle, null, 0, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, name, (uint)name.Capacity)) return false;
        var fileSystem = name.ToString();
        return fileSystem.StartsWith("FAT", StringComparison.OrdinalIgnoreCase) || fileSystem.Equals("exFAT", StringComparison.OrdinalIgnoreCase);
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
        // The innermost real directory: the requested directory, or the directory it
        // resolves to when it is itself a link.
        internal SafeFileHandle Last { get; set; } = null!;
        public void Dispose() { for (var index = Handles.Count - 1; index >= 0; index--) Handles[index].Dispose(); }
    }

    // Pins every directory from the volume root to the requested directory against
    // rename, deletion and link retargeting while the handles are held. A junction,
    // mounted volume or directory symbolic link on the way is pinned together with the
    // complete directory chain it resolves to, so the path cannot be redirected either.
    internal static PinnedDirectories PinDirectories(string directory)
    {
        var pinned = new PinnedDirectories();
        try
        {
            pinned.Last = PinChain(Path.GetFullPath(directory), pinned, 0);
            return pinned;
        }
        catch { pinned.Dispose(); throw; }
    }

    private static SafeFileHandle PinChain(string full, PinnedDirectories pinned, int depth)
    {
        if (depth > 8) throw new IOException("A parent directory resolves through too many links. Removal and recovery are blocked.");
        var root = Path.GetPathRoot(full)!;
        var current = root;
        var last = Pin(current);
        foreach (var part in full[root.Length..].Split(Path.DirectorySeparatorChar, StringSplitOptions.RemoveEmptyEntries))
        {
            current = Path.Combine(current, part);
            last = Pin(current);
        }
        return last;

        SafeFileHandle Pin(string path)
        {
            // List access takes part in sharing checks (attribute-only opens do not), so other
            // processes cannot delete, rename or retarget the directory while this is held.
            var handle = CreateFile(ExtendedPath(path), 0x81, 3, IntPtr.Zero, 3, 0x02200000, IntPtr.Zero);
            if (handle.IsInvalid && Marshal.GetLastWin32Error() == 5)
            {
                // A traverse-only ancestor (common on network shares) refuses list access.
                // Hold it with attribute access, as 1.1.2 did; deeper pins still apply.
                handle.Dispose();
                handle = CreateFile(ExtendedPath(path), 0x80, 3, IntPtr.Zero, 3, 0x02200000, IntPtr.Zero);
            }
            if (handle.IsInvalid) { handle.Dispose(); throw new IOException("A parent directory could not be pinned safely.", new Win32Exception(Marshal.GetLastWin32Error())); }
            pinned.Handles.Add(handle);
            if (!GetFileInformationByHandle(handle, out var information) || (information.FileAttributes & (uint)FileAttributes.Directory) == 0)
                throw new IOException("A parent path is not a directory. Removal and recovery are blocked.");
            if ((information.FileAttributes & (uint)FileAttributes.ReparsePoint) == 0) return handle;
            var tag = ReparseTag(handle);
            // Non-link reparse points (for example cloud-sync folders) are ordinary directories.
            if ((tag & 0x20000000) == 0) return handle;
            if (tag is not (0xA0000003 or 0xA000000C))
                throw new IOException("A parent directory is an unsupported link type. Removal and recovery are blocked.");
            return PinChain(ResolveDirectory(path), pinned, depth + 1);
        }
    }

    private static string ResolveDirectory(string path)
    {
        using var target = CreateFile(ExtendedPath(path), 0x80, 7, IntPtr.Zero, 3, 0x02000000, IntPtr.Zero);
        if (target.IsInvalid) throw new IOException("A linked parent directory could not be resolved.", new Win32Exception(Marshal.GetLastWin32Error()));
        var buffer = new System.Text.StringBuilder(32768);
        // Volume GUID paths also work for mounted volumes without a drive letter.
        var length = GetFinalPathNameByHandle(target, buffer, (uint)buffer.Capacity, 1);
        if (length == 0 || length >= buffer.Capacity)
            throw new IOException("A linked parent directory could not be resolved.", new Win32Exception(Marshal.GetLastWin32Error()));
        return buffer.ToString();
    }

    internal static uint ReparseTag(SafeFileHandle handle)
    {
        if (!GetFileInformationByHandleEx(handle, 9, out var information, (uint)Marshal.SizeOf<FileAttributeTagInfo>()))
            throw new IOException("The reparse point type could not be read.", new Win32Exception(Marshal.GetLastWin32Error()));
        return (information.FileAttributes & (uint)FileAttributes.ReparsePoint) == 0 ? 0 : information.ReparseTag;
    }

    // True for junctions, mount points, symbolic links and other name-surrogate reparse
    // points that redirect a path. False for ordinary entries and data reparse points.
    internal static bool IsLink(string path)
    {
        using var handle = CreateFile(ExtendedPath(Path.GetFullPath(path)), 0x80, 7, IntPtr.Zero, 3, 0x02200000, IntPtr.Zero);
        if (handle.IsInvalid) throw new IOException("The entry could not be inspected.", new Win32Exception(Marshal.GetLastWin32Error()));
        return (ReparseTag(handle) & 0x20000000) != 0;
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

    [StructLayout(LayoutKind.Sequential)]
    private struct FileAttributeTagInfo { public uint FileAttributes; public uint ReparseTag; }

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetFileInformationByHandleEx(SafeFileHandle handle, int informationClass, out FileAttributeTagInfo information, uint size);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true, EntryPoint = "GetVolumeInformationByHandleW")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetVolumeInformationByHandle(SafeFileHandle handle, System.Text.StringBuilder? volumeName, uint volumeNameLength,
        IntPtr serialNumber, IntPtr maximumComponentLength, IntPtr flags, System.Text.StringBuilder fileSystemName, uint fileSystemNameLength);

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
