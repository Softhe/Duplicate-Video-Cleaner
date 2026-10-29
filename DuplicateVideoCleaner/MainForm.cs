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
    private readonly HashSet<string> _selectedPaths = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, ReviewedVideo> _reviewed = new(StringComparer.OrdinalIgnoreCase);
    private readonly SettingsService _settingsService = new();
    private readonly MetadataProbe _metadataProbe = new();
    private readonly SettingsSaveQueue _settingsQueue;
    private bool _settingsFlushed;
    private bool _savingBeforeClose;
    private ReviewSession? _session;
    private ReviewSession Session => _session is not null && ReferenceEquals(_session.Groups, _groups) ? _session : _session = new ReviewSession(_groups, _selectedPaths, _scanContext, _reviewed);
    private Control _scanPage = null!;
    private Control _reviewPage = null!;
    private Control _activityPage = null!;
    private IReadOnlyList<DuplicateGroup> _groups = [];
    private WorkspacePage _page = WorkspacePage.Scan;
    private bool _busy;
    private ScanContext? _scanContext;
    private CancellationTokenSource? _scanCancellation;
    private CancellationTokenSource? _exportCancellation;
    private readonly Button _cancelExportButton = new() { Text = "Cancel export", AutoSize = true, Visible = false, AccessibleName = "Cancel export" };
    private bool _closeWhenIdle;

    public MainForm() : this(null) { }

    internal MainForm(Action<AppSettings>? settingsWriter)
    {
        _settingsQueue = new SettingsSaveQueue(settingsWriter ?? _settingsService.Save);
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
            var root = _scanContext?.Root ?? _folderText.Text.Trim();
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
        ruleRow.Controls.Add(RuleCard("SCAN SCOPE", "Folder traversal", _subfolders), 2, 0);
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

        _groups = [];
        _reviewPageIndex = 0;
        _scanContext = null;
        _reviewed.Clear();
        _selectedPaths.Clear();
        ClearCards();
        SaveSettingsSafe();
        Navigate(WorkspacePage.Scan);
        using var cancellation = new CancellationTokenSource();
        _scanCancellation = cancellation;
        SetBusy(true, "Scanning video files…");
        try
        {
            var threshold = (int)_threshold.Value;
            var keep = _keepMode.SelectedIndex == 0 ? KeepMode.Oldest : KeepMode.Newest;
            var context = new ScanContext(Path.GetFullPath(root), threshold, keep, _subfolders.Checked);
            _scanContext = context;
            var scanClock = Stopwatch.StartNew();
            var scan = await Task.Run(() => DuplicateScanner.ScanDetailed(context.Root, threshold, context.IncludeSubfolders, keep, cancellation.Token));
            _groups = scan.Groups;
            var enumerationTime = scanClock.Elapsed;
            cancellation.Token.ThrowIfCancellationRequested();
            SetScanStatus("Saving scan diagnostics…");
            var messages = scan.Diagnostics.Select(diagnostic => $"SCAN SKIPPED | {diagnostic.Path} | {diagnostic.Message}")
                .Prepend($"SCAN | Folder={context.Root} | Threshold={threshold}s | Keep={keep} | Subfolders={context.IncludeSubfolders} | Groups={_groups.Count} | Files={_groups.Sum(g => g.Files.Count)}");
            var loggingError = await Task.Run(() => (diagnosticWriter ?? AppendScanDiagnostics)(context.LogPath, messages, cancellation.Token));
            if (loggingError is not null) AddActivity("Scan logging failed", loggingError, false);
            foreach (var diagnostic in scan.Diagnostics.Take(199))
                AddActivity("Scan entry skipped", $"{diagnostic.Path}: {diagnostic.Message}", false);
            if (scan.Diagnostics.Count > 199)
                AddActivity("Additional scan entries skipped", $"{scan.Diagnostics.Count - 199} additional entries were skipped; see the cleanup log for all diagnostics.", false);
            var skipped = scan.Diagnostics.Count == 0 ? string.Empty : $" {scan.Diagnostics.Count} entries were skipped; see Activity.";
            if (loggingError is not null) skipped += " Scan diagnostics could not be fully saved; see Activity.";
            if (_groups.Count == 0)
            {
                SetScanStatus($"No potential duplicate groups found within {threshold} seconds.{skipped}");
                AddActivity("Scan complete", "No duplicate groups were found." + skipped, scan.Diagnostics.Count == 0 && loggingError is null);
                return;
            }

            SetScanStatus("Copying media to an isolated workspace and reading metadata. Temporary disk space is required…");
            if (_metadataProbe.IsAvailable)
            {
                var progress = new LatestMetadataProgress();
                using var progressTimer = new System.Windows.Forms.Timer { Interval = 150 };
                progressTimer.Tick += (_, _) =>
                {
                    if (progress.Latest is { } update && !cancellation.IsCancellationRequested)
                        SetScanStatus($"{update.Phase}: {update.CompletedFiles}/{update.TotalFiles} files · {FormatSize(update.BytesCopied)} / {FormatSize(update.TotalBytes)} copied · {update.Elapsed.TotalSeconds:0.0}s");
                };
                progressTimer.Start();
                var candidates = _groups.SelectMany(group => group.Files).ToArray();
                var metadata = await _metadataProbe.ProbeManyAsync(candidates, cancellation.Token, progress);
                progressTimer.Stop();
                if (_metadataProbe.LastBatchMetrics is { } metrics)
                    AddActivity("Scan performance", $"Enumeration {enumerationTime.TotalSeconds:0.00}s; setup {metrics.Setup.TotalSeconds:0.00}s; copying {metrics.Staging.TotalSeconds:0.00}s; parsing {metrics.Parsing.TotalSeconds:0.00}s; metadata elapsed {metrics.Elapsed.TotalSeconds:0.00}s; {metrics.CacheHits}/{metrics.Files} cached; {FormatSize(metrics.StagedBytes)} staged. Copying and parsing times sum worker durations.", true);
                foreach (var group in _groups)
                {
                    var referenceMetadata = metadata[group.SuggestedKeep.FullPath];
                    foreach (var file in group.Files)
                    {
                        var fileMetadata = metadata[file.FullPath];
                        var confidence = ConfidenceEvaluator.Evaluate(file, fileMetadata, group.SuggestedKeep, referenceMetadata);
                        _reviewed[file.FullPath] = new ReviewedVideo(file, fileMetadata, confidence, ReferenceEquals(file, group.SuggestedKeep));
                    }
                }
            }
            else
            {
                foreach (var group in _groups)
                    foreach (var file in group.Files)
                        _reviewed[file.FullPath] = new ReviewedVideo(file, new MediaMetadata(null, null, null, MetadataProbeStatus.Unavailable, "ffprobe was not found"), DuplicateConfidence.Low, ReferenceEquals(file, group.SuggestedKeep));
            }

            foreach (var group in _groups) RenamedFilePolicy.Apply(group, _reviewed);
            var metadataWarnings = _reviewed.Values.Where(video => !string.IsNullOrWhiteSpace(video.Metadata.Error) || video.Metadata.Status is MetadataProbeStatus.Failed or MetadataProbeStatus.TimedOut).ToArray();
            foreach (var warning in metadataWarnings.Take(20))
                AddActivity("Metadata needs attention", $"{warning.Candidate.RelativePath}: {warning.Metadata.Error ?? warning.Metadata.Status.ToString()}", false);
            if (metadataWarnings.Length > 20)
                AddActivity("Additional metadata warnings", $"{metadataWarnings.Length - 20} additional files need attention. Metadata confidence is shown in Review.", false);
            if (metadataWarnings.Length > 0) skipped += $" Metadata warnings affect {metadataWarnings.Length} file(s); see Activity and Review.";

            foreach (var group in _groups)
                foreach (var file in group.Files.Where(file => _reviewed[file.FullPath].SuggestRemoval)) _selectedPaths.Add(file.FullPath);
            BuildCards();
            cancellation.Token.ThrowIfCancellationRequested();
            _operationStatus.Text = context.Description + (_metadataProbe.IsAvailable
                ? string.Empty : " · Metadata unavailable: ffprobe was not found. Time-based review remains available.") + skipped;
            _operationStatus.AccessibleName = _operationStatus.Text;
            AddActivity("Scan complete", $"Found {_groups.Count} duplicate group(s) containing {_groups.Sum(group => group.Files.Count)} files.{skipped}", scan.Diagnostics.Count == 0 && loggingError is null && metadataWarnings.Length == 0);
            Navigate(WorkspacePage.Review);
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
        {
            _groups = [];
            _scanContext = null;
            _reviewed.Clear();
            _selectedPaths.Clear();
            ClearCards();
            Navigate(WorkspacePage.Scan);
            SetScanStatus("Scan cancelled. No video files were changed.");
            AddActivity("Scan cancelled", "Partial scan results were discarded.", false);
        }
        catch (Exception ex)
        {
            _groups = [];
            _scanContext = null;
            _reviewed.Clear();
            _selectedPaths.Clear();
            ClearCards();
            Navigate(WorkspacePage.Scan);
            SetScanStatus($"Scan failed: {ex.Message}");
            AddActivity("Scan failed", ex.Message, false);
        }
        finally { _scanCancellation = null; SetBusy(false); }
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
            card = new DuplicateGroupCard(group, _reviewed, path => _selectedPaths.Contains(path), SelectionChanged,
                suggested =>
                {
                    foreach (var file in group.Files)
                    {
                        Session.SetSelected(file, suggested && _reviewed[file.FullPath].SuggestRemoval);
                    }
                    card!.RefreshSelectionState();
                    UpdateSelectionStatus();
                }, () => Session.GroupSelection(group.Number));
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
        Session.SetSelected(file, selected);
        foreach (var card in _cards) card.RefreshSelectionState();
        UpdateSelectionStatus();
    }

    private List<VideoCandidate> SelectedFiles() => Session.SelectedFiles();

    private void SetSuggestedSelection()
    {
        foreach (var group in _groups)
            foreach (var file in group.Files) Session.SetSelected(file, _reviewed[file.FullPath].SuggestRemoval);
        RefreshCardsAndSummary();
    }

    private void SetAllSelections(bool selected)
    {
        foreach (var group in _groups)
            foreach (var file in group.Files) Session.SetSelected(file, selected);
        RefreshCardsAndSummary();
    }

    private void RefreshCardsAndSummary()
    {
        foreach (var card in _cards) card.RefreshSelectionState();
        UpdateSelectionStatus();
    }

    private async Task DeleteSelectedAsync()
    {
        if (_busy || _scanContext is null) return;
        var selected = SelectedFiles();
        if (selected.Count == 0) return;
        var permanent = _deletionMode.SelectedIndex == 1;
        var groupsWithoutSurvivors = _groups.Count(group => group.Files.All(file => _selectedPaths.Contains(file.FullPath)));
        using var confirmation = new DeletionConfirmationDialog(selected, permanent, FormatSize(selected.Sum(file => file.Size)), groupsWithoutSurvivors);
        if (confirmation.ShowDialog(this) != DialogResult.OK) return;
        if (!CanWriteLog(out var logError))
        {
            MessageBox.Show(this, $"Deletion was not started because the log cannot be written:\n\n{logError}", "Log unavailable", MessageBoxButtons.OK, MessageBoxIcon.Error);
            AddActivity("Removal blocked", "The cleanup log could not be written.", false);
            return;
        }

        SetBusy(true, "Removing selected files…");
        try
        {
            var result = await FileRemovalService.RemoveReviewedAsync(selected.Select(file => _reviewed[file.FullPath]).ToArray(), permanent, GetLogPath());
            ReconcileRemoval(result);
            _operationStatus.Text = $"Removed {result.RemovedPaths.Count} file(s); {result.Failures.Count} failed.";
            var successful = result.Failures.Count == 0 && result.AuditError is null;
            AddActivity(successful ? "Removal complete" : "Removal partially complete", $"Removed {result.RemovedPaths.Count}; failed {result.Failures.Count}. {result.AuditError}", successful);
            if (!successful)
            {
                var details = string.Join(Environment.NewLine, result.Failures.Take(15).Select(failure => $"{failure.File.RelativePath}: {failure.Error}"));
                MessageBox.Show(this, $"Removed {result.RemovedPaths.Count} file(s); {result.Failures.Count} failed or skipped.\n{result.AuditError}\n{details}", "Removal needs attention", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            }
            else MessageBox.Show(this, $"Successfully removed {result.RemovedPaths.Count} file(s).", "Removal complete", MessageBoxButtons.OK, MessageBoxIcon.Information);
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
        var session = Session.Reconcile(result);
        _groups = session.Groups;
        _reviewed.Clear();
        foreach (var (path, video) in session.Reviewed) _reviewed.Add(path, video);
        _session = session;
        BuildCards();
    }

    private void ClearResults()
    {
        _groups = [];
        _scanContext = null;
        _reviewed.Clear();
        _selectedPaths.Clear();
        ClearCards();
        SetScanStatus("Results cleared. Choose a folder or adjust the rules for another scan.");
        AddActivity("Results cleared", "The in-memory review was cleared; no video files were changed.", true);
        Navigate(WorkspacePage.Scan);
        UpdateSelectionStatus();
    }

    private void UpdateSelectionStatus()
    {
        var selected = Session;
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
        foreach (var control in new Control[] { _scanButton, _browseButton, _folderText, _threshold, _keepMode, _subfolders, _rescanButton, _exportButton, _clearButton, _deletionMode, _selectSuggestedButton, _selectAllButton, _selectNoneButton, _cardHost, _scanNav, _reviewNav, _activityNav }) control.Enabled = !busy;
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
        if (_busy || _groups.Count == 0 || _scanContext is null) return;
        using var dialog = new FolderBrowserDialog { Description = "Choose a folder for the CSV and text dry-run reports", ShowNewFolderButton = true };
        if (Directory.Exists(_scanContext.Root)) dialog.InitialDirectory = _scanContext.Root;
        if (dialog.ShowDialog(this) != DialogResult.OK) return;
        await ExportDryRunToAsync(dialog.SelectedPath);
    }

    internal async Task ExportDryRunToAsync(string destination,
        Func<string, DryRunContext, CancellationToken, ExportResult>? export = null)
    {
        if (_busy || _groups.Count == 0 || _scanContext is null) return;
        var context = BuildDryRunContext();
        using var cancellation = new CancellationTokenSource();
        _exportCancellation = cancellation;
        SetBusy(true, "Exporting dry run…");
        try
        {
            var result = await Task.Run(() => export is null
                ? DryRunExporter.Export(destination, context, cancellationToken: cancellation.Token)
                : export(destination, context, cancellation.Token));
            _operationStatus.Text = result.Errors.Count == 0
                ? $"Dry run exported: {Path.GetFileName(result.CsvPath)} and {Path.GetFileName(result.TextPath)}"
                : $"Dry run partially exported. {string.Join("; ", result.Errors)}";
            if (cancellation.IsCancellationRequested)
                _operationStatus.Text = $"Export cancelled. Completed reports: {(result.CsvPath is null ? 0 : 1) + (result.TextPath is null ? 0 : 1)}. Completed reports were retained.";
            _operationStatus.AccessibleName = _operationStatus.Text;
            AddActivity(cancellation.IsCancellationRequested ? "Export cancelled" : result.Errors.Count == 0 ? "Dry run exported" : "Dry run partially exported", result.Errors.Count == 0 ? destination : string.Join("; ", result.Errors), result.Errors.Count == 0);
        }
        catch (Exception ex) { _operationStatus.Text = $"Export failed: {ex.Message}"; AddActivity("Export failed", ex.Message, false); }
        finally { _exportCancellation = null; SetBusy(false); }
    }

    private void AddActivity(string title, string detail, bool success)
    {
        if (_activityHost.Controls.Count >= 200) _activityHost.Controls[^1].Dispose();
        var item = new ActivityItem(DateTime.Now, title, detail, success);
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
    private bool CanWriteLog(out string error) { try { AppendLog(GetLogPath(), "DELETE SESSION START"); error = string.Empty; return true; } catch (Exception ex) { error = ex.Message; return false; } }
    internal static string? AppendScanDiagnostics(string logPath, IEnumerable<string> messages, CancellationToken cancellationToken)
    {
        try
        {
            using var directories = FileSnapshot.PinDirectories(Path.GetDirectoryName(Path.GetFullPath(logPath))!);
            using var stream = FileSnapshot.OpenAppendLog(logPath);
            using var writer = new StreamWriter(stream, new UTF8Encoding(false), leaveOpen: true);
            var count = 0;
            foreach (var message in messages)
            {
                cancellationToken.ThrowIfCancellationRequested();
                writer.WriteLine($"{DateTime.Now:yyyy-MM-dd HH:mm:ss} | {message.Replace("\r", "\\r").Replace("\n", "\\n")}");
                if (++count % 64 == 0) writer.Flush();
            }
            writer.Flush();
            stream.Flush(true);
            return null;
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex) { return ex.Message; }
    }
    internal string GetLogPath() => _scanContext?.LogPath ?? Path.Combine(_folderText.Text.Trim(), "DuplicateCleanupLog.txt");

    internal DryRunContext BuildDryRunContext()
    {
        return Session.CreateDryRun();
    }
    private static void AppendLog(string logPath, string message)
    {
        using var directories = FileSnapshot.PinDirectories(Path.GetDirectoryName(Path.GetFullPath(logPath))!);
        using var stream = FileSnapshot.OpenAppendLog(logPath);
        using var writer = new StreamWriter(stream, new UTF8Encoding(false), leaveOpen: true);
        writer.WriteLine($"{DateTime.Now:yyyy-MM-dd HH:mm:ss} | {message.Replace("\r", "\\r").Replace("\n", "\\n")}");
        writer.Flush();
        stream.Flush(true);
    }

    internal static string FormatSize(long bytes)
    {
        string[] units = ["B", "KB", "MB", "GB", "TB"];
        double value = bytes;
        var unit = 0;
        while (value >= 1024 && unit < units.Length - 1) { value /= 1024; unit++; }
        return $"{value:0.##} {units[unit]}";
    }

    private void RestoreSettings(AppSettings settings)
    {
        _folderText.Text = settings.LastFolder;
        _threshold.Value = settings.ThresholdSeconds;
        _keepMode.SelectedIndex = settings.KeepMode == KeepMode.Newest ? 1 : 0;
        _subfolders.Checked = settings.IncludeSubfolders;
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
                IncludeSubfolders = _subfolders.Checked, PermanentDeletion = _deletionMode.SelectedIndex == 1,
                WindowX = bounds.X, WindowY = bounds.Y, WindowWidth = bounds.Width, WindowHeight = bounds.Height,
                Maximized = WindowState == FormWindowState.Maximized
            });
        }
        catch { }
    }
}
