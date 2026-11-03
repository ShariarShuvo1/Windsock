namespace Windsock.Core.Notifications;

/// <summary>
/// One thing Windsock wants to tell the person using it, whether or not the
/// window is open.
/// </summary>
/// <param name="Topic">
/// What kind of notice this is, such as <see cref="NoticeTopics.Test"/>. Each
/// topic can be switched off on its own, and flood control counts by it.
/// </param>
/// <param name="Title">The first line, in a few words.</param>
/// <param name="Message">What happened, in a sentence.</param>
public sealed record Notice(string Topic, string Title, string Message)
{
    /// <summary>
    /// Replaces an earlier notice carrying the same tag rather than stacking a
    /// second one beside it: "Back online" takes the place of "Connection lost".
    /// </summary>
    public string? Tag { get; init; }

    /// <summary>
    /// The section of the window to open when the notice is clicked, or nothing
    /// to open the window where it was.
    /// </summary>
    public string? Opens { get; init; }

    /// <summary>
    /// Never plays a sound, whatever the setting says. For notices that report
    /// something already over.
    /// </summary>
    public bool Quiet { get; init; }

    /// <summary>
    /// The sound chosen for this kind of notice on the Alerts page. The default
    /// follows <see cref="Quiet"/>; any other choice is the person's, and wins.
    /// </summary>
    public NoticeSound Sound { get; init; }
}

/// <summary>The topics Windsock sends notices about.</summary>
public static class NoticeTopics
{
    /// <summary>The one sent from Settings to check notices arrive at all.</summary>
    public const string Test = "test";

    /// <summary>The internet went away.</summary>
    public const string ConnectionLost = "connection-lost";

    /// <summary>The internet came back.</summary>
    public const string ConnectionRestored = "connection-restored";

    /// <summary>The PC moved to another network.</summary>
    public const string NetworkChanged = "network-changed";

    /// <summary>A VPN went on or off.</summary>
    public const string VpnChanged = "vpn-changed";

    /// <summary>A data plan's cycle passed one of its levels.</summary>
    public const string PlanLevel = "plan-level";

    /// <summary>Response time or packet loss stayed above a level for a while.</summary>
    public const string PingHigh = "ping-high";

    /// <summary>Download stayed below a level while something was downloading.</summary>
    public const string SpeedDrop = "speed-drop";

    /// <summary>Upload stayed above a level for a while.</summary>
    public const string UploadHigh = "upload-high";

    /// <summary>A graphics adapter is too hot.</summary>
    public const string GraphicsHot = "graphics-hot";

    /// <summary>A drive is too hot.</summary>
    public const string DriveHot = "drive-hot";

    /// <summary>A drive is nearly full.</summary>
    public const string DriveFull = "drive-full";

    /// <summary>A drive's wear passed a level.</summary>
    public const string DriveWear = "drive-wear";

    /// <summary>The processor is too hot.</summary>
    public const string ProcessorHot = "processor-hot";

    /// <summary>This PC's address on a network it knows changed.</summary>
    public const string AddressChanged = "address-changed";

    /// <summary>The DNS servers on a network it knows changed.</summary>
    public const string DnsChanged = "dns-changed";

    /// <summary>The router on a network it knows is not the one it was.</summary>
    public const string RouterChanged = "router-changed";

    /// <summary>The system proxy changed.</summary>
    public const string ProxyChanged = "proxy-changed";

    /// <summary>A newer Windsock can be installed.</summary>
    public const string UpdateReady = "update-ready";

    /// <summary>What happened while the screen was locked.</summary>
    public const string AwaySummary = "away-summary";

    /// <summary>The alerts held during quiet hours or a snooze, in one notice.</summary>
    public const string QuietSummary = "quiet-summary";

    /// <summary>A program used the network for the first time.</summary>
    public const string NewApp = "new-app";
}
