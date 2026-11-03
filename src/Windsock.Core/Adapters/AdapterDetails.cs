using System.Net;
using Windsock.Core.Networking;

namespace Windsock.Core.Adapters;

/// <summary>What kind of link an adapter is.</summary>
public enum AdapterKind
{
    Other,

    Ethernet,

    WiFi,

    /// <summary>A mobile broadband modem.</summary>
    Cellular,

    /// <summary>A VPN's adapter, by its type or its name.</summary>
    Vpn,

    /// <summary>A tunnel Windows makes by itself for IPv6: Teredo, 6to4, ISATAP, IP-HTTPS.</summary>
    Transition,

    /// <summary>A virtual machine's or container's switch: Hyper-V, WSL, VMware, VirtualBox, Docker.</summary>
    Virtual,

    Bluetooth,

    Loopback,
}

/// <summary>Whether an adapter is carrying traffic.</summary>
public enum AdapterState
{
    Up,

    Down,

    /// <summary>The hardware is gone - unplugged, or switched off in its driver.</summary>
    NotPresent,

    /// <summary>Waiting on something: testing, dormant, or the layer under it is down.</summary>
    Waiting,

    Unknown,
}

/// <summary>Where a DNS server's address came from.</summary>
public enum DnsServerOrigin
{
    Unknown,

    /// <summary>Typed into the adapter's settings.</summary>
    Manual,

    /// <summary>Handed out with the address, by DHCP or the router's announcements.</summary>
    Automatic,
}

/// <summary>What Windows could do about encrypting lookups to one DNS server.</summary>
/// <remarks>
/// Windows keeps no documented, unelevated answer to "are lookups to this
/// server encrypted right now". What it does keep is a table of servers it
/// knows how to reach over HTTPS, and the per-adapter choice made in Settings.
/// So the strongest claim made here is what was configured, never what a
/// particular lookup used.
/// </remarks>
public enum DnsEncryption
{
    /// <summary>Not looked into.</summary>
    Unknown,

    /// <summary>Windows knows no way to encrypt to this server, so lookups to it are plain.</summary>
    NotAvailable,

    /// <summary>Windows could encrypt to this server, but nothing in its settings asks it to.</summary>
    Available,

    /// <summary>The adapter's settings ask Windows to encrypt lookups to this server.</summary>
    Configured,
}

/// <summary>Windows' view of what a network costs to use.</summary>
/// <param name="Kind">How it is charged: the same <see cref="NetworkCost"/> history keeps for each network.</param>
/// <param name="Roaming">Whether a mobile connection is roaming.</param>
/// <param name="OverDataLimit">Whether the plan's limit has been passed.</param>
/// <param name="ApproachingDataLimit">Whether the limit is near.</param>
/// <param name="BackgroundDataRestricted">Whether Windows is holding back background data.</param>
public sealed record AdapterCost(
    NetworkCost Kind,
    bool Roaming,
    bool OverDataLimit,
    bool ApproachingDataLimit,
    bool BackgroundDataRestricted)
{
    /// <summary>Whether Windows treats the network as metered.</summary>
    public bool IsMetered => Kind is NetworkCost.Fixed or NetworkCost.Variable || Roaming || OverDataLimit;
}

/// <summary>One of an adapter's addresses.</summary>
/// <param name="Address">The address.</param>
/// <param name="PrefixLength">How many leading bits name the network it is on.</param>
/// <param name="Scope">What kind of address it is.</param>
/// <param name="Preferred">Whether it is ready for use, rather than still being checked or on its way out.</param>
/// <param name="Temporary">Whether it is one of IPv6's rotating privacy addresses.</param>
/// <param name="FromDhcp">Whether a DHCP server leased it.</param>
public sealed record AdapterAddress(
    IPAddress Address,
    int PrefixLength,
    AddressScope Scope,
    bool Preferred,
    bool Temporary,
    bool FromDhcp)
{
    /// <summary>The address and its prefix, as "192.168.0.10/24".</summary>
    public override string ToString() => string.Concat(Address.ToString(), "/", PrefixLength.ToString(System.Globalization.CultureInfo.InvariantCulture));
}

