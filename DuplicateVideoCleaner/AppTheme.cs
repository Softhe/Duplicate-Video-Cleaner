using System.Runtime.InteropServices;

namespace DuplicateVideoCleaner;

internal static class AppTheme
{
    internal static Font UiFont { get; } = new("Segoe UI", 10F);
    internal static readonly Color WindowBack = Color.FromArgb(16, 20, 29);
    internal static readonly Color SidebarBack = Color.FromArgb(22, 28, 40);
    internal static readonly Color SurfaceBack = Color.FromArgb(34, 44, 61);
    internal static readonly Color CardBack = Color.FromArgb(28, 36, 50);
    internal static readonly Color CardInsetBack = Color.FromArgb(21, 28, 40);
    internal static readonly Color InputBack = Color.FromArgb(14, 21, 32);
    internal static readonly Color Foreground = Color.FromArgb(239, 244, 252);
    internal static readonly Color MutedForeground = Color.FromArgb(180, 195, 217);
    internal static readonly Color Border = Color.FromArgb(66, 83, 108);
    internal static readonly Color Accent = Color.FromArgb(102, 224, 205);
    internal static readonly Color AccentMuted = Color.FromArgb(29, 68, 69);
    internal static readonly Color OnAccent = Color.FromArgb(10, 36, 35);
    internal static readonly Color Danger = Color.FromArgb(164, 43, 64);
    internal static readonly Color SuccessText = Color.FromArgb(139, 235, 179);
    internal static readonly Color SuccessMuted = Color.FromArgb(30, 67, 51);
    internal static readonly Color WarningText = Color.FromArgb(255, 211, 128);
    internal static readonly Color WarningMuted = Color.FromArgb(71, 55, 30);
    internal static readonly Color DangerText = Color.FromArgb(255, 163, 177);
    internal static readonly Color DangerMuted = Color.FromArgb(70, 34, 47);
    internal static readonly Color KeepMuted = Color.FromArgb(23, 49, 44);
    internal static readonly Color RemoveMuted = Color.FromArgb(58, 32, 45);

    internal static void Apply(Control control)
    {
        if (control is not DataGridView)
        {
            control.BackColor = control switch
            {
                TextBox or ComboBox or NumericUpDown or ListBox => InputBack,
                Button => SurfaceBack,
                _ when Equals(control.Tag, "surface") => CardBack,
                _ when control.BackColor.IsSystemColor => control.Parent?.BackColor ?? WindowBack,
                _ => control.BackColor
            };
            if (Equals(control.Tag, "muted")) control.ForeColor = MutedForeground;
            else if (control.ForeColor.IsSystemColor) control.ForeColor = Foreground;
        }

        if (control is Button button)
        {
            button.FlatStyle = FlatStyle.Flat;
            button.UseVisualStyleBackColor = false;
            button.Cursor = Cursors.Hand;
            button.Padding = new Padding(10, 4, 10, 4);
            button.MinimumSize = new Size(0, 32);
            button.FlatAppearance.BorderColor = Border;
            button.FlatAppearance.MouseOverBackColor = Color.FromArgb(52, 70, 91);
            button.FlatAppearance.MouseDownBackColor = AccentMuted;
        }

        foreach (Control child in control.Controls) Apply(child);
    }

    internal static void Primary(Button button, bool danger = false)
    {
        button.BackColor = danger ? Danger : Accent;
        button.ForeColor = danger ? Color.White : OnAccent;
        button.FlatAppearance.BorderSize = 0;
        button.FlatAppearance.MouseOverBackColor = danger ? Color.FromArgb(192, 55, 79) : Color.FromArgb(139, 240, 222);
        button.FlatAppearance.MouseDownBackColor = danger ? DangerMuted : Color.FromArgb(70, 190, 174);
    }

    internal static void EnableDarkTitleBar(Form form)
    {
        if (!OperatingSystem.IsWindows()) return;
        var enabled = 1;
        _ = DwmSetWindowAttribute(form.Handle, 20, ref enabled, sizeof(int));
    }

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr window, int attribute, ref int value, int valueSize);
}
