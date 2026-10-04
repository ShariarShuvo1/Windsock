using System.Net.NetworkInformation;

namespace Windsock.Core.Connectivity;

/// <summary>Tells a VPN adapter from an ordinary one.</summary>
/// <remarks>
/// Windows has no flag for "this is a VPN". Tunnel and point-to-point interface
/// types catch the built-in client and older VPNs; the rest - WireGuard, OpenVPN's
/// TAP and Wintun drivers, and most commercial clients - present as ordinary
/// virtual adapters and are known by their descriptions. The list errs toward
/// calling something a VPN only when its name says so, because a false "VPN
/// connected" is worse than a missed one.
/// </remarks>
public static class VpnDetector
{
    private static readonly string[] Signs =
    [
        "vpn",
        "wireguard",
        "wintun",
        "tap-windows",
        "openvpn",
        "tailscale",
        "zerotier",
        "nordlynx",
        "anyconnect",
        "globalprotect",
        "fortinet",
        "forticlient",
        "juniper",
        "pulse secure",
        "sonicwall",
        "protonvpn",
        "expressvpn",
        "surfshark",
        "mullvad",
        "cloudflare warp",
    ];

    /// <summary>Whether an adapter looks like a VPN.</summary>
    public static bool IsVpn(NetworkInterfaceType type, string? description, string? name)
    {
        if (type is NetworkInterfaceType.Ppp or NetworkInterfaceType.Tunnel)
        {
            // The IPv6 transition tunnels Windows adds by itself are not VPNs.
            return !IsTransition(description) && !IsTransition(name);
        }

        return Mentions(description) || Mentions(name);
    }

    private static bool Mentions(string? text) =>
        text is { Length: > 0 } && Signs.Any(sign => text.Contains(sign, StringComparison.OrdinalIgnoreCase));

    private static bool IsTransition(string? text) =>
        text is { Length: > 0 }
        && (text.Contains("teredo", StringComparison.OrdinalIgnoreCase)
            || text.Contains("isatap", StringComparison.OrdinalIgnoreCase)
            || text.Contains("6to4", StringComparison.OrdinalIgnoreCase)
            || text.Contains("ip-https", StringComparison.OrdinalIgnoreCase));
}
