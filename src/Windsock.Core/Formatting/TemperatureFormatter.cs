using System.Globalization;

namespace Windsock.Core.Formatting;

/// <summary>Which scale temperatures are shown in.</summary>
public enum TemperatureUnit
{
    Celsius,

    Fahrenheit,
}

/// <summary>
/// Writes temperatures in the scale the user chose.
/// </summary>
/// <remarks>
/// Every reading is kept in degrees Celsius - the sensors, the history file,
/// the thresholds that call a part hot - and turned into the chosen scale only
/// here, at the moment it is written. So switching scale changes no stored
/// figure and no threshold.
/// </remarks>
public static class TemperatureFormatter
{
    /// <summary>A reading in the chosen scale.</summary>
    public static double Convert(double celsius, TemperatureUnit unit) =>
        unit == TemperatureUnit.Fahrenheit ? (celsius * 9 / 5) + 32 : celsius;

    /// <summary>
    /// A difference between two readings in the chosen scale: a degree
    /// Fahrenheit is five ninths of one Celsius, with no offset.
    /// </summary>
    public static double ConvertDifference(double celsius, TemperatureUnit unit) =>
        unit == TemperatureUnit.Fahrenheit ? celsius * 9 / 5 : celsius;

    /// <summary>The unit's symbol, degree sign included.</summary>
    public static string Symbol(TemperatureUnit unit) =>
        unit == TemperatureUnit.Fahrenheit ? "°F" : "°C";

    /// <summary>
    /// A reading as a whole number and its unit apart, as the taskbar meter
    /// sets them.
    /// </summary>
    public static (string Value, string Unit) Split(double celsius, TemperatureUnit unit) =>
        (Whole(Convert(celsius, unit)), Symbol(unit));

    /// <summary>A reading as a whole number with its unit, as the pages show it: "64°C".</summary>
    public static string Format(double celsius, TemperatureUnit unit) =>
        string.Concat(Whole(Convert(celsius, unit)), Symbol(unit));

    /// <summary>A reading, or a dash where there is none.</summary>
    public static string Format(double? celsius, TemperatureUnit unit) =>
        celsius is { } degrees && double.IsFinite(degrees) ? Format(degrees, unit) : "—";

    private static string Whole(double value)
    {
        double rounded = Math.Round(value, MidpointRounding.AwayFromZero);

        // Never "-0": a reading that rounds to nothing is nothing.
        return (rounded == 0 ? 0 : rounded).ToString("F0", CultureInfo.InvariantCulture);
    }
}
