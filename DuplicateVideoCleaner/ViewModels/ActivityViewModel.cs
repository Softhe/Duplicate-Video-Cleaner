using System.Collections.ObjectModel;
using System.Windows.Input;
using DuplicateVideoCleaner.Mvvm;

namespace DuplicateVideoCleaner.ViewModels;

internal sealed class ActivityItemViewModel(ActivityEntry entry)
{
    public string Title => entry.Title;
    public string Detail => entry.Detail;
    public bool HasDetail => !string.IsNullOrWhiteSpace(entry.Detail);
    public string TimeText => DisplayText.Moment(entry.Time, DateTime.Now);
    public string AutomationName => $"Activity at {entry.Time:t}: {entry.Title}. {entry.Detail}";

    // Icon tone (Success | Caution | Neutral) and glyph. Restores are success, permanent
    // deletion and anything that needs attention are caution, everything else is neutral.
    public string Tone => !entry.Success ? "Caution" :
        entry.Title.StartsWith("Restored", StringComparison.Ordinal) ? "Success" :
        entry.Title.StartsWith("Permanently deleted", StringComparison.Ordinal) ? "Caution" : "Neutral";

    public string Glyph => !entry.Success ? "" :
        entry.Title.StartsWith("Restored", StringComparison.Ordinal) ? "" :
        entry.Title.StartsWith("Permanently deleted", StringComparison.Ordinal) ? "" :
        entry.Title.StartsWith("Removal", StringComparison.Ordinal) ? "" :
        entry.Title.Contains("export", StringComparison.OrdinalIgnoreCase) || entry.Title.StartsWith("Dry run", StringComparison.Ordinal) ? "" :
        entry.Title.StartsWith("Scan", StringComparison.Ordinal) ? "" : "";
}

internal sealed class RecoveryItemViewModel : ObservableObject
{
    private readonly ActivityViewModel _owner;
    private bool _isSelected;

    internal RecoveryItemViewModel(ActivityViewModel owner, RecoveryEntry entry, string root)
    {
        _owner = owner;
        Entry = entry;
        Name = Path.GetFileName(entry.OriginalPath);
        var directory = Path.GetDirectoryName(entry.OriginalPath) ?? string.Empty;
        var relative = Path.GetRelativePath(root, directory);
        Folder = relative == "." ? "Scanned folder" : relative.StartsWith("..", StringComparison.Ordinal) ? directory + Path.DirectorySeparatorChar : relative + Path.DirectorySeparatorChar;
        MovedText = DisplayText.Moment(entry.RemovedAt.LocalDateTime, DateTime.Now);
        SizeText = DisplayText.Size(entry.Size);
    }

    internal RecoveryEntry Entry { get; }
    public string Name { get; }
    public string Folder { get; }
    public string OriginalPath => Entry.OriginalPath;
    public string MovedText { get; }
    public string SizeText { get; }
    public string AutomationName => $"Recovery file {Entry.OriginalPath}; moved {MovedText}; {SizeText}";

    public bool IsSelected
    {
        get => _isSelected;
        set { if (Set(ref _isSelected, value)) _owner.OnRecoverySelectionChanged(); }
    }

    internal void SetSelectedSilently(bool value) { if (Set(ref _isSelected, value, nameof(IsSelected))) { } }
}

internal sealed class ActivityViewModel : ObservableObject
{
    private readonly MainViewModel _main;
    private readonly Func<string, Action<string>, CancellationToken, bool, IReadOnlyList<RecoveryEntry>> _list;
    private IReadOnlyList<RecoveryItemViewModel> _items = [];
    private string _recoveryStatus = string.Empty;
    private string? _root;
    private bool _stale = true;
    private bool _inspectArchived;
    private bool _stopRequested;
    private CancellationTokenSource? _listingCancellation;

    internal ActivityViewModel(MainViewModel main,
        Func<string, Action<string>, CancellationToken, bool, IReadOnlyList<RecoveryEntry>>? list = null)
    {
        _main = main;
        _list = list ?? ((path, warning, token, archived) => RecoveryStore.List(path, warning, token, includeArchivedPayloads: archived));
        Entries = [];
        foreach (var entry in main.Activity.Entries) Entries.Add(new ActivityItemViewModel(entry));
        main.Activity.Added += (entry, evicted) =>
        {
            if (evicted is not null && Entries.Count > 0) Entries.RemoveAt(Entries.Count - 1);
            Entries.Insert(0, new ActivityItemViewModel(entry));
        };
        RefreshCommand = new AsyncCommand(ReloadAsync, () => !_main.IsBusy && HasRoot);
        RestoreCommand = new AsyncCommand(() => OperateAsync(permanent: false), () => !_main.IsBusy && SelectedCount > 0);
        PurgeCommand = new AsyncCommand(() => OperateAsync(permanent: true), () => !_main.IsBusy && SelectedCount > 0);
        ToggleAllCommand = new RelayCommand(() => SetAll(AllSelected != true), () => !_main.IsBusy && Items.Count > 0);
        ArchiveHistoryCommand = new AsyncCommand(ArchiveHistoryAsync, () => !_main.IsBusy && HasRoot);
        OpenLogCommand = new RelayCommand(_main.OpenCleanupLog);
    }

