using System.Buffers.Binary;
using System.Net;
using System.Text;
using Windsock.Core.Adapters;
using Windsock.Core.Home;
using Xunit;

namespace Windsock.Core.Tests;

public sealed class HomeNetworkTests
{
    private static readonly MacAddress Router = Mac("50-C7-BF-01-02-03");
    private static readonly MacAddress Phone = Mac("DA-A1-19-00-00-01");
    private static readonly MacAddress Printer = Mac("00-11-22-33-44-55");

    private static HomeLink Link => new("net", "Home", "{A}", 7, IPAddress.Parse("192.168.0.103"), 24, IPAddress.Parse("192.168.0.1"));

    [Fact]
    public void NeighbourRows_KeepOnlyDevices()
    {
        Assert.True(NeighbourFilter.IsDevice(Row("192.168.0.1", Router, NeighbourState.Reachable)));
        Assert.True(NeighbourFilter.IsDevice(Row("192.168.0.20", Printer, NeighbourState.Stale)));
        Assert.True(NeighbourFilter.IsDevice(Row("fe80::1", Printer, NeighbourState.Delay)));

        Assert.False(NeighbourFilter.IsDevice(Row("192.168.0.255", MacAddress.Broadcast, NeighbourState.Permanent)));
        Assert.False(NeighbourFilter.IsDevice(Row("224.0.0.22", Mac("01-00-5E-00-00-16"), NeighbourState.Permanent)));
        Assert.False(NeighbourFilter.IsDevice(Row("192.168.0.30", Printer, NeighbourState.Unreachable)));
        Assert.False(NeighbourFilter.IsDevice(Row("192.168.0.30", Printer, NeighbourState.Incomplete)));
        Assert.False(NeighbourFilter.IsDevice(Row("169.254.169.254", Printer, NeighbourState.Reachable)));
        Assert.False(NeighbourFilter.IsDevice(Row("127.0.0.1", Printer, NeighbourState.Reachable)));
        Assert.False(NeighbourFilter.IsDevice(Row("ff02::fb", Printer, NeighbourState.Reachable)));
        Assert.False(NeighbourFilter.IsDevice(Row("192.168.0.30", new MacAddress(0), NeighbourState.Reachable)));
        Assert.False(NeighbourFilter.IsDevice(Row("192.168.0.30", Printer, NeighbourState.Reachable) with { IsUnreachable = true }));
    }

    /// <summary>One sighting per adapter, the IPv4 address on the link, a global IPv6 over a link-local one, the router marked.</summary>
    [Fact]
    public void Sightings_GatherEachDevicesAddresses()
    {
        Neighbour[] rows =
        [
            Row("192.168.0.1", Router, NeighbourState.Reachable),
            Row("192.168.0.20", Printer, NeighbourState.Stale),
            Row("fe80::20", Printer, NeighbourState.Stale),
            Row("2001:db8::20", Printer, NeighbourState.Stale),
            Row("192.168.0.40", Phone, NeighbourState.Reachable) with { InterfaceIndex = 9 },
        ];

        List<DeviceSighting> sightings = HomeDevices.Sightings(rows, [Link]);

        Assert.Equal(2, sightings.Count);
        DeviceSighting router = sightings.Single(each => each.Mac == Router);
        DeviceSighting printer = sightings.Single(each => each.Mac == Printer);
        Assert.True(router.IsRouter);
        Assert.False(printer.IsRouter);
        Assert.Equal(IPAddress.Parse("192.168.0.20"), printer.Address);
        Assert.Equal(IPAddress.Parse("2001:db8::20"), printer.Address6);
    }

