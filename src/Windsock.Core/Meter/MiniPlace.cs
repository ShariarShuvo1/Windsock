using Windsock.Core.Docking;
using Windsock.Core.Settings;
using Windsock.Core.Windowing;

namespace Windsock.Core.Meter;

/// <summary>
/// Where the compact window opens: where it was left, if that is still on a
/// monitor, else the main monitor's lower right corner above the taskbar.
/// </summary>
/// <remarks>
/// A place is kept in screen pixels with the monitor's name, as the floating
/// meter's is. It is honoured when its corner lies in some monitor's working
/// area (the monitor may have been renamed by a reconnect without moving) and
/// pulled in to fit there, so a window left half off an edge, or kept from a
/// larger desktop, always opens whole.
/// </remarks>
public static class MiniPlace
{
    /// <summary>The gap from the working area's edges for the default place, at 100% scale.</summary>
    public const int Margin = 16;

    /// <summary>The window's rectangle, in screen pixels.</summary>
    /// <param name="spot">Where it was left, or nothing.</param>
    /// <param name="monitors">The monitors there are now, the main one marked.</param>
    /// <param name="width">Its width, in pixels.</param>
    /// <param name="height">Its height, in pixels.</param>
    public static PixelRect Choose(MeterSpot? spot, IReadOnlyList<ScreenArea> monitors, int width, int height)
    {
        ArgumentNullException.ThrowIfNull(monitors);

        width = Math.Max(1, width);
        height = Math.Max(1, height);

        if (monitors.Count == 0)
        {
            return PixelRect.FromSize(spot?.X ?? 0, spot?.Y ?? 0, width, height);
        }

        if (spot is not null && Holding(spot, monitors) is { } kept)
        {
            return Within(PixelRect.FromSize(spot.X, spot.Y, width, height), kept.Work);
        }

        ScreenArea main = monitors.FirstOrDefault(each => each.Primary) ?? monitors[0];
        int margin = (int)Math.Round(Margin * Math.Max(96, main.Dpi) / 96.0);

        return Within(
            PixelRect.FromSize(main.Work.Right - width - margin, main.Work.Bottom - height - margin, width, height),
            main.Work);
    }

    /// <summary>The monitor a kept place is on: the one named, else any whose working area holds its corner.</summary>
    private static ScreenArea? Holding(MeterSpot spot, IReadOnlyList<ScreenArea> monitors)
    {
        ScreenArea? named = monitors.FirstOrDefault(each =>
            string.Equals(each.Name, spot.Monitor, StringComparison.OrdinalIgnoreCase)
            && each.Work.Contains(spot.X, spot.Y));

        return named ?? monitors.FirstOrDefault(each => each.Work.Contains(spot.X, spot.Y));
    }

    /// <summary>Moves a rectangle the least distance that puts it inside an area, its top left winning when it cannot fit.</summary>
    private static PixelRect Within(PixelRect rect, PixelRect area)
    {
        int left = Math.Max(area.Left, Math.Min(rect.Left, area.Right - rect.Width));
        int top = Math.Max(area.Top, Math.Min(rect.Top, area.Bottom - rect.Height));

        return PixelRect.FromSize(left, top, rect.Width, rect.Height);
    }
}