/// <summary>A DNS server an adapter uses.</summary>
/// <param name="Address">Its address.</param>
/// <param name="Origin">Where it came from.</param>
/// <param name="Encryption">What Windows could do about encrypting lookups to it.</param>
/// <param name="Template">The DNS-over-HTTPS address Windows would use for it, when it knows one.</param>
public sealed record DnsServer(IPAddress Address, DnsServerOrigin Origin, DnsEncryption Encryption, string? Template);

/// <summary>Everything about one network adapter that can be read without sending a packet.</summary>
/// <param name="Id">Windows' identifier for the adapter, a GUID in braces.</param>
/// <param name="Luid">The adapter's locally unique id, as History's adapter choice stores it.</param>
/// <param name="Index">Its IPv4 interface index, or 0.</param>
/// <param name="Name">The name people see, such as "Wi-Fi".</param>
/// <param name="Description">The driver's description, such as "Intel(R) Wi-Fi 6E AX211".</param>
/// <param name="Kind">What kind of link it is.</param>
/// <param name="InterfaceType">The IANA interface type Windows reports.</param>
/// <param name="TunnelType">The tunnel type Windows reports, or 0 for none.</param>
/// <param name="State">Whether it is carrying traffic.</param>
/// <param name="HardwareAddress">Its MAC address as "AA-BB-CC-DD-EE-FF", or nothing for one without.</param>
/// <param name="Mtu">The largest packet it sends, in bytes, or nothing when not known.</param>
/// <param name="TransmitBitsPerSecond">The link speed sending, or nothing when not known.</param>
/// <param name="ReceiveBitsPerSecond">The link speed receiving, or nothing when not known.</param>
/// <param name="Addresses">Its own addresses.</param>
/// <param name="Gateways">The routers it sends to for anything off its own network.</param>
/// <param name="DnsServers">The DNS servers it asks.</param>
/// <param name="DnsSuffix">The connection's own DNS suffix, or empty.</param>
/// <param name="DhcpEnabled">Whether it takes its IPv4 address from DHCP.</param>
/// <param name="DhcpServer">The DHCP server that leased the address, when there is one.</param>
/// <param name="Ipv4Metric">The cost Windows gives IPv4 routes through it; the lowest carries traffic.</param>
/// <param name="Ipv6Metric">The same for IPv6.</param>
/// <param name="RouteOrder">Where Windows lists it: the order follows the route metrics, so 0 is preferred.</param>
public sealed record AdapterDetails(
    string Id,
    ulong Luid,
    uint Index,
    string Name,
    string Description,
    AdapterKind Kind,
    uint InterfaceType,
    uint TunnelType,
    AdapterState State,
    string? HardwareAddress,
    uint? Mtu,
    ulong? TransmitBitsPerSecond,
    ulong? ReceiveBitsPerSecond,
    IReadOnlyList<AdapterAddress> Addresses,
    IReadOnlyList<IPAddress> Gateways,
    IReadOnlyList<DnsServer> DnsServers,
    string DnsSuffix,
    bool DhcpEnabled,
    IPAddress? DhcpServer,
    uint Ipv4Metric,
    uint Ipv6Metric,
    int RouteOrder)
{
    /// <summary>What Windows says this adapter's network costs, when it says.</summary>
    public AdapterCost? Cost { get; init; }

    /// <summary>The Windows network profile's name for the network on it ("Home", "Network 3"), when known.</summary>
    public string? NetworkName { get; init; }

    /// <summary>Whether it is up.</summary>
    public bool IsUp => State == AdapterState.Up;

    /// <summary>Whether it is a VPN.</summary>
    public bool IsVpn => Kind == AdapterKind.Vpn;

    /// <summary>Whether it has a router to reach other networks by.</summary>
    public bool HasGateway => Gateways.Count > 0;

    /// <summary>
    /// Whether it is worth a row of its own on a page about the connection:
    /// up, or a real link that is down, but not loopback, Windows' own IPv6
    /// tunnels, or a virtual switch with nothing behind it.
    /// </summary>
    public bool IsWorthShowing => Kind switch
    {
        AdapterKind.Loopback or AdapterKind.Transition => false,
        AdapterKind.Virtual or AdapterKind.Bluetooth or AdapterKind.Other => IsUp && Addresses.Any(each => each.Scope is not AddressScope.LinkLocal),
        _ => State != AdapterState.NotPresent,
    };

    /// <summary>The adapter's IPv4 addresses.</summary>
    public IEnumerable<AdapterAddress> FourAddresses =>
        Addresses.Where(each => each.Address.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork);

    /// <summary>The adapter's IPv6 addresses.</summary>
    public IEnumerable<AdapterAddress> SixAddresses =>
        Addresses.Where(each => each.Address.AddressFamily == System.Net.Sockets.AddressFamily.InterNetworkV6);

    /// <summary>Whether it holds an IPv6 address the internet can route to.</summary>
    public bool HasPublicSix => SixAddresses.Any(each => each.Scope == AddressScope.Public && each.Preferred);

    /// <summary>Whether it has a default IPv6 router.</summary>
    public bool HasSixGateway => Gateways.Any(each => each.AddressFamily == System.Net.Sockets.AddressFamily.InterNetworkV6);

    /// <summary>Whether it has a default IPv4 router.</summary>
    public bool HasFourGateway => Gateways.Any(each => each.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork);
}

