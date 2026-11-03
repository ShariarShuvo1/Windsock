using System.Globalization;
using Windsock.Core.Formatting;

namespace Windsock.Core.Meter;

/// <summary>
/// Rates squeezed into the notification-area icon: four characters at most,
/// the unit a single letter after the figure.
/// </summary>
/// <remarks>
/// An icon is 16 pixels square at 100% and 32 at 200%, with room for two short
/// lines and no more, so a rate is written the way a phone's status bar
/// writes it: 512, 1.2K, 12K, 123K, 1.2M. Bytes count in 1,024s as everywhere
/// else in Windsock; bits in 1,000s, with a lower-case k. A figure that would
/// round up to 1,000 of its unit moves to the next one, so "1000K" never
/// appears.
/// </remarks>
public static class TrayFigures
{
    private static readonly char[] ByteUnits = ['K', 'M', 'G', 'T'];
    private static readonly char[] BitUnits = ['k', 'M', 'G', 'T'];

    /// <summary>A rate in four characters or fewer.</summary>
    public static string Compact(double bytesPerSecond, RateFamily family)
    {
        double value = double.IsFinite(bytesPerSecond) && bytesPerSecond > 0 ? bytesPerSecond : 0;
        double step = family == RateFamily.Bits ? 1000 : 1024;
        char[] units = family == RateFamily.Bits ? BitUnits : ByteUnits;

        if (family == RateFamily.Bits)
        {
            value *= 8;
        }

        if (Math.Round(value) < 1000)
        {
            return Math.Round(value).ToString("0", CultureInfo.InvariantCulture);
        }

        int unit = -1;

        do
        {
            value /= step;
            unit++;
        }
        while (Math.Round(value) >= 1000 && unit < units.Length - 1);

        string figure = Math.Round(value, 1) < 10
            ? value.ToString("0.0", CultureInfo.InvariantCulture)
            : Math.Round(value).ToString("0", CultureInfo.InvariantCulture);

        return string.Concat(figure, units[unit].ToString());
    }
}
