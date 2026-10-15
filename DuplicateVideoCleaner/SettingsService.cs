using System.Text.Json;

namespace DuplicateVideoCleaner;

internal sealed record AppSettings
{
    public int SchemaVersion { get; init; } = 1;
    public string LastFolder { get; init; } = string.Empty;
    public int ThresholdSeconds { get; init; } = 60;
    public KeepMode KeepMode { get; init; } = KeepMode.Oldest;
    public bool IncludeSubfolders { get; init; }
    public bool PermanentDeletion { get; init; }
    public int WindowX { get; init; } = -1;
    public int WindowY { get; init; } = -1;
    public int WindowWidth { get; init; } = 1160;
    public int WindowHeight { get; init; } = 740;
    public bool Maximized { get; init; }
}

internal sealed class SettingsService
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };
    internal string SettingsPath { get; }

    internal SettingsService(string? settingsPath = null)
    {
        SettingsPath = settingsPath ?? Environment.GetEnvironmentVariable("DVC_SETTINGS_PATH") ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "DuplicateVideoCleaner", "settings.json");
    }

    internal AppSettings Load()
    {
        try
        {
            if (!File.Exists(SettingsPath)) return new AppSettings();
            var settings = JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(SettingsPath), JsonOptions);
            return Validate(settings ?? new AppSettings());
        }
        catch
        {
            return new AppSettings();
        }
    }

    internal void Save(AppSettings settings)
    {
        settings = Validate(settings);
        var directory = Path.GetDirectoryName(SettingsPath)!;
        Directory.CreateDirectory(directory);
        var temporary = SettingsPath + ".tmp";
        File.WriteAllText(temporary, JsonSerializer.Serialize(settings, JsonOptions));
        File.Move(temporary, SettingsPath, true);
    }

    internal static AppSettings Validate(AppSettings settings) => settings with
    {
        SchemaVersion = 1,
        LastFolder = settings.LastFolder ?? string.Empty,
        ThresholdSeconds = Math.Clamp(settings.ThresholdSeconds, 1, 86400),
        KeepMode = Enum.IsDefined(settings.KeepMode) ? settings.KeepMode : KeepMode.Oldest,
        WindowWidth = Math.Clamp(settings.WindowWidth, 920, 7680),
        WindowHeight = Math.Clamp(settings.WindowHeight, 600, 4320)
    };
}
