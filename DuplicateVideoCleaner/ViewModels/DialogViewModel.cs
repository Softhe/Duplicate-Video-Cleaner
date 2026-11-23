using System.Windows.Input;
using DuplicateVideoCleaner.Mvvm;

namespace DuplicateVideoCleaner.ViewModels;

internal enum DialogTone { Neutral, Success, Caution, Critical }

internal sealed record DialogFact(string Label, string Value, string? Detail = null);

// A row in a dialog list: short text, with the full value as tooltip and accessible name.
internal sealed record DialogListItem(string Text, string Full);

// An in-window content dialog. The close (Cancel) button is the default and gets focus, so
// Enter and Esc never confirm a destructive action.
internal sealed class DialogViewModel : ObservableObject
{
    private readonly TaskCompletionSource<bool> _result = new(TaskCreationOptions.RunContinuationsAsynchronously);

    internal DialogViewModel(string title, string message, string closeText = "Close", string? primaryText = null,
        bool primaryIsDestructive = false)
    {
        Title = title;
        Message = message;
        CloseText = closeText;
        PrimaryText = primaryText;
        PrimaryIsDestructive = primaryIsDestructive;
        PrimaryCommand = new RelayCommand(() => Complete(true));
        CloseCommand = new RelayCommand(() => Complete(false));
    }

    public string Title { get; }
    public string Message { get; }
    public string CloseText { get; }
    public string? PrimaryText { get; }
    public bool HasPrimary => PrimaryText is not null;
    public bool PrimaryIsDestructive { get; }
    public IReadOnlyList<DialogFact> Facts { get; init; } = [];
    public string? Warning { get; init; }
    public DialogTone WarningTone { get; init; } = DialogTone.Caution;
    public string? ListHeading { get; init; }
    public IReadOnlyList<DialogListItem> ListItems { get; init; } = [];
    public string? ListAutomationName { get; init; }
    public ICommand PrimaryCommand { get; }
    public ICommand CloseCommand { get; }
    public bool IsCompleted => _result.Task.IsCompleted;

    internal Task<bool> Result => _result.Task;

    internal void Complete(bool confirmed) => _result.TrySetResult(confirmed);
}

internal interface IFolderPicker
{
    string? PickFolder(string title, string? initialFolder);
}

internal interface IShellLauncher
{
    void Open(string path);
}
