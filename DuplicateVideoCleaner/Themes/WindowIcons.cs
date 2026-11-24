using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using Microsoft.Win32;

namespace DuplicateVideoCleaner.Themes;

// Monochrome window icons that follow the theme. Windows has no theme-aware icons for classic
// desktop apps, so the window's icons are swapped at runtime:
// - the small icon (title bar) follows the app's theme, because the app draws that title bar colour;
// - the big icon (taskbar, Alt+Tab) follows the Windows app mode, because Windows draws those surfaces.
// The EXE, shortcuts and installer keep the static tile icon (assets\DuplicateVideoCleaner.ico).
internal sealed class WindowIcons : IDisposable
{
    private const int WmSetIcon = 0x0080;
    private const int IconSmall = 0;
    private const int IconBig = 1;
    private const int SmCxSmIcon = 49;
    private const int SmCxIcon = 11;
    private static readonly Lazy<IconFile> OnDark = new(() => IconFile.Load("assets/GlyphOnDark.ico"));
    private static readonly Lazy<IconFile> OnLight = new(() => IconFile.Load("assets/GlyphOnLight.ico"));
    private readonly Window _window;
    private IntPtr _small;
    private IntPtr _big;

    internal WindowIcons(Window window)
    {
        _window = window;
        window.SourceInitialized += (_, _) => Apply();
        window.DpiChanged += (_, _) => Apply();
        ThemeManager.Changed += Apply;
        SystemEvents.UserPreferenceChanged += OnUserPreferenceChanged;
    }

    private void OnUserPreferenceChanged(object? sender, UserPreferenceChangedEventArgs e)
    {
        if (e.Category == UserPreferenceCategory.General) _window.Dispatcher.BeginInvoke(Apply);
    }

    internal void Apply()
    {
        var handle = new WindowInteropHelper(_window).Handle;
        if (handle == IntPtr.Zero) return;
        try
        {
            var dpi = (uint)Math.Round(96 * System.Windows.Media.VisualTreeHelper.GetDpi(_window).DpiScaleX);
            var small = (ThemeManager.IsDark ? OnDark : OnLight).Value.CreateIcon(GetSystemMetricsForDpi(SmCxSmIcon, dpi));
            var big = (SystemUsesDarkMode() ? OnDark : OnLight).Value.CreateIcon(GetSystemMetricsForDpi(SmCxIcon, dpi));
            SendMessage(handle, WmSetIcon, IconSmall, small);
            SendMessage(handle, WmSetIcon, IconBig, big);
            Replace(ref _small, small);
            Replace(ref _big, big);
        }
        catch (Exception) { }
    }

    // Windows app mode decides the taskbar's light or dark look for app icons.
    private static bool SystemUsesDarkMode()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
            return key?.GetValue("SystemUsesLightTheme") is int value && value == 0;
        }
        catch { return false; }
    }

    private static void Replace(ref IntPtr field, IntPtr value)
    {
        var previous = field;
        field = value;
        if (previous != IntPtr.Zero && previous != value) DestroyIcon(previous);
    }

    public void Dispose()
    {
        ThemeManager.Changed -= Apply;
        SystemEvents.UserPreferenceChanged -= OnUserPreferenceChanged;
        Replace(ref _small, IntPtr.Zero);
        Replace(ref _big, IntPtr.Zero);
    }

    // The PNG images of one .ico resource, by size.
    private sealed class IconFile(IReadOnlyList<(int Size, byte[] Png)> images)
    {
        internal static IconFile Load(string resource)
        {
            var info = Application.GetResourceStream(new Uri("pack://application:,,,/DuplicateVideoCleaner;component/" + resource))
                ?? throw new IOException("Icon resource missing: " + resource);
            using var stream = info.Stream;
            using var buffer = new MemoryStream();
            stream.CopyTo(buffer);
            var bytes = buffer.ToArray();
            var count = BitConverter.ToUInt16(bytes, 4);
            var images = new List<(int, byte[])>();
            for (var index = 0; index < count; index++)
            {
                var entry = 6 + 16 * index;
                var size = bytes[entry] == 0 ? 256 : bytes[entry];
                var length = (int)BitConverter.ToUInt32(bytes, entry + 8);
                var offset = (int)BitConverter.ToUInt32(bytes, entry + 12);
                images.Add((size, bytes[offset..(offset + length)]));
            }
            return new IconFile(images.OrderBy(image => image.Item1).ToArray());
        }

        // The exact size, or the next larger one scaled down by Windows.
        internal IntPtr CreateIcon(int size)
        {
            var image = images.FirstOrDefault(candidate => candidate.Size >= size);
            if (image.Png is null) image = images[^1];
            var icon = CreateIconFromResourceEx(image.Png, (uint)image.Png.Length, true, 0x00030000, size, size, 0);
            return icon != IntPtr.Zero ? icon : throw new IOException("The window icon could not be created.");
        }
    }

    [DllImport("user32.dll")]
    private static extern IntPtr SendMessage(IntPtr window, int message, int wParam, IntPtr lParam);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr CreateIconFromResourceEx(byte[] data, uint size, bool icon, uint version, int width, int height, uint flags);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool DestroyIcon(IntPtr icon);

    [DllImport("user32.dll")]
    private static extern int GetSystemMetricsForDpi(int index, uint dpi);
}
