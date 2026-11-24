using System.Windows.Input;
using DuplicateVideoCleaner.Mvvm;

namespace DuplicateVideoCleaner.ViewModels;

internal sealed class ReviewViewModel : ObservableObject
{
    private readonly MainViewModel _main;
    private IReadOnlyList<GroupItemViewModel> _groups = [];
    private GroupItemViewModel? _selectedGroup;
    private string _status = string.Empty;
    private bool _permanentDeletion;
    private CancellationTokenSource? _exportCancellation;

    internal ReviewViewModel(MainViewModel main)
    {
        _main = main;
        _permanentDeletion = main.Settings.PermanentDeletion;
        SelectSuggestedCommand = new RelayCommand(SelectSuggested, () => CanAct);
        SelectAllCommand = new RelayCommand(() => SelectAll(true), () => CanAct);
        SelectNoneCommand = new RelayCommand(() => SelectAll(false), () => CanAct);
        RescanCommand = new AsyncCommand(() => _main.Scan.ScanAsync(), () => !IsBusy);
        ClearResultsCommand = new RelayCommand(ClearResults, () => !IsBusy && _main.Workspace.Context is not null);
        ExportCommand = new AsyncCommand(ExportAsync, () => CanAct);
        CancelExportCommand = new RelayCommand(CancelExport, () => _exportCancellation is not null);
        MoveCommand = new AsyncCommand(RemoveSelectedAsync, () => CanAct && _main.Workspace.SelectedCount > 0);
        OpenLogCommand = new RelayCommand(_main.OpenCleanupLog);
    }

    internal ReviewWorkspace Workspace => _main.Workspace;
    internal IThumbnailProvider? Thumbnails { get; set; }
    internal bool IsBusy => _main.IsBusy;
    private bool CanAct => !IsBusy && Workspace.HasResults;

    public ICommand SelectSuggestedCommand { get; }
    public ICommand SelectAllCommand { get; }
    public ICommand SelectNoneCommand { get; }
    public ICommand RescanCommand { get; }
    public ICommand ClearResultsCommand { get; }
    public ICommand ExportCommand { get; }
    public ICommand CancelExportCommand { get; }
    public ICommand MoveCommand { get; }
    public ICommand OpenLogCommand { get; }

    public IReadOnlyList<GroupItemViewModel> Groups
    {
        get => _groups;
        private set => Set(ref _groups, value);
    }

    public GroupItemViewModel? SelectedGroup
    {
        get => _selectedGroup;
        set
        {
            var previous = _selectedGroup;
            if (!Set(ref _selectedGroup, value)) return;
            Thumbnails?.CancelPending();
            previous?.ReleaseThumbnails();
            Raise(nameof(HasSelectedGroup));
        }
    }
    public bool HasSelectedGroup => SelectedGroup is not null;

    // Scan settings as one muted line, e.g. "14 groups · 41 files in D:\Recordings · 60 s window · keep oldest · subfolders included".
    public string Summary
    {
        get
        {
            if (Workspace.Context is not { } context) return string.Empty;
            return $"{(Workspace.Groups.Count == 1 ? "1 group" : $"{Workspace.Groups.Count} groups")} · {DisplayText.Files(Workspace.FileCount)} in {context.Root} · " +
                $"{context.ThresholdSeconds} s window · keep {(context.KeepMode == KeepMode.Newest ? "newest" : "oldest")} · " +
                $"{(context.IncludeSubfolders ? "subfolders included" : "this folder only")}" +
                (context.ProtectUndated ? " · undated clips protected" : string.Empty);
        }
    }

    // Warnings from the scan, or the outcome of the last removal or export.
    public string Status
    {
        get => _status;
        internal set { if (Set(ref _status, value)) Raise(nameof(HasStatus)); }
    }
    public bool HasStatus => !string.IsNullOrWhiteSpace(Status);

    public string SelectionTotal => Workspace.SelectedCount == 0
        ? "Nothing selected"
        : $"{DisplayText.Files(Workspace.SelectedCount)} · {DisplayText.Size(Workspace.SelectedBytes)}";

    public string SelectionDetail
    {
        get
        {
            if (Workspace.SelectedCount == 0) return "Tick files to remove them";
            var groups = Workspace.Groups.Count(group => Workspace.GroupSelection(group.Number).Count > 0);
            return $"selected across {(groups == 1 ? "1 group" : $"{groups} groups")}";
        }
    }

