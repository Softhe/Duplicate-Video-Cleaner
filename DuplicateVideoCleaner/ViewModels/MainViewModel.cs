using System.Windows.Input;
using DuplicateVideoCleaner.Mvvm;
using DuplicateVideoCleaner.Themes;

namespace DuplicateVideoCleaner.ViewModels;

internal enum WorkspacePage { Scan, Review, Activity }
internal enum BusyOperation { None, Scan, Export, Removal, Recovery }

// Placement of the main window in device pixels, as stored in settings.
internal readonly record struct WindowPlacementSnapshot(int X, int Y, int Width, int Height, bool Maximized);

internal sealed class MainViewModel : ObservableObject
{
    private readonly SettingsSaveQueue _settingsQueue;
    private AppSettings _settings;
    private WorkspacePage _page = WorkspacePage.Scan;
    private BusyOperation _busy;
    private DialogViewModel? _dialog;
    private readonly Queue<DialogViewModel> _pendingDialogs = new();

    internal MainViewModel(SettingsService settingsService, Action<AppSettings>? settingsWriter = null, MetadataProbe? probe = null,
        IFolderPicker? folderPicker = null, IShellLauncher? shell = null,
        Func<string, Action<string>, CancellationToken, bool, IReadOnlyList<RecoveryEntry>>? recoveryList = null)
    {
        _settingsQueue = new SettingsSaveQueue(settingsWriter ?? settingsService.Save);
        _settings = settingsService.Load();
        Activity = new ActivityLog();
        Workspace = new ReviewWorkspace(Activity);
        FolderPicker = folderPicker ?? new NullFolderPicker();
        Shell = shell ?? new NullShellLauncher();
        Scan = new ScanViewModel(this, new ScanWorkflow(probe ?? new MetadataProbe(), Activity), _settings);
        Review = new ReviewViewModel(this);
        ActivityPage = new ActivityViewModel(this, recoveryList);
        NavigateCommand = new RelayCommand(parameter => Navigate(parameter is WorkspacePage page ? page : Enum.Parse<WorkspacePage>((string)parameter!)),
            parameter => !IsBusy && (parameter is not (WorkspacePage.Review or "Review") || Workspace.HasResults));
        SetThemeCommand = new RelayCommand(parameter => Theme = Enum.Parse<ThemePreference>((string)parameter!));
        Activity.Add("Ready", "No file operations have been performed.", true);
    }

    internal ActivityLog Activity { get; }
    internal ReviewWorkspace Workspace { get; }
    internal IFolderPicker FolderPicker { get; }
    internal IShellLauncher Shell { get; }
    public ScanViewModel Scan { get; }
    public ReviewViewModel Review { get; }
    public ActivityViewModel ActivityPage { get; }
    public ICommand NavigateCommand { get; }
    public ICommand SetThemeCommand { get; }
    internal AppSettings Settings => _settings;
    internal Func<WindowPlacementSnapshot?>? PlacementProvider { get; set; }

    public string Version { get; } = typeof(MainViewModel).Assembly.GetName().Version?.ToString(3) ?? "Unknown";

    public WorkspacePage Page
    {
        get => _page;
        private set
        {
            if (!Set(ref _page, value)) return;
            Raise(nameof(IsScanPage), nameof(IsReviewPage), nameof(IsActivityPage));
        }
    }
    public bool IsScanPage => Page == WorkspacePage.Scan;
    public bool IsReviewPage => Page == WorkspacePage.Review;
    public bool IsActivityPage => Page == WorkspacePage.Activity;

    public BusyOperation Busy
    {
        get => _busy;
        private set
        {
            if (!Set(ref _busy, value)) return;
            Raise(nameof(IsBusy));
            CommandManager.InvalidateRequerySuggested();
            Scan.OnBusyChanged();
            Review.OnBusyChanged();
            ActivityPage.OnBusyChanged();
        }
    }
    public bool IsBusy => Busy != BusyOperation.None;

    // Group count shown beside Review in the navigation pane.
    public string ReviewCount => Workspace.HasResults ? Workspace.Groups.Count.ToString(System.Globalization.CultureInfo.CurrentCulture) : string.Empty;

    public ThemePreference Theme
    {
        get => _settings.Theme;
        set
        {
            if (_settings.Theme == value) return;
            _settings = _settings with { Theme = value };
            ThemeManager.Apply(value);
            Raise();
            SaveSettings();
        }
    }

    // Video thumbnails in Review (on by default).
    public bool ShowThumbnails
    {
        get => _settings.ShowThumbnails;
        set
        {
            if (_settings.ShowThumbnails == value) return;
            _settings = _settings with { ShowThumbnails = value };
            if (Review.Thumbnails is { } thumbnails) thumbnails.Enabled = value;
            Review.Reload();
            Raise();
            SaveSettings();
        }
    }

