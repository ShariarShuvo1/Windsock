using System.Net;
using System.Net.Sockets;
using Windsock.Core.Adapters;

namespace Windsock.Core.Home;

/// <summary>How Windows classes a network (NLM_NETWORK_CATEGORY in netlistmgr.h).</summary>
public enum NetworkCategory
{
    /// <summary>A shared network - a café, a hotel, an airport - as Windows assumes a new one is.</summary>
    Public = 0,

    /// <summary>A network somebody told Windows they trust: home, usually.</summary>
    Private = 1,

    /// <summary>A workplace network, signed in to a domain.</summary>
    DomainAuthenticated = 2,
}

/// <summary>A network Windows is connected to, with its class and the adapters on it.</summary>
/// <param name="Key">Windows' id for it, the GUID lower case: the same key history files minutes under.</param>
/// <param name="Name">What Windows calls it.</param>
/// <param name="Category">How Windows classes it.</param>
/// <param name="AdapterIds">The adapters connected to it, as "{GUID}".</param>
public sealed record ConnectedNetwork(string Key, string Name, NetworkCategory Category, IReadOnlyList<string> AdapterIds);

/// <summary>Asks Windows which networks it is connected to and how it classes each.</summary>
public interface IConnectedNetworkSource
{
    /// <summary>The networks as they stand. Never throws; empty when Windows could not be asked.</summary>
    IReadOnlyList<ConnectedNetwork> Read();
}

/// <summary>
/// One adapter on a home network, with what the home features need to work on
/// it: its own IPv4 address and the link's prefix, and the router.
/// </summary>
/// <param name="NetworkKey">The network's key (see <see cref="ConnectedNetwork.Key"/>).</param>
/// <param name="NetworkName">What Windows calls the network.</param>
/// <param name="AdapterId">The adapter, as "{GUID}".</param>
/// <param name="InterfaceIndex">The adapter's interface index, which the neighbour table names rows by.</param>
/// <param name="Address">This PC's IPv4 address on the link, or nothing.</param>
/// <param name="PrefixLength">The link's prefix length for that address.</param>
/// <param name="Gateway">The router's IPv4 address on the link, or nothing.</param>
public sealed record HomeLink(
    string NetworkKey,
    string NetworkName,
    string AdapterId,
    uint InterfaceIndex,
    IPAddress? Address,
    int PrefixLength,
    IPAddress? Gateway)
{
    /// <summary>The link's IPv4 broadcast address, or nothing without an address.</summary>
    public IPAddress? Broadcast => Address is null ? null : Subnets.Broadcast(Address, PrefixLength);

    /// <summary>Whether an IPv4 address is on this link.</summary>
    public bool Contains(IPAddress address) =>
        Address is not null && Subnets.SameSubnet(Address, address, PrefixLength);
}

/// <summary>Why the home features are not looking.</summary>
public enum HomeAbsence
{
    /// <summary>They are: there is a home network.</summary>
    None,

    /// <summary>No network at all.</summary>
    NoNetwork,

    /// <summary>Only networks Windows classes as public: somebody else's, or one not yet trusted.</summary>
    PublicOnly,

    /// <summary>Only a workplace network.</summary>
    WorkOnly,

    /// <summary>Windows could not say how it classes the network, so nothing is assumed.</summary>
    Unknown,

    /// <summary>
    /// A private network, but not a local one Windsock will look on: carried by a
    /// VPN or another tunnel, or with no router of its own on the link.
    /// </summary>
    NotLocal,
}

