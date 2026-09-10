namespace Windsock.Core.Formatting;

/// <summary>
/// Picks a font size for a live readout from how many characters it holds,
/// with hysteresis so the size does not oscillate.
/// </summary>
public sealed class ReadoutSizer(double maximumSize)
{
    private static readonly int[] UpperBounds = [6, 8, 11];
    private static readonly double[] Factors = [1.0, 0.80, 0.64, 0.52];
    private const int ReleaseMargin = 1;
    private int _tier;

    /// <summary>Size used when the text is short enough to need no reduction.</summary>
    public double MaximumSize { get; } = maximumSize;

    /// <summary>The size currently in effect.</summary>
    public double Current => Math.Round(MaximumSize * Factors[_tier]);

    /// <summary>Updates the size for a text of <paramref name="length"/> characters.</summary>
    public double Update(int length)
    {
        int desired = TierFor(length);

        if (desired > _tier)
        {
            // Too long for the current size: shrink at once, it has to fit.
            _tier = desired;
        }
        else if (desired < _tier && length <= ReleaseLength(_tier))
        {
            _tier = desired;
        }

        return Current;
    }

    private static int TierFor(int length)
    {
        for (int tier = 0; tier < UpperBounds.Length; tier++)
        {
            if (length <= UpperBounds[tier])
            {
                return tier;
            }
        }

        return UpperBounds.Length;
    }

    private static int ReleaseLength(int tier) =>
        tier >= 1 && tier <= UpperBounds.Length
            ? UpperBounds[tier - 1] - ReleaseMargin
            : int.MaxValue;
}
