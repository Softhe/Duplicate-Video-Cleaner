using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Threading;
using DuplicateVideoCleaner.Themes;
using DuplicateVideoCleaner.ViewModels;

namespace DuplicateVideoCleaner.Views;

public partial class MainWindow : Window
{
    private readonly MainViewModel _viewModel;
    private bool _closeAllowed;

    internal MainWindow(MainViewModel viewModel)
    {
        _viewModel = viewModel;
        DataContext = viewModel;
        InitializeComponent();
        viewModel.PlacementProvider = CurrentPlacement;
        viewModel.CloseRequested += () => Dispatcher.BeginInvoke(() => { _closeAllowed = true; Close(); });
        viewModel.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(MainViewModel.Dialog) && viewModel.Dialog is not null)
                Dispatcher.BeginInvoke(DispatcherPriority.Input, () => DialogClose.Focus());
        };
        ThemeManager.Changed += OnThemeChanged;
        SourceInitialized += (_, _) =>
        {
            RestorePlacement(viewModel.Settings);
            ThemeManager.ApplyTitleBar(this);
        };
        Closing += OnClosing;
        Closed += (_, _) => ThemeManager.Changed -= OnThemeChanged;
    }

    internal MainViewModel ViewModel => _viewModel;

    private void OnThemeChanged() => ThemeManager.ApplyTitleBar(this);

    private async void OnClosing(object? sender, CancelEventArgs e)
    {
        if (_closeAllowed) return;
        e.Cancel = true;
        if (await _viewModel.RequestCloseAsync()) { _closeAllowed = true; Close(); }
    }

    // Settings store the restored (normal) bounds in device pixels, like Windows does.
    private WindowPlacementSnapshot? CurrentPlacement()
    {
        var handle = new WindowInteropHelper(this).Handle;
        if (handle == IntPtr.Zero) return null;
        var placement = new NativePlacement { Length = Marshal.SizeOf<NativePlacement>() };
        if (!GetWindowPlacement(handle, ref placement)) return null;
        var bounds = placement.NormalPosition;
        // GetWindowPlacement reports workspace coordinates; add the work-area offset of that monitor.
        var offset = WorkAreaOffset(handle);
        return new WindowPlacementSnapshot(bounds.Left + offset.X, bounds.Top + offset.Y, bounds.Right - bounds.Left, bounds.Bottom - bounds.Top,
            placement.ShowCmd == ShowMaximized);
    }

    private void RestorePlacement(AppSettings settings)
    {
        var handle = new WindowInteropHelper(this).Handle;
        var desired = new PixelRect(settings.WindowX, settings.WindowY, settings.WindowWidth, settings.WindowHeight);
        var visible = settings.WindowX != -1 || settings.WindowY != -1;
        if (visible && WindowPlacement.HasReachableTitleBar(desired, WindowPlacement.WorkAreas()))
        {
            _ = SetWindowPos(handle, IntPtr.Zero, desired.X, desired.Y, desired.Width, desired.Height, SwpNoZOrder | SwpNoActivate);
            WindowStartupLocation = WindowStartupLocation.Manual;
        }
        else
        {
            // Saved size only (scaled from pixels to this window's DIPs), centred.
            var scale = VisualTreeHelperDpi();
            Width = Math.Max(MinWidth, settings.WindowWidth / scale);
            Height = Math.Max(MinHeight, settings.WindowHeight / scale);
        }
        if (settings.Maximized) WindowState = WindowState.Maximized;
    }

    private double VisualTreeHelperDpi() => System.Windows.Media.VisualTreeHelper.GetDpi(this).DpiScaleX;

    private static (int X, int Y) WorkAreaOffset(IntPtr handle)
    {
        var monitor = MonitorFromWindow(handle, MonitorDefaultToNearest);
        var info = new MonitorInfo { Size = Marshal.SizeOf<MonitorInfo>() };
        if (monitor == IntPtr.Zero || !GetMonitorInfo(monitor, ref info)) return (0, 0);
        return (info.Work.Left - info.Monitor.Left, info.Work.Top - info.Monitor.Top);
    }

    private const int ShowMaximized = 3;
    private const uint SwpNoZOrder = 0x0004, SwpNoActivate = 0x0010;
    private const uint MonitorDefaultToNearest = 2;

    [StructLayout(LayoutKind.Sequential)]
    private struct NativePlacement
    {
        public int Length, Flags, ShowCmd;
        public NativePoint MinPosition, MaxPosition;
        public NativeRect NormalPosition;
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool GetWindowPlacement(IntPtr window, ref NativePlacement placement);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool SetWindowPos(IntPtr window, IntPtr insertAfter, int x, int y, int width, int height, uint flags);

    [DllImport("user32.dll")]
    private static extern IntPtr MonitorFromWindow(IntPtr window, uint flags);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern bool GetMonitorInfo(IntPtr monitor, ref MonitorInfo info);
}

[StructLayout(LayoutKind.Sequential)]
internal struct NativePoint { public int X, Y; }

[StructLayout(LayoutKind.Sequential)]
internal struct NativeRect { public int Left, Top, Right, Bottom; }

[StructLayout(LayoutKind.Sequential)]
internal struct MonitorInfo
{
    public int Size;
    public NativeRect Monitor, Work;
    public uint Flags;
}

internal readonly record struct PixelRect(int X, int Y, int Width, int Height)
{
    internal PixelRect Intersect(PixelRect other)
    {
        var left = Math.Max(X, other.X);
        var top = Math.Max(Y, other.Y);
        var right = Math.Min(X + Width, other.X + other.Width);
        var bottom = Math.Min(Y + Height, other.Y + other.Height);
        return right <= left || bottom <= top ? default : new PixelRect(left, top, right - left, bottom - top);
    }
}

internal static class WindowPlacement
{
    // A restored window must have at least 100×24 px of its title bar on some monitor's work area.
    internal static bool HasReachableTitleBar(PixelRect bounds, IEnumerable<PixelRect> workAreas) =>
        workAreas.Any(area =>
        {
            var visible = area.Intersect(new PixelRect(bounds.X, bounds.Y, bounds.Width, 32));
            return visible.Width >= 100 && visible.Height >= 24;
        });

    internal static IReadOnlyList<PixelRect> WorkAreas()
    {
        var areas = new List<PixelRect>();
        EnumDisplayMonitors(IntPtr.Zero, IntPtr.Zero, (monitor, _, _, _) =>
        {
            var info = new MonitorInfo { Size = Marshal.SizeOf<MonitorInfo>() };
            if (GetMonitorInfo(monitor, ref info))
                areas.Add(new PixelRect(info.Work.Left, info.Work.Top, info.Work.Right - info.Work.Left, info.Work.Bottom - info.Work.Top));
            return true;
        }, IntPtr.Zero);
        return areas;
    }

    private delegate bool MonitorEnumProc(IntPtr monitor, IntPtr hdc, IntPtr rect, IntPtr data);

    [DllImport("user32.dll")]
    private static extern bool EnumDisplayMonitors(IntPtr hdc, IntPtr clip, MonitorEnumProc callback, IntPtr data);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern bool GetMonitorInfo(IntPtr monitor, ref MonitorInfo info);
}
