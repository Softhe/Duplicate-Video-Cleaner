using System.Globalization;
using System.ComponentModel;

namespace DuplicateVideoCleaner;

internal sealed class DarkComboBox : ComboBox
{
    internal DarkComboBox()
    {
        DrawMode = DrawMode.OwnerDrawFixed;
        FlatStyle = FlatStyle.Flat;
        ItemHeight = 24;
        BackColor = AppTheme.InputBack;
        ForeColor = AppTheme.Foreground;
    }

    protected override void OnDrawItem(DrawItemEventArgs e)
    {
        var highlighted = (e.State & DrawItemState.Selected) != 0 && (e.State & DrawItemState.ComboBoxEdit) == 0;
        using var background = new SolidBrush(highlighted ? AppTheme.AccentMuted : AppTheme.InputBack);
        e.Graphics.FillRectangle(background, e.Bounds);
        var text = e.Index >= 0 && e.Index < Items.Count ? GetItemText(Items[e.Index]) : Text;
        var bounds = new Rectangle(e.Bounds.X + 8, e.Bounds.Y, Math.Max(0, e.Bounds.Width - 12), e.Bounds.Height);
        TextRenderer.DrawText(e.Graphics, text, Font, bounds, Enabled ? AppTheme.Foreground : AppTheme.MutedForeground,
            TextFormatFlags.VerticalCenter | TextFormatFlags.Left | TextFormatFlags.EndEllipsis);
        if ((e.State & DrawItemState.Focus) != 0) e.DrawFocusRectangle();
    }
}

internal sealed class DoubleBufferedFlowPanel : FlowLayoutPanel
{
    internal DoubleBufferedFlowPanel()
    {
        DoubleBuffered = true;
        ResizeRedraw = true;
    }
}

internal sealed class DoubleBufferedPanel : Panel
{
    internal DoubleBufferedPanel()
    {
        DoubleBuffered = true;
        ResizeRedraw = true;
    }
}

internal sealed class SidebarButton : Button
{
    private bool _selected;
    private readonly Font _normalFont = new(AppTheme.UiFont, FontStyle.Regular);
    private readonly Font _selectedFont = new(AppTheme.UiFont, FontStyle.Bold);

    internal SidebarButton(string text, string accessibleName)
    {
        Text = text;
        AccessibleName = accessibleName;
        Height = 46;
        Dock = DockStyle.Top;
        TextAlign = ContentAlignment.MiddleLeft;
        Padding = new Padding(18, 0, 0, 0);
        FlatStyle = FlatStyle.Flat;
        FlatAppearance.BorderSize = 0;
        Cursor = Cursors.Hand;
        TabStop = true;
        UpdateColors();
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);
        if (_selected)
        {
            using var accent = new SolidBrush(AppTheme.Accent);
            e.Graphics.FillRectangle(accent, 0, 8, 3, Height - 16);
        }
    }

    [Browsable(false), DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    internal bool Selected
    {
        get => _selected;
        set { if (_selected == value) return; _selected = value; UpdateColors(); }
    }

    protected override void OnGotFocus(EventArgs e) { base.OnGotFocus(e); FlatAppearance.BorderSize = 1; FlatAppearance.BorderColor = AppTheme.Accent; }
    protected override void OnLostFocus(EventArgs e) { base.OnLostFocus(e); FlatAppearance.BorderSize = 0; }

    private void UpdateColors()
    {
        BackColor = _selected ? AppTheme.AccentMuted : AppTheme.SidebarBack;
        ForeColor = _selected ? AppTheme.Accent : AppTheme.MutedForeground;
        Font = _selected ? _selectedFont : _normalFont;
        FlatAppearance.MouseOverBackColor = AppTheme.SurfaceBack;
        FlatAppearance.BorderSize = Focused ? 1 : 0;
    }

    protected override void Dispose(bool disposing)
    {
        base.Dispose(disposing);
        if (disposing) { _normalFont.Dispose(); _selectedFont.Dispose(); }
    }
}

