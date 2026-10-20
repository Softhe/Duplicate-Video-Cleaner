namespace DuplicateVideoCleaner;

internal static class Program
{
    [STAThread]
    private static void Main()
    {
        ApplicationConfiguration.Initialize();
        var maintenance = Task.Run<IReadOnlyList<string>>(() =>
        {
            try { return AbandonedProbeCleanup.Run(); }
            catch (Exception ex) { return new[] { "Abandoned metadata cleanup was skipped: " + ex.Message }; }
        });
        using var form = new MainForm();
        form.Shown += async (_, _) =>
        {
            var warnings = await maintenance;
            if (!form.IsDisposed && !form.Disposing) form.ReportMaintenanceWarnings(warnings);
        };
        Application.Run(form);
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