    public DialogViewModel? Dialog
    {
        get => _dialog;
        private set { if (Set(ref _dialog, value)) Raise(nameof(HasDialog)); }
    }
    public bool HasDialog => Dialog is not null;

    // Shows a dialog over the window. Dialogs requested while one is open wait their turn.
    internal async Task<bool> ShowDialogAsync(DialogViewModel dialog)
    {
        if (Dialog is null) Dialog = dialog;
        else _pendingDialogs.Enqueue(dialog);
        var result = await dialog.Result;
        if (ReferenceEquals(Dialog, dialog)) Dialog = _pendingDialogs.Count > 0 ? _pendingDialogs.Dequeue() : null;
        return result;
    }

    internal Task ShowMessageAsync(string title, string message) => ShowDialogAsync(new DialogViewModel(title, message, "OK"));

    internal void Navigate(WorkspacePage page)
    {
        if (page == WorkspacePage.Review && !Workspace.HasResults) return;
        Page = page;
        if (page == WorkspacePage.Activity) ActivityPage.OnNavigatedTo();
    }

    // Operations take the shared busy flag so only one file operation runs at a time.
    internal bool TryBeginBusy(BusyOperation operation)
    {
        if (IsBusy) return false;
        Busy = operation;
        return true;
    }

    internal void EndBusy()
    {
        Busy = BusyOperation.None;
        if (CloseWhenIdle) CloseRequested?.Invoke();
    }

    internal void OnResultsChanged()
    {
        Raise(nameof(ReviewCount));
        CommandManager.InvalidateRequerySuggested();
    }

    // The folder the user chose; Recovery and the cleanup log fall back to it before a scan.
    internal string ChosenFolder => Scan.Folder.Trim();

    internal void RememberFolder(string folder) => _settings = _settings.WithRecentFolder(folder);

    internal void SaveSettings()
    {
        try
        {
            var placement = PlacementProvider?.Invoke();
            _settings = _settings with
            {
                LastFolder = Scan.Folder.Trim(),
                ThresholdSeconds = Scan.ThresholdSeconds,
                KeepMode = Scan.KeepNewest ? KeepMode.Newest : KeepMode.Oldest,
                IncludeSubfolders = Scan.IncludeSubfolders,
                ProtectUndatedClips = Scan.ProtectUndated,
                PermanentDeletion = Review.PermanentDeletion,
            };
            if (placement is { } window)
                _settings = _settings with
                {
                    WindowX = window.X, WindowY = window.Y, WindowWidth = window.Width, WindowHeight = window.Height, Maximized = window.Maximized
                };
            _settingsQueue.Enqueue(_settings);
        }
        catch { }
    }

    // Closing: a running operation is cancelled first; the window closes when it is idle and
    // settings are written.
    internal bool CloseWhenIdle { get; private set; }
    internal event Action? CloseRequested;
    private bool _settingsFlushed;
    private bool _flushing;

    // Returns true when the window may close now.
    internal async Task<bool> RequestCloseAsync()
    {
        if (IsBusy)
        {
            CloseWhenIdle = true;
            switch (Busy)
            {
                case BusyOperation.Scan: Scan.Cancel(); break;
                case BusyOperation.Export: Review.CancelExport(); Review.Status = "Cancelling export before closing…"; break;
                case BusyOperation.Recovery: ActivityPage.CancelForClose(); break;
                default: Review.Status = "Waiting for removal results before closing…"; break;
            }
            return false;
        }
        if (_settingsFlushed) return true;
        if (_flushing) return false;
        _flushing = true;
        SaveSettings();
        await _settingsQueue.FlushAsync();
        if (_settingsQueue.LastError is { } error)
            await ShowMessageAsync("Preferences were not saved", $"Preferences could not be saved. Your current files and recovery data are unaffected.\n\n{error.Message}");
        _settingsFlushed = true;
        CloseRequested?.Invoke();
        return false;
    }

    internal void ReportMaintenanceWarnings(IReadOnlyList<string> warnings)
    {
        foreach (var warning in warnings.Take(20)) Activity.Add("Workspace cleanup needs attention", warning, false);
        if (warnings.Count > 20)
            Activity.Add("Additional workspace cleanup warnings", $"{warnings.Count - 20} additional private workspace cleanup failures occurred. Temporary copies may remain until a later startup can remove them.", false);
    }

    internal void OpenCleanupLog()
    {
        var path = Workspace.LogPath(ChosenFolder);
        if (!File.Exists(path))
        {
            _ = ShowMessageAsync("Cleanup log", "No cleanup log exists for the selected folder yet.");
            return;
        }
        try { Shell.Open(path); }
        catch (Exception ex) { _ = ShowMessageAsync("Could not open cleanup log", ex.Message); }
    }

    private sealed class NullFolderPicker : IFolderPicker { public string? PickFolder(string title, string? initialFolder) => null; }
    private sealed class NullShellLauncher : IShellLauncher { public void Open(string path) { } }
}
