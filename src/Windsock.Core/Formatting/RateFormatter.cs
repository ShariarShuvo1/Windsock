using System.Globalization;

namespace Windsock.Core.Formatting;

/// <summary>
/// Formats transfer rates for display, splitting the number from its unit so
/// the two can be styled at different sizes.
/// </summary>
public static class RateFormatter
{
    /// <summary>Formats a rate in the requested family and scale.</summary>
    public static (string Value, string Unit) Split(
        double bytesPerSecond,
        RateFamily family,
        RateScale scale)
    {
        if (scale == RateScale.Auto)
        {
            return SplitAuto(bytesPerSecond, family);
        }

        int step = RateUnits.StepOf(scale);
        string label = RateUnits.Label(family, step);

        if (!IsUsable(bytesPerSecond))
        {
            return ("0", label);
        }

        return (Fixed(bytesPerSecond / RateUnits.BytesPerUnit(family, step)), label);
    }

    /// <summary>Formats a rate as a single string, for example <c>12.4 MB/s</c>.</summary>
    public static string Format(double bytesPerSecond, RateFamily family, RateScale scale)
    {
        (string value, string unit) = Split(bytesPerSecond, family, scale);
        return string.Concat(value, " ", unit);
    }

    private static (string Value, string Unit) SplitAuto(double bytesPerSecond, RateFamily family)
    {
        string baseLabel = RateUnits.Label(family, 0);

        if (!IsUsable(bytesPerSecond))
        {
            return ("0", baseLabel);
        }

        double factor = RateUnits.StepFactor(family);
        double value = bytesPerSecond / RateUnits.BytesPerUnit(family, 0);

        int step = 0;
        while (value >= factor && step < RateUnits.MaxStep)
        {
            value /= factor;
            step++;
        }
        string text = step == 0
            ? value.ToString("F0", CultureInfo.InvariantCulture)
            : Scaled(value);

        return (text, RateUnits.Label(family, step));
    }

    private static bool IsUsable(double bytesPerSecond) =>
        bytesPerSecond > 0 && !double.IsNaN(bytesPerSecond) && !double.IsInfinity(bytesPerSecond);

    private static string Scaled(double value) => value switch
    {
        < 10 => value.ToString("F2", CultureInfo.InvariantCulture),
        < 100 => value.ToString("F1", CultureInfo.InvariantCulture),
        _ => value.ToString("F0", CultureInfo.InvariantCulture),
    };

    private static string Fixed(double value)
    {
        double magnitude = Math.Abs(value);

        if (magnitude >= 1000)
        {
            return value.ToString("N0", CultureInfo.InvariantCulture);
        }

        if (magnitude >= 100)
        {
            return value.ToString("F1", CultureInfo.InvariantCulture);
        }

        if (magnitude >= 1)
        {
            return value.ToString("F2", CultureInfo.InvariantCulture);
        }
        string text = value.ToString("F3", CultureInfo.InvariantCulture);
        return text == "0.000" ? "0" : text;
    }
}
