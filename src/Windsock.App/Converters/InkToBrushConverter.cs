using System.Globalization;
using System.Windows;
using System.Windows.Data;
using System.Windows.Media;

namespace Windsock.App.Converters;

/// <summary>
/// Turns a colour written as text into something to paint with.
/// </summary>
public sealed class InkToBrushConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is string { Length: > 0 } ink)
        {
            try
            {
                if (ColorConverter.ConvertFromString(ink) is Color found)
                {
                    SolidColorBrush brush = new(found);
                    brush.Freeze();
                    return brush;
                }
            }
            catch (FormatException)
            {
                // Not a colour, so nothing was chosen.
            }
        }

        return Application.Current?.TryFindResource("TextFillColorPrimaryBrush")
            ?? (object)Brushes.White;
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}

/// <summary>
/// Turns "this figure carries extra weight" into the weight it carries.
/// </summary>
public sealed class BoolToWeightConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is true ? FontWeights.SemiBold : FontWeights.Normal;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}
