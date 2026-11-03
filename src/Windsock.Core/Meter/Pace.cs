namespace Windsock.Core.Meter;

/// <summary>
/// How lively the animated windsock is for a rate: still when nothing moves,
/// fluttering harder as more does.
/// </summary>
/// <remarks>
/// The rate is taken on a logarithmic scale, because a link idles at a few
/// kilobytes and runs at hundreds of megabytes, and a sock that only stirred at
/// the top of that range would never move. Ten frames a second at most keeps
/// it cheap: a frame is one small geometry and nothing else.
/// </remarks>
public static class Pace
{
    /// <summary>The most frames the windsock draws a second.</summary>
    public const double Fastest = 10;

    /// <summary>Below this many bytes a second, the windsock hangs still.</summary>
    public const double Calm = 1024;

    /// <summary>How many times a second the windsock flutters at a rate.</summary>
    public static double FlutterPerSecond(double bytesPerSecond)
    {
        if (!double.IsFinite(bytesPerSecond) || bytesPerSecond < Calm)
        {
            return 0;
        }

        // 1 KB/s is a lazy flap a second; each tenfold rise adds 1.5.
        double flaps = 1 + (1.5 * Math.Log10(bytesPerSecond / Calm));

        return Math.Clamp(flaps, 0, Fastest);
    }

    /// <summary>
    /// How far the sock hangs down, from 0 (straight out in a gale) to 1 (limp
    /// against the pole).
    /// </summary>
    public static double Droop(double bytesPerSecond) =>
        1 - (FlutterPerSecond(bytesPerSecond) / Fastest);
}
