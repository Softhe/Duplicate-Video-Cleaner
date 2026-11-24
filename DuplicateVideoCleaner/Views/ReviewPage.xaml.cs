using System.Windows;
using System.Windows.Controls;

namespace DuplicateVideoCleaner.Views;

public partial class ReviewPage : UserControl
{
    public ReviewPage()
    {
        InitializeComponent();
        IsVisibleChanged += (_, _) =>
        {
            if (IsVisible) Dispatcher.BeginInvoke(() => GroupList.Focus(), System.Windows.Threading.DispatcherPriority.Input);
        };
    }

    // The overflow menu opens on click (and keyboard), not only on right-click.
    private void OnMoreClick(object sender, RoutedEventArgs e) => OpenMenu((Button)sender);

    internal static void OpenMenu(Button button)
    {
        if (button.ContextMenu is not { } menu) return;
        menu.PlacementTarget = button;
        menu.DataContext = button.DataContext;
        menu.IsOpen = true;
    }
}
