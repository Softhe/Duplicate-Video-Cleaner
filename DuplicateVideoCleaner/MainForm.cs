using System.Diagnostics;
using System.Text;

namespace DuplicateVideoCleaner;

internal enum WorkspacePage { Scan, Review, Activity }

public sealed class MainForm : Form
{
    private readonly TextBox _folderText = new() { Dock = DockStyle.Fill, AccessibleName = "Video folder" };
    private readonly Button _browseButton = new() { Text = "Browse…", AutoSize = true, AccessibleName = "Browse for video folder" };
    private readonly NumericUpDown _threshold = new() { Minimum = 1, Maximum = 86400, Value = 60, Width = 110, AccessibleName = "Duplicate threshold in seconds" };
    private readonly DarkComboBox _keepMode = new() { DropDownStyle = ComboBoxStyle.DropDownList, Width = 150, AccessibleName = "File to keep" };
    private readonly CheckBox _subfolders = new() { Text = "Include subfolders", AutoSize = true, AccessibleName = "Include subfolders" };
    private readonly CheckBox _protectUndated = new() { Text = "Protect clips without a date in the name", AutoSize = true, Margin = new Padding(0, 6, 0, 0), AccessibleName = "Protect clips without a recording date in the file name", AccessibleDescription = "When on, videos whose names contain no recording date are kept, and only originals with matching metadata or another original nearby can be removed" };
    private readonly DarkComboBox _deletionMode = new() { DropDownStyle = ComboBoxStyle.DropDownList, Width = 170, AccessibleName = "Deletion method" };
    private readonly Button _scanButton = new() { Text = "Scan videos", AutoSize = true, AccessibleName = "Scan videos", Font = new Font(AppTheme.UiFont, FontStyle.Bold) };
    private readonly Button _cancelScanButton = new() { Text = "Cancel scan", AutoSize = true, Visible = false, AccessibleName = "Cancel scan" };
    private readonly Button _rescanButton = new() { Text = "Rescan", AutoSize = true, AccessibleName = "Rescan videos" };
    private readonly Button _clearButton = new() { Text = "Clear results", AutoSize = true, AccessibleName = "Clear results" };
    private readonly Button _exportButton = new() { Text = "Export dry run…", AutoSize = true, AccessibleName = "Export dry run" };
    private readonly Button _deleteButton = new() { Text = "Move selected…", AutoSize = true, Enabled = false, AccessibleName = "Move selected files", Font = new Font(AppTheme.UiFont, FontStyle.Bold) };
    private readonly Button _selectSuggestedButton = new() { Text = "Suggested", AutoSize = true, AccessibleName = "Select all suggested removals" };
    private readonly Button _selectAllButton = new() { Text = "All", AutoSize = true, AccessibleName = "Select all files" };
    private readonly Button _selectNoneButton = new() { Text = "None", AutoSize = true, AccessibleName = "Clear all selections" };
    private readonly Button _openLogButton = new() { Text = "Open cleanup log", AutoSize = true, AccessibleName = "Open cleanup log" };
    private readonly Button _recoveryButton = new() { Text = "Recovery…", AutoSize = true, AccessibleName = "Open recovery" };
    private readonly Label _scanStatus = new() { AutoSize = true, Text = "Choose a folder to begin.", ForeColor = AppTheme.MutedForeground, AccessibleName = "Scan status: Choose a folder to begin" };
    private readonly Label _reviewSummary = new() { AutoSize = true, Font = new Font(AppTheme.UiFont, FontStyle.Bold), AccessibleName = "Review summary" };
    private readonly Label _operationStatus = new() { AutoSize = true, ForeColor = AppTheme.MutedForeground, AccessibleName = "Review status" };
    private readonly Label _selectionSummary = new() { AutoSize = true, Text = "Nothing selected", Font = new Font(AppTheme.UiFont, FontStyle.Bold), AccessibleName = "Nothing selected" };
    private readonly ProgressBar _scanProgress = new() { Style = ProgressBarStyle.Marquee, Visible = false, Width = 190 };
    private readonly ProgressBar _reviewProgress = new() { Style = ProgressBarStyle.Marquee, Visible = false, Width = 150 };
    private readonly Panel _contentHost = new() { Dock = DockStyle.Fill };
    private readonly DoubleBufferedPanel _cardHost = new() { Dock = DockStyle.Fill, AutoScroll = true, Padding = new Padding(0, 0, 12, 0), AccessibleName = "Duplicate group cards" };
    private readonly DoubleBufferedPanel _activityHost = new() { Dock = DockStyle.Fill, AutoScroll = true, Padding = new Padding(0, 8, 10, 0), AccessibleName = "Current session activity" };
    private readonly Panel _actionDock = new() { Dock = DockStyle.Bottom, Height = 82, Visible = false, BackColor = AppTheme.SidebarBack };
    private readonly SidebarButton _scanNav = new("  Scan", "Scan workspace, Control 1");
    private readonly SidebarButton _reviewNav = new("  Review", "Review workspace, Control 2");
    private readonly SidebarButton _activityNav = new("  Activity", "Activity workspace, Control 3");
    private readonly List<DuplicateGroupCard> _cards = [];
    private const int ReviewPageSize = 25;
    private int _reviewPageIndex;
    private readonly Button _previousGroups = new() { Text = "Previous groups", AutoSize = true, AccessibleName = "Previous review page" };
    private readonly Button _nextGroups = new() { Text = "Next groups", AutoSize = true, AccessibleName = "Next review page" };
    private readonly Label _groupPageStatus = new() { AutoSize = true, Margin = new Padding(8), AccessibleName = "Review page" };
    private readonly FlowLayoutPanel _groupPager = new() { AutoSize = true, Dock = DockStyle.Top, WrapContents = false };
    private readonly SettingsService _settingsService = new();
    private readonly MetadataProbe _metadataProbe = new();
    private readonly SettingsSaveQueue _settingsQueue;
    private readonly ActivityLog _activity = new();
    private readonly ReviewWorkspace _workspace;
    private readonly ScanWorkflow _scanWorkflow;
    private bool _settingsFlushed;
    private bool _savingBeforeClose;
    private Control _scanPage = null!;
    private Control _reviewPage = null!;
    private Control _activityPage = null!;
    private IReadOnlyList<DuplicateGroup> _groups => _workspace.Groups;
    private WorkspacePage _page = WorkspacePage.Scan;
    private bool _busy;
    private CancellationTokenSource? _scanCancellation;
    private CancellationTokenSource? _exportCancellation;
    private readonly Button _cancelExportButton = new() { Text = "Cancel export", AutoSize = true, Visible = false, AccessibleName = "Cancel export" };
    private bool _closeWhenIdle;

