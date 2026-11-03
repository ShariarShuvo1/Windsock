using Windsock.Core.Notifications;
using Windsock.Core.Reports;
using Windsock.Core.Settings;

namespace Windsock.Core.Alerts;

/// <summary>The part of the Alerts page a kind of alert is listed under.</summary>
public enum AlertGroup
{
    /// <summary>The internet coming and going, and how well it answers.</summary>
    Connection,

    /// <summary>How fast things move.</summary>
    Speed,

    /// <summary>How much was used.</summary>
    Usage,

    /// <summary>Temperatures and drives.</summary>
    Hardware,

    /// <summary>Changes someone else may have made: addresses, DNS, the router, the proxy.</summary>
    Changes,

    /// <summary>Windsock itself.</summary>
    Windsock,
}

/// <summary>How much an alert matters, for its mark in the list and on the charts.</summary>
public enum AlertLevel
{
    /// <summary>Worth knowing.</summary>
    Info,

    /// <summary>Worth a look.</summary>
    Warning,

    /// <summary>Something is wrong.</summary>
    Problem,
}

/// <summary>One kind of alert: what it is called, where it is listed, and its switch.</summary>
public sealed class AlertKind
{
    internal AlertKind(string topic, string name, string description, AlertGroup group, AlertLevel level)
    {
        Topic = topic;
        Name = name;
        Description = description;
        Group = group;
        Level = level;
    }

    /// <summary>The notice topic it is sent under, which is also how its choices are kept.</summary>
    public string Topic { get; }

    /// <summary>Its name on the Alerts page.</summary>
    public string Name { get; }

    /// <summary>What it is for, in a sentence.</summary>
    public string Description { get; }

    /// <summary>Where it is listed.</summary>
    public AlertGroup Group { get; }

    /// <summary>How much it matters.</summary>
    public AlertLevel Level { get; }

    /// <summary>Whether only the full edition can raise it.</summary>
    public bool FullEditionOnly { get; init; }

    /// <summary>
    /// Whether it is listed on the Alerts page. The summary of held alerts is
    /// a kind too, for its mark and its name, but has no switch of its own.
    /// </summary>
    public bool IsListed { get; init; } = true;

    /// <summary>
    /// How long the same alert - same topic and tag - is not raised again, even
    /// across restarts: a drive still nearly full is not news every start.
    /// Zero leaves it to flood control and the feature's own sense.
    /// </summary>
    public TimeSpan Cooldown { get; init; }

    /// <summary>Whether it is switched on, or null for a kind with no switch of its own.</summary>
    public Func<WindsockSettings, bool>? IsOn { get; init; }

    /// <summary>Switches it on or off; null for a kind with no switch of its own.</summary>
    public Action<WindsockSettings, bool>? SetOn { get; init; }

    /// <summary>
    /// The topics sharing this kind's switch: losing and getting back the
    /// internet are one switch, as are network and VPN changes.
    /// </summary>
    public string? SharesSwitchWith { get; init; }

    /// <summary>Whether this build can raise it.</summary>
    public bool IsOffered => !FullEditionOnly || !AlertCatalogue.IsStoreEdition;
}

/// <summary>
/// Every kind of alert Windsock raises, in one list: the Alerts page draws
/// from it, the record names alerts by it, and the charts mark them by it.
/// </summary>
public static class AlertCatalogue
{
    /// <summary>Whether this is the Store edition, which leaves out what needs elevation.</summary>
#if STORE
    public const bool IsStoreEdition = true;
#else
    public const bool IsStoreEdition = false;
#endif

