using System.Globalization;
using System.Windows;
using System.Windows.Data;

namespace Windsock.App.Converters;

/// <summary>
/// Shows something only when there is nothing else to show.
/// </summary>
public sealed class EmptyToVisibilityConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        IsEmpty(value) ? Visibility.Visible : Visibility.Collapsed;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();

    private static bool IsEmpty(object? value) => value switch
    {
        null => true,
        int count => count == 0,
        string text => text.Length == 0,
        System.Collections.ICollection collection => collection.Count == 0,
        _ => false,
    };
}

/// <summary>
/// Shows something only when there is something to show.
/// </summary>
public sealed class TextToVisibilityConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is string { Length: > 0 } ? Visibility.Visible : Visibility.Collapsed;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}
