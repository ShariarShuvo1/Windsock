using System.Globalization;

namespace Windsock.Core.Formatting;

/// <summary>
/// Reads the durations typed into a column filter, such as <c>2h</c>,
/// <c>90m</c> or <c>1d 6h</c>.
/// </summary>
public static class DurationThreshold
{
    /// <summary>Reads a duration and returns it in seconds.</summary>
    public static bool TryParse(string? text, out double seconds)
    {
        seconds = 0;

        if (string.IsNullOrWhiteSpace(text))
        {
            return false;
        }

        ReadOnlySpan<char> value = text.AsSpan().Trim();
        bool any = false;
        int index = 0;

        while (index < value.Length)
        {
            while (index < value.Length && char.IsWhiteSpace(value[index]))
            {
                index++;
            }

            int start = index;

            while (index < value.Length && (char.IsAsciiDigit(value[index]) || value[index] is '.' or ','))
            {
                index++;
            }

            if (index == start)
            {
                return false;
            }

            if (!double.TryParse(value[start..index], NumberStyles.Float, CultureInfo.CurrentCulture, out double number)
                && !double.TryParse(value[start..index], NumberStyles.Float, CultureInfo.InvariantCulture, out number))
            {
                return false;
            }

            while (index < value.Length && char.IsWhiteSpace(value[index]))
            {
                index++;
            }
            double multiplier = 60;

            if (index < value.Length)
            {
                multiplier = char.ToUpperInvariant(value[index]) switch
                {
                    'S' => 1,
                    'M' => 60,
                    'H' => 3600,
                    'D' => 86400,
                    _ => 0,
                };

                if (multiplier == 0)
                {
                    return false;
                }

                index++;

                // Allow the long forms: sec, min, hours, days.
                while (index < value.Length && char.IsAsciiLetter(value[index]))
                {
                    index++;
                }
            }

            seconds += number * multiplier;
            any = true;
        }

        return any;
    }
}