    /// <summary>Private networks only: never a public one, never a workplace's.</summary>
    [Fact]
    public void OnlyPrivateNetworks_AreHome()
    {
        AdapterDetails wifi = Adapter("{A}", 7, "192.168.0.103", 24, "192.168.0.1", order: 1);
        AdapterDetails wired = Adapter("{B}", 8, "10.0.0.5", 8, "10.0.0.1", order: 0);

        IReadOnlyList<HomeLink> links = HomeScope.Links(
            [
                new ConnectedNetwork("home", "Home", NetworkCategory.Private, ["{a}"]),
                new ConnectedNetwork("cafe", "Cafe", NetworkCategory.Public, ["{B}"]),
            ],
            [wifi, wired]);

        HomeLink link = Assert.Single(links);
        Assert.Equal("home", link.NetworkKey);
        Assert.Equal(7u, link.InterfaceIndex);
        Assert.Equal(IPAddress.Parse("192.168.0.255"), link.Broadcast);
        Assert.True(link.Contains(IPAddress.Parse("192.168.0.77")));
        Assert.False(link.Contains(IPAddress.Parse("192.168.1.77")));

        IReadOnlyList<ConnectedNetwork> publicOnly = [new ConnectedNetwork("cafe", "Cafe", NetworkCategory.Public, ["{B}"])];
        Assert.Equal(HomeAbsence.PublicOnly, HomeScope.Absence(publicOnly, HomeScope.Links(publicOnly, [wired])));

        IReadOnlyList<ConnectedNetwork> work = [new ConnectedNetwork("work", "Corp", NetworkCategory.DomainAuthenticated, ["{B}"])];
        Assert.Equal(HomeAbsence.WorkOnly, HomeScope.Absence(work, HomeScope.Links(work, [wired])));
        Assert.Equal(HomeAbsence.NoNetwork, HomeScope.Absence([], []));
    }

    /// <summary>
    /// A VPN or tunnel adapter on a network Windows calls private is not home,
    /// however its network is set: sweeping it would send ARP, NetBIOS, PTR and
    /// UPnP across the tunnel to whoever is on the other end (found in review).
    /// </summary>
    [Theory]
    [InlineData(AdapterKind.Vpn, 6u, 0u)]
    [InlineData(AdapterKind.Ethernet, 6u, 14u)]
    [InlineData(AdapterKind.Other, 53u, 0u)]
    [InlineData(AdapterKind.Ethernet, 53u, 0u)]
    [InlineData(AdapterKind.Ethernet, 131u, 0u)]
    [InlineData(AdapterKind.Ethernet, 23u, 0u)]
    [InlineData(AdapterKind.Cellular, 243u, 0u)]
    [InlineData(AdapterKind.Virtual, 53u, 0u)]
    [InlineData(AdapterKind.Transition, 131u, 2u)]
    public void TunnelsAndVpns_AreNotHome(AdapterKind kind, uint interfaceType, uint tunnelType)
    {
        AdapterDetails tunnel = Adapter("{T}", 9, "10.8.0.6", 24, ["10.8.0.1"], 0, kind, interfaceType, tunnelType);
        IReadOnlyList<ConnectedNetwork> networks = [new ConnectedNetwork("vpn", "Office VPN", NetworkCategory.Private, ["{T}"])];

        IReadOnlyList<HomeLink> links = HomeScope.Links(networks, [tunnel]);

        Assert.Empty(links);
        Assert.Equal(HomeAbsence.NotLocal, HomeScope.Absence(networks, links));
        Assert.NotEmpty(HomeScope.Describe(HomeAbsence.NotLocal));
    }

    /// <summary>A Hyper-V external switch is the real LAN under another name, so it stays home.</summary>
    [Fact]
    public void AnExternalSwitch_IsHome()
    {
        AdapterDetails external = Adapter("{X}", 12, "192.168.1.40", 24, ["192.168.1.1"], 0, AdapterKind.Virtual, 6, 0);

        HomeLink link = Assert.Single(HomeScope.Links(
            [new ConnectedNetwork("home", "Home", NetworkCategory.Private, ["{X}"])],
            [external]));

        Assert.Equal(IPAddress.Parse("192.168.1.1"), link.Gateway);
    }

    /// <summary>
    /// The router must be on the link: no gateway at all (a mesh overlay such
    /// as ZeroTier set private), or one off the subnet (a routed tunnel), is not
    /// a home network; nor is an address in carrier NAT space (Tailscale).
    /// </summary>
    [Theory]
    [InlineData("10.147.20.5", 24, new string[0])]
    [InlineData("10.8.0.6", 24, new[] { "10.9.0.1" })]
    [InlineData("192.168.0.103", 24, new[] { "192.168.0.103" })]
    [InlineData("192.168.0.103", 24, new[] { "fe80::1" })]
    [InlineData("100.101.102.103", 24, new[] { "100.101.102.1" })]
    public void WithoutARouterOnTheLink_ThereIsNoHome(string address, int prefix, string[] gateways)
    {
        AdapterDetails adapter = Adapter("{A}", 7, address, prefix, gateways, 0, AdapterKind.Ethernet, 6, 0);
        IReadOnlyList<ConnectedNetwork> networks = [new ConnectedNetwork("net", "Network", NetworkCategory.Private, ["{A}"])];

        IReadOnlyList<HomeLink> links = HomeScope.Links(networks, [adapter]);

        Assert.Empty(links);
        Assert.Equal(HomeAbsence.NotLocal, HomeScope.Absence(networks, links));
    }