    public MainForm() : this(null) { }

    internal MainForm(Action<AppSettings>? settingsWriter)
    {
        _settingsQueue = new SettingsSaveQueue(settingsWriter ?? _settingsService.Save);
        _workspace = new ReviewWorkspace(_activity);
        _scanWorkflow = new ScanWorkflow(_metadataProbe, _activity);
        _activity.Added += ShowActivity;
        Text = "Duplicate Video Cleaner";
        Program.ApplyIcon(this);
        StartPosition = FormStartPosition.CenterScreen;
        MinimumSize = new Size(980, 640);
        Size = new Size(1240, 790);
        BackColor = AppTheme.WindowBack;
        ForeColor = AppTheme.Foreground;
        Font = AppTheme.UiFont;
        AutoScaleMode = AutoScaleMode.Dpi;

        _keepMode.Items.AddRange(["Oldest", "Newest"]);
        _keepMode.SelectedIndex = 0;
        _deletionMode.Items.AddRange(["Recovery", "Permanent"]);
        _deletionMode.SelectedIndex = 0;
        RestoreSettings(_settingsService.Load());

        Controls.Add(BuildShell());
        AppTheme.Apply(this);
        AppTheme.Primary(_scanButton);
        AppTheme.Primary(_deleteButton);
        Navigate(WorkspacePage.Scan);

        _browseButton.Click += BrowseClicked;
        _scanButton.Click += async (_, _) => await ScanAsync();
        _cancelScanButton.Click += (_, _) => CancelScan();
        _rescanButton.Click += async (_, _) => await ScanAsync();
        _clearButton.Click += (_, _) => ClearResults();
        _exportButton.Click += async (_, _) => await ExportDryRunAsync();
        _cancelExportButton.Click += (_, _) => _exportCancellation?.Cancel();
        _deleteButton.Click += async (_, _) => await DeleteSelectedAsync();
        _selectSuggestedButton.Click += (_, _) => SetSuggestedSelection();
        _selectAllButton.Click += (_, _) => SetAllSelections(true);
        _selectNoneButton.Click += (_, _) => SetAllSelections(false);
        _deletionMode.SelectedIndexChanged += (_, _) => { UpdateDeleteButtonText(); SaveSettingsSafe(); };
        _scanNav.Click += (_, _) => Navigate(WorkspacePage.Scan);
        _reviewNav.Click += (_, _) => Navigate(WorkspacePage.Review);
        _activityNav.Click += (_, _) => Navigate(WorkspacePage.Activity);
        _openLogButton.Click += (_, _) => OpenCleanupLog();
        _recoveryButton.Click += (_, _) =>
        {
            if (_busy) return;
            var root = _workspace.Context?.Root ?? _folderText.Text.Trim();
            if (!Directory.Exists(root)) { SetScanStatus("Choose an existing folder before opening Recovery."); return; }
            using var dialog = new RecoveryDialog(root);
            dialog.ShowDialog(this);
        };
        _cardHost.SizeChanged += (_, _) => ResizeCardWidths();
        _activityHost.SizeChanged += (_, _) => ResizeActivityWidths();
        Shown += (_, _) => AppTheme.EnableDarkTitleBar(this);
        FormClosing += async (_, e) =>
        {
            if (_busy)
            {
                e.Cancel = true;
                _closeWhenIdle = true;
                if (_scanCancellation is not null) CancelScan();
                else if (_exportCancellation is not null) { _exportCancellation.Cancel(); _operationStatus.Text = "Cancelling export before closing…"; }
                else _operationStatus.Text = "Waiting for removal results before closing…";
                return;
            }
            if (_settingsFlushed) return;
            e.Cancel = true;
            if (_savingBeforeClose) return;
            _savingBeforeClose = true;
            SaveSettingsSafe();
            Enabled = false;
            await _settingsQueue.FlushAsync();
            await Task.Yield();
            if (_settingsQueue.LastError is { } settingsError)
                MessageBox.Show(this, $"Preferences could not be saved. Your current files and recovery data are unaffected.\n\n{settingsError.Message}", "Preferences were not saved", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            _settingsFlushed = true;
            Close();
        };
    }

    private Control BuildShell()
    {
        var shell = new Panel { Dock = DockStyle.Fill, BackColor = AppTheme.WindowBack };
        var sidebar = BuildSidebar();
        _scanPage = BuildScanPage();
        _reviewPage = BuildReviewPage();
        _activityPage = BuildActivityPage();
        _contentHost.Controls.AddRange([_activityPage, _reviewPage, _scanPage]);
        BuildActionDock();
        shell.Controls.Add(_contentHost);
        shell.Controls.Add(_actionDock);
        shell.Controls.Add(sidebar);
        return shell;
    }

    private Control BuildSidebar()
    {
        var sidebar = new Panel { Dock = DockStyle.Left, Width = 224, Padding = new Padding(12), BackColor = AppTheme.SidebarBack };
        var brand = new Panel { Dock = DockStyle.Top, Height = 156, BackColor = AppTheme.SidebarBack, AccessibleName = "Duplicate Video Cleaner navigation" };
        brand.Controls.Add(new Label { Text = "DVC /", AutoSize = true, Location = new Point(12, 10), Font = new Font(AppTheme.UiFont.FontFamily, 25, FontStyle.Bold), ForeColor = AppTheme.Accent, AccessibleName = "Duplicate Video Cleaner" });
        brand.Controls.Add(new Label { Text = "Duplicate Video Cleaner", AutoSize = true, Location = new Point(12, 65), Font = new Font(AppTheme.UiFont.FontFamily, 9, FontStyle.Bold) });
        brand.Controls.Add(new Label { Text = "YOUR CLEANUP WORKSPACE", AutoSize = true, Location = new Point(12, 104), Font = new Font(AppTheme.UiFont.FontFamily, 8), ForeColor = AppTheme.MutedForeground });
        var appVersion = typeof(MainForm).Assembly.GetName().Version?.ToString(3) ?? "Unknown";
        var version = new Label { Text = $"v{appVersion}", Dock = DockStyle.Bottom, Height = 36, ForeColor = AppTheme.MutedForeground, TextAlign = ContentAlignment.MiddleCenter, AccessibleName = $"Version {appVersion}" };
        sidebar.Controls.Add(version);
        sidebar.Controls.Add(_activityNav);
        sidebar.Controls.Add(_reviewNav);
        sidebar.Controls.Add(_scanNav);
        sidebar.Controls.Add(brand);
        return sidebar;
    }

    private Control BuildScanPage()
    {
        var page = new TableLayoutPanel { Dock = DockStyle.Fill, Padding = new Padding(34, 28, 34, 28), RowCount = 4, ColumnCount = 1, AccessibleName = "Scan workspace" };
        page.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        page.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        page.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        page.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        page.Controls.Add(PageHeading("Scan your video folder", "Choose a source and tune the matching rules before anything changes on disk."), 0, 0);

        var folderSurface = new TableLayoutPanel { Dock = DockStyle.Top, AutoSize = true, ColumnCount = 2, RowCount = 3, Padding = new Padding(24, 22, 24, 22), Margin = new Padding(0, 22, 0, 16), BackColor = AppTheme.CardBack, Tag = "surface", AllowDrop = true, AccessibleName = "Folder drop area" };
        folderSurface.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        folderSurface.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        folderSurface.Controls.Add(new Label { Text = "VIDEO SOURCE", AutoSize = true, Font = new Font(AppTheme.UiFont, FontStyle.Bold), ForeColor = AppTheme.Accent }, 0, 0);
        folderSurface.Controls.Add(new Label { Text = "Drop a folder here, paste a path, or browse", AutoSize = true, ForeColor = AppTheme.MutedForeground, Margin = new Padding(0, 6, 0, 14) }, 0, 1);
        folderSurface.SetColumnSpan(folderSurface.GetControlFromPosition(0, 1)!, 2);
        folderSurface.Controls.Add(_folderText, 0, 2);
        folderSurface.Controls.Add(_browseButton, 1, 2);
        folderSurface.DragEnter += FolderDragEnter;
        folderSurface.DragDrop += FolderDragDrop;
        page.Controls.Add(folderSurface, 0, 1);

        var ruleRow = new TableLayoutPanel { Dock = DockStyle.Top, AutoSize = true, ColumnCount = 3, Margin = new Padding(0, 0, 0, 18) };
        ruleRow.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 34));
        ruleRow.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 33));
        ruleRow.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 33));
        ruleRow.Controls.Add(RuleCard("TIME WINDOW", "Neighboring timestamps", _threshold, "seconds"), 0, 0);
        ruleRow.Controls.Add(RuleCard("RETENTION", "Suggested file to keep", _keepMode), 1, 0);
        var scope = new FlowLayoutPanel { AutoSize = true, FlowDirection = FlowDirection.TopDown, WrapContents = false, BackColor = AppTheme.CardBack, Margin = Padding.Empty };
        scope.Controls.AddRange([_subfolders, _protectUndated]);
        ruleRow.Controls.Add(RuleCard("SCAN SCOPE", "Folder traversal and protection", scope), 2, 0);
        page.Controls.Add(ruleRow, 0, 2);

        var launch = new TableLayoutPanel { Dock = DockStyle.Top, AutoSize = true, ColumnCount = 2, Padding = new Padding(20), BackColor = AppTheme.CardBack, Tag = "surface" };
        launch.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        launch.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        var statusStack = new FlowLayoutPanel { AutoSize = true, FlowDirection = FlowDirection.TopDown, WrapContents = false, BackColor = AppTheme.CardBack };
        statusStack.Controls.Add(_scanStatus);
        statusStack.Controls.Add(_scanProgress);
        launch.Controls.Add(statusStack, 0, 0);
        var scanActions = new FlowLayoutPanel { AutoSize = true, WrapContents = false };
        scanActions.Controls.AddRange([_cancelScanButton, _scanButton]);
        launch.Controls.Add(scanActions, 1, 0);
        page.Controls.Add(launch, 0, 3);
        return page;
    }

    private Control BuildReviewPage()
    {
        var page = new TableLayoutPanel { Dock = DockStyle.Fill, Padding = new Padding(28, 24, 28, 16), RowCount = 3, ColumnCount = 1, AccessibleName = "Review workspace" };
        page.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        page.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        page.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        page.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        var top = new TableLayoutPanel { Dock = DockStyle.Top, AutoSize = true, ColumnCount = 2 };
        top.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        top.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        top.Controls.Add(PageHeading("Review duplicate groups", "Open a group for full metadata or adjust selections directly."), 0, 0);
        var actions = new FlowLayoutPanel { AutoSize = true, WrapContents = false, FlowDirection = FlowDirection.RightToLeft };
        actions.Controls.AddRange([_clearButton, _rescanButton]);
        top.Controls.Add(actions, 1, 0);

        var toolbar = new TableLayoutPanel { Dock = DockStyle.Top, AutoSize = true, MinimumSize = new Size(0, 82), ColumnCount = 3, Padding = new Padding(16, 12, 12, 10), Margin = new Padding(0, 18, 0, 16), BackColor = AppTheme.CardBack, Tag = "surface" };
        toolbar.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        toolbar.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        toolbar.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        toolbar.Controls.Add(_reviewSummary, 0, 0);
        var bulk = new FlowLayoutPanel { AutoSize = true, WrapContents = false, BackColor = AppTheme.CardBack };
        bulk.Controls.Add(new Label { Text = "Select:", AutoSize = true, ForeColor = AppTheme.MutedForeground, Margin = new Padding(0, 8, 6, 0) });
        bulk.Controls.AddRange([_selectSuggestedButton, _selectAllButton, _selectNoneButton]);
        toolbar.Controls.Add(bulk, 1, 0);
        toolbar.Controls.Add(_reviewProgress, 2, 0);
        toolbar.Controls.Add(_operationStatus, 0, 1);
        toolbar.SetColumnSpan(_operationStatus, 3);
        _groupPager.Controls.AddRange([_previousGroups, _groupPageStatus, _nextGroups]);
        _previousGroups.Click += (_, _) => { if (!_busy && _reviewPageIndex > 0) { _reviewPageIndex--; BuildCards(); } };
        _nextGroups.Click += (_, _) => { if (!_busy && (_reviewPageIndex + 1) * ReviewPageSize < _groups.Count) { _reviewPageIndex++; BuildCards(); } };
        toolbar.Controls.Add(_groupPager, 0, 2);
        toolbar.SetColumnSpan(_groupPager, 3);
        toolbar.SizeChanged += (_, _) => _operationStatus.MaximumSize =
            new Size(Math.Max(1, toolbar.ClientSize.Width - toolbar.Padding.Horizontal - _operationStatus.Margin.Horizontal), 0);

        page.Controls.Add(top, 0, 0);
        page.Controls.Add(toolbar, 0, 1);
        page.Controls.Add(_cardHost, 0, 2);
        return page;
    }

    private Control BuildActivityPage()
    {
        var page = new TableLayoutPanel { Dock = DockStyle.Fill, Padding = new Padding(32, 26, 32, 24), RowCount = 3, ColumnCount = 1, AccessibleName = "Activity workspace" };
        page.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        page.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        page.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        page.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        var heading = new TableLayoutPanel { AutoSize = true, Dock = DockStyle.Top, ColumnCount = 2 };
        heading.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        heading.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        heading.Controls.Add(PageHeading("Session activity", "Scans, exports, and removals from this run of the app."), 0, 0);
        var activityActions = new FlowLayoutPanel { AutoSize = true, WrapContents = false };
        activityActions.Controls.AddRange([_recoveryButton, _openLogButton]);
        heading.Controls.Add(activityActions, 1, 0);
        var note = new Label { Text = "Activity shows the latest 200 entries and resets when the app closes. Cleanup outcomes are also written to DuplicateCleanupLog.txt in the selected folder.", AutoSize = true, MaximumSize = new Size(850, 0), ForeColor = AppTheme.MutedForeground, Margin = new Padding(0, 18, 0, 12), AccessibleName = "Activity is current session only; latest 200 entries" };
        page.SizeChanged += (_, _) => note.MaximumSize = new Size(Math.Max(1, page.ClientSize.Width - page.Padding.Horizontal - note.Margin.Horizontal), 0);
        page.Controls.Add(heading, 0, 0);
        page.Controls.Add(note, 0, 1);
        page.Controls.Add(_activityHost, 0, 2);
        AddActivity("Ready", "No file operations have been performed.", true);
        return page;
    }

    private void BuildActionDock()
    {
        var content = new TableLayoutPanel { Dock = DockStyle.Fill, Padding = new Padding(28, 18, 28, 14), ColumnCount = 4, BackColor = AppTheme.SidebarBack };
        content.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        content.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        content.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        content.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        content.Controls.Add(_selectionSummary, 0, 0);
        var mode = new FlowLayoutPanel { AutoSize = true, WrapContents = false, BackColor = AppTheme.SidebarBack, Margin = new Padding(0, 0, 12, 0) };
        mode.Controls.Add(new Label { Text = "Removal method", AutoSize = true, ForeColor = AppTheme.MutedForeground, Margin = new Padding(0, 8, 7, 0) });
        mode.Controls.Add(_deletionMode);
        content.Controls.Add(mode, 1, 0);
        var exportActions = new FlowLayoutPanel { AutoSize = true, WrapContents = false };
        exportActions.Controls.AddRange([_exportButton, _cancelExportButton]);
        content.Controls.Add(exportActions, 2, 0);
        content.Controls.Add(_deleteButton, 3, 0);
        _actionDock.Controls.Add(content);
    }

    private static Control PageHeading(string title, string subtitle)
    {
        var stack = new TableLayoutPanel { AutoSize = true, Dock = DockStyle.Top };
        stack.Controls.Add(new Label { Text = title, AutoSize = true, Font = new Font(AppTheme.UiFont.FontFamily, 22, FontStyle.Bold), AccessibleName = title });
        stack.Controls.Add(new Label { Text = subtitle, AutoSize = true, ForeColor = AppTheme.MutedForeground, Margin = new Padding(0, 5, 0, 0) });
        return stack;
    }

    private static Control RuleCard(string eyebrow, string description, Control input, string? suffix = null)
    {
        var card = new TableLayoutPanel { Dock = DockStyle.Fill, AutoSize = true, Padding = new Padding(16), Margin = new Padding(0, 0, 10, 0), BackColor = AppTheme.CardBack, Tag = "surface" };
        card.Controls.Add(new Label { Text = eyebrow, AutoSize = true, ForeColor = AppTheme.Accent, Font = new Font(AppTheme.UiFont, FontStyle.Bold) });
        card.Controls.Add(new Label { Text = description, AutoSize = true, ForeColor = AppTheme.MutedForeground, Margin = new Padding(0, 4, 0, 12) });
        if (suffix is null) card.Controls.Add(input);
        else
        {
            var row = new FlowLayoutPanel { AutoSize = true, WrapContents = false, BackColor = AppTheme.CardBack };
            row.Controls.Add(input);
            row.Controls.Add(new Label { Text = suffix, AutoSize = true, ForeColor = AppTheme.MutedForeground, Margin = new Padding(7, 7, 0, 0) });
            card.Controls.Add(row);
        }
        return card;
    }

    private void BrowseClicked(object? sender, EventArgs e)
    {
        using var dialog = new FolderBrowserDialog { Description = "Select the directory containing video files", ShowNewFolderButton = false };
        if (Directory.Exists(_folderText.Text)) dialog.InitialDirectory = _folderText.Text;
        if (dialog.ShowDialog(this) == DialogResult.OK) _folderText.Text = dialog.SelectedPath;
    }

    private void FolderDragEnter(object? sender, DragEventArgs e) => e.Effect = !_busy && e.Data?.GetDataPresent(DataFormats.FileDrop) == true ? DragDropEffects.Copy : DragDropEffects.None;
    private void FolderDragDrop(object? sender, DragEventArgs e)
    {
        if (!_busy && e.Data?.GetData(DataFormats.FileDrop) is string[] paths && paths.FirstOrDefault(Directory.Exists) is { } directory) _folderText.Text = directory;
    }

    internal async Task ScanAsync(Func<string, IEnumerable<string>, CancellationToken, string?>? diagnosticWriter = null)
    {
        if (_busy) return;
        var root = _folderText.Text.Trim();
        if (!Directory.Exists(root))
        {
            Navigate(WorkspacePage.Scan);
            SetScanStatus("Select an existing folder before scanning.");
            _folderText.Focus();
            AddActivity("Scan failed", "The selected folder does not exist.", false);
            return;
        }

        _reviewPageIndex = 0;
        _workspace.Clear();
        ClearCards();
        SaveSettingsSafe();
        Navigate(WorkspacePage.Scan);
        using var cancellation = new CancellationTokenSource();
        _scanCancellation = cancellation;
        SetBusy(true, "Scanning video files…");
        try
        {
            var keep = _keepMode.SelectedIndex == 0 ? KeepMode.Oldest : KeepMode.Newest;
            var context = new ScanContext(Path.GetFullPath(root), (int)_threshold.Value, keep, _subfolders.Checked, _protectUndated.Checked);
            _workspace.Begin(context);
            var outcome = await _scanWorkflow.RunAsync(context, new ScanProgressView(this, cancellation), cancellation.Token, diagnosticWriter);
            _workspace.Load(outcome);
            if (outcome.Groups.Count == 0)
            {
                SetScanStatus(outcome.Status);
                return;
            }
            BuildCards();
            _operationStatus.Text = outcome.Status;
            _operationStatus.AccessibleName = _operationStatus.Text;
            Navigate(WorkspacePage.Review);
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
        {
            _workspace.Clear();
            ClearCards();
            Navigate(WorkspacePage.Scan);
            SetScanStatus("Scan cancelled. No video files were changed.");
            AddActivity("Scan cancelled", "Partial scan results were discarded.", false);
        }
        catch (Exception ex)
        {
            _workspace.Clear();
            ClearCards();
            Navigate(WorkspacePage.Scan);
            SetScanStatus($"Scan failed: {ex.Message}");
            AddActivity("Scan failed", ex.Message, false);
        }
        finally { _scanCancellation = null; SetBusy(false); }
    }

    // Shows scan progress on the scan page and polls metadata progress without one UI post per file.
    private sealed class ScanProgressView(MainForm form, CancellationTokenSource cancellation) : IScanProgress
    {
        private System.Windows.Forms.Timer? _timer;

        public void Status(string text) => form.SetScanStatus(text);

        public void MetadataStarted(LatestMetadataProgress progress)
        {
            _timer = new System.Windows.Forms.Timer { Interval = 150 };
            _timer.Tick += (_, _) =>
            {
                if (progress.Latest is { } update && !cancellation.IsCancellationRequested)
                    form.SetScanStatus(ScanWorkflow.FormatProgress(update));
            };
            _timer.Start();
        }

        public void MetadataFinished()
        {
            _timer?.Stop();
            _timer?.Dispose();
            _timer = null;
        }
    }

    private void CancelScan()
    {
        _scanCancellation?.Cancel();
        _cancelScanButton.Enabled = false;
        SetScanStatus("Cancelling scan…");
    }

    private void BuildCards()
    {
        ClearCards();
        _reviewPageIndex = Math.Clamp(_reviewPageIndex, 0, Math.Max(0, (_groups.Count - 1) / ReviewPageSize));
        _groupPager.Visible = _groups.Count > ReviewPageSize;
        _previousGroups.Enabled = !_busy && _reviewPageIndex > 0;
        _nextGroups.Enabled = !_busy && (_reviewPageIndex + 1) * ReviewPageSize < _groups.Count;
        _groupPageStatus.Text = $"Groups {_reviewPageIndex * ReviewPageSize + 1}–{Math.Min(_groups.Count, (_reviewPageIndex + 1) * ReviewPageSize)} of {_groups.Count}";
        _cardHost.SuspendLayout();
        foreach (var group in _groups.Skip(_reviewPageIndex * ReviewPageSize).Take(ReviewPageSize).Reverse())
        {
            DuplicateGroupCard? card = null;
            card = new DuplicateGroupCard(group, _workspace.Reviewed, _workspace.IsSelected, SelectionChanged,
                suggested =>
                {
                    _workspace.SelectGroup(group, suggested);
                    card!.RefreshSelectionState();
                    UpdateSelectionStatus();
                }, () => _workspace.GroupSelection(group.Number));
            _cards.Add(card);
            _cardHost.Controls.Add(card);
            _cardHost.Controls.SetChildIndex(card, 0);
        }
        _cards.Reverse();
        _cardHost.ResumeLayout();
        ResizeCardWidths();
        UpdateSelectionStatus();
    }

    private void ClearCards()
    {
        foreach (var card in _cardHost.Controls.Cast<Control>().ToArray()) card.Dispose();
        _cardHost.Controls.Clear();
        _cards.Clear();
    }

    private void SelectionChanged(VideoCandidate file, bool selected)
    {
        if (_busy) return;
        _workspace.SetSelected(file, selected);
        foreach (var card in _cards) card.RefreshSelectionState();
        UpdateSelectionStatus();
    }

    private void SetSuggestedSelection()
    {
        _workspace.SelectSuggested();
        RefreshCardsAndSummary();
    }

    private void SetAllSelections(bool selected)
    {
        _workspace.SelectAll(selected);
        RefreshCardsAndSummary();
    }

    private void RefreshCardsAndSummary()
    {
        foreach (var card in _cards) card.RefreshSelectionState();
        UpdateSelectionStatus();
    }

    private async Task DeleteSelectedAsync()
    {
        if (_busy || _workspace.Context is null) return;
        var selected = _workspace.SelectedFiles();
        if (selected.Count == 0) return;
        var permanent = _deletionMode.SelectedIndex == 1;
        var groupsWithoutSurvivors = _workspace.GroupsWithoutSurvivors();
        using var confirmation = new DeletionConfirmationDialog(selected, permanent, FormatSize(selected.Sum(file => file.Size)), groupsWithoutSurvivors);
        if (confirmation.ShowDialog(this) != DialogResult.OK) return;
        if (!_workspace.TryStartRemoval(out var logError))
        {
            MessageBox.Show(this, $"Deletion was not started because the log cannot be written:\n\n{logError}", "Log unavailable", MessageBoxButtons.OK, MessageBoxIcon.Error);
            return;
        }

        SetBusy(true, "Removing selected files…");
        try
        {
            var outcome = await _workspace.RemoveAsync(selected, permanent);
            BuildCards();
            _operationStatus.Text = outcome.Status;
            if (outcome.Successful) MessageBox.Show(this, outcome.Message, "Removal complete", MessageBoxButtons.OK, MessageBoxIcon.Information);
            else MessageBox.Show(this, outcome.Message, "Removal needs attention", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            if (_groups.Count == 0) Navigate(WorkspacePage.Scan);
        }
        catch (Exception ex)
        {
            ClearResults();
            AddActivity("Removal interrupted", $"Rescan before continuing. {ex.Message}", false);
            MessageBox.Show(this, $"Removal was interrupted. Some files may already have been removed. Rescan before continuing.\n\n{ex.Message}",
                "Removal interrupted", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
        finally { SetBusy(false); }
    }

    internal void ReconcileRemoval(RemovalResult result)
    {
        _workspace.Reconcile(result);
        BuildCards();
    }

    private void ClearResults()
    {
        _workspace.Clear();
        ClearCards();
        SetScanStatus("Results cleared. Choose a folder or adjust the rules for another scan.");
        AddActivity("Results cleared", "The in-memory review was cleared; no video files were changed.", true);
        Navigate(WorkspacePage.Scan);
        UpdateSelectionStatus();
    }

    private void UpdateSelectionStatus()
    {
        var selected = _workspace;
        var bytes = selected.SelectedBytes;
        _selectionSummary.Text = selected.SelectedCount == 0 ? "Nothing selected" : $"{selected.SelectedCount} file(s) · {FormatSize(bytes)}";
        _selectionSummary.AccessibleName = selected.SelectedCount == 0 ? "Nothing selected" : $"{selected.SelectedCount} files selected, {FormatSize(bytes)}";
        _reviewSummary.Text = $"{_groups.Count} group(s)   ·   {selected.FileCount} files   ·   {selected.SelectedCount} selected   ·   {FormatSize(bytes)}";
        _reviewSummary.AccessibleName = $"{_groups.Count} duplicate groups; {selected.FileCount} files; {selected.SelectedCount} selected; {FormatSize(bytes)} selected size";
        _deleteButton.Enabled = selected.SelectedCount > 0 && !_busy;
        _exportButton.Enabled = _groups.Count > 0 && !_busy;
        _reviewNav.Enabled = _groups.Count > 0 && !_busy;
        UpdateDeleteButtonText();
    }

    private void UpdateDeleteButtonText()
    {
        var permanent = _deletionMode.SelectedIndex == 1;
        _deleteButton.Text = permanent ? "Delete permanently…" : "Move selected…";
        _deleteButton.AccessibleName = permanent ? "Delete selected files permanently" : "Move selected files to Recovery";
        AppTheme.Primary(_deleteButton, permanent);
    }

    private void Navigate(WorkspacePage page)
    {
        if (page == WorkspacePage.Review && _groups.Count == 0) return;
        _page = page;
        _scanPage.Visible = page == WorkspacePage.Scan;
        _reviewPage.Visible = page == WorkspacePage.Review;
        _activityPage.Visible = page == WorkspacePage.Activity;
        if (page == WorkspacePage.Scan) _scanPage.BringToFront();
        else if (page == WorkspacePage.Review) _reviewPage.BringToFront();
        else _activityPage.BringToFront();
        _scanNav.Selected = page == WorkspacePage.Scan;
        _reviewNav.Selected = page == WorkspacePage.Review;
        _activityNav.Selected = page == WorkspacePage.Activity;
        _actionDock.Visible = page == WorkspacePage.Review;
        AcceptButton = page == WorkspacePage.Scan ? _scanButton : null;
        if (page == WorkspacePage.Scan) _folderText.Focus();
        else if (page == WorkspacePage.Review && _cards.Count > 0) _cards[0].Focus();
        else _activityNav.Focus();
    }

    private void SetBusy(bool busy, string? status = null)
    {
        _busy = busy;
        _cancelExportButton.Visible = busy && _exportCancellation is not null;
        _cancelScanButton.Visible = busy && _scanCancellation is not null;
        _cancelScanButton.Enabled = _cancelScanButton.Visible;
        _scanProgress.Visible = busy && _page == WorkspacePage.Scan;
        _reviewProgress.Visible = busy && _page == WorkspacePage.Review;
        foreach (var control in new Control[] { _scanButton, _browseButton, _folderText, _threshold, _keepMode, _subfolders, _protectUndated, _rescanButton, _exportButton, _clearButton, _deletionMode, _selectSuggestedButton, _selectAllButton, _selectNoneButton, _cardHost, _scanNav, _reviewNav, _activityNav }) control.Enabled = !busy;
        _deleteButton.Enabled = false;
        if (status is not null)
        {
            if (_page == WorkspacePage.Scan) SetScanStatus(status); else { _operationStatus.Text = status; _operationStatus.AccessibleName = $"Review status: {status}"; }
        }
        if (!busy)
        {
            _previousGroups.Enabled = _reviewPageIndex > 0;
            _nextGroups.Enabled = (_reviewPageIndex + 1) * ReviewPageSize < _groups.Count;
            UpdateSelectionStatus();
            if (_closeWhenIdle) BeginInvoke(new Action(Close));
        }
    }

    protected override bool ProcessCmdKey(ref Message message, Keys keyData)
    {
        if (!_busy && keyData == (Keys.Control | Keys.D1)) { Navigate(WorkspacePage.Scan); return true; }
        if (!_busy && keyData == (Keys.Control | Keys.D2)) { Navigate(WorkspacePage.Review); return true; }
        if (!_busy && keyData == (Keys.Control | Keys.D3)) { Navigate(WorkspacePage.Activity); return true; }
        if (!_busy && _page == WorkspacePage.Review && keyData == (Keys.Alt | Keys.S)) { SetSuggestedSelection(); return true; }
        if (!_busy && _page == WorkspacePage.Review && keyData == (Keys.Alt | Keys.A)) { SetAllSelections(true); return true; }
        if (!_busy && _page == WorkspacePage.Review && keyData == (Keys.Alt | Keys.N)) { SetAllSelections(false); return true; }
        return base.ProcessCmdKey(ref message, keyData);
    }

    private async Task ExportDryRunAsync()
    {
        if (_busy || _groups.Count == 0 || _workspace.Context is not { } context) return;
        using var dialog = new FolderBrowserDialog { Description = "Choose a folder for the CSV and text dry-run reports", ShowNewFolderButton = true };
        if (Directory.Exists(context.Root)) dialog.InitialDirectory = context.Root;
        if (dialog.ShowDialog(this) != DialogResult.OK) return;
        await ExportDryRunToAsync(dialog.SelectedPath);
    }

    internal async Task ExportDryRunToAsync(string destination,
        Func<string, DryRunContext, CancellationToken, ExportResult>? export = null)
    {
        if (_busy || _groups.Count == 0 || _workspace.Context is null) return;
        using var cancellation = new CancellationTokenSource();
        _exportCancellation = cancellation;
        SetBusy(true, "Exporting dry run…");
        try
        {
            _operationStatus.Text = await _workspace.ExportDryRunAsync(destination, cancellation.Token, export);
            _operationStatus.AccessibleName = _operationStatus.Text;
        }
        finally { _exportCancellation = null; SetBusy(false); }
    }

    private void AddActivity(string title, string detail, bool success) => _activity.Add(title, detail, success);

    private void ShowActivity(ActivityEntry entry, ActivityEntry? evicted)
    {
        if (evicted is not null && _activityHost.Controls.Count > 0) _activityHost.Controls[^1].Dispose();
        var item = new ActivityItem(entry.Time, entry.Title, entry.Detail, entry.Success);
        _activityHost.Controls.Add(item);
        _activityHost.Controls.SetChildIndex(item, 0);
        ResizeActivityWidths();
    }

    private void OpenCleanupLog()
    {
        var path = GetLogPath();
        if (!File.Exists(path))
        {
            MessageBox.Show(this, "No cleanup log exists for the selected folder yet.", "Cleanup log", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }
        try { Process.Start(new ProcessStartInfo(path) { UseShellExecute = true }); }
        catch (Exception ex) { MessageBox.Show(this, ex.Message, "Could not open cleanup log", MessageBoxButtons.OK, MessageBoxIcon.Error); }
    }

    internal void ReportMaintenanceWarnings(IReadOnlyList<string> warnings)
    {
        foreach (var warning in warnings.Take(20)) AddActivity("Workspace cleanup needs attention", warning, false);
        if (warnings.Count > 20)
            AddActivity("Additional workspace cleanup warnings", $"{warnings.Count - 20} additional private workspace cleanup failures occurred. Temporary copies may remain until a later startup can remove them.", false);
    }

    private void SetScanStatus(string text) { _scanStatus.Text = text; _scanStatus.AccessibleName = $"Scan status: {text}"; }
    private void ResizeCardWidths() { var width = Math.Max(300, _cardHost.ClientSize.Width - _cardHost.Padding.Horizontal - SystemInformation.VerticalScrollBarWidth - 6); foreach (Control card in _cardHost.Controls) card.Width = width; }
    private void ResizeActivityWidths() { var width = Math.Max(300, _activityHost.ClientSize.Width - _activityHost.Padding.Horizontal - SystemInformation.VerticalScrollBarWidth - 6); foreach (Control item in _activityHost.Controls) item.Width = width; }
    internal string GetLogPath() => _workspace.LogPath(_folderText.Text);
    internal ReviewWorkspace Workspace => _workspace;
    internal DryRunContext BuildDryRunContext() => _workspace.CreateDryRun();
    internal static string FormatSize(long bytes) => SizeFormatter.Format(bytes);

    private void RestoreSettings(AppSettings settings)
    {
        _folderText.Text = settings.LastFolder;
        _threshold.Value = settings.ThresholdSeconds;
        _keepMode.SelectedIndex = settings.KeepMode == KeepMode.Newest ? 1 : 0;
        _subfolders.Checked = settings.IncludeSubfolders;
        _protectUndated.Checked = settings.ProtectUndatedClips;
        _deletionMode.SelectedIndex = settings.PermanentDeletion ? 1 : 0;
        var desired = new Rectangle(settings.WindowX, settings.WindowY, settings.WindowWidth, settings.WindowHeight);
        var visible = desired.Location != new Point(-1, -1) && HasReachableTitleBar(desired, Screen.AllScreens.Select(screen => screen.WorkingArea));
        if (visible) { StartPosition = FormStartPosition.Manual; Bounds = desired; } else Size = new Size(settings.WindowWidth, settings.WindowHeight);
        if (settings.Maximized) WindowState = FormWindowState.Maximized;
    }

    internal static bool HasReachableTitleBar(Rectangle bounds, IEnumerable<Rectangle> workingAreas) =>
        workingAreas.Any(area => { var visible = Rectangle.Intersect(area, new Rectangle(bounds.X, bounds.Y, bounds.Width, 32)); return visible.Width >= 100 && visible.Height >= 24; });

    private void SaveSettingsSafe()
    {
        try
        {
            var bounds = WindowState == FormWindowState.Normal ? Bounds : RestoreBounds;
            _settingsQueue.Enqueue(new AppSettings
            {
                LastFolder = _folderText.Text.Trim(), ThresholdSeconds = (int)_threshold.Value,
                KeepMode = _keepMode.SelectedIndex == 1 ? KeepMode.Newest : KeepMode.Oldest,
                IncludeSubfolders = _subfolders.Checked, ProtectUndatedClips = _protectUndated.Checked,
                PermanentDeletion = _deletionMode.SelectedIndex == 1,
                WindowX = bounds.X, WindowY = bounds.Y, WindowWidth = bounds.Width, WindowHeight = bounds.Height,
                Maximized = WindowState == FormWindowState.Maximized
            });
        }
        catch { }
    }
}
