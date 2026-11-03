namespace Windsock.Core.Docking;

/// <summary>Where the meter goes on one taskbar, and what it found there.</summary>
/// <param name="Screen">The meter's rectangle, in screen pixels.</param>
/// <param name="Crowded">
/// Whether the meter covers buttons it could not avoid: the bar had no room
/// left between its apps and the notification area.
/// </param>
public readonly record struct DockSpot(PixelRect Screen, bool Crowded)
{
    /// <summary>The same rectangle in the bar's own coordinates, which a child window is placed in.</summary>
    public PixelRect On(PixelRect bar) => Screen.RelativeTo(bar);

    /// <summary>
    /// The same rectangle in the coordinates of a parent that may be mirrored,
    /// as a right-to-left Windows mirrors the taskbar.
    /// </summary>
    public PixelRect On(PixelRect bar, bool mirrored) => DockGeometry.InParent(Screen, bar, mirrored);
}

/// <summary>
/// Works out where the meter sits on a taskbar of any shape, on any edge.
/// </summary>
/// <remarks>
/// The meter hangs from the notification area, on the side facing the apps:
/// to its left on a bar along the top or bottom, above it on a bar down a
/// side, and to its right where a right-to-left Windows has put the
/// notification area at the left. Anything that never moves and stands right
/// beside the notification area - the Widgets button on a left-aligned
/// Windows 11 taskbar - counts as part of it, so the meter hangs from that
/// instead of covering it. Lengths are written in device-independent pixels
/// and scaled to the bar's own DPI, so a bar at 150% gets a meter at 150%.
/// </remarks>
public static class DockGeometry
{
    /// <summary>Space between the meter and the notification area.</summary>
    public const double Gap = 6;

    /// <summary>Space left between the meter and each long edge of the bar.</summary>
    public const double Inset = 3;

    /// <summary>
    /// How close a button that never moves must stand to the notification
    /// area to count as part of it.
    /// </summary>
    public const double Adjacent = 12;

    /// <summary>
    /// Room kept for the clock at the end of another monitor's Windows 11
    /// taskbar, when nothing could see how wide it is. A clock with the date
    /// under it and the show-desktop sliver measure 75 to 90 pixels at 100%.
    /// </summary>
    public const double UnseenClock = 96;

    /// <summary>
    /// The least of a classic task band that is left when it is narrowed to
    /// make room: about two buttons' worth.
    /// </summary>
    public const double SmallestBand = 96;

    /// <summary>Room left between a narrowed task band and the meter.</summary>
    public const double BandGap = 2;

    /// <summary>
    /// The thickness the meter has: the bar's, less a small inset at each
    /// long edge.
    /// </summary>
    public static int Across(TaskbarFrame frame)
    {
        ArgumentNullException.ThrowIfNull(frame);

        return Math.Max(1, frame.Thickness - (2 * frame.Pixels(Inset)));
    }

    /// <summary>
    /// Where a meter <paramref name="length"/> pixels long sits on the bar.
    /// </summary>
    public static DockSpot Place(TaskbarFrame frame, int length)
    {
        ArgumentNullException.ThrowIfNull(frame);

        Axis axis = new(frame.IsVertical);
        PixelRect bar = frame.Bar;
        int barStart = axis.Start(bar);
        int barEnd = axis.End(bar);
        int along = Math.Max(1, length);
        int gap = frame.Pixels(Gap);
        (int anchor, bool atStart) = Anchor(frame, axis);

        int start = atStart ? anchor + gap : anchor - gap - along;
        bool squeezed = false;

        if (start < barStart)
        {
            start = barStart;
            squeezed = true;
        }
        else if (start + along > barEnd)
        {
            start = Math.Max(barStart, barEnd - along);
            squeezed = true;
        }

        int inset = frame.Pixels(Inset);
        int across = Across(frame);
        PixelRect spot = axis.Rect(start, along, axis.AcrossStart(bar) + inset, across);

        return new DockSpot(spot, squeezed || Covers(frame, axis, spot, anchor, atStart));
    }

    /// <summary>
    /// The end of the notification area facing the apps, and whether the
    /// notification area is at the start of the bar rather than its end.
    /// </summary>
    private static (int Anchor, bool AtStart) Anchor(TaskbarFrame frame, Axis axis)
    {
        PixelRect? tray = TrayOf(frame);

        if (tray is not { } found)
        {
            int end = axis.End(frame.Bar);
            int kept = frame is { Kind: TaskbarKind.Modern, IsPrimary: false }
                ? frame.Pixels(UnseenClock)
                : 0;

            return (Grow(frame, axis, end - kept, atStart: false), false);
        }

        bool atStart = axis.Middle(found) < axis.Middle(frame.Bar);
        int anchor = atStart ? axis.End(found) : axis.Start(found);

        return (Grow(frame, axis, anchor, atStart), atStart);
    }

    /// <summary>
    /// Takes in every button that never moves and stands right beside the
    /// notification area, one after another.
    /// </summary>
    private static int Grow(TaskbarFrame frame, Axis axis, int anchor, bool atStart)
    {
        int near = frame.Pixels(Adjacent);
        bool grew = true;

        for (int round = 0; grew && round < 16; round++)
        {
            grew = false;

            foreach (TaskbarObstacle obstacle in frame.Obstacles)
            {
                if (obstacle.Kind == ObstacleKind.Task || obstacle.Rect.IsEmpty)
                {
                    continue;
                }

                int start = axis.Start(obstacle.Rect);
                int end = axis.End(obstacle.Rect);

                if (!atStart && end > anchor - near && start < anchor)
                {
                    anchor = start;
                    grew = true;
                }
                else if (atStart && start < anchor + near && end > anchor)
                {
                    anchor = end;
                    grew = true;
                }
            }
        }

        return anchor;
    }