/// <summary>Decides which adapters are on a home network.</summary>
/// <remarks>
/// <para>
/// Only a network Windows classes as Private counts. A public one - a café's
/// Wi-Fi, a hotel's - would list strangers' devices, and asking them for their
/// names announces this PC to everybody on it. A domain network is a
/// workplace's, which is no business of a home feature either, and where
/// security software reads a sweep of the network as an attack.
/// </para>
/// <para>
/// And only a local link: an Ethernet or Wi-Fi adapter (a Hyper-V external
/// switch, which presents as Ethernet, included) with an IPv4 address and a
/// router on its own subnet. Never a VPN or any other tunnel, whatever Windows
/// calls its network: Tailscale, ZeroTier or an OpenVPN TAP marked private would
/// otherwise have the sweep, NetBIOS and multicast DNS run across the tunnel to
/// peers on the internet, and a full tunnel would make the VPN's gateway "the
/// router" for the router check, the household counters and Wake-on-LAN. A VPN
/// is known by its kind, its tunnel type, its interface type (proprietary
/// virtual, tunnel, PPP) or its address in 100.64.0.0/10, where Tailscale and
/// carrier NAT live; an adapter of no recognised kind is not assumed to be a
/// LAN. What can still slip through is a VPN that presents as plain Ethernet
/// under a name nobody knows, on a network somebody marked private - Windows
/// classes a new VPN's network as public.
/// </para>
/// </remarks>
public static class HomeScope
{
    /// <summary>The adapters on home networks, the busiest route first.</summary>
    public static IReadOnlyList<HomeLink> Links(IReadOnlyList<ConnectedNetwork> networks, IReadOnlyList<AdapterDetails> adapters)
    {
        ArgumentNullException.ThrowIfNull(networks);
        ArgumentNullException.ThrowIfNull(adapters);

        List<(int Order, HomeLink Link)> links = [];

        foreach (ConnectedNetwork network in networks)
        {
            if (network.Category != NetworkCategory.Private)
            {
                continue;
            }

            foreach (string id in network.AdapterIds)
            {
                AdapterDetails? adapter = adapters.FirstOrDefault(each => string.Equals(each.Id, id, StringComparison.OrdinalIgnoreCase));

                if (adapter is null || !adapter.IsUp || !IsLocalKind(adapter))
                {
                    continue;
                }

                AdapterAddress? own = adapter.FourAddresses
                    .Where(each => each.Scope != AddressScope.LinkLocal && !IsSharedSpace(each.Address))
                    .OrderByDescending(each => each.Preferred)
                    .FirstOrDefault();

                if (own is null)
                {
                    continue;
                }

                // The router must be on the link itself: a gateway off the
                // subnet is a tunnel's or a misconfiguration's, not a home router.
                IPAddress? gateway = adapter.Gateways.FirstOrDefault(each =>
                    each.AddressFamily == AddressFamily.InterNetwork
                    && Subnets.SameSubnet(own.Address, each, own.PrefixLength)
                    && !each.Equals(own.Address));

                if (gateway is null)
                {
                    continue;
                }

                links.Add((adapter.RouteOrder, new HomeLink(
                    network.Key,
                    network.Name,
                    adapter.Id,
                    adapter.Index,
                    own.Address,
                    own.PrefixLength,
                    gateway)));
            }
        }

        return [.. links.OrderBy(each => each.Order).Select(each => each.Link)];
    }

    /// <summary>Whether an adapter is a local link rather than a VPN, a tunnel or something unrecognised.</summary>
    public static bool IsLocalKind(AdapterDetails adapter)
    {
        ArgumentNullException.ThrowIfNull(adapter);

        if (adapter.IsVpn
            || adapter.TunnelType != 0
            || adapter.InterfaceType is AdapterClassifier.ProprietaryVirtual or AdapterClassifier.Tunnel or AdapterClassifier.Ppp)
        {
            return false;
        }

        return adapter.Kind switch
        {
            AdapterKind.Ethernet or AdapterKind.WiFi => true,

            // A Hyper-V external switch carries the real LAN and presents as Ethernet.
            AdapterKind.Virtual => adapter.InterfaceType is AdapterClassifier.Ethernet or AdapterClassifier.GigabitEthernet,
            _ => false,
        };
    }

    /// <summary>Whether an address is in 100.64.0.0/10: carrier NAT, and Tailscale's own addresses.</summary>
    public static bool IsSharedSpace(IPAddress address)
    {
        ArgumentNullException.ThrowIfNull(address);
        return Subnets.SameSubnet(address, SharedSpace, 10);
    }

    private static readonly IPAddress SharedSpace = new([100, 64, 0, 0]);