internal sealed class ConfidenceBadge : Label
{
    internal ConfidenceBadge(DuplicateConfidence confidence)
    {
        AutoSize = true;
        Padding = new Padding(8, 4, 8, 4);
        Margin = new Padding(6, 0, 0, 0);
        Font = new Font(AppTheme.UiFont, FontStyle.Bold);
        Text = $"{confidence} confidence";
        AccessibleName = $"Confidence: {confidence}";
        ForeColor = confidence switch
        {
            DuplicateConfidence.High => AppTheme.SuccessText,
            DuplicateConfidence.Medium => AppTheme.WarningText,
            _ => AppTheme.DangerText
        };
        BackColor = confidence switch
        {
            DuplicateConfidence.High => AppTheme.SuccessMuted,
            DuplicateConfidence.Medium => AppTheme.WarningMuted,
            _ => AppTheme.DangerMuted
        };
    }
}

internal sealed class ReviewedFileRow : Panel
{
    private readonly CheckBox _selection;
    private readonly bool _suggestedKeep;
    private bool _suppress;
    private readonly Func<bool, string> _accessibleName;

    internal ReviewedFileRow(int groupNumber, ReviewedVideo reviewed, VideoCandidate reference, bool selected, Action<VideoCandidate, bool> selectionChanged)
    {
        var file = reviewed.Candidate;
        _accessibleName = selectedState => BuildAccessibleName(groupNumber, reviewed, reference, selectedState);
        _suggestedKeep = reviewed.SuggestedKeep;
        AutoSize = true;
        Dock = DockStyle.Top;
        Padding = new Padding(12, 10, 12, 10);
        Margin = new Padding(0, 1, 0, 0);
        BackColor = selected ? AppTheme.RemoveMuted : reviewed.SuggestedKeep ? AppTheme.KeepMuted : AppTheme.CardInsetBack;
        AccessibleName = BuildAccessibleName(groupNumber, reviewed, reference, selected);

        var layout = new TableLayoutPanel { AutoSize = true, Dock = DockStyle.Top, ColumnCount = 3, RowCount = 2, BackColor = BackColor };
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));

        _selection = new CheckBox
        {
            Checked = selected,
            AutoSize = true,
            Margin = new Padding(0, 5, 12, 0),
            AccessibleName = $"Group {groupNumber}, select {file.RelativePath} for removal",
            AccessibleDescription = reviewed.SuggestedKeep ? "Suggested keep file; select only to override the suggestion" : "Suggested duplicate file"
        };
        _selection.CheckedChanged += (_, _) =>
        {
            if (_suppress) return;
            selectionChanged(file, _selection.Checked);
            SetSelected(_selection.Checked);
        };

        var name = new Label
        {
            Text = file.RelativePath,
            AutoEllipsis = true,
            Dock = DockStyle.Fill,
            Height = 24,
            Font = new Font(AppTheme.UiFont, FontStyle.Bold),
            AccessibleName = $"File: {file.RelativePath}"
        };
        var state = new Label
        {
            Text = reviewed.SuggestedKeep ? "KEEP" : "REMOVE",
            AutoSize = true,
            Padding = new Padding(7, 3, 7, 3),
            ForeColor = reviewed.SuggestedKeep ? AppTheme.SuccessText : AppTheme.DangerText,
            BackColor = reviewed.SuggestedKeep ? AppTheme.SuccessMuted : AppTheme.DangerMuted,
            AccessibleName = reviewed.SuggestedKeep ? "Suggested action: Keep" : "Suggested action: Remove"
        };
        var metadata = new Label
        {
            AutoSize = true,
            MaximumSize = new Size(1000, 0),
            Text = $"{file.Timestamp:yyyy-MM-dd HH:mm:ss} · {(file.TimestampSource == TimestampSource.FileName ? "File name" : "Creation time")}   |   {reviewed.Metadata.DurationText}   |   {reviewed.Metadata.ResolutionText}   |   {MainForm.FormatSize(file.Size)}   |   {(file.Size == reference.Size ? "Exact size" : "Different size")}",
            ForeColor = AppTheme.MutedForeground,
            AccessibleName = $"Effective timestamp: {file.Timestamp:yyyy-MM-dd HH:mm:ss}; Timestamp source: {file.TimestampSource}; Duration: {reviewed.Metadata.DurationText}; Resolution: {reviewed.Metadata.ResolutionText}; Size: {file.Size.ToString(CultureInfo.InvariantCulture)} bytes; Size comparison: {(file.Size == reference.Size ? "Exact match" : "Different")}; Confidence: {reviewed.Confidence}"
        };
        layout.SizeChanged += (_, _) => metadata.MaximumSize =
            new Size(Math.Max(1, layout.ClientSize.Width - _selection.Width - _selection.Margin.Horizontal - metadata.Margin.Horizontal), 0);
        var badge = new ConfidenceBadge(reviewed.Confidence);

        var badges = new FlowLayoutPanel { AutoSize = true, WrapContents = false, BackColor = BackColor, Margin = new Padding(0) };
        badges.Controls.Add(state);
        badges.Controls.Add(badge);
        layout.Controls.Add(_selection, 0, 0);
        layout.SetRowSpan(_selection, 2);
        layout.Controls.Add(name, 1, 0);
        layout.Controls.Add(badges, 2, 0);
        layout.Controls.Add(metadata, 1, 1);
        layout.SetColumnSpan(metadata, 2);
        Controls.Add(layout);
    }

    internal void SetSelected(bool selected)
    {
        _suppress = true;
        _selection.Checked = selected;
        _suppress = false;
        AccessibleName = _accessibleName(selected);
        BackColor = selected ? AppTheme.RemoveMuted : _suggestedKeep ? AppTheme.KeepMuted : AppTheme.CardInsetBack;
        if (Controls.Count > 0) Controls[0].BackColor = BackColor;
    }

    private static string BuildAccessibleName(int group, ReviewedVideo reviewed, VideoCandidate reference, bool selected) =>
        $"Group {group}; File {reviewed.Candidate.RelativePath}; {(selected ? "Selected for removal" : "Not selected")}; {(reviewed.SuggestedKeep ? "Suggested keep" : "Suggested removal")}; Timestamp {reviewed.Candidate.Timestamp:yyyy-MM-dd HH:mm:ss}; Source {reviewed.Candidate.TimestampSource}; Duration {reviewed.Metadata.DurationText}; Resolution {reviewed.Metadata.ResolutionText}; Size {reviewed.Candidate.Size} bytes; Size comparison {(reviewed.Candidate.Size == reference.Size ? "Exact match" : "Different")}; Confidence {reviewed.Confidence}";
}

