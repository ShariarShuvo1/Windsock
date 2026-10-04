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
}
