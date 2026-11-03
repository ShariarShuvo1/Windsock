using Windsock.Core.Settings;

namespace Windsock.Core.Formatting;

/// <summary>
/// The room a reading on the meter keeps for itself, so the meter's width
/// holds still while its digits change.
/// </summary>
/// <remarks>
/// The room is written as a template - the widest figure with every digit a
/// zero - and the meter measures that in the reading's own font. With the
/// figures drawn tabular every digit is as wide as a zero, so the template is
/// exactly as wide as the widest figure it stands for.
///
/// What the widest figure is comes from the formatters. An automatic rate is
/// never more than four digits in any unit: below 1,024 it stays in the unit
/// it has (1023 B/s, 1023 KB/s), and above it moves to the next. A share is
/// at most three digits. Temperatures, watts and a rate in a fixed unit have
/// no ceiling the formatter enforces, so they start from what they usually
/// show and keep the widest they have shown since, for as long as the unit
/// stays the same.
/// </remarks>
public static class MeterReserve
{
    private static readonly string[] MemoryUnits = ["B", "KB", "MB", "GB", "TB", "PB"];
    private static readonly string[] WifiUnits = ["%", "% · 2.4 GHz", "% · 5 GHz", "% · 6 GHz"];

    // Asked for on every reading, so made once.
    private static readonly string[] ByteRates = Steps(RateFamily.Bytes);
    private static readonly string[] BitRates = Steps(RateFamily.Bits);

    /// <summary>
    /// The template to keep room for: the widest of the field's usual widest
    /// figure, the figure on show now, and what was kept before in this unit.
    /// </summary>
    /// <param name="field">What the place shows.</param>
    /// <param name="scale">How far it is scaled.</param>
    /// <param name="value">The figure on show now.</param>
    /// <param name="kept">The template kept so far in the same unit, or nothing after the unit changed.</param>
    /// <param name="temperature">The scale temperatures are written in: Fahrenheit needs three digits from 38 °C up.</param>
    public static string Template(
        MeterField field,
        RateScale scale,
        string? value,
        string? kept,
        TemperatureUnit temperature = TemperatureUnit.Celsius)
    {
        string widest = Floor(field, scale, temperature);

        if (value is { Length: > 0 } shown && shown != MeterReadout.Unknown)
        {
            widest = Wider(widest, Shape(shown));
        }

        return kept is { Length: > 0 } ? Wider(widest, kept) : widest;
    }

    /// <summary>
    /// Every unit a reading can move between, so the room kept for the unit
    /// is the widest of them and the meter does not move when the unit does.
    /// </summary>
    public static IReadOnlyList<string> Units(
        MeterField field,
        RateFamily family,
        RateScale scale,
        TemperatureUnit temperature = TemperatureUnit.Celsius) => field switch
    {
        MeterField.Download or MeterField.Upload or MeterField.NetworkTotal when scale == RateScale.Auto =>
            family == RateFamily.Bits ? BitRates : ByteRates,

        MeterField.Download or MeterField.Upload or MeterField.NetworkTotal =>
            [RateUnits.Label(family, RateUnits.StepOf(scale))],

        MeterField.ProcessorClock => ["GHz", "MHz"],
        MeterField.MemoryUsed or MeterField.TodayTotal or MeterField.MonthTotal => MemoryUnits,
        MeterField.WifiSignal => WifiUnits,
        MeterField.Empty or MeterField.Pacer or MeterField.LocalAddress or MeterField.Feed or MeterField.Plan
            or MeterField.BatteryTime => [],
        MeterField.PublicAddress => ["WW"],
        _ => [MeterReadout.Unit(field, family, scale, temperature)],
    };

    private static string[] Steps(RateFamily family) =>
        [.. Enumerable.Range(0, RateUnits.MaxStep + 1).Select(step => RateUnits.Label(family, step))];

    /// <summary>A figure with every digit turned to a zero.</summary>
    public static string Shape(string value)
    {
        ArgumentNullException.ThrowIfNull(value);

        return string.Create(value.Length, value, static (span, source) =>
        {
            for (int each = 0; each < source.Length; each++)
            {
                span[each] = char.IsAsciiDigit(source[each]) ? '0' : source[each];
            }
        });
    }

    /// <summary>
    /// The widest figure a field usually shows, which is all it can show for
    /// most fields.
    /// </summary>
    private static string Floor(MeterField field, RateScale scale, TemperatureUnit temperature) => field switch
    {
        MeterField.Download or MeterField.Upload or MeterField.NetworkTotal =>
            scale == RateScale.Auto ? "0000" : "0.00",

        MeterField.ProcessorLoad or MeterField.GraphicsLoad or MeterField.GraphicsMemory
            or MeterField.MemoryLoad or MeterField.StorageLoad or MeterField.StorageUsed
            or MeterField.BatteryCharge or MeterField.WifiSignal or MeterField.NpuLoad => "000",

        MeterField.ProcessorTemperature or MeterField.BoardTemperature or MeterField.GraphicsTemperature
            or MeterField.StorageTemperature => temperature == TemperatureUnit.Fahrenheit ? "000" : "00",

        MeterField.ProcessorClock => "0.00",
        MeterField.MemoryUsed or MeterField.TodayTotal or MeterField.MonthTotal => "0000",
        MeterField.RouterPing or MeterField.InternetPing => "000",
        MeterField.BatteryTime => "0:00",
        MeterField.LocalAddress => "000.000.0.000",
        MeterField.Empty or MeterField.Pacer => string.Empty,
        _ => "00",
    };

    /// <summary>
    /// The wider of two templates. A digit counts whole and a separator half,
    /// which is near enough how a tabular font draws them.
    /// </summary>
    private static string Wider(string first, string second) =>
        Weight(second) > Weight(first) ? second : first;

    private static double Weight(string template)
    {
        double weight = 0;

        foreach (char each in template)
        {
            weight += each is '.' or ',' ? 0.5 : 1;
        }

        return weight;
    }
}