internal sealed class DuplicateGroupCard : Panel
{
    private const int SummaryHeight = 116;
    private readonly DuplicateGroup _group;
    private readonly IReadOnlyDictionary<string, ReviewedVideo> _reviewed;
    private readonly Func<string, bool> _isSelected;
    private readonly Func<(int Count, long Bytes)>? _selectionTotals;
    private readonly Action<VideoCandidate, bool> _selectionChanged;
    private readonly Panel _detailsHost;
    private readonly Panel _detailRowsHost = new() { Dock = DockStyle.Fill };
    private readonly FlowLayoutPanel _detailPager = new() { Dock = DockStyle.Bottom, Height = 36, WrapContents = false };
    private readonly Button _previousDetailPage;
    private readonly Button _nextDetailPage;
    private readonly Label _detailPageRange = new() { AutoSize = true, Margin = new Padding(8) };
    private const int DetailPageSize = 25;
    private int _detailPageIndex;
    private readonly Button _expandButton;
    private readonly Label _selectionLabel;
    private readonly Dictionary<string, ReviewedFileRow> _rows = new(StringComparer.OrdinalIgnoreCase);
    private bool _built;
    private bool _expanded;

    internal DuplicateGroupCard(DuplicateGroup group, IReadOnlyDictionary<string, ReviewedVideo> reviewed,
        Func<string, bool> isSelected, Action<VideoCandidate, bool> selectionChanged, Action<bool> setGroupSelection, Func<(int Count, long Bytes)>? selectionTotals = null)
    {
        _group = group;
        _reviewed = reviewed;
        _isSelected = isSelected;
        _selectionTotals = selectionTotals;
        _selectionChanged = selectionChanged;
        _previousDetailPage = SmallButton("Previous files", $"Previous files in group {group.Number}");
        _nextDetailPage = SmallButton("Next files", $"Next files in group {group.Number}");
        _previousDetailPage.Click += (_, _) => ChangeDetailPage(-1);
        _nextDetailPage.Click += (_, _) => ChangeDetailPage(1);
        _detailPager.Controls.AddRange([_previousDetailPage, _detailPageRange, _nextDetailPage]);
        Dock = DockStyle.Top;
        AutoSize = false;
        Height = SummaryHeight;
        Margin = Padding.Empty;
        Padding = new Padding(4, 0, 0, 6);
        BackColor = AppTheme.WindowBack;
        Font = AppTheme.UiFont;
        Paint += (_, e) =>
        {
            using var stripe = new SolidBrush(_group.Files.Any(file => _isSelected(file.FullPath)) ? AppTheme.Accent : AppTheme.Border);
            e.Graphics.FillRectangle(stripe, 0, 0, LogicalToDeviceUnits(3), Height - Padding.Bottom);
        };
        AccessibleName = GroupAccessibleName();

        var shell = new TableLayoutPanel { Dock = DockStyle.Fill, RowCount = 2, ColumnCount = 1, BackColor = AppTheme.CardBack };
        shell.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        shell.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        shell.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        var header = new TableLayoutPanel { Dock = DockStyle.Top, AutoSize = true, ColumnCount = 2, Padding = new Padding(12, 8, 12, 8), BackColor = AppTheme.CardBack };
        header.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        header.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));

        var titleStack = new TableLayoutPanel { AutoSize = true, Dock = DockStyle.Top, ColumnCount = 1, Margin = Padding.Empty, BackColor = AppTheme.CardBack };
        titleStack.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        var confidence = group.Files.Select(file => reviewed[file.FullPath].Confidence).Min();
        var span = group.Files[^1].Timestamp - group.Files[0].Timestamp;
        var candidates = group.Files.Where(file => !ReferenceEquals(file, group.SuggestedKeep)).ToList();
        var preview = string.Join("  ·  ", candidates.Take(3).Select(file => file.RelativePath));
        if (candidates.Count > 3) preview += $"  ·  +{candidates.Count - 3} more";
        var heading = new FlowLayoutPanel { AutoSize = true, WrapContents = false, BackColor = AppTheme.CardBack };
        heading.Controls.Add(new Label { Text = $"Group {group.Number}", AutoSize = true, Font = new Font(AppTheme.UiFont, FontStyle.Bold), AccessibleName = $"Duplicate group {group.Number}" });
        heading.Controls.Add(new ConfidenceBadge(confidence));
        titleStack.Controls.Add(heading);
        titleStack.Controls.Add(new Label { Text = $"Keep  {group.SuggestedKeep.RelativePath}", Dock = DockStyle.Top, AutoEllipsis = true, Height = 18, ForeColor = AppTheme.SuccessText, Margin = new Padding(0, 3, 0, 0), AccessibleName = $"Suggested keep: {group.SuggestedKeep.RelativePath}" });
        titleStack.Controls.Add(new Label { Text = $"Remove  {preview}", Dock = DockStyle.Top, AutoEllipsis = true, Height = 18, ForeColor = AppTheme.MutedForeground, Margin = new Padding(0, 2, 0, 0), AccessibleName = $"Suggested removals: {string.Join(", ", candidates.Select(file => file.RelativePath))}" });
        titleStack.Controls.Add(new Label { Text = $"{group.Files.Count} files · {FormatSpan(span)} apart · {MainForm.FormatSize(candidates.Sum(file => file.Size))} suggested removal size", Dock = DockStyle.Top, AutoEllipsis = true, Height = 18, ForeColor = AppTheme.MutedForeground, Margin = new Padding(0, 2, 0, 0), AccessibleName = $"{group.Files.Count} files; time span {FormatSpan(span)}; suggested removal size {MainForm.FormatSize(candidates.Sum(file => file.Size))}" });

        var groupActions = new FlowLayoutPanel { AutoSize = true, WrapContents = false, BackColor = AppTheme.CardBack, Margin = new Padding(8, 0, 8, 0) };
        var suggested = SmallButton("Suggested", $"Select suggested removals in group {group.Number}");
        var none = SmallButton("None", $"Clear selections in group {group.Number}");
        suggested.Click += (_, _) => setGroupSelection(true);
        none.Click += (_, _) => setGroupSelection(false);
        groupActions.Controls.AddRange([suggested, none]);

        _selectionLabel = new Label { AutoSize = true, Font = new Font(AppTheme.UiFont, FontStyle.Bold), ForeColor = AppTheme.Accent, TextAlign = ContentAlignment.MiddleRight, Margin = new Padding(8, 10, 10, 0) };
        _expandButton = SmallButton("Show details  ▾", $"Expand group {group.Number}");
        _expandButton.Click += (_, _) => ToggleExpanded();

        header.Controls.Add(titleStack, 0, 0);
        groupActions.Controls.Add(_expandButton);
        var actionStack = new TableLayoutPanel { AutoSize = true, ColumnCount = 1, Margin = Padding.Empty, BackColor = AppTheme.CardBack };
        actionStack.Controls.Add(groupActions, 0, 0);
        actionStack.Controls.Add(_selectionLabel, 0, 1);
        header.Controls.Add(actionStack, 1, 0);

        _detailsHost = new Panel { Dock = DockStyle.Fill, Visible = false, BackColor = AppTheme.CardInsetBack, Padding = new Padding(12, 0, 12, 12) };
        _detailsHost.Controls.Add(_detailRowsHost);
        _detailsHost.Controls.Add(_detailPager);
        shell.Controls.Add(header, 0, 0);
        shell.Controls.Add(_detailsHost, 0, 1);
        Controls.Add(shell);
        RefreshSelectionState();
    }

    internal void RefreshSelectionState()
    {
        var totals = SelectionTotals();
        var selected = totals.Count;
        _selectionLabel.Text = $"{selected} selected · {MainForm.FormatSize(totals.Bytes)}";
        _selectionLabel.AccessibleName = $"Group {_group.Number}: {selected} files selected";
        foreach (var (path, row) in _rows) row.SetSelected(_isSelected(path));
        AccessibleName = GroupAccessibleName();
        Invalidate();
    }

    private void ToggleExpanded()
    {
        if (!_built) BuildDetails();
        _expanded = !_expanded;
        _detailsHost.Visible = _expanded;
        UpdateExpandedHeight();
        _expandButton.Text = _expanded ? "Hide details  ▴" : "Show details  ▾";
        _expandButton.AccessibleName = $"{(_expanded ? "Collapse" : "Expand")} group {_group.Number}";
        Parent?.PerformLayout();
    }

    private void BuildDetails()
    {
        _detailRowsHost.SuspendLayout();
        foreach (Control control in _detailRowsHost.Controls.Cast<Control>().ToArray()) control.Dispose();
        _rows.Clear();
        var first = _detailPageIndex * DetailPageSize;
        for (var index = Math.Min(_group.Files.Count, first + DetailPageSize) - 1; index >= first; index--)
        {
            var file = _group.Files[index];
            var row = new ReviewedFileRow(_group.Number, _reviewed[file.FullPath], _group.SuggestedKeep, _isSelected(file.FullPath), _selectionChanged);
            row.SizeChanged += (_, _) => { if (_expanded) UpdateExpandedHeight(); };
            _rows[file.FullPath] = row;
            _detailRowsHost.Controls.Add(row);
        }
        _detailRowsHost.ResumeLayout();
        _detailPager.Visible = _group.Files.Count > DetailPageSize;
        _previousDetailPage.Enabled = first > 0;
        _nextDetailPage.Enabled = first + DetailPageSize < _group.Files.Count;
        _detailPageRange.Text = $"Files {first + 1}–{Math.Min(_group.Files.Count, first + DetailPageSize)} of {_group.Files.Count}";
        _built = true;
    }

    private void ChangeDetailPage(int delta)
    {
        var focused = _previousDetailPage.Focused ? _previousDetailPage : _nextDetailPage.Focused ? _nextDetailPage : null;
        _detailPageIndex += delta;
        BuildDetails();
        UpdateExpandedHeight();
        if (focused is not null)
        {
            var target = focused.Enabled ? focused : _previousDetailPage.Enabled ? _previousDetailPage : _nextDetailPage;
            target.Focus();
        }
    }

    private void UpdateExpandedHeight() => Height = LogicalToDeviceUnits(SummaryHeight) +
        (_expanded ? Math.Max(LogicalToDeviceUnits(84), _rows.Values.Sum(row => row.Height) + _detailsHost.Padding.Vertical +
            (_detailPager.Visible ? _detailPager.Height : 0)) : 0);

    private (int Count, long Bytes) SelectionTotals()
    {
        if (_selectionTotals is not null) return _selectionTotals();
        var count = 0; long bytes = 0;
        foreach (var file in _group.Files) if (_isSelected(file.FullPath)) { count++; bytes += file.Size; }
        return (count, bytes);
    }

    private string GroupAccessibleName() => $"Duplicate group {_group.Number}; {_group.Files.Count} files; {SelectionTotals().Count} selected; suggested keep {_group.SuggestedKeep.RelativePath}";
    private static Button SmallButton(string text, string accessibleName)
    {
        var button = new Button { Text = text, AccessibleName = accessibleName, AutoSize = true, Margin = new Padding(4, 0, 0, 0) };
        AppTheme.Apply(button);
        return button;
    }
    private static string FormatSpan(TimeSpan span) => span.TotalHours >= 1
        ? string.Create(CultureInfo.InvariantCulture, $"{(long)span.TotalHours}:{span.Minutes:00}:{span.Seconds:00}")
        : span.ToString(@"m\:ss", CultureInfo.InvariantCulture);
}

internal sealed class ActivityItem : Panel
{
    internal ActivityItem(DateTime occurred, string title, string detail, bool success)
    {
        Dock = DockStyle.Top;
        Height = 66;
        Padding = new Padding(14, 10, 14, 8);
        Margin = new Padding(0, 0, 0, 8);
        BackColor = AppTheme.CardBack;
        AccessibleName = $"Activity at {occurred:t}: {title}. {detail}";
        var titleLabel = new Label { Text = title, Dock = DockStyle.Top, Height = 23, Font = new Font(AppTheme.UiFont, FontStyle.Bold), ForeColor = success ? AppTheme.SuccessText : AppTheme.DangerText };
        var detailLabel = new Label { Text = $"{occurred:t}  ·  {detail}", Dock = DockStyle.Fill, AutoEllipsis = true, ForeColor = AppTheme.MutedForeground };
        Controls.Add(detailLabel);
        Controls.Add(titleLabel);
    }
}
