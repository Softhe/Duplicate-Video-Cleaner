namespace DuplicateVideoCleaner;

internal sealed class DeletionConfirmationDialog : Form
{
    internal DeletionConfirmationDialog(IReadOnlyList<VideoCandidate> files, bool permanent, string formattedSize)
    {
        Text = "Confirm deletion";
        Program.ApplyIcon(this);
        StartPosition = FormStartPosition.CenterParent;
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MinimizeBox = false;
        MaximizeBox = false;
        ShowInTaskbar = false;
        ClientSize = new Size(640, 460);
        BackColor = AppTheme.WindowBack;
        ForeColor = AppTheme.Foreground;
        Font = AppTheme.UiFont;

        var root = new TableLayoutPanel { Dock = DockStyle.Fill, RowCount = 3, ColumnCount = 1 };
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 112));
        root.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 76));

        var header = new Panel { Dock = DockStyle.Fill, Padding = new Padding(24, 18, 24, 14), BackColor = permanent ? AppTheme.DangerMuted : AppTheme.AccentMuted };
        var mode = new Label
        {
            Text = permanent ? "PERMANENT REMOVAL" : "RECYCLE BIN",
            Dock = DockStyle.Top,
            Height = 23,
            Font = new Font(AppTheme.UiFont, FontStyle.Bold),
            ForeColor = permanent ? AppTheme.DangerText : Color.FromArgb(151, 202, 255),
            AccessibleName = permanent ? "Mode: Permanent deletion" : "Mode: Recycle Bin"
        };
        var title = new Label
        {
            Text = permanent ? "Permanently delete selected files?" : "Move selected files to the Recycle Bin?",
            Dock = DockStyle.Fill,
            Font = new Font(AppTheme.UiFont.FontFamily, 16, FontStyle.Bold),
            TextAlign = ContentAlignment.MiddleLeft
        };
        header.Controls.Add(title);
        header.Controls.Add(mode);

        var body = new TableLayoutPanel { Dock = DockStyle.Fill, Padding = new Padding(24, 20, 24, 14), RowCount = 3, ColumnCount = 1 };
        body.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        body.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        body.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        var summaryRow = new FlowLayoutPanel { AutoSize = true, WrapContents = false };
        summaryRow.Controls.Add(SummaryChip($"{files.Count} file(s)", $"Selected files: {files.Count}"));
        summaryRow.Controls.Add(SummaryChip(formattedSize, $"Selected size: {formattedSize}"));
        var consequence = new Label
        {
            Text = permanent
                ? "This action cannot be undone. The files will not go to the Recycle Bin."
                : "The files can be restored later from the Recycle Bin.",
            AutoSize = true,
            ForeColor = permanent ? AppTheme.DangerText : AppTheme.MutedForeground,
            Margin = new Padding(0, 12, 0, 12)
        };
        var previewCard = new Panel { Dock = DockStyle.Fill, Padding = new Padding(14), BackColor = AppTheme.CardBack, AccessibleName = "Selected file preview" };
        var preview = new ListBox { Dock = DockStyle.Fill, IntegralHeight = false, TabStop = false, BorderStyle = BorderStyle.None, AccessibleName = "Files selected for deletion" };
        foreach (var file in files.Take(6)) preview.Items.Add(file.RelativePath);
        if (files.Count > 6) preview.Items.Add($"…and {files.Count - 6} more file(s)");
        previewCard.Controls.Add(preview);
        body.Controls.Add(summaryRow, 0, 0);
        body.Controls.Add(consequence, 0, 1);
        body.Controls.Add(previewCard, 0, 2);

        var footer = new TableLayoutPanel { Dock = DockStyle.Fill, Padding = new Padding(24, 14, 24, 14), ColumnCount = 2, BackColor = AppTheme.SidebarBack };
        footer.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        footer.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        var reminder = new Label { Text = "Cancel is the safe default", AutoSize = true, ForeColor = AppTheme.MutedForeground, Margin = new Padding(0, 9, 0, 0) };
        var actions = new FlowLayoutPanel { AutoSize = true, WrapContents = false, FlowDirection = FlowDirection.RightToLeft, BackColor = AppTheme.SidebarBack };
        var confirm = new Button
        {
            Text = permanent ? "Delete permanently" : "Move to Recycle Bin",
            AutoSize = true,
            DialogResult = DialogResult.OK,
            AccessibleDescription = permanent ? "Permanently deletes the selected files" : "Moves the selected files to the Recycle Bin"
        };
        var cancel = new Button { Text = "Cancel", AutoSize = true, DialogResult = DialogResult.Cancel };
        actions.Controls.Add(confirm);
        actions.Controls.Add(cancel);
        footer.Controls.Add(reminder, 0, 0);
        footer.Controls.Add(actions, 1, 0);

        root.Controls.Add(header, 0, 0);
        root.Controls.Add(body, 0, 1);
        root.Controls.Add(footer, 0, 2);
        Controls.Add(root);
        AppTheme.Apply(root);
        header.BackColor = permanent ? AppTheme.DangerMuted : AppTheme.AccentMuted;
        footer.BackColor = AppTheme.SidebarBack;
        actions.BackColor = AppTheme.SidebarBack;
        previewCard.BackColor = AppTheme.CardBack;
        AppTheme.Primary(confirm, permanent);
        CancelButton = cancel;
        AcceptButton = cancel;
        Shown += (_, _) => { AppTheme.EnableDarkTitleBar(this); cancel.Focus(); };
    }

    private static Label SummaryChip(string text, string accessibleName) => new()
    {
        Text = text,
        AccessibleName = accessibleName,
        AutoSize = true,
        Padding = new Padding(10, 6, 10, 6),
        Margin = new Padding(0, 0, 8, 0),
        BackColor = AppTheme.CardBack,
        Font = new Font(AppTheme.UiFont, FontStyle.Bold)
    };
}
