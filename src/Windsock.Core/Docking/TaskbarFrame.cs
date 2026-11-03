namespace Windsock.Core.Docking;

/// <summary>Which edge of its monitor a taskbar runs along.</summary>
public enum TaskbarEdge
{
    Bottom,

    Top,

    Left,

    Right,
}

/// <summary>Which taskbar Explorer is drawing.</summary>
public enum TaskbarKind
{
    /// <summary>
    /// Windows 11's own, drawn in XAML. Its buttons are not windows, so they
    /// can only be found through UI Automation, and nothing on it moves aside.
    /// </summary>
    Modern,

    /// <summary>
    /// The Windows 10 taskbar, which ExplorerPatcher and StartAllBack bring
    /// back. Its task buttons live in a band that is a window of its own, and
    /// the buttons lay themselves out inside whatever width the band has.
    /// </summary>
    Classic,
}

/// <summary>What a thing standing on the taskbar is, for deciding where the meter may go.</summary>
public enum ObstacleKind
{
    /// <summary>The notification area, the clock and the show-desktop sliver.</summary>
    Tray,

    /// <summary>
    /// A button that stays put while apps come and go: Start, Search, Task
    /// View, Widgets.
    /// </summary>
    Fixed,

    /// <summary>A pinned or running app's button.</summary>
    Task,
}

/// <summary>Something on the taskbar the meter should not cover.</summary>
public readonly record struct TaskbarObstacle(PixelRect Rect, ObstacleKind Kind);

/// <summary>
/// Everything known about one taskbar at one instant, every rectangle in
/// screen pixels taken at the same moment.
/// </summary>
public sealed record TaskbarFrame
{
    /// <summary>The taskbar window itself.</summary>
    public required PixelRect Bar { get; init; }

    /// <summary>The whole monitor the taskbar belongs to.</summary>
    public required PixelRect Monitor { get; init; }

    /// <summary>The part of the monitor windows may use.</summary>
    public PixelRect WorkArea { get; init; }

    /// <summary>Dots per inch of the taskbar, 96 at 100%.</summary>
    public int Dpi { get; init; } = 96;

    /// <summary>Whether this is the main taskbar, the one with the notification area.</summary>
    public bool IsPrimary { get; init; }

    /// <summary>Which taskbar Explorer is drawing.</summary>
    public TaskbarKind Kind { get; init; }

    /// <summary>Whether Windows hides the taskbar until the pointer reaches it.</summary>
    public bool AutoHide { get; init; }

    /// <summary>
    /// The notification area, or on another monitor's taskbar its clock,
    /// when a window marks it.
    /// </summary>
    public PixelRect? Tray { get; init; }

    /// <summary>The band the task buttons are laid out in, on a classic taskbar.</summary>
    public PixelRect? TaskBand { get; init; }

    /// <summary>Buttons found on the bar, when anything could look.</summary>
    public IReadOnlyList<TaskbarObstacle> Obstacles { get; init; } = [];

    /// <summary>Which edge of its monitor the bar runs along.</summary>
    public TaskbarEdge Edge => TaskbarGeometry.EdgeOf(Bar, Monitor);

    /// <summary>Whether the bar runs down the side of the monitor.</summary>
    public bool IsVertical => Edge is TaskbarEdge.Left or TaskbarEdge.Right;

    /// <summary>How thick the bar is, across its length, in pixels.</summary>
    public int Thickness => IsVertical ? Bar.Width : Bar.Height;

    /// <summary>Physical pixels to one device-independent pixel.</summary>
    public double Scale => Math.Max(96, Dpi) / 96d;

    /// <summary>
    /// A length in device-independent pixels, in this bar's pixels; a half
    /// rounds up, so 3 at 150% is 5, not the 4 that rounding to even gives.
    /// </summary>
    public int Pixels(double dips) => (int)Math.Round(dips * Scale, MidpointRounding.AwayFromZero);
}
