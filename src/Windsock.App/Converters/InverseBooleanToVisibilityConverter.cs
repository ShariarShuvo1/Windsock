using System.Globalization;
using System.Windows;
using System.Windows.Data;

namespace Windsock.App.Converters;

/// <summary>
/// Shows an element while a flag is <see langword="false"/>.
/// </summary>
[ValueConversion(typeof(bool), typeof(Visibility))]
public sealed class InverseBooleanToVisibilityConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is true ? Visibility.Collapsed : Visibility.Visible;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is Visibility.Collapsed or Visibility.Hidden;
}
