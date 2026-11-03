using System.Globalization;
using System.Net;
using System.Net.Sockets;
using Windsock.Core.Adapters;
using Windsock.Core.Networking;

namespace Windsock.Core.Alerts;

/// <summary>
/// What a network looked like from this PC: its address there, the DNS servers
/// it handed out and the router behind it. Kept per network, so the next visit
/// can be compared with the last one.
/// </summary>
/// <param name="Key">
/// The network's key (see <see cref="NetworkTag.Key"/>), with <c>|vpn</c> added
/// while traffic goes through a VPN: a VPN brings its own address and DNS
/// servers, so the same network is remembered once with it and once without.
/// </param>
/// <param name="Name">The network's name, as Windows gives it.</param>
/// <param name="Address">This PC's IPv4 address on it, or nothing.</param>
/// <param name="Dns">The IPv4 DNS servers in use, sorted.</param>
/// <param name="Gateway">The router's address on the physical link, or nothing.</param>
/// <param name="RouterMac">The router's hardware address, as "AA-BB-CC-DD-EE-FF", or nothing when not known.</param>
public sealed record NetworkPrint(
    string Key,
    string Name,
    string? Address,
    IReadOnlyList<string> Dns,
    string? Gateway,
    string? RouterMac)
{
    /// <summary>When it was last seen.</summary>
    public DateTimeOffset Seen { get; init; }

    /// <summary>The DNS servers as one line, for the record and for a notice.</summary>
    public string DnsText => Dns.Count == 0 ? string.Empty : string.Join(", ", Dns);

    /// <summary>
    /// A print of the network the snapshot shows, or nothing when there is no
    /// network or nothing carries traffic.
    /// </summary>
    /// <param name="snapshot">Every adapter, at one moment.</param>
    /// <param name="network">The network in use, as Windows names it.</param>
    /// <param name="routerMac">
    /// The hardware address of a router on an adapter, from the neighbour cache
    /// Windows already keeps; nothing when it has no entry. Asked only for the
    /// physical link's router: a VPN's far end has no hardware address here.
    /// </param>
    /// <param name="at">When the snapshot was read.</param>
    public static NetworkPrint? From(
        AdapterSnapshot snapshot,
        NetworkTag? network,
        Func<AdapterDetails, IPAddress, string?> routerMac,
        DateTimeOffset at)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        ArgumentNullException.ThrowIfNull(routerMac);

        if (network is null || snapshot.Carrier is not { } carrier)
        {
            return null;
        }

        AdapterDetails physical = snapshot.Physical ?? carrier;

        string? address = carrier.FourAddresses
            .Where(each => each.Preferred && each.Scope is not (AddressScope.LinkLocal or AddressScope.Loopback or AddressScope.Unspecified))
            .Select(each => each.Address.ToString())
            .FirstOrDefault();

        // IPv4 servers only: an IPv6 one handed out by the router carries the
        // provider's prefix, which many providers rotate, and a rotated prefix
        // is not somebody changing the DNS servers.
        string[] dns = [.. carrier.DnsServers
            .Select(each => each.Address)
            .Where(each => each.AddressFamily == AddressFamily.InterNetwork)
            .Where(each => AddressScopes.Of(each) is not (AddressScope.SiteLocal or AddressScope.Unspecified))
            .Select(each => each.ToString())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Order(StringComparer.OrdinalIgnoreCase)];

        IPAddress? gateway = physical.Gateways.FirstOrDefault(each => each.AddressFamily == AddressFamily.InterNetwork)
            ?? (physical.Gateways.Count > 0 ? physical.Gateways[0] : null);

        string? mac = gateway is null ? null : Normalise(routerMac(physical, gateway));

        return new NetworkPrint(
            carrier.IsVpn ? string.Concat(network.Key, "|vpn") : network.Key,
            network.Name,
            address,
            dns,
            gateway?.ToString(),
            mac)
        {
            Seen = at,
        };
    }

    /// <summary>
    /// A hardware address in one form - upper case, dashes - or nothing for none,
    /// all zeros or broadcast, which name no router.
    /// </summary>
    public static string? Normalise(string? mac)
    {
        if (string.IsNullOrWhiteSpace(mac))
        {
            return null;
        }

        string[] parts = mac.Trim().Split(['-', ':'], StringSplitOptions.RemoveEmptyEntries);

        if (parts.Length != 6 || parts.Any(part => part.Length != 2 || !byte.TryParse(part, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out _)))
        {
            return null;
        }

        string joined = string.Join('-', parts).ToUpperInvariant();

        return joined is "00-00-00-00-00-00" or "FF-FF-FF-FF-FF-FF" ? null : joined;
    }
}

