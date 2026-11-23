using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using DuplicateVideoCleaner.ViewModels;

namespace DuplicateVideoCleaner.Views;

public partial class ScanPage : UserControl
{
    public ScanPage()
    {
        InitializeComponent();
        IsVisibleChanged += (_, _) =>
        {
            if (IsVisible) Dispatcher.BeginInvoke(() => FolderBox.Focus(), System.Windows.Threading.DispatcherPriority.Input);
        };
    }

    private ScanViewModel? ViewModel => DataContext as ScanViewModel;

    private void OnDragOver(object sender, DragEventArgs e)
    {
        var accepted = ViewModel?.CanEdit == true && e.Data.GetDataPresent(DataFormats.FileDrop);
        e.Effects = accepted ? DragDropEffects.Copy : DragDropEffects.None;
        DropOutline.Stroke = (System.Windows.Media.Brush)FindResource(accepted ? "AccentBrush" : "StrongStrokeBrush");
        DropOutline.Opacity = accepted ? 1 : 0.55;
        e.Handled = true;
    }

    private void OnDragLeave(object sender, DragEventArgs e) => ResetOutline();

    private void OnDrop(object sender, DragEventArgs e)
    {
        ResetOutline();
        if (e.Data.GetData(DataFormats.FileDrop) is string[] paths) ViewModel?.TryDropFolder(paths);
    }

    private void ResetOutline()
    {
        DropOutline.SetResourceReference(System.Windows.Shapes.Shape.StrokeProperty, "StrongStrokeBrush");
        DropOutline.Opacity = 0.55;
    }

    private void OnThresholdLostFocus(object sender, KeyboardFocusChangedEventArgs e) => ViewModel?.CommitThresholdText();

    private void OnThresholdKeyDown(object sender, KeyEventArgs e)
    {
        if (ViewModel is not { } model) return;
        if (e.Key == Key.Up) { model.ThresholdSeconds++; e.Handled = true; }
        else if (e.Key == Key.Down) { model.ThresholdSeconds--; e.Handled = true; }
    }
}
