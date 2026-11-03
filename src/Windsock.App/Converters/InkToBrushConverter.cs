using System.Globalization;
using System.Windows;
using System.Windows.Data;
using System.Windows.Media;
using Windsock.App.Theming;

namespace Windsock.App.Converters;

/// <summary>
/// Turns a colour written as text into something to paint with. Only the hex
/// forms the meter writes are read (<see cref="HexColour"/>), never through
/// WPF's ColorConverter, which would fetch a profile named in the text.
/// </summary>
public sealed class InkToBrushConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        HexColour.Brush(value as string)
            ?? Application.Current?.TryFindResource("TextFillColorPrimaryBrush")
            ?? (object)Brushes.White;

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