/// <summary>What changed on a network since it was last seen.</summary>
public enum NetworkPrintChange
{
    /// <summary>This PC's address there.</summary>
    Address,

    /// <summary>The DNS servers it hands out.</summary>
    Dns,

    /// <summary>The router behind it.</summary>
    Router,
}

/// <summary>One change, with what it was and what it is now, in words.</summary>
/// <param name="Change">What changed.</param>
/// <param name="Network">The network's name.</param>
/// <param name="Was">What it was.</param>
/// <param name="Now">What it is now.</param>
public sealed record NetworkPrintDifference(NetworkPrintChange Change, string Network, string Was, string Now);

/// <summary>Compares a network with how it looked the last time.</summary>
public static class NetworkPrintJudge
{
    /// <summary>
    /// What changed on the same network since it was last seen. A network not
    /// seen before, or a different one, is no change: moving networks is the
    /// network alert's business. Anything not known on either side is not
    /// compared - a router missing from the neighbour cache is not a new router.
    /// </summary>
    public static IReadOnlyList<NetworkPrintDifference> Compare(NetworkPrint? known, NetworkPrint now)
    {
        ArgumentNullException.ThrowIfNull(now);

        if (known is null || !string.Equals(known.Key, now.Key, StringComparison.Ordinal))
        {
            return [];
        }

        List<NetworkPrintDifference> changes = [];

        // A VPN hands out an address per connection, so on a VPN a new address
        // is the VPN reconnecting, not news.
        if (!IsTunnelled(now.Key)
            && known.Address is { } wasAddress
            && now.Address is { } nowAddress
            && !string.Equals(wasAddress, nowAddress, StringComparison.OrdinalIgnoreCase))
        {
            changes.Add(new NetworkPrintDifference(NetworkPrintChange.Address, now.Name, wasAddress, nowAddress));
        }

        if (known.Dns.Count > 0
            && now.Dns.Count > 0
            && !known.Dns.Order(StringComparer.OrdinalIgnoreCase).SequenceEqual(now.Dns.Order(StringComparer.OrdinalIgnoreCase), StringComparer.OrdinalIgnoreCase))
        {
            changes.Add(new NetworkPrintDifference(NetworkPrintChange.Dns, now.Name, known.DnsText, now.DnsText));
        }

        if (known.RouterMac is { } wasMac
            && now.RouterMac is { } nowMac
            && !string.Equals(wasMac, nowMac, StringComparison.OrdinalIgnoreCase))
        {
            changes.Add(new NetworkPrintDifference(
                NetworkPrintChange.Router,
                now.Name,
                Router(known.Gateway, wasMac),
                Router(now.Gateway, nowMac)));
        }

        return changes;
    }

    /// <summary>
    /// What to remember of a network now: the new reading, keeping what it could
    /// not see - a router briefly missing from the neighbour cache stays known.
    /// </summary>
    public static NetworkPrint Remember(NetworkPrint? known, NetworkPrint now)
    {
        ArgumentNullException.ThrowIfNull(now);

        if (known is null || !string.Equals(known.Key, now.Key, StringComparison.Ordinal))
        {
            return now;
        }

        return now with
        {
            Address = now.Address ?? known.Address,
            Dns = now.Dns.Count > 0 ? now.Dns : known.Dns,
            Gateway = now.RouterMac is null ? known.Gateway ?? now.Gateway : now.Gateway,
            RouterMac = now.RouterMac ?? known.RouterMac,
        };
    }

    /// <summary>Whether a print's key is a network seen through a VPN.</summary>
    public static bool IsTunnelled(string key)
    {
        ArgumentNullException.ThrowIfNull(key);
        return key.EndsWith("|vpn", StringComparison.Ordinal);
    }

    private static string Router(string? gateway, string mac) =>
        gateway is null ? mac : string.Concat(gateway, " (", mac, ")");
}
