using System.Diagnostics;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using FlaUI.Core.AutomationElements;
using FlaUI.Core.Definitions;
using FlaUI.Core.Input;
using FlaUI.Core.Tools;
using FlaUI.Core.WindowsAPI;
using FlaUI.UIA3;
using AutomationApplication = FlaUI.Core.Application;

if (args.Length != 3) throw new ArgumentException("Usage: ReadmeScreenshots <application.exe> <sample.mp4> <output-directory>");
var executable = Path.GetFullPath(args[0]);
var sample = Path.GetFullPath(args[1]);
var output = Path.GetFullPath(args[2]);
if (!File.Exists(executable) || !File.Exists(sample)) throw new FileNotFoundException("Application and sample video must exist.");
Directory.CreateDirectory(output);
var temporary = Path.Combine(Path.GetTempPath(), "dvc-readme-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(temporary);
var drive = Enumerable.Range('R', 8).Select(letter => ((char)letter) + ":")
    .FirstOrDefault(candidate => !Directory.Exists(candidate + "\\") && !DriveInfo.GetDrives().Any(d => d.Name.StartsWith(candidate)))
    ?? throw new IOException("No unused temporary drive letter is available.");
var mapped = false;
AutomationApplication? app = null;
var oldSettings = Environment.GetEnvironmentVariable("DVC_SETTINGS_PATH");
var oldProbe = Environment.GetEnvironmentVariable("FFPROBE_PATH");
var oldPointer = Mouse.Position;
try
{
    Subst(drive, temporary);
    mapped = true;
    var recordings = Path.Combine(drive + "\\", "Sample recordings");
    Directory.CreateDirectory(recordings);
    AddPair(10);
    Environment.SetEnvironmentVariable("DVC_SETTINGS_PATH", Path.Combine(temporary, "settings.json"));
    Environment.SetEnvironmentVariable("FFPROBE_PATH", Path.Combine(Path.GetDirectoryName(executable)!, "tools", "ffprobe.exe"));
    app = AutomationApplication.Launch(executable);
    using var automation = new UIA3Automation();
    var window = app.GetMainWindow(automation, TimeSpan.FromSeconds(10)) ?? throw new IOException("Application window did not appear.");
    window.Focus();
    var folder = Find("Video folder").AsTextBox();
    folder.Text = recordings;
    Find("Scan videos").Focus();
    Capture("scan", window);
    Invoke("Scan videos");
    Wait("Review workspace");
    Wait("Expand group 1");
    Capture("review", window);
    Invoke("Expand group 1");
    Wait("File: 2026-10-10 18-30-25 recording-copy.mp4");
    Capture("review-details", window);
    Find("Move selected files to Recycle Bin").AsButton().Click();
    var dialog = Retry.WhileNull(() => automation.GetDesktop().FindFirstDescendant(cf =>
        cf.ByName("Confirm deletion").And(cf.ByControlType(ControlType.Window)))?.AsWindow(), TimeSpan.FromSeconds(10)).Result
        ?? throw new IOException("Confirmation dialog did not appear.");
    Capture("confirmation", dialog);
    dialog.FindFirstDescendant(cf => cf.ByName("Cancel"))!.AsButton().Invoke();
    Wait("Review workspace");
    for (var day = 11; day <= 15; day++) AddPair(day);
    Invoke("Rescan videos");
    Wait("Expand group 6");
    Capture("review-six-groups", window);
    window.Focus();
    Keyboard.Press(VirtualKeyShort.CONTROL);
    Keyboard.Type(VirtualKeyShort.KEY_3);
    Keyboard.Release(VirtualKeyShort.CONTROL);
    Wait("Activity workspace");
    Capture("activity", window);
    if (Directory.EnumerateFiles(recordings, "*.mp4").Count() != 12)
        throw new IOException("Screenshot capture unexpectedly changed the media fixtures.");
    Console.WriteLine($"Captured six application-only screenshots in {output}");

    void AddPair(int day)
    {
        File.Copy(sample, Path.Combine(recordings, $"2026-10-{day:00} 18-30-00 recording.mp4"));
        File.Copy(sample, Path.Combine(recordings, $"2026-10-{day:00} 18-30-25 recording-copy.mp4"));
    }

    AutomationElement Find(string name) => window.FindFirstDescendant(cf => cf.ByName(name))
        ?? throw new IOException($"Control not found: {name}");
    void Wait(string name)
    {
        if (Retry.WhileNull(() => window.FindFirstDescendant(cf => cf.ByName(name)), TimeSpan.FromSeconds(10)).Result is null)
            throw new IOException($"Control did not appear: {name}");
    }
    void Invoke(string name) => Find(name).AsButton().Invoke();
    void Capture(string name, Window target)
    {
        target.Focus();
        Mouse.MoveTo(0, 0);
        Thread.Sleep(250);
        WindowCapture.Save(target.Properties.NativeWindowHandle.Value, Path.Combine(output, name + ".png"));
    }
}
finally
{
    if (app is not null)
    {
        try { app.Close(); }
        finally { if (!app.HasExited) app.Kill(); app.Dispose(); }
    }
    Environment.SetEnvironmentVariable("DVC_SETTINGS_PATH", oldSettings);
    Environment.SetEnvironmentVariable("FFPROBE_PATH", oldProbe);
    Mouse.MoveTo(oldPointer);
    if (mapped) Subst(drive, "/D");
    var resolved = Path.GetFullPath(temporary);
    if (Path.GetDirectoryName(resolved) == Path.GetFullPath(Path.GetTempPath()).TrimEnd('\\') &&
        Path.GetFileName(resolved).StartsWith("dvc-readme-", StringComparison.Ordinal)) Directory.Delete(resolved, true);
}

static void Subst(string drive, string target)
{
    var start = new ProcessStartInfo("subst.exe") { UseShellExecute = false, CreateNoWindow = true };
    start.ArgumentList.Add(drive);
    start.ArgumentList.Add(target);
    using var process = Process.Start(start)!;
    process.WaitForExit();
    if (process.ExitCode != 0) throw new IOException("Temporary drive mapping failed.");
}

internal static class WindowCapture
{
    internal static void Save(nint handle, string path)
    {
        if (!GetWindowRect(handle, out var full)) throw new IOException("Window bounds could not be read.");
        if (DwmGetWindowAttribute(handle, 9, out var visible, Marshal.SizeOf<NativeRect>()) != 0) visible = full;
        using var rendered = new Bitmap(full.Right - full.Left, full.Bottom - full.Top, PixelFormat.Format32bppArgb);
        using (var graphics = Graphics.FromImage(rendered))
        {
            graphics.Clear(Color.FromArgb(15, 19, 27));
            var dc = graphics.GetHdc();
            try
            {
                if (!PrintWindow(handle, dc, 2)) throw new IOException("Window rendering failed.");
            }
            finally { graphics.ReleaseHdc(dc); }
        }
        var bounds = new Rectangle(visible.Left - full.Left, visible.Top - full.Top, visible.Right - visible.Left, visible.Bottom - visible.Top);
        using var cropped = rendered.Clone(bounds, PixelFormat.Format32bppArgb);
        // DWM rounded corners are outside the window. Mask them rather than capture desktop pixels.
        const double radius = 8;
        for (var y = 0; y < radius; y++)
            for (var x = 0; x < radius; x++)
            {
                var dx = radius - x - .5;
                var dy = radius - y - .5;
                if (dx * dx + dy * dy <= radius * radius) continue;
                cropped.SetPixel(x, y, Color.Transparent);
                cropped.SetPixel(cropped.Width - 1 - x, y, Color.Transparent);
                cropped.SetPixel(x, cropped.Height - 1 - y, Color.Transparent);
                cropped.SetPixel(cropped.Width - 1 - x, cropped.Height - 1 - y, Color.Transparent);
            }
        cropped.Save(path, ImageFormat.Png);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeRect { public int Left, Top, Right, Bottom; }
    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetWindowRect(nint handle, out NativeRect bounds);
    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool PrintWindow(nint handle, nint dc, uint flags);
    [DllImport("dwmapi.dll")]
    private static extern int DwmGetWindowAttribute(nint handle, uint attribute, out NativeRect value, int size);
}
