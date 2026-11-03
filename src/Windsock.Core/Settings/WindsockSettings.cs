using Windsock.Core.Formatting;

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
    /// Data plans, names for networks, and scheduled export, backup and digest.
    /// </summary>
    public UsageSettings Usage
    {
        get => field ??= new UsageSettings();
        set => field = value ?? new UsageSettings();
    }

    /// <summary>
    /// The connection tools that reach beyond this PC - public address, speed
    /// tests, the NAT test, DNS timing - each off until switched on.
    /// </summary>
    public ConnectionToolsSettings ConnectionTools
    {
        get => field ??= new ConnectionToolsSettings();
        set => field = value ?? new ConnectionToolsSettings();
    }

    /// <summary>
    /// New-app notices, the blocklists (off until switched on) and the
    /// connection timeline (off until switched on).
    /// </summary>
    public InsightSettings Insight
    {
        get => field ??= new InsightSettings();
        set => field = value ?? new InsightSettings();
    }

    /// <summary>
    /// Quiet hours, each alert's sound and levels, the alerts added in 0.2.0,
    /// how long the record of alerts is kept, and forwarding.
    /// </summary>
    public AlertSettings Alerts
    {
        get => field ??= new AlertSettings();
        set => field = value ?? new AlertSettings();
    }

    /// <summary>
    /// The devices on the home network, the router check and the router's
    /// traffic counters, each off until switched on.
    /// </summary>
    public HomeSettings Home
    {
        get => field ??= new HomeSettings();
        set => field = value ?? new HomeSettings();
    }

    /// <summary>The local status feed, off until switched on.</summary>
    public FeedSettings Feed
    {
        get => field ??= new FeedSettings();
        set => field = value ?? new FeedSettings();
    }

    /// <summary>The accent colour and the density the window is drawn with.</summary>
    public AppearanceSettings Appearance
    {
        get => field ??= new AppearanceSettings();
        set => field = value ?? new AppearanceSettings();
    }

    /// <summary>The page the window opens on, and where it was on each monitor.</summary>
    public WindowSettings Window
    {
        get => field ??= new WindowSettings();
        set => field = value ?? new WindowSettings();
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
    /// Whether the way to each far end is traced and drawn, on the map and in
    /// an app's own window. Off until asked for: tracing sends packets of
    /// Windsock's own towards every place drawn.
    /// </summary>
    /// <remarks>
    /// One switch since 2026-10-05, shown on the map, in an app's window and on
    /// the privacy page, all three the same setting. It replaced
    /// <c>FollowRoutes</c>, which was on by default and sat under a second,
    /// per-map switch; a settings file still carrying that key is read as if it
    /// were not there, so nobody finds tracing on without having asked.
    /// </remarks>
    public bool TraceRoutes { get; set; }

    /// <summary>
    /// Whether an address the DNS client's cache cannot name is looked up in
    /// reverse through the resolver this PC uses. Off until asked for: the
    /// lookup leaves the machine. The Store edition never makes it.
    /// </summary>
    public bool ReverseLookups { get; set; }

    /// <summary>Whether the network chart counts in bytes or in bits.</summary>
    public RateFamily ChartFamily { get; set; } = RateFamily.Bytes;

    /// <summary>The network chart's scale: Auto, or a pinned magnitude.</summary>
    public RateScale ChartScale { get; set; } = RateScale.Auto;

    /// <summary>
    /// The scale every temperature is shown in, the taskbar meter's included.
    /// Readings, history and the thresholds that call a part hot stay in
    /// Celsius; only what is written changes.
    /// </summary>
    public TemperatureUnit Temperature { get; set; } = TemperatureUnit.Celsius;

    /// <summary>
    /// The adapters to measure, by interface LUID, or empty for all of them.
    /// </summary>
    public List<ulong> Adapters { get; set; } = [];

    /// <summary>
    /// Takes every choice from another set of settings - an imported file -
    /// except what belongs to this PC: the places named by
    /// <see cref="SettingsTransfer.StaysOnThisPc"/>, the same list the export
    /// leaves out and the change list never shows, and where alerts are
    /// forwarded, which is never in a settings file at all.
    /// </summary>
    /// <remarks>
    /// Every group is assigned from one copy made through that list, so a
    /// setting added later crosses with an import without a line here, and a
    /// group can no longer be listed as changing while never being copied.
    /// </remarks>
    public void CopyFrom(WindsockSettings other)
    {
        ArgumentNullException.ThrowIfNull(other);

        WindsockSettings taken = SettingsTransfer.ForThisPc(other, this);
        ForwardSettings forward = Alerts.Forward;

        Theme = taken.Theme;
        Taskbar = taken.Taskbar;
        Notifications = taken.Notifications;
        Connection = taken.Connection;
        Usage = taken.Usage;
        ConnectionTools = taken.ConnectionTools;
        Insight = taken.Insight;
        Alerts = taken.Alerts;
        Home = taken.Home;
        Feed = taken.Feed;
        Appearance = taken.Appearance;
        Window = taken.Window;
        OnClose = taken.OnClose;
        SidebarOpen = taken.SidebarOpen;
        AppsShowProcesses = taken.AppsShowProcesses;
        CheckForUpdates = taken.CheckForUpdates;
        RecordHistory = taken.RecordHistory;
        KeepMinuteDetailDays = taken.KeepMinuteDetailDays;
        HistoryRowsPerPage = taken.HistoryRowsPerPage;
        OutageRowsPerPage = taken.OutageRowsPerPage;
        TraceRoutes = taken.TraceRoutes;
        ReverseLookups = taken.ReverseLookups;
        ChartFamily = taken.ChartFamily;
        ChartScale = taken.ChartScale;
        Temperature = taken.Temperature;
        Adapters = taken.Adapters;

        Alerts.Forward = forward;
    }
}
