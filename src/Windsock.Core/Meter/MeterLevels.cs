using System.Globalization;
using Windsock.Core.Formatting;
using Windsock.Core.Settings;

namespace Windsock.Core.Meter;

/// <summary>How a level is written while it is being set: in the unit a person thinks of the reading in.</summary>
/// <param name="Label">The unit's name, such as MB/s.</param>
/// <param name="PerUnit">How many of the reading's base units one of these is.</param>
/// <param name="Fahrenheit">Whether the figure is degrees Fahrenheit, which need an offset as well.</param>
public readonly record struct LevelUnit(string Label, double PerUnit, bool Fahrenheit = false)
{
    /// <summary>A level, from the base unit it is kept in to this one.</summary>
    public double Show(double at) => Fahrenheit ? (at * 9 / 5) + 32 : at / PerUnit;

    /// <summary>A level typed in this unit, in the base unit it is kept in.</summary>
    public double Keep(double shown) => Fahrenheit ? (shown - 32) * 5 / 9 : shown * PerUnit;
}

/// <summary>
/// Colours a reading takes past levels: a response time going amber then red,
/// a battery running low.
/// </summary>
/// <remarks>
/// A reading takes the colour of the furthest level it has passed. A level
/// passed going up is passed when the reading is at it or above; going down,
/// at it or below. If a reading has passed levels both ways, the one listed
/// later wins. The named colours - good, fair, slow - are not colours but
/// rules: each is drawn in a shade that reads on the taskbar's own theme, and
/// drawn again when that changes.
/// </remarks>
public static class MeterLevels
{
    /// <summary>A reading in good shape: green.</summary>
    public const string Good = "good";

    /// <summary>A reading to keep an eye on: amber.</summary>
    public const string Fair = "fair";

    /// <summary>A reading in poor shape: red.</summary>
    public const string Slow = "slow";

    /// <summary>The most levels one place keeps.</summary>
    public const int Most = 8;

    /// <summary>Whether a colour is one of the named ones that follow the theme.</summary>
    public static bool IsNamed(string? colour) => colour is Good or Fair or Slow;

    /// <summary>
    /// The levels a field has before anyone sets any. A response time is
    /// tinted as the AIM thresholds for games have it - fair from 50 ms, poor
    /// from 100 ms, as the Connection page grades it - a battery goes amber
    /// at 20% and red at 10%, where Windows warns, and a data plan amber once
    /// four fifths of it is used and red once it is all gone.
    /// </summary>
    public static IReadOnlyList<MeterLevel> Defaults(MeterField field) => field switch
    {
        MeterField.RouterPing or MeterField.InternetPing =>
        [
            new() { At = 0, Colour = Good },
            new() { At = 50, Colour = Fair },
            new() { At = 100, Colour = Slow },
        ],

        MeterField.BatteryCharge =>
        [
            new() { At = 20, Colour = Fair, Below = true },
            new() { At = 10, Colour = Slow, Below = true },
        ],

        MeterField.Plan =>
        [
            new() { At = 80, Colour = Fair },
            new() { At = 100, Colour = Slow },
        ],

        _ => [],
    };

    /// <summary>
    /// The levels a place applies to one of its readings: the ones set on the
    /// place, for its own field, else the field's defaults.
    /// </summary>
    public static IReadOnlyList<MeterLevel> For(MeterSlot slot, MeterField field)
    {
        ArgumentNullException.ThrowIfNull(slot);

        return field == slot.Field && slot.Levels is { } set ? set : Defaults(field);
    }

    /// <summary>The colour a value takes past the levels, or nothing when it has passed none.</summary>
    public static string? Colour(IReadOnlyList<MeterLevel> levels, double? value)
    {
        ArgumentNullException.ThrowIfNull(levels);

        if (value is not { } reading || !double.IsFinite(reading) || levels.Count == 0)
        {
            return null;
        }

        MeterLevel? up = null;
        MeterLevel? down = null;
        int upAt = -1;
        int downAt = -1;

        for (int each = 0; each < levels.Count; each++)
        {
            MeterLevel level = levels[each];

            if (level is null || !double.IsFinite(level.At))
            {
                continue;
            }

            if (!level.Below && reading >= level.At && (up is null || level.At >= up.At))
            {
                up = level;
                upAt = each;
            }
            else if (level.Below && reading <= level.At && (down is null || level.At <= down.At))
            {
                down = level;
                downAt = each;
            }
        }

        return (up, down) switch
        {
            (null, null) => null,
            ({ } only, null) => only.Colour,
            (null, { } only) => only.Colour,
            _ => downAt > upAt ? down!.Colour : up!.Colour,
        };
    }

    /// <summary>
    /// The unit a level is set in for a field, or nothing for a field with no
    /// number to compare: an address, a plan, the windsock.
    /// </summary>
    public static LevelUnit? UnitOf(MeterField field, RateFamily family, TemperatureUnit temperature) => field switch
    {
        MeterField.Download or MeterField.Upload or MeterField.NetworkTotal => family == RateFamily.Bits
            ? new LevelUnit("Mbit/s", 125_000)
            : new LevelUnit("MB/s", 1024 * 1024),

        MeterField.TodayTotal or MeterField.MonthTotal or MeterField.MemoryUsed => new LevelUnit("GB", 1024d * 1024 * 1024),

        MeterField.ProcessorLoad or MeterField.GraphicsLoad or MeterField.GraphicsMemory or MeterField.MemoryLoad
            or MeterField.StorageLoad or MeterField.StorageUsed or MeterField.WifiSignal
            or MeterField.BatteryCharge or MeterField.NpuLoad => new LevelUnit("%", 1),

        MeterField.ProcessorTemperature or MeterField.BoardTemperature or MeterField.GraphicsTemperature
            or MeterField.StorageTemperature => temperature == TemperatureUnit.Fahrenheit
                ? new LevelUnit("°F", 1, Fahrenheit: true)
                : new LevelUnit("°C", 1),

        MeterField.ProcessorClock => new LevelUnit("GHz", 1000),
        MeterField.GraphicsPower or MeterField.ProcessorPower => new LevelUnit("W", 1),
        MeterField.RouterPing or MeterField.InternetPing => new LevelUnit("ms", 1),
        MeterField.BatteryTime => new LevelUnit("min", 1),
        MeterField.Feed => new LevelUnit(string.Empty, 1),
        MeterField.Plan => new LevelUnit("% used", 1),
        _ => null,
    };

    /// <summary>A level as it is shown while it is being set, to two places at most.</summary>
    public static string Describe(MeterLevel level, LevelUnit unit)
    {
        ArgumentNullException.ThrowIfNull(level);

        string figure = Math.Round(unit.Show(level.At), 2).ToString("0.##", CultureInfo.CurrentCulture);
        string sign = level.Below ? "≤" : "≥";

        return unit.Label.Length == 0 ? string.Concat(sign, " ", figure) : string.Concat(sign, " ", figure, " ", unit.Label);
    }
}
