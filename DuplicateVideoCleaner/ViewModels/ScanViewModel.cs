using System.Collections.ObjectModel;
using System.Globalization;
using System.Windows.Input;
using DuplicateVideoCleaner.Mvvm;

namespace DuplicateVideoCleaner.ViewModels;

internal sealed class ScanViewModel : ObservableObject, IScanProgress
{
    internal const int MinThreshold = 1;
    internal const int MaxThreshold = 86400;
    internal const string ReadyStatus = "Choose a folder to begin.";
    internal const string ReadyToScanStatus = "Ready to scan.";
    private readonly MainViewModel _main;
    private readonly ScanWorkflow _workflow;
    private string _folder;
    private int _threshold;
    private string _thresholdText;
    private bool _keepNewest;
    private bool _includeSubfolders;
    private bool _protectUndated;
    private string _status = ReadyStatus;
    private string _currentFile = string.Empty;
    private double _progress;
    private bool _progressIndeterminate = true;
    private bool _cancelling;
    private CancellationTokenSource? _cancellation;

    internal ScanViewModel(MainViewModel main, ScanWorkflow workflow, AppSettings settings)
    {
        _main = main;
        _workflow = workflow;
        _folder = settings.LastFolder;
        _threshold = Math.Clamp(settings.ThresholdSeconds, MinThreshold, MaxThreshold);
        _thresholdText = _threshold.ToString(CultureInfo.CurrentCulture);
        _keepNewest = settings.KeepMode == KeepMode.Newest;
        _includeSubfolders = settings.IncludeSubfolders;
        _protectUndated = settings.ProtectUndatedClips;
        RecentFolders = new ObservableCollection<string>(settings.RecentFolders);
        if (!string.IsNullOrWhiteSpace(_folder)) _status = ReadyToScanStatus;
        ScanCommand = new AsyncCommand(() => ScanAsync(), () => !_main.IsBusy && !string.IsNullOrWhiteSpace(Folder));
        CancelCommand = new RelayCommand(Cancel, () => IsScanning && !_cancelling);
        BrowseCommand = new RelayCommand(Browse, () => !_main.IsBusy);
        IncreaseThresholdCommand = new RelayCommand(() => ThresholdSeconds = Math.Min(MaxThreshold, ThresholdSeconds + 1), () => !_main.IsBusy);
        DecreaseThresholdCommand = new RelayCommand(() => ThresholdSeconds = Math.Max(MinThreshold, ThresholdSeconds - 1), () => !_main.IsBusy);
    }

    public ObservableCollection<string> RecentFolders { get; }
    public ICommand ScanCommand { get; }
    public ICommand CancelCommand { get; }
    public ICommand BrowseCommand { get; }
    public ICommand IncreaseThresholdCommand { get; }
    public ICommand DecreaseThresholdCommand { get; }

    public string Folder
    {
        get => _folder;
        set
        {
            if (!Set(ref _folder, value ?? string.Empty)) return;
            if (Status is ReadyStatus or ReadyToScanStatus) Status = string.IsNullOrWhiteSpace(_folder) ? ReadyStatus : ReadyToScanStatus;
            CommandManager.InvalidateRequerySuggested();
        }
    }

    public int ThresholdSeconds
    {
        get => _threshold;
        set
        {
            var clamped = Math.Clamp(value, MinThreshold, MaxThreshold);
            Set(ref _threshold, clamped);
            Set(ref _thresholdText, clamped.ToString(CultureInfo.CurrentCulture), nameof(ThresholdText));
        }
    }

    // Free text while typing; committed (clamped) when it parses, restored on lost focus by the view.
    public string ThresholdText
    {
        get => _thresholdText;
        set
        {
            if (!Set(ref _thresholdText, value)) return;
            if (int.TryParse(value, NumberStyles.Integer, CultureInfo.CurrentCulture, out var seconds))
                Set(ref _threshold, Math.Clamp(seconds, MinThreshold, MaxThreshold), nameof(ThresholdSeconds));
        }
    }

    internal void CommitThresholdText() => ThresholdSeconds = _threshold;

    public bool KeepNewest
    {
        get => _keepNewest;
        set { if (Set(ref _keepNewest, value)) Raise(nameof(KeepOldest)); }
    }
    public bool KeepOldest
    {
        get => !_keepNewest;
        set => KeepNewest = !value;
    }
    public bool IncludeSubfolders { get => _includeSubfolders; set => Set(ref _includeSubfolders, value); }
    public bool ProtectUndated { get => _protectUndated; set => Set(ref _protectUndated, value); }

    public string Status { get => _status; private set => Set(ref _status, value); }
    public string CurrentFile { get => _currentFile; private set => Set(ref _currentFile, value); }
    public double Progress { get => _progress; private set => Set(ref _progress, value); }
    public bool ProgressIndeterminate { get => _progressIndeterminate; private set => Set(ref _progressIndeterminate, value); }
    public bool IsScanning => _main.Busy == BusyOperation.Scan;
    public bool CanEdit => !_main.IsBusy;

