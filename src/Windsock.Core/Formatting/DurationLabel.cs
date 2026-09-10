using System.Globalization;

namespace Windsock.Core.Formatting;

/// <summary>Formats a time span for a compact axis label.</summary>
public static class DurationLabel
{
    private const int Minute = 60;
    private const int Hour = 60 * Minute;
    private const int Day = 24 * Hour;

    /// <summary>
    /// Formats <paramref name="seconds"/> as the shortest readable label, for
    /// example <c>45s</c>, <c>2m 30s</c>, <c>3h</c> or <c>5d 6h</c>.
    /// </summary>
    public static string Format(double seconds)
    {
        if (double.IsNaN(seconds) || seconds <= 0)
        {
            return "0s";
        }

        int total = (int)Math.Round(seconds);

        if (total < Minute)
        {
            return Compose(total, "s");
        }

        if (total < Hour)
        {
            return Compose(total / Minute, "m", total % Minute, "s", coarseAbove: 10);
        }

        if (total < Day)
        {
            return Compose(total / Hour, "h", (total % Hour) / Minute, "m", coarseAbove: 10);
        }

        return Compose(total / Day, "d", (total % Day) / Hour, "h", coarseAbove: 10);
    }

    private static string Compose(int value, string unit) =>
        string.Create(CultureInfo.InvariantCulture, $"{value}{unit}");

    private static string Compose(int major, string majorUnit, int minor, string minorUnit, int coarseAbove)
    {
        if (minor == 0 || major >= coarseAbove)
        {
            return Compose(major, majorUnit);
        }

        return string.Create(CultureInfo.InvariantCulture, $"{major}{majorUnit} {minor}{minorUnit}");
    }
}
