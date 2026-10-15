namespace DuplicateVideoCleaner;

internal static class Program
{
    [STAThread]
    private static void Main()
    {
        ApplicationConfiguration.Initialize();
        Application.Run(new MainForm());
    }

    internal static void ApplyIcon(Form form)
    {
        try
        {
            using var icon = Icon.ExtractAssociatedIcon(Application.ExecutablePath);
            if (icon is not null) form.Icon = (Icon)icon.Clone();
        }
        catch { }
    }
}
