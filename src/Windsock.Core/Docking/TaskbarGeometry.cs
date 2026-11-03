namespace Windsock.Core.Docking;

/// <summary>
/// Reads a taskbar's rectangle: which edge it is on, and whether it is a
/// taskbar's shape at all.
/// </summary>
public static class TaskbarGeometry
{
    /// <summary>
    /// The edge a bar runs along, from its shape and where it sits.
    /// </summary>
    /// <remarks>
    /// Worked out from the rectangle rather than asked of the shell, because
    /// the shell answers only for the main taskbar and every other monitor's
    /// bar needs the same answer. A bar wider than it is tall runs along the
    /// top or the bottom; which one is whichever its middle is nearer, which
    /// still holds for an auto-hiding bar that has slid almost all the way
    /// off its monitor.
    /// </remarks>
    public static TaskbarEdge EdgeOf(PixelRect bar, PixelRect monitor)
    {
        if (bar.Width >= bar.Height)
        {
            int above = bar.CentreY - monitor.Top;
            int below = monitor.Bottom - bar.CentreY;

            return above < below ? TaskbarEdge.Top : TaskbarEdge.Bottom;
        }

        int left = bar.CentreX - monitor.Left;
        int right = monitor.Right - bar.CentreX;

        return left < right ? TaskbarEdge.Left : TaskbarEdge.Right;
    }

    /// <summary>
    /// Whether a rectangle looks like a taskbar at all: running along most of
    /// one side of its monitor and thin across it. Anything else - a bar
    /// mid-rebuild, a replacement drawing something new - is not docked into,
    /// because a meter placed by rules written for a bar would land anywhere.
    /// </summary>
    public static bool IsRecognised(PixelRect bar, PixelRect monitor)
    {
        if (bar.IsEmpty || monitor.IsEmpty)
        {
            return false;
        }

        bool lying = bar.Width >= bar.Height;
        int length = lying ? bar.Width : bar.Height;
        int thickness = lying ? bar.Height : bar.Width;
        int side = lying ? monitor.Width : monitor.Height;
        int across = lying ? monitor.Height : monitor.Width;

        return thickness >= 8 && length * 2 >= side && thickness * 3 <= across;
    }
}
