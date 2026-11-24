using System.Globalization;
using System.Windows;
using System.Windows.Data;

namespace DuplicateVideoCleaner.Views;

// true → Visible (or Collapsed with Invert), and non-null/non-empty objects count as true.
public sealed class VisibleWhenConverter : IValueConverter
{
    public bool Invert { get; set; }

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        var truthy = value switch
        {
            bool flag => flag,
            string text => !string.IsNullOrWhiteSpace(text),
            int count => count != 0,
            null => false,
            _ => true
        };
        return truthy != Invert ? Visibility.Visible : Visibility.Collapsed;
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => throw new NotSupportedException();
}

public sealed class NotConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) => value is not true;
    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => value is not true;
}

// Two-way radio binding to an enum: IsChecked="{Binding Theme, Converter={StaticResource EnumIs}, ConverterParameter=Dark}".
public sealed class EnumIsConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        string.Equals(value?.ToString(), parameter as string, StringComparison.Ordinal);

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is true && parameter is string name ? Enum.Parse(targetType, name) : Binding.DoNothing;
}
