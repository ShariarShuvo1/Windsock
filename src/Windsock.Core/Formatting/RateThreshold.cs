using System.Globalization;

namespace Windsock.Core.Formatting;

/// <summary>
/// Parses the short rate expressions typed into a column filter, such as
/// <c>500</c>, <c>10k</c> or <c>&gt;= 1.5 MB/s</c>.
/// </summary>
public static class RateThreshold
{
    /// <summary>Reads a minimum rate, in bytes per second.</summary>
    public static bool TryParse(string? text, out double bytesPerSecond)
    {
        bytesPerSecond = 0;

        if (string.IsNullOrWhiteSpace(text))
        {
            return false;
        }

        ReadOnlySpan<char> value = text.AsSpan().Trim();
        value = value.TrimStart('>').TrimStart('=').Trim();

        // Trailing unit words, longest first so "kb/s" does not leave a stray b.
        foreach (string suffix in Suffixes)
        {
            if (value.EndsWith(suffix, StringComparison.OrdinalIgnoreCase))
            {
                value = value[..^suffix.Length].TrimEnd();
                break;
            }
        }

        double multiplier = 1;

        if (value.Length > 0)
        {
            multiplier = char.ToUpperInvariant(value[^1]) switch
            {
                'K' => 1024d,
                'M' => 1024d * 1024,
                'G' => 1024d * 1024 * 1024,
                'T' => 1024d * 1024 * 1024 * 1024,
                _ => 1,
            };

            if (multiplier > 1)
            {
                value = value[..^1].TrimEnd();
            }
        }

        if (!double.TryParse(value, NumberStyles.Float, CultureInfo.CurrentCulture, out double number)
            && !double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out number))
        {
            return false;
        }

        if (double.IsNaN(number) || number < 0)
        {
            return false;
        }

        bytesPerSecond = number * multiplier;
        return true;
    }

    private static readonly string[] Suffixes = ["bytes/s", "bits/s", "byte/s", "bps", "b/s", "ps", "b"];
}