    // Session activity, newest first (mirrors ActivityLog, including its 200-entry cap).
    public ObservableCollection<ActivityItemViewModel> Entries { get; }
    public ICommand RefreshCommand { get; }
    public ICommand RestoreCommand { get; }
    public ICommand PurgeCommand { get; }
    public ICommand ToggleAllCommand { get; }
    public ICommand ArchiveHistoryCommand { get; }
    public ICommand OpenLogCommand { get; }

    public IReadOnlyList<RecoveryItemViewModel> Items
    {
        get => _items;
        private set { if (Set(ref _items, value)) { Raise(nameof(HasItems), nameof(IsEmpty)); OnRecoverySelectionChanged(); } }
    }
    public bool HasItems => Items.Count > 0;
    public bool IsEmpty => !IsLoading && Items.Count == 0;
    public bool IsLoading => _listingCancellation is not null;
    public bool IsWorking => _main.Busy == BusyOperation.Recovery;

    public string? Root
    {
        get => _root;
        private set { if (Set(ref _root, value)) Raise(nameof(HasRoot), nameof(Subtitle)); }
    }
    public bool HasRoot => Root is not null;

    public string Subtitle => Root is null
        ? "Choose a folder on the Scan page to see its Recovery files."
        : Items.Count == 0 ? $"Recovery under {Root}"
        : $"{DisplayText.Files(Items.Count)} · {DisplayText.Size(Items.Sum(item => item.Entry.Size))} in Recovery under {Root}";

    public string RecoveryStatus
    {
        get => _recoveryStatus;
        private set { if (Set(ref _recoveryStatus, value)) Raise(nameof(HasRecoveryStatus)); }
    }
    public bool HasRecoveryStatus => !string.IsNullOrWhiteSpace(RecoveryStatus);

    public bool InspectArchived
    {
        get => _inspectArchived;
        set
        {
            if (!Set(ref _inspectArchived, value) || _main.IsBusy) return;
            _ = ReloadAsync();
        }
    }

    public int SelectedCount => Items.Count(item => item.IsSelected);
    public long SelectedBytes => Items.Where(item => item.IsSelected).Sum(item => item.Entry.Size);
    public string SelectionText => SelectedCount == 0 ? string.Empty : $"{SelectedCount} selected · {DisplayText.Size(SelectedBytes)}";

    // Header check box: all, none, or some (null).
    public bool? AllSelected => Items.Count == 0 || SelectedCount == 0 ? false : SelectedCount == Items.Count ? true : null;

    internal void OnBusyChanged() => Raise(nameof(IsWorking), nameof(IsEmpty));

    internal void OnRecoverySelectionChanged()
    {
        Raise(nameof(SelectedCount), nameof(SelectedBytes), nameof(SelectionText), nameof(AllSelected), nameof(Subtitle));
        CommandManager.InvalidateRequerySuggested();
    }

    internal void SetAll(bool selected)
    {
        foreach (var item in Items) item.SetSelectedSilently(selected);
        OnRecoverySelectionChanged();
    }

    // Recovery is read from disk only when the page is shown and something may have changed.
    internal void MarkRecoveryStale() => _stale = true;

    internal void OnNavigatedTo()
    {
        var root = _main.Workspace.Context?.Root ?? (Directory.Exists(_main.ChosenFolder) ? Path.GetFullPath(_main.ChosenFolder) : null);
        if (!string.Equals(root, Root, StringComparison.OrdinalIgnoreCase)) { Root = root; _stale = true; Items = []; }
        if (Root is null) { RecoveryStatus = string.Empty; return; }
        if (_stale && !_main.IsBusy) _ = ReloadAsync();
    }

    internal void CancelForClose()
    {
        _stopRequested = true;
        if (_listingCancellation is not null)
        {
            RecoveryStatus = "Cancelling recovery discovery...";
            _listingCancellation.Cancel();
        }
        else RecoveryStatus = "Closing after the current file finishes...";
    }

    internal async Task ReloadAsync()
    {
        if (Root is not { } root || !_main.TryBeginBusy(BusyOperation.Recovery)) return;
        try { await LoadAsync(root); }
        finally { _main.EndBusy(); }
    }

