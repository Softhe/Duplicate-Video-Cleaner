using System.Windows;
using DuplicateVideoCleaner.Services;
using DuplicateVideoCleaner.Themes;
using DuplicateVideoCleaner.ViewModels;
using DuplicateVideoCleaner.Views;

namespace DuplicateVideoCleaner;

public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        var maintenance = Task.Run<IReadOnlyList<string>>(() =>
        {
            try { return AbandonedProbeCleanup.Run(); }
            catch (Exception ex) { return ["Abandoned metadata cleanup was skipped: " + ex.Message]; }
        });
        var settings = new SettingsService();
        var viewModel = new MainViewModel(settings, folderPicker: new FolderPicker(), shell: new ShellLauncher());
        ThemeManager.Apply(viewModel.Theme);
        var window = new MainWindow(viewModel);
        MainWindow = window;
        window.Show();
        window.ContentRendered += async (_, _) => viewModel.ReportMaintenanceWarnings(await maintenance);
    }
}
