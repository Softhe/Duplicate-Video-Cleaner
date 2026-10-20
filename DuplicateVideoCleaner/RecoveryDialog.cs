namespace DuplicateVideoCleaner;

internal sealed class RecoveryDialog : Form
{
    private readonly string _root;
    private readonly ListView _entries = new() { Dock = DockStyle.Fill, View = View.Details, FullRowSelect = true, MultiSelect = false, HideSelection = false, AccessibleName = "Recovery files" };
    private readonly Label _status = new() { AutoSize = true, Dock = DockStyle.Top, AccessibleName = "Recovery status" };
    private readonly Button _restore = new() { Text = "Restore selected", AutoSize = true, Enabled = false, AccessibleName = "Restore recovery file" };
    private readonly Button _purge = new() { Text = "Permanently delete selected", AutoSize = true, Enabled = false, AccessibleName = "Permanently delete recovery file" };
    private readonly Button _refresh = new() { Text = "Refresh", AutoSize = true };
    private readonly Button _archiveHistory = new() { Text = "Archive completed history", AutoSize = true, AccessibleName = "Archive completed recovery history" };
    private readonly CheckBox _includeArchive = new() { Text = "Inspect archived payloads", AutoSize = true, AccessibleName = "Inspect archived recovery payloads" };
    private bool _inspectArchivedPayloads;
    private bool _working;
    private bool _closeRequested;
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
        _entries.Columns.Add("Original path", 620);
        _entries.Columns.Add("Moved at", 200);
        _entries.VirtualMode = true;
        _entries.RetrieveVirtualItem += (_, e) =>
        {
            var entry = _files[e.ItemIndex];
            e.Item = new ListViewItem([entry.OriginalPath, entry.RemovedAt.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss")]);
        };
        layout.Controls.Add(_entries, 0, 1);
        layout.Controls.Add(_status, 0, 2);
        var actions = new FlowLayoutPanel { AutoSize = true, Dock = DockStyle.Fill, WrapContents = true };
        var close = new Button { Text = "Close", AutoSize = true, DialogResult = DialogResult.Cancel };
        actions.Controls.AddRange([_restore, _purge, _refresh, _archiveHistory, _includeArchive, close]);
        layout.Controls.Add(actions, 0, 3);
        Controls.Add(layout);
        AppTheme.Apply(this);
        AppTheme.Primary(_restore);
        AppTheme.Primary(_purge, true);
        CancelButton = close;
        _entries.SelectedIndexChanged += (_, _) => UpdateButtons();
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
            if (_listingCancellation is null) return;
            _closeRequested = true;
            _status.Text = "Cancelling recovery discovery...";
            _listingCancellation.Cancel();
        };
    }

    private void UpdateButtons()
    {
        _restore.Enabled = _purge.Enabled = !_working && _entries.SelectedIndices.Count == 1;
        _refresh.Enabled = _archiveHistory.Enabled = _includeArchive.Enabled = !_working;
    }

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
            _status.Text = $"{entries.Count} recoverable files in this folder and its subfolders.";
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
        if (_working || _entries.SelectedIndices.Count != 1) return;
        var entry = _files[_entries.SelectedIndices[0]];
        if (permanent && MessageBox.Show(this, $"Permanently delete this recovery file? This cannot be undone.\n\n{entry.OriginalPath}",
            "Confirm permanent deletion", MessageBoxButtons.OKCancel, MessageBoxIcon.Warning, MessageBoxDefaultButton.Button2) != DialogResult.OK) return;
        _working = true;
        UpdateButtons();
        string outcome;
        try
        {
            var warning = await Task.Run(() => permanent ? RecoveryStore.Purge(entry) : RecoveryStore.Restore(entry));
            outcome = permanent ? "Recovery file permanently deleted." : "File restored. Rescan to update the review.";
            if (warning is not null) outcome += $" {warning}";
        }
        catch (Exception ex) { outcome = $"Operation stopped: {ex.Message}"; }
        finally { _working = false; }
        await ReloadAsync();
        if (!IsDisposed) _status.Text = $"{outcome} {_status.Text}";
    }
}
