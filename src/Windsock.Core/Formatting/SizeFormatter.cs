using System.Globalization;

namespace Windsock.Core.Formatting;

/// <summary>
/// Formats a quantity of bytes, as opposed to a rate.
/// </summary>
public static class SizeFormatter
{
    private const double Step = 1024;
    private static readonly string[] Units = ["B", "KB", "MB", "GB", "TB", "PB"];

    /// <summary>Formats a byte count, for example <c>1.44 GB</c>.</summary>
    public static string Format(long bytes)
    {
        (string value, string unit) = Split(bytes);
        return string.Concat(value, " ", unit);
    }

    /// <summary>
    /// The same formatting, with the number and its unit kept apart.
    /// </summary>
    public static (string Value, string Unit) Split(long bytes)
    {
        if (bytes <= 0)
        {
            return ("0", "B");
        }

        double value = bytes;
        int unit = 0;

        while (value >= Step && unit < Units.Length - 1)
        {
            value /= Step;
            unit++;
        }

        // Whole bytes are a count, not a measurement, so they carry no decimals.
        string text = unit == 0
            ? value.ToString("F0", CultureInfo.InvariantCulture)
            : value switch
            {
                >= 100 => value.ToString("F0", CultureInfo.InvariantCulture),
                >= 10 => value.ToString("F1", CultureInfo.InvariantCulture),
                _ => value.ToString("F2", CultureInfo.InvariantCulture),
            };

        return (text, Units[unit]);
    }
}