    private async Task LoadAsync(string root)
    {
        using var cancellation = new CancellationTokenSource();
        _listingCancellation = cancellation;
        Raise(nameof(IsLoading), nameof(IsEmpty));
        RecoveryStatus = "Reading recovery files...";
        var archived = InspectArchived;
        try
        {
            var result = await Task.Run(() =>
            {
                var warnings = new List<string>();
                var files = _list(root, warnings.Add, cancellation.Token, archived);
                return (Files: files, Warnings: warnings);
            });
            Items = result.Files.Select(entry => new RecoveryItemViewModel(this, entry, root)).ToArray();
            _stale = false;
            RecoveryStatus = result.Warnings.Count > 0
                ? $"{result.Warnings.Count} locations could not be read. First error: {result.Warnings[0]}"
                : string.Empty;
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
        { RecoveryStatus = "Recovery discovery cancelled."; }
        catch (Exception ex) { RecoveryStatus = $"Recovery could not be read: {ex.Message}"; }
        finally
        {
            _listingCancellation = null;
            Raise(nameof(IsLoading), nameof(IsEmpty), nameof(Subtitle));
        }
    }

    private async Task ArchiveHistoryAsync()
    {
        if (Root is not { } root) return;
        var confirmed = await _main.ShowDialogAsync(new DialogViewModel("Archive completed history?",
            "Archive completed recovery history older than 30 days? Only entries with a successful restore or permanent deletion record and no remaining payload qualify. Their metadata moves into .completed-history beside the recovery store. Active files, incomplete entries and unreadable entries are preserved. No files are permanently deleted, and archived records remain on disk.",
            "Cancel", "Archive"));
        if (!confirmed || !_main.TryBeginBusy(BusyOperation.Recovery)) return;
        string outcome;
        using var cancellation = new CancellationTokenSource();
        _listingCancellation = cancellation;
        RecoveryStatus = "Archiving completed history...";
        try
        {
            var result = await Task.Run(() => RecoveryStore.ArchiveCompletedHistory(root, DateTimeOffset.UtcNow.AddDays(-30), cancellation.Token));
            outcome = $"Archived {result.Archived} completed history entries; preserved {result.Preserved} other entries.";
            if (result.Warnings.Count > 0) outcome += $" {result.Warnings.Count} warnings. First warning: {result.Warnings[0]}";
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
        { outcome = "History archival cancelled. Entries already archived remain available on disk."; }
        catch (Exception ex) { outcome = $"History archival stopped: {ex.Message}"; }
        finally { _listingCancellation = null; }
        try
        {
            if (!_stopRequested) await LoadAsync(root);
            RecoveryStatus = $"{outcome} {RecoveryStatus}".Trim();
        }
        finally { _main.EndBusy(); }
    }

    internal async Task OperateAsync(bool permanent)
    {
        if (Root is not { } root) return;
        var selected = Items.Where(item => item.IsSelected).Select(item => item.Entry).ToArray();
        if (selected.Length == 0) return;
        if (permanent)
        {
            var dialog = new DialogViewModel(
                selected.Length == 1 ? "Permanently delete this file?" : $"Permanently delete {selected.Length} files?",
                $"This cannot be undone. {DisplayText.Size(selected.Sum(entry => entry.Size))} will be freed.", "Cancel", "Delete permanently", primaryIsDestructive: true)
            {
                ListHeading = "Files to delete",
                ListItems = selected.Select(entry => new DialogListItem(Path.GetFileName(entry.OriginalPath), entry.OriginalPath)).ToArray(),
                ListAutomationName = "Recovery files to delete permanently"
            };
            if (!await _main.ShowDialogAsync(dialog)) return;
        }
        if (!_main.TryBeginBusy(BusyOperation.Recovery)) return;
        string outcome;
        RecoveryStatus = permanent ? "Deleting selected files..." : "Restoring selected files...";
        try
        {
            var (completed, warnings, failures) = await Task.Run(() =>
            {
                var done = 0;
                var notes = new List<string>();
                var errors = new List<string>();
                foreach (var entry in selected)
                {
                    if (_stopRequested) break;
                    try
                    {
                        if ((permanent ? RecoveryStore.Purge(entry) : RecoveryStore.Restore(entry)) is { } warning) notes.Add(warning);
                        done++;
                    }
                    catch (Exception ex) { errors.Add($"{entry.OriginalPath}: {ex.Message}"); }
                }
                return (done, notes, errors);
            });
            outcome = selected.Length == 1 && failures.Count == 0
                ? permanent ? "Recovery file permanently deleted." : "File restored. Rescan to update the review."
                : permanent ? $"Permanently deleted {completed} of {selected.Length} recovery files." : $"Restored {completed} of {selected.Length} files. Rescan to update the review.";
            if (warnings.Count > 0) outcome += $" {string.Join(" ", warnings.Distinct())}";
            if (failures.Count > 0) outcome += $" {failures.Count} failed. First error: {failures[0]}";
            if (completed > 0)
                _main.Activity.Add(permanent ? $"Permanently deleted {DisplayText.Files(completed)}" : $"Restored {DisplayText.Files(completed)}",
                    permanent ? "Removed from Recovery; storage freed." : "Moved back to the original folders. Rescan to update the review.", true);
            if (failures.Count > 0)
                _main.Activity.Add(permanent ? "Permanent deletion needs attention" : "Restore needs attention", failures[0], false);
        }
        catch (Exception ex) { outcome = $"Operation stopped: {ex.Message}"; }
        try
        {
            if (!_stopRequested) await LoadAsync(root);
            RecoveryStatus = $"{outcome} {RecoveryStatus}".Trim();
        }
        finally { _main.EndBusy(); }
    }
}