    internal void OnBusyChanged() => Raise(nameof(IsScanning), nameof(CanEdit));

    internal void SetStatus(string text) => Status = text;

    private void Browse()
    {
        var picked = _main.FolderPicker.PickFolder("Select the folder containing video files", Directory.Exists(Folder) ? Folder : null);
        if (picked is not null) Folder = picked;
    }

    // From drag and drop: the first dropped directory.
    internal bool TryDropFolder(IEnumerable<string> paths)
    {
        if (_main.IsBusy || paths.FirstOrDefault(Directory.Exists) is not { } directory) return false;
        Folder = directory;
        return true;
    }

    internal void Cancel()
    {
        if (_cancellation is null) return;
        _cancelling = true;
        _cancellation.Cancel();
        Status = "Cancelling scan…";
        CommandManager.InvalidateRequerySuggested();
    }

    internal async Task ScanAsync(Func<string, IEnumerable<string>, CancellationToken, string?>? diagnosticWriter = null)
    {
        if (_main.IsBusy) return;
        var root = Folder.Trim();
        if (!Directory.Exists(root))
        {
            _main.Navigate(WorkspacePage.Scan);
            Status = "Select an existing folder before scanning.";
            _main.Activity.Add("Scan failed", "The selected folder does not exist.", false);
            return;
        }

        var workspace = _main.Workspace;
        workspace.Clear();
        _main.Review.Reload();
        _main.OnResultsChanged();
        _main.RememberFolder(root);
        RecentFolders.Clear();
        foreach (var folder in _main.Settings.RecentFolders) RecentFolders.Add(folder);
        _main.SaveSettings();
        _main.Navigate(WorkspacePage.Scan);
        using var cancellation = new CancellationTokenSource();
        _cancellation = cancellation;
        _cancelling = false;
        _main.TryBeginBusy(BusyOperation.Scan);
        Status = "Scanning video files…";
        CurrentFile = string.Empty;
        ProgressIndeterminate = true;
        try
        {
            var context = new ScanContext(Path.GetFullPath(root), ThresholdSeconds, KeepNewest ? KeepMode.Newest : KeepMode.Oldest, IncludeSubfolders, ProtectUndated);
            workspace.Begin(context);
            var outcome = await _workflow.RunAsync(context, this, cancellation.Token, diagnosticWriter);
            workspace.Load(outcome);
            _main.Review.Reload(outcome.Status);
            _main.OnResultsChanged();
            if (outcome.Groups.Count == 0)
            {
                Status = outcome.Status;
                return;
            }
            Status = $"Found {outcome.Groups.Count} duplicate group(s). Review them before removing anything.";
            _main.Navigate(WorkspacePage.Review);
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
        {
            Discard();
            Status = "Scan cancelled. No video files were changed.";
            _main.Activity.Add("Scan cancelled", "Partial scan results were discarded.", false);
        }
        catch (Exception ex)
        {
            Discard();
            Status = $"Scan failed: {ex.Message}";
            _main.Activity.Add("Scan failed", ex.Message, false);
        }
        finally
        {
            _cancellation = null;
            _cancelling = false;
            CurrentFile = string.Empty;
            _pollCancellation?.Cancel();
            _main.EndBusy();
        }
    }

    private void Discard()
    {
        _main.Workspace.Clear();
        _main.Review.Reload();
        _main.OnResultsChanged();
        _main.Navigate(WorkspacePage.Scan);
    }

    // IScanProgress: called on the scan's context (the UI thread in the app).
    void IScanProgress.Status(string text) => Status = text;

    private CancellationTokenSource? _pollCancellation;

    void IScanProgress.MetadataStarted(LatestMetadataProgress progress)
    {
        _pollCancellation = new CancellationTokenSource();
        _ = PollAsync(progress, _pollCancellation.Token);
    }

    void IScanProgress.MetadataFinished()
    {
        _pollCancellation?.Cancel();
        _pollCancellation = null;
    }

    // Polls the latest snapshot instead of posting one update per file.
    private async Task PollAsync(LatestMetadataProgress progress, CancellationToken token)
    {
        try
        {
            while (!token.IsCancellationRequested)
            {
                await Task.Delay(150, token);
                if (progress.Latest is not { } update || _cancelling) continue;
                Status = ScanWorkflow.FormatProgress(update);
                if (update.TotalFiles > 0)
                {
                    ProgressIndeterminate = false;
                    Progress = (double)update.CompletedFiles / update.TotalFiles;
                }
                if (update.Path is { } path) CurrentFile = Path.GetFileName(path);
            }
        }
        catch (OperationCanceledException) { }
    }
}
