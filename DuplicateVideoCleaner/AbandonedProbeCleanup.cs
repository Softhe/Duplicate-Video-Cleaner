using System.Diagnostics;
using System.Security.Principal;
using System.Text.Json;

namespace DuplicateVideoCleaner;

// The marker lease and process creation time protect live owners, including PID reuse.
// Unmarked folders and legacy staging directories are deliberately left alone.
internal static class AbandonedProbeCleanup
{
    internal const string MarkerName = ".dvc-owner.json";
    internal sealed record Owner(int Version, string Token, string UserSid, int ProcessId, long ProcessStartUtcTicks, long CreatedUtcTicks);

    internal static FileStream CreateMarker(string root, string token)
    {
        var marker = new FileStream(Path.Combine(root, MarkerName), FileMode.CreateNew, FileAccess.ReadWrite, FileShare.Read);
        try
        {
            using var process = Process.GetCurrentProcess();
            JsonSerializer.Serialize(marker, new Owner(1, token, WindowsIdentity.GetCurrent().User!.Value,
                process.Id, process.StartTime.ToUniversalTime().Ticks, DateTime.UtcNow.Ticks));
            marker.Flush(true);
            return marker;
        }
        catch { marker.Dispose(); throw; }
    }

    internal static IReadOnlyList<string> Run(TimeSpan? minimumAge = null, string? onlyRoot = null)
    {
        var warnings = new List<string>();
        var age = minimumAge ?? TimeSpan.FromHours(24);
        if (age < TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(minimumAge));
        var temporary = Path.GetFullPath(Path.GetTempPath());
        using var parent = FileSnapshot.PinDirectories(temporary);
        if (onlyRoot is not null && !string.Equals(Path.GetDirectoryName(Path.GetFullPath(onlyRoot)), Path.TrimEndingDirectorySeparator(temporary), StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("Cleanup roots must be direct temporary-directory children.", nameof(onlyRoot));
        IEnumerable<string> roots = onlyRoot is null ? Directory.EnumerateDirectories(temporary, "dvc-sandbox-*", SearchOption.TopDirectoryOnly) : [Path.GetFullPath(onlyRoot)];
        foreach (var root in roots)
        {
            if (!Path.GetFileName(root).StartsWith("dvc-sandbox-", StringComparison.Ordinal)) continue;
            var token = Path.GetFileName(root)["dvc-sandbox-".Length..];
            if (token.Length != 32 || !Guid.TryParseExact(token, "N", out _)) continue;
            var markerLeased = false;
            try
            {
                using (var pinned = FileSnapshot.PinDirectories(root))
                {
                    var markerPath = Path.Combine(root, MarkerName);
                    if (!File.Exists(markerPath) || (File.GetAttributes(markerPath) & FileAttributes.ReparsePoint) != 0) continue;
                    using var marker = new FileStream(markerPath, FileMode.Open, FileAccess.Read, FileShare.None);
                    markerLeased = true;
                    if (marker.Length > 4096) continue;
                    var owner = JsonSerializer.Deserialize<Owner>(marker);
                    if (owner is null || owner.Version != 1 || owner.Token != token ||
                        owner.UserSid != WindowsIdentity.GetCurrent().User!.Value || owner.ProcessId <= 0 ||
                        owner.ProcessStartUtcTicks <= 0 || owner.ProcessStartUtcTicks > owner.CreatedUtcTicks ||
                        owner.CreatedUtcTicks <= 0 || owner.CreatedUtcTicks > DateTime.UtcNow.Ticks ||
                        DateTime.UtcNow.Ticks - owner.CreatedUtcTicks < age.Ticks || IsOwnerAlive(owner)) continue;
                    RemoveContents(root, markerPath);
                    WindowsProbeSandbox.DeleteOwnedProfile("DVC.Probe." + token);
                    marker.Dispose();
                    // Keep the directory chain pinned until the marker is removed.
                    // The final nonrecursive root delete cannot follow a substituted link.
                    File.Delete(markerPath);
                }
                Directory.Delete(root, false);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or System.ComponentModel.Win32Exception or ArgumentException)
            {
                // A live marker lease is expected. Once leased, cleanup failures are diagnostic.
                if (!markerLeased && ex is IOException io && (io.HResult & 0xffff) is 32 or 33) continue;
                warnings.Add("Abandoned metadata cleanup skipped " + root + ": " + ex.Message);
            }
        }
        return warnings;
    }

    private static bool IsOwnerAlive(Owner owner)
    {
        try
        {
            using var process = Process.GetProcessById(owner.ProcessId);
            return !process.HasExited && process.StartTime.ToUniversalTime().Ticks == owner.ProcessStartUtcTicks;
        }
        catch (ArgumentException) { return false; }
        catch (InvalidOperationException) { return false; }
        catch (System.ComponentModel.Win32Exception) { return true; }
    }

    private static void RemoveContents(string directory, string marker)
    {
        using var pinned = FileSnapshot.PinDirectories(directory);
        foreach (var path in Directory.EnumerateFileSystemEntries(directory))
        {
            if (path.Equals(marker, StringComparison.OrdinalIgnoreCase)) continue;
            var attributes = File.GetAttributes(path);
            if ((attributes & FileAttributes.ReparsePoint) != 0) throw new IOException("Unexpected reparse point in abandoned metadata staging.");
            if ((attributes & FileAttributes.Directory) != 0)
            {
                RemoveContents(path, marker);
                Directory.Delete(path, false);
            }
            else File.Delete(path);
        }
    }
}