    /// <summary>Why there is no home link, when there is none.</summary>
    public static HomeAbsence Absence(IReadOnlyList<ConnectedNetwork> networks, IReadOnlyList<HomeLink> links)
    {
        ArgumentNullException.ThrowIfNull(networks);
        ArgumentNullException.ThrowIfNull(links);

        if (links.Count > 0)
        {
            return HomeAbsence.None;
        }

        if (networks.Count == 0)
        {
            return HomeAbsence.NoNetwork;
        }

        if (networks.Any(each => each.Category == NetworkCategory.Private))
        {
            return HomeAbsence.NotLocal;
        }

        return networks.All(each => each.Category == NetworkCategory.DomainAuthenticated)
            ? HomeAbsence.WorkOnly
            : HomeAbsence.PublicOnly;
    }

    /// <summary>The words for why the home features are not looking.</summary>
    public static string Describe(HomeAbsence absence) => absence switch
    {
        HomeAbsence.NoNetwork => "Not connected to a network",
        HomeAbsence.PublicOnly => "This network is set as public in Windows, so its devices are not looked for. Set it as private in Windows' network settings if it is your own.",
        HomeAbsence.WorkOnly => "This is a workplace network, so its devices are not looked for",
        HomeAbsence.Unknown => "Windows could not say whether this network is private",
        HomeAbsence.NotLocal => "The private network here is a VPN's or another tunnel's, or has no router of its own, so devices are not looked for through it",
        _ => string.Empty,
    };
}

/// <summary>IPv4 subnet arithmetic.</summary>
public static class Subnets
{
    /// <summary>The broadcast address of an address's subnet.</summary>
    public static IPAddress? Broadcast(IPAddress address, int prefixLength)
    {
        if (ToNumber(address) is not { } number || prefixLength is < 0 or > 32)
        {
            return null;
        }

        uint mask = Mask(prefixLength);
        return FromNumber((number & mask) | ~mask);
    }

    /// <summary>Whether two IPv4 addresses are on the same subnet.</summary>
    public static bool SameSubnet(IPAddress one, IPAddress other, int prefixLength)
    {
        if (ToNumber(one) is not { } first || ToNumber(other) is not { } second || prefixLength is < 0 or > 32)
        {
            return false;
        }

        uint mask = Mask(prefixLength);
        return (first & mask) == (second & mask);
    }

    /// <summary>
    /// Every host address of the subnet, the network and broadcast addresses
    /// left out; empty for a subnet wider than <paramref name="widest"/> bits of
    /// hosts, which nobody sweeps.
    /// </summary>
    public static IReadOnlyList<IPAddress> Hosts(IPAddress address, int prefixLength, int widest = 10)
    {
        if (ToNumber(address) is not { } number || prefixLength is < 1 or > 30 || 32 - prefixLength > widest)
        {
            return [];
        }

        uint mask = Mask(prefixLength);
        uint network = number & mask;
        uint broadcast = network | ~mask;
        List<IPAddress> hosts = new((int)(broadcast - network - 1));

        for (uint each = network + 1; each < broadcast; each++)
        {
            hosts.Add(FromNumber(each));
        }

        return hosts;
    }

    /// <summary>An IPv4 address as a number, most significant octet first.</summary>
    public static uint? ToNumber(IPAddress address)
    {
        ArgumentNullException.ThrowIfNull(address);

        if (address.AddressFamily != AddressFamily.InterNetwork)
        {
            return null;
        }

        Span<byte> octets = stackalloc byte[4];
        address.TryWriteBytes(octets, out _);
        return ((uint)octets[0] << 24) | ((uint)octets[1] << 16) | ((uint)octets[2] << 8) | octets[3];
    }

    /// <summary>The IPv4 address for a number.</summary>
    public static IPAddress FromNumber(uint number) =>
        new([(byte)(number >> 24), (byte)(number >> 16), (byte)(number >> 8), (byte)number]);

    private static uint Mask(int prefixLength) => prefixLength == 0 ? 0 : uint.MaxValue << (32 - prefixLength);
}
