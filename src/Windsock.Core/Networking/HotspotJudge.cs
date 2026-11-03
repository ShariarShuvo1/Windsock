using Windsock.Core.Connectivity;

namespace Windsock.Core.Networking;

/// <summary>Why a network is, or is not, taken for a phone's hotspot.</summary>
public enum HotspotReason
{
    /// <summary>Nothing says it is one.</summary>
    None,

    /// <summary>The person marked it as one.</summary>
    MarkedByYou,

    /// <summary>The person said it is not one, whatever Windows says.</summary>
    UnmarkedByYou,

    /// <summary>It is a mobile broadband link.</summary>
    Mobile,

    /// <summary>A Wi-Fi network Windows treats as metered, which a phone's hotspot says it is.</summary>
    MeteredWiFi,
}

/// <summary>Whether the network in use is taken for a phone's hotspot, and why.</summary>
public readonly record struct HotspotVerdict(bool IsHotspot, HotspotReason Reason)
{
    /// <summary>Not a hotspot, for no reason in particular.</summary>
    public static HotspotVerdict No { get; } = new(false, HotspotReason.None);
}

/// <summary>
/// Decides whether a network is a phone's hotspot, so the meter can change its
/// tint and the data plan can come forward.
/// </summary>
/// <remarks>
/// <para>
/// Windows has no flag for "hotspot". What it has is the cost: an Android phone
/// sharing its connection tells the PC it is metered (the DHCP option Windows
/// remembers as the profile's operator cost - measured on 2026-10-05, a Galaxy
/// hotspot's profile carried <c>OperatorCost = 2</c>, fixed, under
/// <c>HKLM\SOFTWARE\Microsoft\DusmSvc\Profiles</c>), and anyone can mark a
/// network metered in Settings. So a metered Wi-Fi network is taken for a
/// hotspot, a mobile broadband link always is, and the person's own marking
/// beats both, either way.
/// </para>
/// <para>
/// A metered Wi-Fi network that is really a capped broadband line is the case
/// this gets wrong; that is what <see cref="Settings.NetworkChoice.Hotspot"/> is for.
/// </para>
/// </remarks>
public static class HotspotJudge
{
    /// <summary>The verdict for a network, with the person's marking when they gave one.</summary>
    public static HotspotVerdict Judge(NetworkSighting? sighting, bool? marking)
    {
        if (sighting is null)
        {
            return HotspotVerdict.No;
        }

        if (marking is { } marked)
        {
            return new HotspotVerdict(marked, marked ? HotspotReason.MarkedByYou : HotspotReason.UnmarkedByYou);
        }

        return sighting.Network.Kind switch
        {
            NetworkKind.Cellular => new HotspotVerdict(true, HotspotReason.Mobile),
            NetworkKind.WiFi when sighting.IsMetered => new HotspotVerdict(true, HotspotReason.MeteredWiFi),
            _ => HotspotVerdict.No,
        };
    }
}
