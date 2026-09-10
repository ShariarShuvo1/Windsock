using System.Globalization;
using System.Windows;
using System.Windows.Data;
using FluentIcons.Common;
using Windsock.Core.Settings;

namespace Windsock.App.Converters;

/// <summary>
/// Turns the part of the machine a reading is about into the picture that
/// stands for it.
/// </summary>
public sealed class MarkToSymbolConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is MeterMark mark
            ? mark switch
            {
                MeterMark.Download => Symbol.ArrowDownload,
                MeterMark.Upload => Symbol.ArrowUpload,
                MeterMark.Network => Symbol.ArrowsBidirectional,
                MeterMark.Processor => Symbol.DeveloperBoard,
                MeterMark.Graphics => Symbol.Games,
                MeterMark.Memory => Symbol.Ram,
                MeterMark.Storage => Symbol.HardDrive,
                MeterMark.System => Symbol.Temperature,
                _ => Symbol.Circle,
            }
            : Symbol.Circle;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException("A picture is never read back into a reading.");
}

/// <summary>
/// Turns extra weight into a filled icon.
/// </summary>
public sealed class WeightToVariantConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        Heavy(value) ? IconVariant.Filled : IconVariant.Regular;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException("A picture is never read back into a weight.");

    private static bool Heavy(object? value) => value switch
    {
        bool asked => asked,
        FontWeight weight => weight.ToOpenTypeWeight() >= FontWeights.SemiBold.ToOpenTypeWeight(),
        _ => false,
    };
}
