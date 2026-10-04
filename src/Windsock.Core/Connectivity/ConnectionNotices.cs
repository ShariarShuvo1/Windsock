using System.Globalization;
using Windsock.Core.Notifications;

namespace Windsock.Core.Connectivity;

/// <summary>What Windsock says, in a notification, about the connection.</summary>
public static class ConnectionNotices
{
    /// <summary>The window section a connection notice opens.</summary>
    public const string Section = "connection";

    /// <summary>
    /// Lost and restored share a tag, so "Back online" takes the place of the
    /// notice that said it was gone rather than stacking beside it.
    /// </summary>
    private const string OutageTag = "connection";

    /// <summary>The internet has gone.</summary>
    public static Notice Lost(Outage outage)
    {
        ArgumentNullException.ThrowIfNull(outage);

        string title = outage.Cause switch
        {
            OutageCause.NoNetwork => "Disconnected from the network",
            OutageCause.SignIn => "This network wants you to sign in",
            _ => "Internet connection lost",
        };

        return new Notice(NoticeTopics.ConnectionLost, title, OutageWords.Explain(outage.Cause))
        {
            Tag = OutageTag,
            Opens = Section,
        };
    }

    /// <summary>The internet is back.</summary>
    public static Notice Restored(Outage outage)
    {
        ArgumentNullException.ThrowIfNull(outage);

        string message = outage.Duration is { } duration
            ? string.Create(
                CultureInfo.CurrentCulture,
                $"The internet was out for {OutageWords.Spoken(duration)}, from {outage.Start.ToLocalTime():t}.")
            : "The internet is reachable again.";

        return new Notice(NoticeTopics.ConnectionRestored, "Back online", message)
        {
            Tag = OutageTag,
            Opens = Section,
            Quiet = true,
        };
    }

    /// <summary>The PC moved to another network.</summary>
    public static Notice NetworkSwitched(NetworkChanged change)
    {
        ArgumentNullException.ThrowIfNull(change);

        string message = change.From is { } from
            ? string.Concat("This PC was on ", from.Describe(), " before.")
            : "This PC is on a different network now.";

        return new Notice(NoticeTopics.NetworkChanged, string.Concat("Switched to ", change.To.Describe()), message)
        {
            Tag = "network",
            Opens = Section,
            Quiet = true,
        };
    }

    /// <summary>A VPN went on or off.</summary>
    /// <remarks>Only a VPN going off makes a sound: that is the one somebody may need to act on.</remarks>
    public static Notice VpnSwitched(VpnChanged change)
    {
        ArgumentNullException.ThrowIfNull(change);

        return change.Active
            ? new Notice(NoticeTopics.VpnChanged, "VPN connected", "Your traffic is going through the VPN.")
            {
                Tag = "vpn",
                Opens = Section,
                Quiet = true,
            }
            : new Notice(NoticeTopics.VpnChanged, "VPN disconnected", "Your traffic is no longer going through the VPN.")
            {
                Tag = "vpn",
                Opens = Section,
            };
    }
}