    private static readonly AlertKind[] Kinds =
    [
        new(NoticeTopics.ConnectionLost, "Internet lost and back", "When the internet drops, and when it comes back.", AlertGroup.Connection, AlertLevel.Problem)
        {
            IsOn = settings => settings.Connection.NotifyLostAndRestored,
            SetOn = (settings, on) => settings.Connection.NotifyLostAndRestored = on,
            SharesSwitchWith = NoticeTopics.ConnectionRestored,
        },
        new(NoticeTopics.ConnectionRestored, "Back online", "When the internet comes back after an outage.", AlertGroup.Connection, AlertLevel.Info)
        {
            IsListed = false,
            IsOn = settings => settings.Connection.NotifyLostAndRestored,
            SetOn = (settings, on) => settings.Connection.NotifyLostAndRestored = on,
            SharesSwitchWith = NoticeTopics.ConnectionLost,
        },
        new(NoticeTopics.NetworkChanged, "Network or VPN changed", "When this PC moves to another network, or a VPN goes on or off.", AlertGroup.Connection, AlertLevel.Info)
        {
            IsOn = settings => settings.Connection.NotifyNetworkChanges,
            SetOn = (settings, on) => settings.Connection.NotifyNetworkChanges = on,
            SharesSwitchWith = NoticeTopics.VpnChanged,
        },
        new(NoticeTopics.VpnChanged, "VPN on or off", "When a VPN goes on or off.", AlertGroup.Connection, AlertLevel.Warning)
        {
            IsListed = false,
            IsOn = settings => settings.Connection.NotifyNetworkChanges,
            SetOn = (settings, on) => settings.Connection.NotifyNetworkChanges = on,
            SharesSwitchWith = NoticeTopics.NetworkChanged,
        },
        new(NoticeTopics.PingHigh, "Slow responses or lost pings", "When the response time or the share of unanswered checks stays above a level.", AlertGroup.Connection, AlertLevel.Warning)
        {
            Cooldown = TimeSpan.FromHours(1),
            IsOn = settings => settings.Alerts.Ping.Enabled,
            SetOn = (settings, on) => settings.Alerts.Ping.Enabled = on,
        },
        new(NoticeTopics.SpeedDrop, "Download slows down", "When a download that was going faster drops under a level and stays there for several minutes.", AlertGroup.Speed, AlertLevel.Warning)
        {
            Cooldown = TimeSpan.FromHours(1),
            IsOn = settings => settings.Alerts.SpeedDrop.Enabled,
            SetOn = (settings, on) => settings.Alerts.SpeedDrop.Enabled = on,
        },
        new(NoticeTopics.UploadHigh, "Unusual upload", "When the upload stays above a level: a runaway backup, a sync gone wrong, or something worse.", AlertGroup.Speed, AlertLevel.Warning)
        {
            Cooldown = TimeSpan.FromHours(1),
            IsOn = settings => settings.Alerts.Upload.Enabled,
            SetOn = (settings, on) => settings.Alerts.Upload.Enabled = on,
        },
        new(NoticeTopics.PlanLevel, "Data plan levels", "When a data plan passes one of its levels, such as 80% and 100%.", AlertGroup.Usage, AlertLevel.Warning)
        {
            IsOn = settings => settings.Alerts.PlanLevels,
            SetOn = (settings, on) => settings.Alerts.PlanLevels = on,
        },
        new(WeeklyDigest.Topic, "Weekly digest", "Once a week, what the week used, any outages and the busiest app.", AlertGroup.Usage, AlertLevel.Info)
        {
            IsOn = settings => settings.Usage.Digest.Enabled,
            SetOn = (settings, on) => settings.Usage.Digest.Enabled = on,
        },
        new(NoticeTopics.GraphicsHot, "Graphics too hot", "When a graphics adapter stays above a temperature.", AlertGroup.Hardware, AlertLevel.Warning)
        {
            Cooldown = TimeSpan.FromHours(1),
            IsOn = settings => settings.Alerts.Hardware.GraphicsHot,
            SetOn = (settings, on) => settings.Alerts.Hardware.GraphicsHot = on,
        },
        new(NoticeTopics.ProcessorHot, "Processor too hot", "When the processor stays above a temperature. Needs PawnIO and Windsock running as administrator.", AlertGroup.Hardware, AlertLevel.Warning)
        {
            FullEditionOnly = true,
            Cooldown = TimeSpan.FromHours(1),
            IsOn = settings => settings.Alerts.Hardware.ProcessorHot,
            SetOn = (settings, on) => settings.Alerts.Hardware.ProcessorHot = on,
        },
        new(NoticeTopics.DriveHot, "Drive too hot", "When a drive stays above a temperature.", AlertGroup.Hardware, AlertLevel.Warning)
        {
            Cooldown = TimeSpan.FromHours(1),
            IsOn = settings => settings.Alerts.Hardware.DriveHot,
            SetOn = (settings, on) => settings.Alerts.Hardware.DriveHot = on,
        },
        new(NoticeTopics.DriveFull, "Drive nearly full", "When a drive passes a level of fullness. Said once a day at most.", AlertGroup.Hardware, AlertLevel.Warning)
        {
            Cooldown = TimeSpan.FromDays(1),
            IsOn = settings => settings.Alerts.Hardware.DriveFull,
            SetOn = (settings, on) => settings.Alerts.Hardware.DriveFull = on,
        },
        new(NoticeTopics.DriveWear, "Drive wearing out", "When a drive has used a share of its rated life. Said once for each drive.", AlertGroup.Hardware, AlertLevel.Warning)
        {
            Cooldown = TimeSpan.FromDays(3650),
            IsOn = settings => settings.Alerts.Hardware.DriveWear,
            SetOn = (settings, on) => settings.Alerts.Hardware.DriveWear = on,
        },
        new(NoticeTopics.AddressChanged, "Address changed", "When this PC's address changes on a network it has been on before.", AlertGroup.Changes, AlertLevel.Info)
        {
            IsOn = settings => settings.Alerts.Changes.Address,
            SetOn = (settings, on) => settings.Alerts.Changes.Address = on,
        },
        new(NoticeTopics.DnsChanged, "DNS servers changed", "When a known network starts handing out different DNS servers: a sign of a VPN leak or a hijacked router.", AlertGroup.Changes, AlertLevel.Warning)
        {
            IsOn = settings => settings.Alerts.Changes.Dns,
            SetOn = (settings, on) => settings.Alerts.Changes.Dns = on,
        },
        new(NoticeTopics.RouterChanged, "Router changed", "When the router on a known network is not the one it was: a sign of a rogue hotspot or spoofing.", AlertGroup.Changes, AlertLevel.Warning)
        {
            IsOn = settings => settings.Alerts.Changes.Router,
            SetOn = (settings, on) => settings.Alerts.Changes.Router = on,
        },
        new(NoticeTopics.ProxyChanged, "Proxy changed", "When the system proxy changes while Windows' own settings are not open.", AlertGroup.Changes, AlertLevel.Warning)
        {
            IsOn = settings => settings.Alerts.Changes.Proxy,
            SetOn = (settings, on) => settings.Alerts.Changes.Proxy = on,
        },
        new(NoticeTopics.NewApp, "New app online", "When a program uses the network for the first time Windsock knows of, with who signed it.", AlertGroup.Changes, AlertLevel.Info)
        {
            IsOn = settings => settings.Insight.NewAppNotices,
            SetOn = (settings, on) => settings.Insight.NewAppNotices = on,
        },
        new(Home.DeviceJoinedNotices.Topic, "New device on the home network", "When a device is seen on your home network for the first time. Needs Find devices on my home network, under Settings › Data and privacy.", AlertGroup.Changes, AlertLevel.Info)
        {
            IsOn = settings => settings.Home.NotifyNewDevices,
            SetOn = (settings, on) => settings.Home.NotifyNewDevices = on,
        },
        new(NoticeTopics.UpdateReady, "Update ready", "When a newer Windsock can be installed.", AlertGroup.Windsock, AlertLevel.Info)
        {
            FullEditionOnly = true,
            Cooldown = TimeSpan.FromDays(30),
            IsOn = settings => settings.Alerts.UpdateReady,
            SetOn = (settings, on) => settings.Alerts.UpdateReady = on,
        },
        new(NoticeTopics.AwaySummary, "While you were away", "On unlocking the screen, what was used and what happened while it was locked.", AlertGroup.Windsock, AlertLevel.Info)
        {
            IsOn = settings => settings.Alerts.Away.Enabled,
            SetOn = (settings, on) => settings.Alerts.Away.Enabled = on,
        },
        new(NoticeTopics.QuietSummary, "Held during quiet hours", "The alerts held during quiet hours or a snooze, in one notice.", AlertGroup.Windsock, AlertLevel.Info)
        {
            IsListed = false,
        },
    ];

    private static readonly Dictionary<string, AlertKind> ByTopic = Kinds.ToDictionary(kind => kind.Topic, StringComparer.Ordinal);

    /// <summary>Every kind this build can raise, in the order the Alerts page lists them.</summary>
    public static IReadOnlyList<AlertKind> All { get; } = [.. Kinds.Where(kind => kind.IsOffered)];

    /// <summary>The kinds with a switch of their own, as the Alerts page lists them.</summary>
    public static IReadOnlyList<AlertKind> Listed { get; } = [.. All.Where(kind => kind.IsListed)];

    /// <summary>The kind sent under a topic, or null for a topic this list does not know.</summary>
    public static AlertKind? Find(string? topic) =>
        topic is not null && ByTopic.TryGetValue(topic, out AlertKind? kind) ? kind : null;

    /// <summary>How much an alert under a topic matters; a topic this list does not know is worth knowing.</summary>
    public static AlertLevel LevelOf(string? topic) => Find(topic)?.Level ?? AlertLevel.Info;

    /// <summary>The name of a kind of alert, or the topic itself for one this list does not know.</summary>
    public static string NameOf(string? topic) => Find(topic)?.Name ?? topic ?? string.Empty;
}