    /// <summary>
    /// With a full-tunnel VPN up, the Wi-Fi underneath is still home, and it is
    /// the primary link even though the VPN's routes come first; the gateway
    /// chosen is the one on the subnet, not whichever is listed first.
    /// </summary>
    [Fact]
    public void UnderAFullTunnel_TheLocalLinkIsPrimary()
    {
        AdapterDetails vpn = Adapter("{V}", 20, "10.8.0.6", 24, ["10.8.0.1"], 0, AdapterKind.Vpn, 53, 0);
        AdapterDetails wifi = Adapter("{W}", 7, "192.168.0.103", 24, ["10.0.0.1", "192.168.0.1"], 5, AdapterKind.WiFi, 71, 0);
        AdapterDetails wired = Adapter("{E}", 8, "192.168.5.20", 24, ["192.168.5.1"], 2, AdapterKind.Ethernet, 6, 0);

        IReadOnlyList<HomeLink> links = HomeScope.Links(
            [
                new ConnectedNetwork("vpn", "Office VPN", NetworkCategory.Private, ["{V}"]),
                new ConnectedNetwork("home", "Home", NetworkCategory.Private, ["{W}", "{E}"]),
            ],
            [vpn, wifi, wired]);

        Assert.Equal(["{E}", "{W}"], links.Select(each => each.AdapterId));
        Assert.Equal(IPAddress.Parse("192.168.0.1"), links[1].Gateway);
    }

    [Fact]
    public void Subnets_ListTheirHostsAndRefuseTheHuge()
    {
        IReadOnlyList<IPAddress> hosts = Subnets.Hosts(IPAddress.Parse("192.168.0.103"), 24);

        Assert.Equal(254, hosts.Count);
        Assert.Equal(IPAddress.Parse("192.168.0.1"), hosts[0]);
        Assert.Equal(IPAddress.Parse("192.168.0.254"), hosts[^1]);
        Assert.Equal(1022, Subnets.Hosts(IPAddress.Parse("10.1.2.3"), 22).Count);
        Assert.Empty(Subnets.Hosts(IPAddress.Parse("10.1.2.3"), 16));
        Assert.Equal(IPAddress.Parse("10.255.255.255"), Subnets.Broadcast(IPAddress.Parse("10.1.2.3"), 8));
    }

    /// <summary>Six bytes of 0xFF, then the address sixteen times: 102 bytes.</summary>
    [Fact]
    public void MagicPacket_IsTheAmdLayout()
    {
        byte[] packet = WakeOnLan.Packet(Printer);

        Assert.Equal(102, packet.Length);
        Assert.All(packet[..6], each => Assert.Equal(0xFF, each));

        for (int copy = 0; copy < 16; copy++)
        {
            Assert.Equal(new byte[] { 0x00, 0x11, 0x22, 0x33, 0x44, 0x55 }, packet[(6 + (copy * 6))..(12 + (copy * 6))]);
        }

        Assert.Equal(
            [new IPEndPoint(IPAddress.Parse("192.168.0.255"), 9), new IPEndPoint(IPAddress.Broadcast, 9)],
            WakeOnLan.Targets(Link));
    }

    /// <summary>RFC 1002 4.2.17: the name "*" first-level encoded is "CK" followed by thirty "A"s.</summary>
    [Fact]
    public void NetBiosRequest_IsTheNodeStatusQuery()
    {
        byte[] request = NetBiosStatus.Request(0x1234);

        Assert.Equal(50, request.Length);
        Assert.Equal(0x1234, BinaryPrimitives.ReadUInt16BigEndian(request));
        Assert.Equal(1, BinaryPrimitives.ReadUInt16BigEndian(request.AsSpan(4)));
        Assert.Equal(32, request[12]);
        Assert.Equal("CK" + new string('A', 30), Encoding.ASCII.GetString(request, 13, 32));
        Assert.Equal(0, request[45]);
        Assert.Equal(0x0021, BinaryPrimitives.ReadUInt16BigEndian(request.AsSpan(46)));
        Assert.Equal(0x0001, BinaryPrimitives.ReadUInt16BigEndian(request.AsSpan(48)));
    }

