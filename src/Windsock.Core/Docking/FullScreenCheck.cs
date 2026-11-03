namespace Windsock.Core.Docking;

/// <summary>What is known about the window in front, for deciding whether it is full screen.</summary>
/// <param name="Rect">Its rectangle, in screen pixels.</param>
/// <param name="ClassName">Its window class.</param>
/// <param name="Framed">
/// Whether it has a resizing or dialog frame, or is a tool window. An ordinary
/// window maximised over an auto-hiding taskbar covers the whole monitor too,
/// frame and all, and is not full screen; a full-screen game, video or
/// browser has no frame. The same test Chromium makes.
/// </param>
public readonly record struct FrontWindow(PixelRect Rect, string ClassName, bool Framed);

/// <summary>
/// Decides whether something is running full screen in front of a monitor:
/// a game, a video, a slide show.
/// </summary>
/// <remarks>
/// Two signals, because neither is enough alone. The shell's own answer
/// (SHQueryUserNotificationState) knows about exclusive Direct3D and
/// presentation mode but says nothing about which monitor, and misses most
/// borderless games. The window in front filling its monitor catches those,
/// and names the monitor. The shell's own windows fill monitors as a matter of
/// course - the desktop, Task View, the Start menu's host - and are left out.
/// </remarks>
public static class FullScreenCheck
{
    private static readonly HashSet<string> ShellClasses = new(StringComparer.Ordinal)
    {
        "Progman",
        "WorkerW",
        "Shell_TrayWnd",
        "Shell_SecondaryTrayWnd",
        "XamlExplorerHostIslandWindow",
        "MultitaskingViewFrame",
        "TaskSwitcherWnd",
        "TaskSwitcherOverlayWnd",
        "ForegroundStaging",
        "Windows.UI.Core.CoreWindow",
        "NotifyIconOverflowWindow",
        "TopLevelWindowForOverflowXamlIsland",
        "LockScreenControllerProxyWindow",
    };

    private const uint DialogFrame = 0x00400000;
    private const uint ThickFrame = 0x00040000;
    private const uint WindowEdge = 0x00000100;
    private const uint ToolWindow = 0x00000080;

    /// <summary>
    /// Whether a window's styles give it a frame: a resizing or dialog frame,
    /// a raised edge, or the tool-window style overlays use.
    /// </summary>
    public static bool IsFramed(uint style, uint extendedStyle) =>
        (style & (DialogFrame | ThickFrame)) != 0 || (extendedStyle & (WindowEdge | ToolWindow)) != 0;

    /// <summary>Whether a window class belongs to the shell itself.</summary>
    public static bool IsShell(string? className) =>
        className is { Length: > 0 } named && ShellClasses.Contains(named);

    /// <summary>
    /// What the shell's own answer means: a game in exclusive full screen
    /// (QUNS_RUNNING_D3D_FULL_SCREEN, 3) or presentation settings on
    /// (QUNS_PRESENTATION_MODE, 4). QUNS_BUSY (2) is left out: overlays such
    /// as NVIDIA's set it with nothing full screen at all, which is why
    /// PowerToys made it a separate, opt-in signal.
    /// </summary>
    public static bool IsBusy(int notificationState) => notificationState is 3 or 4;

    /// <summary>
    /// Whether the window in front covers <paramref name="monitor"/> the way
    /// a full-screen app does.
    /// </summary>
    public static bool Covers(FrontWindow front, PixelRect monitor, bool shellBusy)
    {
        if (front.Rect.IsEmpty || monitor.IsEmpty || front.Framed || IsShell(front.ClassName))
        {
            return false;
        }

        if (front.Rect.Contains(monitor))
        {
            return true;
        }

        // An exclusive game can leave its window a pixel short of the monitor
        // while the shell already says it is full screen.
        return shellBusy && front.Rect.Intersect(monitor).Area * 10 >= monitor.Area * 9;
    }
}
