using Windsock.Core.Docking;
using Windsock.Core.Settings;

namespace Windsock.Core.Windowing;

/// <summary>One monitor as the window sees it: its name, its whole area, its work area and its DPI.</summary>
/// <param name="Name">Windows' device name: <c>\\.\DISPLAY1</c>.</param>
/// <param name="Bounds">The whole monitor, in screen pixels.</param>
/// <param name="Work">The part not taken by taskbars, in screen pixels.</param>
/// <param name="Primary">Whether it is the main monitor.</param>
/// <param name="Dpi">Its DPI.</param>
public sealed record ScreenArea(string Name, PixelRect Bounds, PixelRect Work, bool Primary, int Dpi);

/// <summary>
/// Puts the main window back where it was, on whichever monitor that was, and
/// never anywhere it cannot be seen.
/// </summary>
/// <remarks>
/// <para>
/// A place is kept per monitor, so a laptop that spends its days on a desk
/// with a second screen and its evenings without opens where it was on each.
/// The monitor the window was last on wins when it is there; otherwise the
/// main monitor's own place, then any monitor that is there; otherwise nothing,
/// and the window opens centred as it always did.
/// </para>
/// <para>
/// A kept place is held inside the monitor's work area: smaller if the monitor
/// is smaller now, moved in if its resolution changed, and scaled by the change
/// in DPI so a monitor set to a larger scale since does not shrink the window.
/// </para>
/// </remarks>
public static class WindowFit
{
    /// <summary>
    /// The place to open at and the monitor it is on, or nothing when no kept
    /// place belongs to a monitor that is there.
    /// </summary>
    public static (WindowPlace Place, ScreenArea Monitor)? Choose(WindowSettings window, IReadOnlyList<ScreenArea> monitors)
    {
        ArgumentNullException.ThrowIfNull(window);
        ArgumentNullException.ThrowIfNull(monitors);

        if (monitors.Count == 0 || window.Places.Count == 0)
        {
            return null;
        }

        ScreenArea? Find(string name) =>
            monitors.FirstOrDefault(each => string.Equals(each.Name, name, StringComparison.OrdinalIgnoreCase));

        WindowPlace? Kept(string name) =>
            window.Places.FirstOrDefault(each => string.Equals(each.Monitor, name, StringComparison.OrdinalIgnoreCase)
                && each.Width > 0 && each.Height > 0);

        if (Find(window.LastMonitor) is { } last && Kept(last.Name) is { } there)
        {
            return (there, last);
        }

        if (monitors.FirstOrDefault(each => each.Primary) is { } primary && Kept(primary.Name) is { } main)
        {
            return (main, primary);
        }

        foreach (WindowPlace place in window.Places)
        {
            if (place.Width > 0 && place.Height > 0 && Find(place.Monitor) is { } monitor)
            {
                return (place, monitor);
            }
        }

        return null;
    }

    /// <summary>
    /// A kept place, inside a monitor's work area: scaled by the change in DPI,
    /// no larger than the work area, no smaller than the window's minimum, and
    /// moved in as little as it can be.
    /// </summary>
    /// <param name="place">Where the window was.</param>
    /// <param name="monitor">The monitor it goes back on.</param>
    /// <param name="minimumWidth">The window's minimum width, in device-independent pixels.</param>
    /// <param name="minimumHeight">The window's minimum height, in device-independent pixels.</param>
    public static PixelRect Fit(WindowPlace place, ScreenArea monitor, double minimumWidth, double minimumHeight)
    {
        ArgumentNullException.ThrowIfNull(place);
        ArgumentNullException.ThrowIfNull(monitor);

        PixelRect work = monitor.Work.IsEmpty ? monitor.Bounds : monitor.Work;
        double scale = place.Dpi > 0 && monitor.Dpi > 0 ? (double)monitor.Dpi / place.Dpi : 1.0;
        double pixelsPerDip = monitor.Dpi > 0 ? monitor.Dpi / 96.0 : 1.0;

        int width = (int)Math.Round(place.Width * scale);
        int height = (int)Math.Round(place.Height * scale);

        int leastWidth = (int)Math.Ceiling(minimumWidth * pixelsPerDip);
        int leastHeight = (int)Math.Ceiling(minimumHeight * pixelsPerDip);

        width = Math.Min(Math.Max(width, leastWidth), work.Width);
        height = Math.Min(Math.Max(height, leastHeight), work.Height);

        // A monitor moved in the arrangement since can leave the kept corner
        // off it altogether; the window is then centred on it instead.
        PixelRect kept = PixelRect.FromSize(place.Left, place.Top, width, height);

        if (!kept.Overlaps(monitor.Bounds))
        {
            kept = PixelRect.FromSize(work.Left + ((work.Width - width) / 2), work.Top + ((work.Height - height) / 2), width, height);
        }

        return kept.Within(work);
    }
}