    /// <summary>
    /// The notification area: the window that marks it, widened by any of
    /// its buttons UI Automation found outside that window.
    /// </summary>
    private static PixelRect? TrayOf(TaskbarFrame frame)
    {
        PixelRect? tray = frame.Tray is { IsEmpty: false } marked ? marked : null;

        foreach (TaskbarObstacle obstacle in frame.Obstacles)
        {
            if (obstacle.Kind != ObstacleKind.Tray || obstacle.Rect.IsEmpty)
            {
                continue;
            }

            tray = tray is { } widened
                ? new PixelRect(
                    Math.Min(widened.Left, obstacle.Rect.Left),
                    Math.Min(widened.Top, obstacle.Rect.Top),
                    Math.Max(widened.Right, obstacle.Rect.Right),
                    Math.Max(widened.Bottom, obstacle.Rect.Bottom))
                : obstacle.Rect;
        }

        return tray;
    }

    /// <summary>
    /// Whether the meter covers a button that is not part of the notification
    /// area's group.
    /// </summary>
    private static bool Covers(TaskbarFrame frame, Axis axis, PixelRect spot, int anchor, bool atStart)
    {
        foreach (TaskbarObstacle obstacle in frame.Obstacles)
        {
            PixelRect rect = obstacle.Rect;
            bool grouped = atStart ? axis.End(rect) <= anchor : axis.Start(rect) >= anchor;

            if (!grouped && axis.Start(rect) < axis.End(spot) && axis.End(rect) > axis.Start(spot))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// A rectangle given in screen pixels, in the client coordinates of a
    /// parent window whose client area is all of it.
    /// </summary>
    /// <remarks>
    /// A right-to-left Windows (Arabic, Hebrew) mirrors the taskbar: the parent
    /// carries WS_EX_LAYOUTRTL, and an x given to SetWindowPos counts from its
    /// right edge. Left alone, a meter meant beside the notification area lands
    /// at the far end of the bar, and a narrowed band walks off the screen.
    /// </remarks>
    public static PixelRect InParent(PixelRect screen, PixelRect parent, bool mirrored)
    {
        int left = mirrored ? parent.Right - screen.Right : screen.Left - parent.Left;

        return PixelRect.FromSize(left, screen.Top - parent.Top, screen.Width, screen.Height);
    }

    /// <summary>
    /// How far a classic taskbar's task band should reach so its buttons are
    /// laid out clear of the meter, or nothing when it need not move or would
    /// be left too small to be any use.
    /// </summary>
    /// <param name="frame">The bar, with its task band as Explorer last laid it out.</param>
    /// <param name="meter">Where the meter is, in screen pixels.</param>
    public static PixelRect? Narrowed(TaskbarFrame frame, PixelRect meter)
    {
        ArgumentNullException.ThrowIfNull(frame);

        return frame.TaskBand is { } band ? Narrowed(frame, band, meter) : null;
    }

    /// <summary>
    /// The same, for a task band given apart from the frame: as Explorer laid
    /// it out, before it was narrowed.
    /// </summary>
    public static PixelRect? Narrowed(TaskbarFrame frame, PixelRect band, PixelRect meter)
    {
        ArgumentNullException.ThrowIfNull(frame);

        if (frame.Kind != TaskbarKind.Classic || band.IsEmpty)
        {
            return null;
        }

        Axis axis = new(frame.IsVertical);

        if (axis.Start(band) >= axis.End(meter) || axis.End(band) <= axis.Start(meter))
        {
            return null;
        }

        int gap = frame.Pixels(BandGap);
        int least = frame.Pixels(SmallestBand);
        bool meterFirst = axis.Middle(meter) < axis.Middle(band);

        if (meterFirst)
        {
            int start = axis.End(meter) + gap;

            return axis.End(band) - start < least ? null : axis.WithStart(band, start);
        }

        int end = axis.Start(meter) - gap;

        return end - axis.Start(band) < least ? null : axis.WithEnd(band, end);
    }

    /// <summary>
    /// The bottom corner of a monitor's working area, for a floating meter on
    /// a monitor with no taskbar to sit on.
    /// </summary>
    public static PixelRect Corner(PixelRect workArea, int width, int height, int dpi)
    {
        int margin = (int)Math.Round(8 * Math.Max(96, dpi) / 96d, MidpointRounding.AwayFromZero);

        return PixelRect.FromSize(
            workArea.Right - margin - width,
            workArea.Bottom - margin - height,
            width,
            height);
    }

    /// <summary>Reads rectangles along a bar, whichever way the bar runs.</summary>
    private readonly record struct Axis(bool Vertical)
    {
        public int Start(PixelRect rect) => Vertical ? rect.Top : rect.Left;

        public int End(PixelRect rect) => Vertical ? rect.Bottom : rect.Right;

        public int Middle(PixelRect rect) => Vertical ? rect.CentreY : rect.CentreX;

        public int AcrossStart(PixelRect rect) => Vertical ? rect.Left : rect.Top;

        public PixelRect Rect(int start, int length, int acrossStart, int across) => Vertical
            ? PixelRect.FromSize(acrossStart, start, across, length)
            : PixelRect.FromSize(start, acrossStart, length, across);

        public PixelRect WithStart(PixelRect rect, int start) => Vertical
            ? rect with { Top = start }
            : rect with { Left = start };

        public PixelRect WithEnd(PixelRect rect, int end) => Vertical
            ? rect with { Bottom = end }
            : rect with { Right = end };
    }
}
