using System.ComponentModel;
using System.Diagnostics;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace DuplicateVideoCleaner;

// The broker copies only the selected media and trusted probe files. The parser
// receives a zero-capability AppContainer token and a non-breakaway one-process job.
internal sealed class WindowsProbeSandbox : IDisposable
{
    private readonly string _profileName = "DVC.Probe." + Guid.NewGuid().ToString("N");
    private readonly string _root;
    private FileStream? _ownerMarker;
    private IntPtr _sid;
    private bool _profileCreated;
    private bool _disposed;
    // Serialize disk copies across probes so each free-space check includes prior
    // completed writes. Parsing remains concurrent after the copy gate is released.
    private static readonly SemaphoreSlim StagingGate = new(1, 1);
    internal string ExecutablePath { get; private set; } = "";
    internal string Root => _root;
    internal List<string> CleanupWarnings { get; } = [];

    internal WindowsProbeSandbox(string executable, CancellationToken cancellationToken = default)
    {
        _root = Path.Combine(Path.GetTempPath(), "dvc-sandbox-" + _profileName["DVC.Probe.".Length..]);
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("Metadata isolation requires Windows AppContainer.");
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            Directory.CreateDirectory(_root);
            _ownerMarker = AbandonedProbeCleanup.CreateMarker(_root, _profileName["DVC.Probe.".Length..]);
            Marshal.ThrowExceptionForHR(CreateAppContainerProfile(_profileName, "DVC metadata isolation", "Temporary read-only metadata parser", IntPtr.Zero, 0, out _sid));
            _profileCreated = true;
            var directory = Directory.CreateDirectory(_root);
            var security = directory.GetAccessControl();
            security.AddAccessRule(new FileSystemAccessRule(new SecurityIdentifier(_sid), FileSystemRights.ReadAndExecute,
                InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit, PropagationFlags.None, AccessControlType.Allow));
            directory.SetAccessControl(security);
            var tools = Directory.CreateDirectory(Path.Combine(_root, "tools")).FullName;
            var original = Path.GetFullPath(executable);
            var executableBase = Path.GetFileNameWithoutExtension(original);
            var sourceDirectory = Path.GetDirectoryName(original)!;
            foreach (var file in Directory.EnumerateFiles(sourceDirectory))
            {
                cancellationToken.ThrowIfCancellationRequested();
                var extension = Path.GetExtension(file);
                var name = Path.GetFileName(file);
                var runtimeConfiguration = name.Equals(executableBase + ".deps.json", StringComparison.OrdinalIgnoreCase) ||
                    name.Equals(executableBase + ".runtimeconfig.json", StringComparison.OrdinalIgnoreCase);
                if (!extension.Equals(".dll", StringComparison.OrdinalIgnoreCase) &&
                    !runtimeConfiguration &&
                    !Path.GetFullPath(file).Equals(original, StringComparison.OrdinalIgnoreCase)) continue;
                if ((File.GetAttributes(file) & FileAttributes.ReparsePoint) != 0) throw new IOException("Metadata tool files must not be symbolic links.");
                File.Copy(file, Path.Combine(tools, Path.GetFileName(file)));
            }
            ExecutablePath = Path.Combine(tools, Path.GetFileName(original));
            if (!File.Exists(ExecutablePath)) throw new IOException("The metadata executable could not be staged.");
        }
        catch { Dispose(); throw; }
    }

    internal async Task<string> StageMediaAsync(string path, CancellationToken cancellationToken, VideoCandidate? expected = null, Action<int>? copied = null)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        var folder = Directory.CreateDirectory(Path.Combine(_root, Guid.NewGuid().ToString("N"))).FullName;
        var staged = Path.Combine(folder, Path.GetFileName(path));
        var volume = Path.GetPathRoot(_root)!;
        await StagingGate.WaitAsync(cancellationToken);
        try
        {
            using var directories = FileSnapshot.PinDirectories(Path.GetDirectoryName(Path.GetFullPath(path))!);
            await using var source = OpenReadOnlyMedia(path);
            expected ??= FileSnapshot.CaptureReadHandle(source.SafeFileHandle, Path.GetFullPath(path));
            FileSnapshot.ValidateReadHandle(source.SafeFileHandle, expected);
            if (new DriveInfo(volume).AvailableFreeSpace - 64L * 1024 * 1024 < source.Length)
                throw new IOException("Insufficient temporary disk space for an isolated metadata copy. Free space and scan again.");
            await using var destination = new FileStream(staged, FileMode.CreateNew, FileAccess.Write, FileShare.None, 1024 * 1024, FileOptions.Asynchronous);
            var buffer = new byte[1024 * 1024];
            while (true)
            {
                var count = await source.ReadAsync(buffer.AsMemory(), cancellationToken);
                if (count == 0) break;
                await destination.WriteAsync(buffer.AsMemory(0, count), cancellationToken);
                copied?.Invoke(count);
            }
            return staged;
        }
        catch { TryRemoveMediaDirectory(folder); throw; }
        finally { StagingGate.Release(); }
    }

    internal static FileStream OpenReadOnlyMedia(string path)
    {
        var full = Path.GetFullPath(path);
        var extended = full.StartsWith(@"\\?\", StringComparison.Ordinal) ? full : full.StartsWith(@"\\", StringComparison.Ordinal) ? @"\\?\UNC\" + full[2..] : @"\\?\" + full;
        var handle = CreateFile(extended, 0x80000000, 1, IntPtr.Zero, 3, 0x08200000, IntPtr.Zero);
        try
        {
            if (handle.IsInvalid) throw new IOException("The selected media could not be opened read-only.", new Win32Exception(Marshal.GetLastWin32Error()));
            return new FileStream(handle, FileAccess.Read, 1024 * 1024);
        }
        catch { handle.Dispose(); throw; }
    }

    internal SandboxedProcess Start(IReadOnlyList<string> arguments)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        return SandboxedProcess.Start(ExecutablePath, _root, _sid, arguments);
    }

    internal string? RemoveMedia(string staged)
    {
        var directory = Path.GetDirectoryName(Path.GetFullPath(staged))!;
        if (!Path.GetDirectoryName(directory)!.Equals(_root, StringComparison.OrdinalIgnoreCase)) throw new IOException("Unexpected metadata staging path.");
        return TryRemoveMediaDirectory(directory);
    }

    private static string? TryRemoveMediaDirectory(string directory)
    {
        // The sandbox owner retries its entire private root at disposal. A temporary
        // sharing violation here must not discard metadata already obtained.
        for (var attempt = 0; attempt < 3; attempt++)
        {
            try { if (Directory.Exists(directory)) Directory.Delete(directory, true); return null; }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                if (attempt == 2) return "Metadata staging cleanup deferred to sandbox disposal: " + ex.Message;
                Thread.Sleep(50);
            }
        }
        return null;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _ownerMarker?.Dispose();
        _ownerMarker = null;
        try
        {
            for (var attempt = 0; Directory.Exists(_root); attempt++)
            {
                try { Directory.Delete(_root, true); break; }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    if (attempt >= 20)
                    {
                        CleanupWarnings.Add($"Temporary metadata files could not be removed from {_root}: {ex.Message}");
                        break;
                    }
                    Thread.Sleep(100);
                }
            }
        }
        finally
        {
            if (_sid != IntPtr.Zero) { FreeSid(_sid); _sid = IntPtr.Zero; }
            if (_profileCreated)
            {
                var result = DeleteAppContainerProfile(_profileName);
                _profileCreated = false;
                if (result < 0) CleanupWarnings.Add("The temporary metadata AppContainer profile could not be removed: " + Marshal.GetExceptionForHR(result)?.Message);
            }
        }
    }

    internal static void DeleteOwnedProfile(string profileName)
    {
        var result = DeleteAppContainerProfile(profileName);
        // A crash can occur after the marker is flushed and before profile creation.
        if (result is unchecked((int)0x80070002) or unchecked((int)0x80070003) or unchecked((int)0x80070490)) return;
        Marshal.ThrowExceptionForHR(result);
    }

    [DllImport("userenv.dll", CharSet = CharSet.Unicode)] private static extern int CreateAppContainerProfile(string name, string display, string description, IntPtr capabilities, uint count, out IntPtr sid);
    [DllImport("userenv.dll", CharSet = CharSet.Unicode)] private static extern int DeleteAppContainerProfile(string name);
    [DllImport("advapi32.dll")] private static extern IntPtr FreeSid(IntPtr sid);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true, EntryPoint = "CreateFileW")] private static extern SafeFileHandle CreateFile(string path, uint access, uint share, IntPtr security, uint creation, uint flags, IntPtr template);
}