/// <summary>Every adapter, read at one moment.</summary>
/// <param name="At">When it was read.</param>
/// <param name="Adapters">Every adapter, in the order Windows prefers them.</param>
public sealed record AdapterSnapshot(DateTimeOffset At, IReadOnlyList<AdapterDetails> Adapters)
{
    /// <summary>Nothing read yet.</summary>
    public static AdapterSnapshot Empty { get; } = new(DateTimeOffset.MinValue, []);

    /// <summary>
    /// The adapter internet traffic leaves by: the first adapter that is up and
    /// has an IPv4 router, in the order Windows ranks routes. A VPN that has
    /// taken the default route comes first, as it should.
    /// </summary>
    public AdapterDetails? Carrier =>
        Adapters.Where(each => each.IsUp && each.HasFourGateway).OrderBy(each => each.RouteOrder).FirstOrDefault()
        ?? Adapters.Where(each => each.IsUp && each.HasGateway).OrderBy(each => each.RouteOrder).FirstOrDefault();

    /// <summary>
    /// The physical link under everything: the first adapter that is up, has a
    /// router, and is neither a VPN nor virtual.
    /// </summary>
    public AdapterDetails? Physical =>
        Adapters.Where(each => each.IsUp && each.HasGateway && each.Kind is AdapterKind.Ethernet or AdapterKind.WiFi or AdapterKind.Cellular or AdapterKind.Other)
            .OrderBy(each => each.RouteOrder)
            .FirstOrDefault();

    /// <summary>The VPN adapters that are up.</summary>
    public IEnumerable<AdapterDetails> Vpns => Adapters.Where(each => each.IsUp && each.IsVpn);

    /// <summary>Whether internet traffic goes through a VPN.</summary>
    public bool TrafficTunnelled => Carrier?.IsVpn == true;

    /// <summary>The adapters worth showing, preferred first.</summary>
    public IEnumerable<AdapterDetails> Shown => Adapters.Where(each => each.IsWorthShowing);

    /// <summary>The adapter with this id, or nothing.</summary>
    public AdapterDetails? Find(string? id) =>
        id is null ? null : Adapters.FirstOrDefault(each => string.Equals(each.Id, id, StringComparison.OrdinalIgnoreCase));
}