    /// <summary>The workstation name is the first unique name with suffix 0x00; group names are skipped.</summary>
    [Fact]
    public void NetBiosAnswer_GivesTheWorkstationName()
    {
        byte[] answer = NodeStatus(0x1234, ("WORKGROUP", 0x00, 0x8400), ("DESKTOP-ABC", 0x20, 0x0400), ("DESKTOP-ABC", 0x00, 0x0400));

        Assert.Equal("DESKTOP-ABC", NetBiosStatus.Parse(answer, 0x1234));
        Assert.Null(NetBiosStatus.Parse(answer, 0x9999));
        Assert.Null(NetBiosStatus.Parse(answer.AsSpan(0, 30), 0x1234));
        Assert.Null(NetBiosStatus.Parse(NodeStatus(1, ("WORKGROUP", 0x00, 0x8400)), 1));
    }

    [Fact]
    public void ReverseQuery_AsksForTheInAddrName()
    {
        byte[] request = ReversePointer.Request(7, IPAddress.Parse("192.168.0.20"));

        // Every flag clear, recursion desired included: the router answers from
        // what it holds and does not pass the question on to the provider.
        Assert.Equal(0x0000, BinaryPrimitives.ReadUInt16BigEndian(request.AsSpan(2)));
        Assert.Equal("20.0.168.192.in-addr.arpa", DnsWire.ReadName(request, 12));
        Assert.Equal(12, BinaryPrimitives.ReadUInt16BigEndian(request.AsSpan(request.Length - 4)));
    }

    /// <summary>An answer whose PTR target points back into the question (RFC 1035 4.1.4 compression).</summary>
    [Fact]
    public void ReverseAnswer_FollowsCompression()
    {
        byte[] question = ReversePointer.Request(7, IPAddress.Parse("192.168.0.20"));
        List<byte> answer = [.. question];
        answer[2] = 0x81;
        answer[3] = 0x80;
        answer[7] = 1;

        answer.AddRange([0xC0, 0x0C, 0x00, 0x0C, 0x00, 0x01, 0, 0, 0, 60]);
        byte[] target = [.. Label("Johns-iPhone"), .. Label("lan"), 0];
        answer.AddRange([0, (byte)target.Length]);
        answer.AddRange(target);

        Assert.Equal("Johns-iPhone", ReversePointer.Parse([.. answer], 7));

        answer[3] = 0x83;
        Assert.Null(ReversePointer.Parse([.. answer], 7));
    }

    [Fact]
    public void DnsNames_ThatLoopAreRefused()
    {
        byte[] looping = [0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0xC0, 12];
        Assert.Null(DnsWire.ReadName(looping, 12));
        Assert.Equal(-1, DnsWire.SkipName([3, (byte)'a', (byte)'b'], 0));
    }

    [Theory]
    [InlineData("Living Room TV._airplay._tcp.local", "_airplay._tcp.local", "Living-Room.local", "Living Room TV")]
    [InlineData("HP LaserJet M1132\\032MFP._ipp._tcp.local", "_ipp._tcp.local", null, "HP LaserJet M1132 MFP")]
    [InlineData("Chromecast-0f1e2d3c4b5a69788796a5b4c3d2e1f0._googlecast._tcp.local", "_googlecast._tcp.local", "0f1e2d3c-4b5a.local", null)]
    [InlineData("a1b2c3d4e5f6._device-info._tcp.local", "_device-info._tcp.local", "Office-NAS.local", "Office-NAS")]
    public void MulticastNames_PreferWhatAPersonWouldCallIt(string instance, string type, string? host, string? expected)
    {
        Assert.Equal(expected, MulticastNames.Choose(instance, type, host));
    }

    [Fact]
    public void DeviceNames_AreCleanedOfControlCharacters()
    {
        Assert.Equal("Printer", DeviceNames.Clean("  Prin\u0007ter\r\n"));
        Assert.Null(DeviceNames.Clean("\u0001\u0002"));
        Assert.Equal(63, DeviceNames.Clean(new string('x', 200))!.Length);
        Assert.Null(DeviceNames.FromHostName("20.0.168.192.in-addr.arpa."));
        Assert.Equal("NAS", DeviceNames.FromHostName("NAS.local."));
    }

