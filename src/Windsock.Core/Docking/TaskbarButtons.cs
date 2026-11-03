namespace Windsock.Core.Docking;

/// <summary>
/// Sorts the buttons UI Automation finds on a Windows 11 taskbar into what
/// they are, for deciding where the meter may go.
/// </summary>
/// <remarks>
/// Read off the taskbar of Windows 11 25H2 (build 26200) through UI
/// Automation on 2026-10-05: app buttons are
/// <c>Taskbar.TaskListButtonAutomationPeer</c> with an automation id of
/// <c>Appid: …</c>; Start is <c>StartButton</c>; everything in the
/// notification area, the clock and Show Desktop included, has a class
/// beginning <c>SystemTray.</c>. Widgets, Search and Task View are named
/// buttons beside Start, and anything not recognised is treated like them:
/// a button that stays where it is.
/// </remarks>
public static class TaskbarButtons
{
    private const string AppPrefix = "Appid:";
    private const string AppButtonClass = "Taskbar.TaskListButtonAutomationPeer";
    private const string TrayPrefix = "SystemTray.";

    /// <summary>What a button is, from its automation id and class name.</summary>
    public static ObstacleKind Classify(string? automationId, string? className)
    {
        if (className is { } named && named.StartsWith(TrayPrefix, StringComparison.Ordinal))
        {
            return ObstacleKind.Tray;
        }

        if (string.Equals(className, AppButtonClass, StringComparison.Ordinal)
            || (automationId is { } id && id.StartsWith(AppPrefix, StringComparison.Ordinal)))
        {
            return ObstacleKind.Task;
        }

        return ObstacleKind.Fixed;
    }
}
