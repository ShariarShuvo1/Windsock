namespace Windsock.Core.Appearance;

/// <summary>
/// How much larger each size of text is drawn for Windows' Text size setting
/// (Settings › Accessibility › Text size, 100 to 225 percent).
/// </summary>
/// <remarks>
/// <para>
/// WPF does nothing with that setting by itself, so Windsock scales its own
/// type ramp. Not by one factor: Microsoft says "the larger text is, the less
/// it's affected by scaling", because a heading is already readable and growing
/// it as much as body text only pushes everything else off the screen. The
/// exact curve Windows uses is not published, so this one is plain: text up to
/// <see cref="FullUpTo"/> grows by the whole factor, text from
/// <see cref="HalfFrom"/> up grows by half of it, and sizes between are
/// blended in a straight line.
/// </para>
/// <para>
/// Icons, charts, spacing and the taskbar meter are never scaled by this: it is
/// text size, not zoom, and the meter has a bar of fixed thickness to fit.
/// </para>
/// </remarks>
public static class TextScaling
{
    /// <summary>The smallest factor Windows reports: text at its normal size.</summary>
    public const double Normal = 1.0;

    /// <summary>The largest factor Windows reports.</summary>
    public const double Largest = 2.25;

    /// <summary>Text this size and smaller grows by the whole factor.</summary>
    public const double FullUpTo = 14;

    /// <summary>Text this size and larger grows by half the factor.</summary>
    public const double HalfFrom = 32;

    /// <summary>
    /// The factor Windows reported, held to the range it documents, with
    /// anything unreadable taken as normal.
    /// </summary>
    public static double Settle(double factor) =>
        double.IsFinite(factor) ? Math.Clamp(factor, Normal, Largest) : Normal;

    /// <summary>The size to draw text of <paramref name="size"/> at, for a Text size factor.</summary>
    public static double Scale(double size, double factor)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(size);

        double growth = Settle(factor) - Normal;

        if (growth <= 0)
        {
            return size;
        }

        double share = size <= FullUpTo
            ? 1.0
            : size >= HalfFrom
                ? 0.5
                : 1.0 - (0.5 * (size - FullUpTo) / (HalfFrom - FullUpTo));

        // Rounded to a quarter of a device-independent pixel, so the ramp does not
        // carry long fractions into layout.
        return Math.Round(size * (1 + (growth * share)) * 4, MidpointRounding.AwayFromZero) / 4;
    }
}