    public string SelectionAutomationName => Workspace.SelectedCount == 0
        ? "Nothing selected"
        : $"{Workspace.SelectedCount} files selected, {DisplayText.Size(Workspace.SelectedBytes)}";

    public bool PermanentDeletion
    {
        get => _permanentDeletion;
        set
        {
            if (!Set(ref _permanentDeletion, value)) return;
            Raise(nameof(RemovalMethodIndex), nameof(MoveGlyph), nameof(MoveText), nameof(MoveAutomationName));
            _main.SaveSettings();
        }
    }

    // 0 = Recovery, 1 = permanent deletion (the removal method combo box).
    public int RemovalMethodIndex
    {
        get => PermanentDeletion ? 1 : 0;
        set => PermanentDeletion = value == 1;
    }

    public string MoveGlyph => PermanentDeletion ? "" : "";
    public string MoveText => PermanentDeletion ? "Delete permanently…" : "Move selected…";
    public string MoveAutomationName => PermanentDeletion ? "Delete selected files permanently" : "Move selected files to Recovery";
    public bool IsExporting => _exportCancellation is not null;
    public bool IsWorking => _main.Busy is BusyOperation.Export or BusyOperation.Removal;

    internal void OnBusyChanged()
    {
        Raise(nameof(IsWorking), nameof(IsExporting));
        if (Thumbnails is { } thumbnails) thumbnails.Paused = _main.Busy is BusyOperation.Scan or BusyOperation.Removal;
    }

    // Rebuilds the group list from the workspace (after a scan, removal or clear).
    internal void Reload(string? scanStatus = null)
    {
        var previous = SelectedGroup?.Number;
        Thumbnails?.CancelPending();
        Groups = Workspace.Groups.Select(group => new GroupItemViewModel(this, Workspace, group)).ToArray();
        SelectedGroup = Groups.FirstOrDefault(group => group.Number == previous) ?? Groups.FirstOrDefault();
        if (scanStatus is not null) Status = ScanWarnings(scanStatus);
        RaiseTotals();
        Raise(nameof(Summary));
    }

    // The scan status repeats the scan settings already shown in Summary; keep only the warnings.
    private string ScanWarnings(string status)
    {
        var description = Workspace.Context?.Description;
        if (description is not null && status.StartsWith(description, StringComparison.Ordinal))
            status = status[description.Length..];
        return status.Trim().TrimStart('·').Trim();
    }

    // Bulk changes refresh every group; a single toggle calls OnTotalsChanged after refreshing its group.
    internal void OnSelectionChanged()
    {
        foreach (var group in Groups) group.Refresh();
        RaiseTotals();
    }

    internal void OnTotalsChanged() => RaiseTotals();

    private void RaiseTotals()
    {
        Raise(nameof(SelectionTotal), nameof(SelectionDetail), nameof(SelectionAutomationName), nameof(Summary));
        CommandManager.InvalidateRequerySuggested();
    }

    internal void SelectSuggested()
    {
        if (!CanAct) return;
        Workspace.SelectSuggested();
        OnSelectionChanged();
    }

    internal void SelectAll(bool selected)
    {
        if (!CanAct) return;
        Workspace.SelectAll(selected);
        OnSelectionChanged();
    }

    internal void ClearResults()
    {
        if (IsBusy) return;
        Workspace.Clear();
        Reload();
        Status = string.Empty;
        _main.OnResultsChanged();
        _main.Scan.SetStatus("Results cleared. Choose a folder or adjust the rules for another scan.");
        _main.Activity.Add("Results cleared", "The in-memory review was cleared; no video files were changed.", true);
        _main.Navigate(WorkspacePage.Scan);
    }

    internal void CancelExport() => _exportCancellation?.Cancel();

    private async Task ExportAsync()
    {
        if (!CanAct || Workspace.Context is not { } context) return;
        var destination = _main.FolderPicker.PickFolder("Choose a folder for the CSV and text dry-run reports", Directory.Exists(context.Root) ? context.Root : null);
        if (destination is null) return;
        await ExportToAsync(destination);
    }

