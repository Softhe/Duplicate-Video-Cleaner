using System.Windows;
using DuplicateVideoCleaner.Services;
using DuplicateVideoCleaner.Themes;
using DuplicateVideoCleaner.ViewModels;
using DuplicateVideoCleaner.Views;

namespace DuplicateVideoCleaner;

public partial class App : Application
{
    private ThumbnailService? _thumbnails;

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
        _thumbnails = new ThumbnailService(new ThumbnailExtractor(), new ThumbnailCache()) { Enabled = viewModel.ShowThumbnails };
        viewModel.Review.Thumbnails = _thumbnails;
        ThemeManager.Apply(viewModel.Theme);
        var window = new MainWindow(viewModel);
        MainWindow = window;
        window.Show();
        window.ContentRendered += async (_, _) => viewModel.ReportMaintenanceWarnings(await maintenance);
    }

    // Stops thumbnail work and removes its sandbox (staged tools and AppContainer profile).
    // A crash leaves them for AbandonedProbeCleanup on the next start.
    protected override void OnExit(ExitEventArgs e)
    {
        try { _thumbnails?.DisposeAsync().AsTask().Wait(TimeSpan.FromSeconds(5)); }
        catch (Exception) { }
        base.OnExit(e);
    }
}
