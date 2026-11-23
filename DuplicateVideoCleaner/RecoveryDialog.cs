namespace DuplicateVideoCleaner;

internal sealed class RecoveryDialog : Form
{
    private readonly string _root;
    private readonly ListView _entries = new() { Dock = DockStyle.Fill, View = View.Details, FullRowSelect = true, MultiSelect = true, HideSelection = false, AccessibleName = "Recovery files" };
    private readonly Label _status = new() { AutoSize = true, Dock = DockStyle.Top, AccessibleName = "Recovery status" };
    private readonly Button _restore = new() { Text = "Restore selected", AutoSize = true, Enabled = false, AccessibleName = "Restore recovery file" };
    private readonly Button _purge = new() { Text = "Permanently delete selected", AutoSize = true, Enabled = false, AccessibleName = "Permanently delete recovery file" };
    private readonly Button _selectAll = new() { Text = "Select all", AutoSize = true, Enabled = false, AccessibleName = "Select all recovery files" };
    private readonly Button _refresh = new() { Text = "Refresh", AutoSize = true };
    private readonly Button _archiveHistory = new() { Text = "Archive completed history", AutoSize = true, AccessibleName = "Archive completed recovery history" };
    private readonly CheckBox _includeArchive = new() { Text = "Inspect archived payloads", AutoSize = true, AccessibleName = "Inspect archived recovery payloads" };
    private bool _inspectArchivedPayloads;
    private bool _working;
    private volatile bool _closeRequested;
    private CancellationTokenSource? _listingCancellation;
    private IReadOnlyList<RecoveryEntry> _files = [];
    private readonly Func<string, Action<string>, CancellationToken, IReadOnlyList<RecoveryEntry>> _list;