    internal async Task ExportToAsync(string destination, Func<string, DryRunContext, CancellationToken, ExportResult>? export = null)
    {
        if (!CanAct || !_main.TryBeginBusy(BusyOperation.Export)) return;
        using var cancellation = new CancellationTokenSource();
        _exportCancellation = cancellation;
        Raise(nameof(IsExporting));
        Status = "Exporting dry run…";
        try { Status = await Workspace.ExportDryRunAsync(destination, cancellation.Token, export); }
        finally
        {
            _exportCancellation = null;
            Raise(nameof(IsExporting));
            _main.EndBusy();
        }
    }

    internal DialogViewModel CreateRemovalConfirmation(IReadOnlyList<VideoCandidate> selected)
    {
        var permanent = PermanentDeletion;
        var bytes = selected.Sum(file => file.Size);
        var groupOf = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        foreach (var group in Workspace.Groups)
            foreach (var file in group.Files) groupOf[file.FullPath] = group.Number;
        var groups = selected.Select(file => groupOf[file.FullPath]).Distinct().Count();
        var withoutSurvivors = Workspace.GroupsWithoutSurvivors();
        var recoveryFolders = selected.Select(file => Path.Combine(Path.GetDirectoryName(file.FullPath)!, RecoveryStore.DirectoryName))
            .Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        var title = permanent
            ? $"Permanently delete {DisplayText.Files(selected.Count)}?"
            : $"Move {DisplayText.Files(selected.Count)} to Recovery?";
        var message = permanent
            ? "This cannot be undone. The files will not go to Recovery."
            : "They'll be moved, not deleted. You can restore them from Activity & Recovery. Storage is freed only after permanent deletion.";
        return new DialogViewModel(title, message, "Cancel", permanent ? "Delete permanently" : "Confirm move", primaryIsDestructive: true)
        {
            Facts =
            [
                new("Files", $"{DisplayText.Files(selected.Count)} from {(groups == 1 ? "1 group" : $"{groups} groups")}"),
                new("Size", DisplayText.Size(bytes)),
                permanent
                    ? new("Method", "Permanent deletion")
                    : new("Method", "Recovery, same-volume move", recoveryFolders.Length == 1 ? recoveryFolders[0] : $"{RecoveryStore.DirectoryName} beside each file's folder ({recoveryFolders.Length} folders)")
            ],
            Warning = withoutSurvivors > 0
                ? $"Every copy of a recording is selected in {(withoutSurvivors == 1 ? "1 group" : $"{withoutSurvivors} groups")}. No file will remain of those recordings."
                : null,
            WarningTone = DialogTone.Caution,
            ListHeading = "Selected files",
            ListItems = selected.Select(file => new DialogListItem(file.RelativePath, file.FullPath)).ToArray(),
            ListAutomationName = "All files selected for removal"
        };
    }

    internal async Task RemoveSelectedAsync()
    {
        if (!CanAct || Workspace.Context is null) return;
        var selected = Workspace.SelectedFiles();
        if (selected.Count == 0) return;
        var permanent = PermanentDeletion;
        if (!await _main.ShowDialogAsync(CreateRemovalConfirmation(selected))) return;
        if (!Workspace.TryStartRemoval(out var logError))
        {
            await _main.ShowMessageAsync("Log unavailable", $"Removal was not started because the cleanup log cannot be written:\n\n{logError}");
            return;
        }
        if (!_main.TryBeginBusy(BusyOperation.Removal)) return;
        Status = "Removing selected files…";
        try
        {
            var outcome = await Workspace.RemoveAsync(selected, permanent);
            Reload();
            Status = outcome.Status;
            _main.OnResultsChanged();
            if (!Workspace.HasResults) _main.Navigate(WorkspacePage.Scan);
            _main.ActivityPage.MarkRecoveryStale();
            _ = _main.ShowMessageAsync(outcome.Successful ? "Removal complete" : "Removal needs attention", outcome.Message);
        }
        catch (Exception ex)
        {
            Workspace.Clear();
            Reload();
            _main.OnResultsChanged();
            _main.Scan.SetStatus("Results cleared. Choose a folder or adjust the rules for another scan.");
            _main.Activity.Add("Removal interrupted", $"Rescan before continuing. {ex.Message}", false);
            _main.Navigate(WorkspacePage.Scan);
            _ = _main.ShowMessageAsync("Removal interrupted", $"Removal was interrupted. Some files may already have been removed. Rescan before continuing.\n\n{ex.Message}");
        }
        finally { _main.EndBusy(); }
    }
}
