using System.Windows;
using System.Windows.Controls;

namespace DuplicateVideoCleaner.Views;

public partial class ActivityPage : UserControl
{
    public ActivityPage() => InitializeComponent();

    private void OnMoreClick(object sender, RoutedEventArgs e) => ReviewPage.OpenMenu((Button)sender);
}