    /// <summary>A browse's PTR, SRV and A answers make one service with its host and address.</summary>
    [Fact]
    public void BrowseAnswers_AreAssembled()
    {
        List<MulticastService> services = WindowsMulticastBrowser.Assemble(
            [
                new DnsAnswer("_services._dns-sd._udp.local", DnsAnswer.Pointer, "_ipp._tcp.local", null),
                new DnsAnswer("_ipp._tcp.local", DnsAnswer.Pointer, "Office Printer._ipp._tcp.local", null),
                new DnsAnswer("Office Printer._ipp._tcp.local", DnsAnswer.Service, "HP0A1B2C.local", null),
                new DnsAnswer("HP0A1B2C.local", DnsAnswer.AddressFour, string.Empty, IPAddress.Parse("192.168.0.20")),
                new DnsAnswer("_ipp._tcp.local", DnsAnswer.Pointer, "Office Printer._ipp._tcp.local", null),
            ],
            ["_ipp._tcp.local"]);

        MulticastService service = Assert.Single(services);
        Assert.Equal("Office Printer", service.DeviceName);
        Assert.Equal("HP0A1B2C.local", service.Host);
        Assert.Equal([IPAddress.Parse("192.168.0.20")], service.Addresses);
    }

    /// <summary>windns.h: DNS_RECORDW's Data union follows three DWORDs after two pointers and two WORDs.</summary>
    [Fact]
    public void DnsRecordOffsets_MatchWindns()
    {
        Assert.Equal(IntPtr.Size == 8 ? 16 : 8, DnsRecordReader.TypeOffset);
        Assert.Equal(IntPtr.Size == 8 ? 32 : 24, DnsRecordReader.DataOffset);
        Assert.Equal(IntPtr.Size == 8 ? 32 : 20, System.Runtime.InteropServices.Marshal.SizeOf<WindowsMulticastBrowser.BrowseRequest>());
    }

    private static Neighbour Row(string address, MacAddress mac, NeighbourState state) =>
        new(IPAddress.Parse(address), mac, 7, state, IsRouter: false, IsUnreachable: false);

    private static MacAddress Mac(string text)
    {
        Assert.True(MacAddress.TryParse(text, out MacAddress mac));
        return mac;
    }

    private static AdapterDetails Adapter(string id, uint index, string address, int prefix, string gateway, int order) =>
        Adapter(id, index, address, prefix, [gateway], order, AdapterKind.WiFi, 71, 0);

    private static AdapterDetails Adapter(
        string id,
        uint index,
        string address,
        int prefix,
        string[] gateways,
        int order,
        AdapterKind kind,
        uint interfaceType,
        uint tunnelType) => new(
        id,
        index,
        index,
        "Adapter",
        "Adapter",
        kind,
        interfaceType,
        tunnelType,
        AdapterState.Up,
        null,
        1500,
        null,
        null,
        [new AdapterAddress(IPAddress.Parse(address), prefix, AddressScope.Private, Preferred: true, Temporary: false, FromDhcp: true)],
        [.. gateways.Select(IPAddress.Parse)],
        [],
        string.Empty,
        true,
        null,
        25,
        25,
        order);

    private static byte[] NodeStatus(ushort id, params (string Name, byte Suffix, ushort Flags)[] names)
    {
        List<byte> packet = [];
        Span<byte> header = stackalloc byte[12];
        BinaryPrimitives.WriteUInt16BigEndian(header, id);
        BinaryPrimitives.WriteUInt16BigEndian(header[2..], 0x8400);
        BinaryPrimitives.WriteUInt16BigEndian(header[6..], 1);
        packet.AddRange(header.ToArray());
        packet.Add(32);
        packet.AddRange(Encoding.ASCII.GetBytes("CK" + new string('A', 30)));
        packet.Add(0);
        packet.AddRange([0x00, 0x21, 0x00, 0x01, 0, 0, 0, 0]);

        int length = 1 + (18 * names.Length) + 46;
        packet.AddRange([(byte)(length >> 8), (byte)length]);
        packet.Add((byte)names.Length);

        foreach ((string name, byte suffix, ushort flags) in names)
        {
            packet.AddRange(Encoding.ASCII.GetBytes(name.PadRight(15)));
            packet.Add(suffix);
            packet.AddRange([(byte)(flags >> 8), (byte)flags]);
        }

        packet.AddRange(new byte[46]);
        return [.. packet];
    }

    private static byte[] Label(string text) => [(byte)text.Length, .. Encoding.ASCII.GetBytes(text)];
}
