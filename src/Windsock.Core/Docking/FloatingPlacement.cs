using Windsock.Core.Settings;

namespace Windsock.Core.Docking;

/// <summary>
/// Where a meter in its own window goes: its taskbar's spot until someone
/// moves it, and wherever they put it after that.
/// </summary>
public static class FloatingPlacement
{
    /// <summary>
    /// How near its own spot a dragged meter must be dropped for the move to
    /// be forgotten, in device-independent pixels. Dropped that close, it was
    /// put back, and goes back to following its taskbar.
    /// </summary>
    public const double HomeReach = 16;

    /// <summary>
    /// Where a remembered spot puts a meter of this size now: kept inside
    /// whichever monitor holds its middle, or nowhere when no monitor does -
    /// the display it was left on has gone.
    /// </summary>
    public static PixelRect? Restore(MeterSpot spot, int width, int height, IReadOnlyList<PixelRect> monitors)
    {
        ArgumentNullException.ThrowIfNull(spot);
        ArgumentNullException.ThrowIfNull(monitors);

        PixelRect wanted = PixelRect.FromSize(spot.X, spot.Y, Math.Max(1, width), Math.Max(1, height));

        foreach (PixelRect monitor in monitors)
        {
            if (monitor.Contains(wanted.CentreX, wanted.CentreY))
            {
                return wanted.Within(monitor);
            }
        }

        return null;
    }

    /// <summary>
    /// Whether a meter dropped at <paramref name="dropped"/> was put back on
    /// its own spot.
    /// </summary>
    public static bool IsHome(PixelRect dropped, PixelRect home, int dpi)
    {
        int reach = (int)Math.Round(HomeReach * Math.Max(96, dpi) / 96d, MidpointRounding.AwayFromZero);

        return Math.Abs(dropped.Left - home.Left) <= reach && Math.Abs(dropped.Top - home.Top) <= reach;
    }

    /// <summary>
    /// Whether a meter at its taskbar's spot should be on screen: not when
    /// its auto-hiding bar has slid away and taken the spot off the monitor
    /// with it. A meter left on screen alone, over a bar that is no longer
    /// there, is the thing this is here to prevent.
    /// </summary>
    public static bool IsShown(PixelRect spot, PixelRect monitor) => monitor.Contains(spot);
}
