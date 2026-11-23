using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using Microsoft.Win32;

namespace DuplicateVideoCleaner.Themes;

// Applies Dark / Light / System by swapping the colour dictionary (Dark.xaml or Light.xaml) that
// Controls.xaml refers to through DynamicResource, and colours the native title bar to match.
internal static class ThemeManager
{
    // Created on use: the pack:// scheme only exists once WPF's Application has registered it.
    private static Uri DarkUri => new("pack://application:,,,/DuplicateVideoCleaner;component/Themes/Dark.xaml");
    private static Uri LightUri => new("pack://application:,,,/DuplicateVideoCleaner;component/Themes/Light.xaml");
    private static ThemePreference _preference = ThemePreference.Dark;
    private static bool _listening;

    internal static ThemePreference Preference => _preference;
    internal static bool IsDark { get; private set; } = true;
    internal static event Action? Changed;

    internal static void Apply(ThemePreference preference)
    {
        _preference = preference;
        if (preference == ThemePreference.System && !_listening)
        {
            SystemEvents.UserPreferenceChanged += OnUserPreferenceChanged;
            _listening = true;
        }
        Refresh();
    }

    private static void OnUserPreferenceChanged(object? sender, UserPreferenceChangedEventArgs e)
    {
        if (_preference != ThemePreference.System || e.Category != UserPreferenceCategory.General) return;
        Application.Current?.Dispatcher.BeginInvoke(Refresh);
    }

    private static void Refresh()
    {
        var dark = _preference switch
        {
            ThemePreference.Light => false,
            ThemePreference.System => !SystemUsesLightTheme(),
            _ => true
        };
        var application = Application.Current;
        if (application is null) return;
        var dictionaries = application.Resources.MergedDictionaries;
        // App.xaml loads the colour dictionary by a relative URI; match on the file name.
        var current = dictionaries.FirstOrDefault(dictionary => IsColourDictionary(dictionary.Source));
        var target = dark ? DarkUri : LightUri;
        if (current is null || !current.Source.OriginalString.EndsWith(dark ? "Dark.xaml" : "Light.xaml", StringComparison.OrdinalIgnoreCase))
        {
            var replacement = new ResourceDictionary { Source = target };
            if (current is null) dictionaries.Insert(0, replacement);
            else dictionaries[dictionaries.IndexOf(current)] = replacement;
        }
        IsDark = dark;
        foreach (Window window in application.Windows) ApplyTitleBar(window);
        Changed?.Invoke();
    }

    private static bool IsColourDictionary(Uri? source) => source is not null &&
        (source.OriginalString.EndsWith("Themes/Dark.xaml", StringComparison.OrdinalIgnoreCase) ||
         source.OriginalString.EndsWith("Themes/Light.xaml", StringComparison.OrdinalIgnoreCase));

    // Windows "app mode": HKCU\...\Themes\Personalize\AppsUseLightTheme (1 = light, missing = light).
    internal static bool SystemUsesLightTheme()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
            return key?.GetValue("AppsUseLightTheme") is not int value || value != 0;
        }
        catch { return true; }
    }

    // Dark/light caption buttons, and a caption colour matching the navigation pane (Windows 11).
    internal static void ApplyTitleBar(Window window)
    {
        var handle = new WindowInteropHelper(window).Handle;
        if (handle == IntPtr.Zero) return;
        var dark = IsDark ? 1 : 0;
        _ = DwmSetWindowAttribute(handle, DwmUseImmersiveDarkMode, ref dark, sizeof(int));
        if (Application.Current?.TryFindResource("CaptionColor") is Color caption)
        {
            var colorRef = caption.R | caption.G << 8 | caption.B << 16;
            _ = DwmSetWindowAttribute(handle, DwmCaptionColor, ref colorRef, sizeof(int));
        }
    }

    private const int DwmUseImmersiveDarkMode = 20;
    private const int DwmCaptionColor = 35;

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);
}