internal sealed class SandboxedProcess : IDisposable
{
    private static readonly object LaunchLock = new();
    private SafeFileHandle? _job;
    private readonly AnonymousPipeServerStream _output;
    private readonly AnonymousPipeServerStream _error;
    internal Process Process { get; }
    internal StreamReader StandardOutput { get; }
    internal StreamReader StandardError { get; }
    internal bool IsolationVerified { get; }

    private SandboxedProcess(Process process, SafeFileHandle job, AnonymousPipeServerStream output, AnonymousPipeServerStream error)
    {
        Process = process; _job = job; _output = output; _error = error;
        StandardOutput = new StreamReader(output, Encoding.UTF8); StandardError = new StreamReader(error, Encoding.UTF8);
        IsolationVerified = true;
    }

    internal static SandboxedProcess Start(string executable, string directory, IntPtr sid, IReadOnlyList<string> arguments)
    {
        var output = new AnonymousPipeServerStream(PipeDirection.In, HandleInheritability.Inheritable);
        var error = new AnonymousPipeServerStream(PipeDirection.In, HandleInheritability.Inheritable);
        var input = new AnonymousPipeServerStream(PipeDirection.Out, HandleInheritability.Inheritable);
        SafeFileHandle? job = null;
        Process? process = null;
        IntPtr attributes = IntPtr.Zero, capabilitiesPointer = IntPtr.Zero, handlesPointer = IntPtr.Zero, environmentPointer = IntPtr.Zero;
        var native = new ProcessInformation();
        try
        {
            job = CreateJobObject(IntPtr.Zero, null);
            if (job.IsInvalid) throw Error("Create metadata job");
            var limits = new ExtendedLimits
            {
                Basic = new BasicLimits { LimitFlags = 0x2000 | 0x8 | 0x100 | 0x2, ActiveProcessLimit = 1, PerProcessUserTimeLimit = TimeSpan.FromSeconds(20).Ticks },
                ProcessMemoryLimit = (UIntPtr)(512UL * 1024 * 1024)
            };
            if (!SetInformationJobObject(job, 9, ref limits, (uint)Marshal.SizeOf<ExtendedLimits>())) throw Error("Limit metadata job");
            nuint bytes = 0;
            InitializeProcThreadAttributeList(IntPtr.Zero, 2, 0, ref bytes);
            attributes = Marshal.AllocHGlobal(checked((int)bytes));
            if (!InitializeProcThreadAttributeList(attributes, 2, 0, ref bytes)) throw Error("Initialize metadata attributes");
            var capabilities = new SecurityCapabilities { AppContainerSid = sid };
            capabilitiesPointer = Marshal.AllocHGlobal(Marshal.SizeOf<SecurityCapabilities>());
            Marshal.StructureToPtr(capabilities, capabilitiesPointer, false);
            if (!UpdateProcThreadAttribute(attributes, 0, (IntPtr)0x20009, capabilitiesPointer, (nuint)Marshal.SizeOf<SecurityCapabilities>(), IntPtr.Zero, IntPtr.Zero)) throw Error("Apply AppContainer token");
            var inherited = new[] { input.ClientSafePipeHandle.DangerousGetHandle(), output.ClientSafePipeHandle.DangerousGetHandle(), error.ClientSafePipeHandle.DangerousGetHandle() };
            handlesPointer = Marshal.AllocHGlobal(IntPtr.Size * inherited.Length);
            Marshal.Copy(inherited, 0, handlesPointer, inherited.Length);
            if (!UpdateProcThreadAttribute(attributes, 0, (IntPtr)0x20002, handlesPointer, (nuint)(IntPtr.Size * inherited.Length), IntPtr.Zero, IntPtr.Zero)) throw Error("Restrict inherited metadata handles");
            var startup = new StartupInfoEx { Attributes = attributes };
            startup.Startup.Size = Marshal.SizeOf<StartupInfoEx>();
            startup.Startup.Flags = 0x100;
            startup.Startup.Input = inherited[0]; startup.Startup.Output = inherited[1]; startup.Startup.Error = inherited[2];
            // Do not expose credentials, proxy configuration, or caller-controlled
            // runtime/startup hooks from the broker's environment to the parser.
            var windows = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
            var environment = new SortedDictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["SystemRoot"] = windows, ["WINDIR"] = windows,
                ["PATH"] = Path.Combine(windows, "System32"), ["TEMP"] = directory, ["TMP"] = directory,
                ["USERPROFILE"] = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                ["LOCALAPPDATA"] = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                ["APPDATA"] = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                ["DOTNET_EnableDiagnostics"] = "0"
            };
            environmentPointer = Marshal.StringToHGlobalUni(string.Join('\0', environment.Select(pair => pair.Key + "=" + pair.Value)) + "\0\0");
            var command = new StringBuilder(string.Join(" ", new[] { executable }.Concat(arguments).Select(Quote)));
            lock (LaunchLock)
            {
                var previous = SetErrorMode(0x8003);
                try
                {
                    if (!CreateProcess(executable, command, IntPtr.Zero, IntPtr.Zero, true, 0x08000000 | 0x00080000 | 0x400 | 0x4, environmentPointer, directory, ref startup, out native)) throw Error("Start isolated metadata process");
                }
                finally { SetErrorMode(previous); }
            }
            process = Process.GetProcessById(native.ProcessId);
            if (!AssignProcessToJobObject(job, native.Process)) throw Error("Assign isolated metadata job");
            VerifyToken(native.Process, sid);
            if (!IsProcessInJob(native.Process, job, out var inJob) || !inJob) throw new IOException("Metadata job isolation could not be verified.");
            if (ResumeThread(native.Thread) == uint.MaxValue) throw Error("Resume isolated metadata process");
            output.DisposeLocalCopyOfClientHandle(); error.DisposeLocalCopyOfClientHandle(); input.DisposeLocalCopyOfClientHandle(); input.Dispose();
            var result = new SandboxedProcess(process, job, output, error);
            job = null; process = null;
            return result;
        }
        catch
        {
            if (native.Process != IntPtr.Zero) TerminateProcess(native.Process, 1);
            process?.Dispose(); job?.Dispose(); output.Dispose(); error.Dispose(); input.Dispose();
            throw;
        }
        finally
        {
            if (native.Thread != IntPtr.Zero) CloseHandle(native.Thread);
            if (native.Process != IntPtr.Zero) CloseHandle(native.Process);
            if (attributes != IntPtr.Zero) { DeleteProcThreadAttributeList(attributes); Marshal.FreeHGlobal(attributes); }
            if (capabilitiesPointer != IntPtr.Zero) Marshal.FreeHGlobal(capabilitiesPointer);
            if (handlesPointer != IntPtr.Zero) Marshal.FreeHGlobal(handlesPointer);
            if (environmentPointer != IntPtr.Zero) Marshal.FreeHGlobal(environmentPointer);
        }
    }

    private static void VerifyToken(IntPtr process, IntPtr expectedSid)
    {
        if (!OpenProcessToken(process, 8, out var token)) throw Error("Inspect metadata token");
        using (token)
        {
            var buffer = Marshal.AllocHGlobal(1024);
            try
            {
                if (!GetTokenInformation(token, 29, buffer, 1024, out _) || Marshal.ReadInt32(buffer) != 1) throw new IOException("Metadata token is not an AppContainer.");
                if (!GetTokenInformation(token, 30, buffer, 1024, out _) || Marshal.ReadInt32(buffer) != 0) throw new IOException("Metadata token contains unexpected capabilities.");
                if (!GetTokenInformation(token, 31, buffer, 1024, out _) || !EqualSid(Marshal.ReadIntPtr(buffer), expectedSid)) throw new IOException("Metadata AppContainer identity does not match.");
                if (!GetTokenInformation(token, 25, buffer, 1024, out _)) throw Error("Inspect metadata integrity level");
                var integritySid = Marshal.ReadIntPtr(buffer);
                var authorityCount = Marshal.ReadByte(GetSidSubAuthorityCount(integritySid));
                if (authorityCount == 0 || (uint)Marshal.ReadInt32(GetSidSubAuthority(integritySid, (uint)(authorityCount - 1))) > 0x1000)
                    throw new IOException("Metadata token does not have low integrity.");
            }
            finally { Marshal.FreeHGlobal(buffer); }
        }
    }

    private static string Quote(string argument)
    {
        var result = new StringBuilder("\""); var slashes = 0;
        foreach (var c in argument)
        {
            if (c == '\\') { slashes++; continue; }
            if (c == '"') { result.Append('\\', slashes * 2 + 1); result.Append(c); }
            else { result.Append('\\', slashes); result.Append(c); }
            slashes = 0;
        }
        return result.Append('\\', slashes * 2).Append('"').ToString();
    }

    private static Win32Exception Error(string operation)
    {
        var error = Marshal.GetLastWin32Error();
        return new Win32Exception(error, $"{operation} failed (Windows error {error}); metadata was not run without isolation.");
    }
    public void Dispose()
    {
        // Closing this non-inherited job handle kills the complete contained job.
        _job?.Dispose(); _job = null;
        try { if (!Process.HasExited && !Process.WaitForExit(5000)) throw new IOException("Isolated metadata process did not exit after its job closed."); }
        catch (InvalidOperationException) { }
        StandardOutput.Dispose(); StandardError.Dispose(); _output.Dispose(); _error.Dispose(); Process.Dispose();
    }

    [StructLayout(LayoutKind.Sequential)] private struct SecurityCapabilities { public IntPtr AppContainerSid, Capabilities; public uint CapabilityCount, Reserved; }
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)] private struct StartupInfo
    {
        public int Size; public IntPtr Reserved, Desktop, Title; public uint X, Y, XSize, YSize, XChars, YChars, FillAttribute, Flags;
        public ushort ShowWindow, Reserved2Count; public IntPtr Reserved2, Input, Output, Error;
    }
    [StructLayout(LayoutKind.Sequential)] private struct StartupInfoEx { public StartupInfo Startup; public IntPtr Attributes; }
    [StructLayout(LayoutKind.Sequential)] private struct ProcessInformation { public IntPtr Process, Thread; public int ProcessId, ThreadId; }
    [StructLayout(LayoutKind.Sequential)] private struct BasicLimits { public long PerProcessUserTimeLimit, PerJobUserTimeLimit; public uint LimitFlags; public UIntPtr MinimumWorkingSet, MaximumWorkingSet; public uint ActiveProcessLimit; public UIntPtr Affinity; public uint PriorityClass, SchedulingClass; }
    [StructLayout(LayoutKind.Sequential)] private struct IoCounters { public ulong ReadOperations, WriteOperations, OtherOperations, ReadBytes, WriteBytes, OtherBytes; }
    [StructLayout(LayoutKind.Sequential)] private struct ExtendedLimits { public BasicLimits Basic; public IoCounters Io; public UIntPtr ProcessMemoryLimit, JobMemoryLimit, PeakProcessMemoryUsed, PeakJobMemoryUsed; }
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern SafeFileHandle CreateJobObject(IntPtr security, string? name);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool SetInformationJobObject(SafeFileHandle job, int type, ref ExtendedLimits limits, uint length);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool AssignProcessToJobObject(SafeFileHandle job, IntPtr process);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool IsProcessInJob(IntPtr process, SafeFileHandle job, out bool result);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool InitializeProcThreadAttributeList(IntPtr list, int count, int flags, ref nuint bytes);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool UpdateProcThreadAttribute(IntPtr list, uint flags, IntPtr attribute, IntPtr value, nuint length, IntPtr previous, IntPtr returned);
    [DllImport("kernel32.dll")] private static extern void DeleteProcThreadAttributeList(IntPtr list);
    [DllImport("kernel32.dll", EntryPoint = "CreateProcessW", CharSet = CharSet.Unicode, SetLastError = true)] private static extern bool CreateProcess(string application, StringBuilder command, IntPtr processSecurity, IntPtr threadSecurity, bool inherit, uint flags, IntPtr environment, string directory, ref StartupInfoEx startup, out ProcessInformation process);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern uint ResumeThread(IntPtr thread);
    [DllImport("kernel32.dll")] private static extern bool TerminateProcess(IntPtr process, uint code);
    [DllImport("kernel32.dll")] private static extern bool CloseHandle(IntPtr handle);
    [DllImport("kernel32.dll")] private static extern uint SetErrorMode(uint mode);
    [DllImport("advapi32.dll", SetLastError = true)] private static extern bool OpenProcessToken(IntPtr process, uint access, out SafeFileHandle token);
    [DllImport("advapi32.dll", SetLastError = true)] private static extern bool GetTokenInformation(SafeFileHandle token, int type, IntPtr buffer, uint length, out uint returned);
    [DllImport("advapi32.dll")] private static extern bool EqualSid(IntPtr first, IntPtr second);
    [DllImport("advapi32.dll")] private static extern IntPtr GetSidSubAuthorityCount(IntPtr sid);
    [DllImport("advapi32.dll")] private static extern IntPtr GetSidSubAuthority(IntPtr sid, uint index);
}
