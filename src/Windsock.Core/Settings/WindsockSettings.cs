namespace Windsock.Core.Settings;

/// <summary>Which colour scheme Windsock should use.</summary>
public enum ThemePreference
{
    System,

    Light,

    Dark,
}

/// <summary>What closing the window does.</summary>
public enum CloseAction
{
    Quit,

    KeepRunning,
}

/// <summary>
/// Everything the user has chosen that outlives a run.
/// </summary>
public sealed class WindsockSettings
{
    private TaskbarSettings _taskbar = new();
    private NotificationSettings _notifications = new();
    private ConnectionSettings _connection = new();

    /// <summary>Which colour scheme to use.</summary>
    public ThemePreference Theme { get; set; } = ThemePreference.System;

    /// <summary>
    /// The meter that sits in the Windows taskbar.
    /// </summary>
    public TaskbarSettings Taskbar
    {
        get => _taskbar;
        set => _taskbar = value ?? new TaskbarSettings();
    }

    /// <summary>
    /// Whether and how Windsock sends notifications.
    /// </summary>
    public NotificationSettings Notifications
    {
        get => _notifications;
        set => _notifications = value ?? new NotificationSettings();
    }

    /// <summary>
    /// How the connection is watched, and what is said about it.
    /// </summary>
    public ConnectionSettings Connection
    {
        get => _connection;
        set => _connection = value ?? new ConnectionSettings();
    }

    /// <summary>
    /// What closing the window does.
    /// </summary>
    public CloseAction OnClose { get; set; } = CloseAction.Quit;

    /// <summary>
    /// Whether the sidebar names its sections, or shows only their icons.
    /// </summary>
    /// <remarks>
    /// What the user asked for, not what is on screen: a window too narrow to
    /// give the names room shows icons regardless, and widening it again
    /// brings the names back.
    /// </remarks>
    public bool SidebarOpen { get; set; } = true;

    /// <summary>
    /// Whether the Apps page's live table and the network map show processes
    /// rather than apps.
    /// </summary>
    /// <remarks>
    /// Only honoured while each process is measured - the full edition, run as
    /// administrator. Otherwise the page shows apps whatever this says, and
    /// keeps the choice for the next time processes can be shown.
    /// </remarks>
    public bool AppsShowProcesses { get; set; }

    /// <summary>
    /// Whether the network map is open at the head of the Apps page's live view.
    /// </summary>
    /// <remarks>
    /// Remembered, because an open map traces the route to every address the
    /// machine is talking to: someone who folded it away should not find it
    /// open again, and those packets going out again, the next time the window
    /// opens.
    /// </remarks>
    public bool ProcessMapOpen { get; set; } = true;

    /// <summary>
    /// Whether Windsock looks for a newer version by itself.
    /// </summary>
    public bool CheckForUpdates { get; set; } = true;

    /// <summary>
    /// Whether usage is written to the history database.
    /// </summary>
    public bool RecordHistory { get; set; } = true;

    /// <summary>
    /// How many days of each app's minute-by-minute use history keeps. Zero,
    /// the default, keeps it for good. Older detail is folded into quarter-hour
    /// totals, so nothing is lost but the minutes; network totals, the machine's
    /// readings and the connection checks are always kept a minute at a time.
    /// </summary>
    public int KeepMinuteDetailDays { get; set; }

    /// <summary>How many rows a page of History's table holds.</summary>
    public int HistoryRowsPerPage { get; set; } = 100;

    /// <summary>How many outages a page of the Connection section's log holds.</summary>
    public int OutageRowsPerPage { get; set; } = 100;

    /// <summary>
    /// Whether the way to each far end is found and drawn.
    /// </summary>
    public bool FollowRoutes { get; set; } = true;

    /// <summary>
    /// The adapters to measure, by interface LUID, or empty for all of them.
    /// </summary>
    public List<ulong> Adapters { get; set; } = [];
}
