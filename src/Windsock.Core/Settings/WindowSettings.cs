namespace Windsock.Core.Settings;

/// <summary>Where the main window was last, on one monitor, in that monitor's physical pixels.</summary>
public sealed class WindowPlace
{
    /// <summary>The monitor's device name, as Windows gives it: <c>\\.\DISPLAY1</c>.</summary>
    public string Monitor { get; set; } = string.Empty;

    /// <summary>The window's left edge, in screen pixels.</summary>
    public int Left { get; set; }

    /// <summary>The window's top edge, in screen pixels.</summary>
    public int Top { get; set; }

    /// <summary>The window's width, in pixels.</summary>
    public int Width { get; set; }

    /// <summary>The window's height, in pixels.</summary>
    public int Height { get; set; }

    /// <summary>The monitor's DPI when the place was kept, so a monitor scaled since keeps the window's size in inches.</summary>
    public int Dpi { get; set; } = 96;

    /// <summary>Whether the window was maximised there. Its place is then where it goes back to when restored.</summary>
    public bool Maximised { get; set; }
}

/// <summary>
/// Which page the window opens on and where it opens.
/// </summary>
public sealed class WindowSettings
{
    /// <summary>The value of <see cref="OpenOn"/> that means the page last open.</summary>
    public const string LastPageChoice = "";

    /// <summary>How many monitors' places are kept.</summary>
    public const int MostPlaces = 8;

    /// <summary>
    /// The page the window opens on, by its link name ("overview",
    /// "history"), or empty for the page that was open when it last closed.
    /// </summary>
    public string OpenOn { get; set; } = LastPageChoice;

    /// <summary>The page open when the window last closed, by its link name.</summary>
    public string LastPage { get; set; } = string.Empty;

    /// <summary>The monitor the window was on when it last closed.</summary>
    public string LastMonitor { get; set; } = string.Empty;

    /// <summary>Where the window was on each monitor it has been on, newest first.</summary>
    public List<WindowPlace> Places
    {
        get => field ??= [];
        set => field = value ?? [];
    }

    /// <summary>
    /// The page to open: the one chosen, or the last one when none is chosen,
    /// or nothing when neither is known, which leaves the window's own first
    /// page. Names that cannot be a page's are ignored.
    /// </summary>
    public string? PageToOpen()
    {
        string wanted = string.IsNullOrEmpty(OpenOn) ? LastPage : OpenOn;
        return Navigation.PageLink.IsName(wanted) ? wanted : null;
    }

    /// <summary>Keeps where the window is on a monitor, replacing what was kept for it before.</summary>
    public void Remember(WindowPlace place)
    {
        ArgumentNullException.ThrowIfNull(place);

        Places.RemoveAll(each => string.Equals(each.Monitor, place.Monitor, StringComparison.OrdinalIgnoreCase));
        Places.Insert(0, place);

        if (Places.Count > MostPlaces)
        {
            Places.RemoveRange(MostPlaces, Places.Count - MostPlaces);
        }

        LastMonitor = place.Monitor;
    }

    /// <summary>A copy taking another machine's choice of page, and keeping this one's places.</summary>
    public WindowSettings ForThisMachine(WindowSettings here)
    {
        ArgumentNullException.ThrowIfNull(here);

        return new WindowSettings
        {
            OpenOn = OpenOn,
            LastPage = here.LastPage,
            LastMonitor = here.LastMonitor,
            Places = [.. here.Places],
        };
    }
}
