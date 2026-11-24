using System.Diagnostics;
using DuplicateVideoCleaner.ViewModels;
using Microsoft.Win32;

namespace DuplicateVideoCleaner.Services;

internal sealed class FolderPicker : IFolderPicker
{
    public string? PickFolder(string title, string? initialFolder)
    {
        var dialog = new OpenFolderDialog { Title = title, Multiselect = false };
        if (initialFolder is not null) dialog.InitialDirectory = initialFolder;
        return dialog.ShowDialog(System.Windows.Application.Current?.MainWindow) == true ? dialog.FolderName : null;
    }
}

internal sealed class ShellLauncher : IShellLauncher
{
    public void Open(string path) => Process.Start(new ProcessStartInfo(path) { UseShellExecute = true })?.Dispose();
}
