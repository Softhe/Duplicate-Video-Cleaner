using System.Runtime.ExceptionServices;
using System.Windows.Threading;
using DuplicateVideoCleaner.ViewModels;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace DuplicateVideoCleaner.UiTests;

// Runs a test on an STA thread with a WPF dispatcher and its synchronization context, so
// view-model continuations, commands and timers behave as they do in the app.
internal static class WpfTestHost
{
    internal static void Run(Func<Task> test, int timeoutSeconds = 60)
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            var dispatcher = Dispatcher.CurrentDispatcher;
            SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(dispatcher));
            var frame = new DispatcherFrame();
            dispatcher.BeginInvoke(async () =>
            {
                try { await test(); }
                catch (Exception ex) { failure = ex; }
                finally { frame.Continue = false; }
            });
            Dispatcher.PushFrame(frame);
            dispatcher.InvokeShutdown();
        }) { IsBackground = true };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        if (!thread.Join(TimeSpan.FromSeconds(timeoutSeconds))) throw new AssertFailedException("The UI-thread test did not finish in time.");
        if (failure is not null) ExceptionDispatchInfo.Throw(failure);
    }

    // A temporary folder with two neighbouring recordings, its own settings file and a metadata parser:
    // the isolated fixture parser (DuplicateVideoCleaner.Tests.exe), real ffprobe when requested and
    // configured, or none.
    internal static void RunWithWorkspace(Func<MainViewModel, string, Task> test, bool useProbeFixture = false,
        bool useRealMediaIfProvided = false, Action<AppSettings>? settingsWriter = null,
        Func<string, Action<string>, CancellationToken, bool, IReadOnlyList<RecoveryEntry>>? recoveryList = null)
    {
        var root = Path.Combine(Path.GetTempPath(), "dvc-ui-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            File.WriteAllText(Path.Combine(root, "2026-01-01 10-00-00 first.mp4"), "one");
            File.WriteAllText(Path.Combine(root, "2026-01-01 10-00-01 second.mp4"), "two");
            var probe = Path.Combine(root, "missing.exe");
            if (useProbeFixture)
            {
                probe = ProbeFixture;
                if (useRealMediaIfProvided && Environment.GetEnvironmentVariable("DVC_TEST_FFPROBE") is { Length: > 0 } realProbe)
                {
                    var media = Environment.GetEnvironmentVariable("DVC_TEST_MEDIA") ?? throw new InvalidOperationException("DVC_TEST_MEDIA is required with DVC_TEST_FFPROBE.");
                    probe = realProbe;
                    foreach (var path in Directory.EnumerateFiles(root, "*.mp4")) File.Copy(media, path, true);
                }
            }
            Run(async () =>
            {
                var settings = new SettingsService(Path.Combine(root, "settings.json"));
                var main = new MainViewModel(settings, settingsWriter, new MetadataProbe(probe), recoveryList: recoveryList);
                main.Scan.Folder = root;
                await test(main, root);
            });
        }
        finally { try { Directory.Delete(root, true); } catch { } }
    }

    internal static string ProbeFixture => Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..",
        "DuplicateVideoCleaner.Tests", "bin", "Release", "net10.0-windows", "DuplicateVideoCleaner.Tests.exe"));

    internal static async Task WaitUntil(Func<bool> condition, string message, int seconds = 10)
    {
        var deadline = DateTime.UtcNow.AddSeconds(seconds);
        while (!condition())
        {
            if (DateTime.UtcNow >= deadline) Assert.Fail(message);
            await Task.Delay(20);
        }
    }
}