    internal RecoveryDialog(string root,
        Func<string, Action<string>, CancellationToken, IReadOnlyList<RecoveryEntry>>? list = null)
    {
        _root = root;
        _list = list ?? ((path, warning, token) => RecoveryStore.List(path, warning, token, includeArchivedPayloads: _inspectArchivedPayloads));
        Text = "Recovery";
        AccessibleName = "Recovery window";
        Program.ApplyIcon(this);
        StartPosition = FormStartPosition.CenterParent;
        Size = new Size(920, 560);
        MinimumSize = new Size(680, 420);
        Font = AppTheme.UiFont;
        var layout = new TableLayoutPanel { Dock = DockStyle.Fill, Padding = new Padding(18), RowCount = 4, ColumnCount = 1 };
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        var note = new Label { Text = "Recovery keeps files beside their original folder. Restore never overwrites another file.\nStorage is freed only by permanent deletion. Rescan after restoring.", AutoSize = true, Margin = new Padding(0, 0, 0, 14) };
        layout.SizeChanged += (_, _) =>
        {
            var available = Math.Max(1, layout.ClientSize.Width - layout.Padding.Horizontal - 8);
            note.MaximumSize = _status.MaximumSize = new Size(available, 0);
        };
        layout.Controls.Add(note, 0, 0);
        _entries.Columns.Add("Original path", 540);
        _entries.Columns.Add("Size", 100, HorizontalAlignment.Right);
        _entries.Columns.Add("Moved at", 180);
        _entries.VirtualMode = true;
        _entries.RetrieveVirtualItem += (_, e) =>
        {
            var entry = _files[e.ItemIndex];
            e.Item = new ListViewItem([entry.OriginalPath, MainForm.FormatSize(entry.Size), entry.RemovedAt.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss")]);
        };
        layout.Controls.Add(_entries, 0, 1);
        layout.Controls.Add(_status, 0, 2);
        var actions = new FlowLayoutPanel { AutoSize = true, Dock = DockStyle.Fill, WrapContents = true };
        var close = new Button { Text = "Close", AutoSize = true, DialogResult = DialogResult.Cancel };
        actions.Controls.AddRange([_restore, _purge, _selectAll, _refresh, _archiveHistory, _includeArchive, close]);
        layout.Controls.Add(actions, 0, 3);
        Controls.Add(layout);
        AppTheme.Apply(this);
        AppTheme.Primary(_restore);
        AppTheme.Primary(_purge, true);
        CancelButton = close;
        _entries.SelectedIndexChanged += (_, _) => UpdateButtons();
        // Virtual lists report shift/drag range selections through this event only.
        _entries.VirtualItemsSelectionRangeChanged += (_, _) => UpdateButtons();
        _selectAll.Click += (_, _) => SelectAll();
        _includeArchive.CheckedChanged += async (_, _) => { _inspectArchivedPayloads = _includeArchive.Checked; if (!_working) await ReloadAsync(); };
        _archiveHistory.Click += async (_, _) => await ArchiveHistoryAsync();
        _refresh.Click += async (_, _) => await ReloadAsync();
        _restore.Click += async (_, _) => await OperateAsync(false);
        _purge.Click += async (_, _) => await OperateAsync(true);
        Shown += async (_, _) => { AppTheme.EnableDarkTitleBar(this); await ReloadAsync(); };
        FormClosing += (_, e) =>
        {
            if (!_working) return;
            e.Cancel = true;
            _closeRequested = true;
            if (_listingCancellation is null)
            {
                // Restore and purge stop after the file in progress, then the window closes.
                _status.Text = "Closing after the current file finishes...";
                return;
            }
            _status.Text = "Cancelling recovery discovery...";
            _listingCancellation.Cancel();
        };
    }

    private void UpdateButtons()
    {
        _restore.Enabled = _purge.Enabled = !_working && _entries.SelectedIndices.Count > 0;
        _selectAll.Enabled = !_working && _files.Count > 0;
        _refresh.Enabled = _archiveHistory.Enabled = _includeArchive.Enabled = !_working;
    }

    private void SelectAll()
    {
        if (_working || _files.Count == 0) return;
        // LVM_SETITEMSTATE with index -1 selects every row of a virtual list at once.
        var item = new ListViewItemState { Mask = 0x8, State = 0x2, StateMask = 0x2 };
        SendMessage(_entries.Handle, 0x102B, -1, ref item);
        _entries.Focus();
        UpdateButtons();
    }

    [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
    private struct ListViewItemState
    {
        public uint Mask; public int Item, SubItem; public uint State, StateMask; public IntPtr Text; public int TextMax, Image; public IntPtr Param;
        public int Indent, GroupId; public uint Columns; public IntPtr ColumnList, ColumnFormats; public int Group;
    }

    [System.Runtime.InteropServices.DllImport("user32.dll", EntryPoint = "SendMessageW")]
    private static extern IntPtr SendMessage(IntPtr window, int message, int index, ref ListViewItemState item);

    private async Task ReloadAsync()
    {
        _working = true;
        using var cancellation = new CancellationTokenSource();
        _listingCancellation = cancellation;
        UpdateButtons();
        _status.Text = "Reading recovery files...";
        try
        {
            var result = await Task.Run(() =>
            {
                var warnings = new List<string>();
                var files = _list(_root, warnings.Add, cancellation.Token);
                return (Files: files, Warnings: warnings);
            });
            var entries = result.Files;
            _entries.BeginUpdate();
            try
            {
                _entries.SelectedIndices.Clear();
                _entries.VirtualListSize = 0;
                _files = entries;
                _entries.VirtualListSize = entries.Count;
            }
            finally { _entries.EndUpdate(); }
            _status.Text = $"{entries.Count} recoverable files ({MainForm.FormatSize(entries.Sum(entry => entry.Size))}) in this folder and its subfolders.";
            if (result.Warnings.Count > 0) _status.Text += $" {result.Warnings.Count} locations could not be read. First error: {result.Warnings[0]}";
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
        { _status.Text = "Recovery discovery cancelled."; }
        catch (Exception ex) { _status.Text = $"Recovery could not be read: {ex.Message}"; }
        finally
        {
            _listingCancellation = null;
            _working = false;
            UpdateButtons();
            if (_closeRequested) Close();
        }
    }

    private async Task ArchiveHistoryAsync()
    {
        if (_working || MessageBox.Show(this,
            "Archive completed recovery history older than 30 days?\n\nOnly entries with a successful restore or permanent deletion record and no remaining payload qualify. Their original metadata is moved into .completed-history beside the recovery store. Active files, incomplete entries and unreadable entries are preserved. No files are permanently deleted. Archived records remain available on disk. Use Inspect archived payloads if external changes or an interrupted archive leave files there.",
            "Archive completed history", MessageBoxButtons.OKCancel, MessageBoxIcon.Information,
            MessageBoxDefaultButton.Button2) != DialogResult.OK) return;
        _working = true;
        using var cancellation = new CancellationTokenSource();
        _listingCancellation = cancellation;
        UpdateButtons();
        _status.Text = "Archiving completed history...";
        string outcome;
        try
        {
            var result = await Task.Run(() => RecoveryStore.ArchiveCompletedHistory(_root,
                DateTimeOffset.UtcNow.AddDays(-30), cancellation.Token));
            outcome = $"Archived {result.Archived} completed history entries; preserved {result.Preserved} other entries.";
            if (result.Warnings.Count > 0) outcome += $" {result.Warnings.Count} warnings. First warning: {result.Warnings[0]}";
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
        { outcome = "History archival cancelled. Entries already archived remain available on disk."; }
        catch (Exception ex) { outcome = $"History archival stopped: {ex.Message}"; }
        finally { _listingCancellation = null; _working = false; UpdateButtons(); }
        if (_closeRequested) { Close(); return; }
        await ReloadAsync();
        if (!IsDisposed) _status.Text = $"{outcome} {_status.Text}";
    }
    private async Task OperateAsync(bool permanent)
    {
        if (_working || _entries.SelectedIndices.Count == 0) return;
        var selected = _entries.SelectedIndices.Cast<int>().Order().Select(index => _files[index]).ToArray();
        var question = selected.Length == 1
            ? $"Permanently delete this recovery file? This cannot be undone.\n\n{selected[0].OriginalPath}"
            : $"Permanently delete {selected.Length} recovery files ({MainForm.FormatSize(selected.Sum(entry => entry.Size))})? This cannot be undone.\n\n" +
              string.Join("\n", selected.Take(10).Select(entry => entry.OriginalPath)) + (selected.Length > 10 ? $"\n… and {selected.Length - 10} more" : string.Empty);
        if (permanent && MessageBox.Show(this, question, "Confirm permanent deletion", MessageBoxButtons.OKCancel, MessageBoxIcon.Warning,
            MessageBoxDefaultButton.Button2) != DialogResult.OK) return;
        _working = true;
        UpdateButtons();
        string outcome;
        try
        {
            var (completed, warnings, failures) = await Task.Run(() =>
            {
                var done = 0;
                var notes = new List<string>();
                var errors = new List<string>();
                foreach (var entry in selected)
                {
                    if (_closeRequested) break;
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
        }
        catch (Exception ex) { outcome = $"Operation stopped: {ex.Message}"; }
        finally { _working = false; }
        if (_closeRequested) { Close(); return; }
        await ReloadAsync();
        if (!IsDisposed) _status.Text = $"{outcome} {_status.Text}";
    }
}
