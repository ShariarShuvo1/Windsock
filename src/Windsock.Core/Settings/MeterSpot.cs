namespace Windsock.Core.Settings;

/// <summary>Which taskbars the meter goes on.</summary>
public enum MeterMonitors
{
    /// <summary>The main taskbar only, the one with the notification area.</summary>
    Main,

    /// <summary>Every monitor's taskbar.</summary>
    Every,

    /// <summary>One monitor the reader picked.</summary>
    Chosen,
}

/// <summary>What the meter does while something runs full screen in front of it.</summary>
public enum MeterFullScreen
{
    /// <summary>Gets out of the way on that monitor until the full-screen app goes.</summary>
    Hide,

    /// <summary>Stays where it is.</summary>
    Stay,
}

/// <summary>What the meter does inside a Remote Desktop session.</summary>
public enum MeterRemote
{
    /// <summary>
    /// Shows, and says whose readings they are: the remote computer's, not
    /// the one the reader is sitting at.
    /// </summary>
    Name,

    /// <summary>Is not shown at all while Windsock runs in a remote session.</summary>
    Hide,
}

/// <summary>
/// Where someone put a floating meter by hand, on one monitor.
/// </summary>
public sealed class MeterSpot
{
    /// <summary>
    /// The monitor the meter belongs to, by the name Windows gives it, such as
    /// <c>\\.\DISPLAY2</c>.
    /// </summary>
    public string Monitor
    {
        get;
        set => field = value ?? string.Empty;
    } = string.Empty;

    /// <summary>Left edge of the meter, in screen pixels.</summary>
    public int X { get; set; }

    /// <summary>Top edge of the meter, in screen pixels.</summary>
    public int Y { get; set; }
}
