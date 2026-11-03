using System.Buffers.Binary;
using System.Net;
using Windsock.Core.Adapters;
using Windsock.Core.Connectivity;
using Windsock.Core.Lookups;
using Windsock.Core.Networking;
using Xunit;

namespace Windsock.Core.Tests;

/// <summary>Adapter classification, address scopes, DNS settings, IPv6 and carrier-grade NAT verdicts.</summary>
public sealed class AdapterDetailsTests
{
    [Theory]
    [InlineData("192.168.0.10", AddressScope.Private)]
    [InlineData("10.20.30.40", AddressScope.Private)]
    [InlineData("172.16.0.1", AddressScope.Private)]
    [InlineData("172.32.0.1", AddressScope.Public)]
    [InlineData("100.64.0.1", AddressScope.SharedCarrier)]
    [InlineData("100.127.255.254", AddressScope.SharedCarrier)]
    [InlineData("100.128.0.1", AddressScope.Public)]
    [InlineData("100.63.255.255", AddressScope.Public)]
    [InlineData("169.254.3.4", AddressScope.LinkLocal)]
    [InlineData("127.0.0.1", AddressScope.Loopback)]
    [InlineData("0.0.0.0", AddressScope.Unspecified)]
    [InlineData("224.0.0.251", AddressScope.Multicast)]
    [InlineData("192.0.2.5", AddressScope.Reserved)]
    [InlineData("198.18.0.1", AddressScope.Reserved)]
    [InlineData("203.0.113.9", AddressScope.Reserved)]
    [InlineData("255.255.255.255", AddressScope.Reserved)]
    [InlineData("1.1.1.1", AddressScope.Public)]
    [InlineData("::1", AddressScope.Loopback)]
    [InlineData("::", AddressScope.Unspecified)]
    [InlineData("fe80::1", AddressScope.LinkLocal)]
    [InlineData("fec0:0:0:ffff::1", AddressScope.SiteLocal)]
    [InlineData("fd12:3456::1", AddressScope.UniqueLocal)]
    [InlineData("ff02::1", AddressScope.Multicast)]
    [InlineData("2001:db8::1", AddressScope.Reserved)]
    [InlineData("2606:4700:4700::1111", AddressScope.Public)]
    [InlineData("::ffff:100.64.1.1", AddressScope.SharedCarrier)]
    public void Scopes_AreTold(string address, AddressScope scope)
    {
        Assert.Equal(scope, AddressScopes.Of(IPAddress.Parse(address)));
    }

    [Theory]
    [InlineData(71u, 0u, "Intel(R) Wi-Fi 6E AX211 160MHz", "Wi-Fi", AdapterKind.WiFi)]
    [InlineData(6u, 0u, "Intel(R) Ethernet Controller (3) I225-V", "Ethernet", AdapterKind.Ethernet)]
    [InlineData(6u, 0u, "WireGuard Tunnel", "wg0", AdapterKind.Vpn)]
    [InlineData(53u, 0u, "TAP-Windows Adapter V9", "OpenVPN", AdapterKind.Vpn)]
    [InlineData(131u, 14u, "Teredo Tunneling Pseudo-Interface", "Teredo", AdapterKind.Transition)]
    [InlineData(131u, 0u, "WAN Miniport (IKEv2)", "Work VPN", AdapterKind.Vpn)]
    [InlineData(6u, 0u, "Hyper-V Virtual Ethernet Adapter", "vEthernet (WSL)", AdapterKind.Virtual)]
    [InlineData(6u, 0u, "Bluetooth Device (Personal Area Network)", "Bluetooth Network Connection", AdapterKind.Bluetooth)]
    [InlineData(24u, 0u, "Software Loopback Interface 1", "Loopback Pseudo-Interface 1", AdapterKind.Loopback)]
    [InlineData(243u, 0u, "Qualcomm Snapdragon X55", "Cellular", AdapterKind.Cellular)]
    [InlineData(71u, 0u, "Microsoft Wi-Fi Direct Virtual Adapter #2", "Local Area Connection* 2", AdapterKind.Virtual)]
    public void Kinds_AreTold(uint type, uint tunnel, string description, string name, AdapterKind kind)
    {
        Assert.Equal(kind, AdapterClassifier.Classify(type, tunnel, description, name));
    }

    [Fact]
    public void Speeds_AndMtu_TreatWindowsUnknownsAsUnknown()
    {
        Assert.Null(AdapterClassifier.Speed(ulong.MaxValue));
        Assert.Null(AdapterClassifier.Speed(0));
        Assert.Equal(1_000_000_000ul, AdapterClassifier.Speed(1_000_000_000));
        Assert.Null(AdapterClassifier.Mtu(uint.MaxValue));
        Assert.Equal(1500u, AdapterClassifier.Mtu(1500));
        Assert.Equal("00-1A-2B-3C-4D-5E", AdapterClassifier.HardwareAddress([0x00, 0x1A, 0x2B, 0x3C, 0x4D, 0x5E]));
        Assert.Null(AdapterClassifier.HardwareAddress([0, 0, 0, 0, 0, 0]));
    }

    [Fact]
    public void SocketAddress_ReadsBothFamilies()
    {
        byte[] four = new byte[16];
        BinaryPrimitives.WriteUInt16LittleEndian(four, SocketAddressReader.InterNetwork);
        four[2] = 0x00;
        four[3] = 0x35;
        new byte[] { 192, 168, 0, 1 }.CopyTo(four, 4);

        Assert.Equal(IPAddress.Parse("192.168.0.1"), SocketAddressReader.Read(four));

        byte[] six = new byte[28];
        BinaryPrimitives.WriteUInt16LittleEndian(six, SocketAddressReader.InterNetworkV6);
        IPAddress.Parse("fe80::1c2b:3a4d").GetAddressBytes().CopyTo(six, 8);
        BinaryPrimitives.WriteUInt32LittleEndian(six.AsSpan(24), 12);

        IPAddress read = SocketAddressReader.Read(six)!;
        Assert.Equal(IPAddress.Parse("fe80::1c2b:3a4d%12"), read);
        Assert.Equal(12, read.ScopeId);

        Assert.Null(SocketAddressReader.Read([17, 0, 0, 0]));
    }

    [Fact]
    public void DnsLists_ReadEitherSeparator()
    {
        Assert.Equal(
            new[] { IPAddress.Parse("1.1.1.1"), IPAddress.Parse("8.8.8.8"), IPAddress.Parse("2606:4700:4700::1111") },
            DnsSettings.ParseList("1.1.1.1,8.8.8.8 2606:4700:4700::1111"));
        Assert.Empty(DnsSettings.ParseList("   "));
        Assert.False(DnsSettings.IsReal(IPAddress.Parse("fec0:0:0:ffff::1")));
        Assert.True(DnsSettings.IsReal(IPAddress.Parse("192.168.0.1")));
    }

    [Fact]
    public void DnsServers_AreDescribed()
    {
        IPAddress router = IPAddress.Parse("192.168.0.1");
        IPAddress cloudflare = IPAddress.Parse("1.1.1.1");
        IPAddress google = IPAddress.Parse("8.8.8.8");

        DnsSettings settings = new(
            new HashSet<IPAddress> { cloudflare, google },
            new Dictionary<IPAddress, string> { [cloudflare] = "https://cloudflare-dns.com/dns-query", [google] = "https://dns.google/dns-query" },
            new HashSet<IPAddress> { cloudflare });

        Assert.Equal(new DnsServer(router, DnsServerOrigin.Automatic, DnsEncryption.NotAvailable, null), settings.Describe(router, true));
        Assert.Equal(DnsEncryption.Configured, settings.Describe(cloudflare, true).Encryption);
        Assert.Equal(DnsServerOrigin.Manual, settings.Describe(google, true).Origin);
        Assert.Equal(DnsEncryption.Available, settings.Describe(google, true).Encryption);
        Assert.Equal(DnsEncryption.Unknown, settings.Describe(google, false).Encryption);
    }

    [Fact]
    public void Carrier_IsTheFirstUpAdapterWithARouter_AndAVpnWinsWhenItHasTheRoute()
    {
        AdapterDetails wifi = Adapter("Wi-Fi", AdapterKind.WiFi, order: 1, gateway: "192.168.0.1", address: "192.168.0.20");
        AdapterDetails vpn = Adapter("WireGuard", AdapterKind.Vpn, order: 0, gateway: "10.6.0.1", address: "10.6.0.2");
        AdapterDetails down = Adapter("Ethernet", AdapterKind.Ethernet, order: 2, gateway: "192.168.1.1", address: "192.168.1.5") with { State = AdapterState.Down };

        AdapterSnapshot snapshot = new(DateTimeOffset.Now, [wifi, vpn, down]);

        Assert.Same(vpn, snapshot.Carrier);
        Assert.Same(wifi, snapshot.Physical);
        Assert.True(snapshot.TrafficTunnelled);
        Assert.Single(snapshot.Vpns);
    }

    [Fact]
    public void Ipv6_IsWorkingOnlyWhenWindowsSaysSo()
    {
        AdapterDetails withSix = Adapter("Ethernet", AdapterKind.Ethernet, 0, "192.168.0.1", "192.168.0.20") with
        {
            Addresses =
            [
                new AdapterAddress(IPAddress.Parse("192.168.0.20"), 24, AddressScope.Private, true, false, true),
                new AdapterAddress(IPAddress.Parse("2a02:1234::20"), 64, AddressScope.Public, true, false, false),
            ],
            Gateways = [IPAddress.Parse("192.168.0.1"), IPAddress.Parse("fe80::1")],
        };

        AdapterSnapshot configured = new(DateTimeOffset.Now, [withSix]);
        AdapterSnapshot fourOnly = new(DateTimeOffset.Now, [Adapter("Ethernet", AdapterKind.Ethernet, 0, "192.168.0.1", "192.168.0.20")]);

        Assert.Equal(Ipv6State.Working, Ipv6Judge.Judge(new WindowsConnectivity(0x440), configured));
        Assert.Equal(Ipv6State.ConfiguredNotConfirmed, Ipv6Judge.Judge(new WindowsConnectivity(0x42), configured));

        // The development machine's reading: IPv4 internet, IPv6 no traffic.
        Assert.Equal(Ipv6State.NotAvailable, Ipv6Judge.Judge(new WindowsConnectivity(0x42), fourOnly));
        Assert.Equal(Ipv6State.Unknown, Ipv6Judge.Judge(new WindowsConnectivity(null), AdapterSnapshot.Empty));
    }

    [Fact]
    public void CarrierNat_ShowsFromTheAdaptersAddress()
    {
        AdapterSnapshot hotspot = new(DateTimeOffset.Now, [Adapter("Wi-Fi", AdapterKind.WiFi, 0, "100.64.0.1", "100.72.10.20")]);

        CarrierNatVerdict verdict = CarrierNatJudge.Judge(hotspot);

        Assert.Equal(CarrierNatState.Yes, verdict.State);
        Assert.Equal(IPAddress.Parse("100.72.10.20"), verdict.Evidence);
    }

    [Fact]
    public void CarrierNat_FromTheRoutersOutsideAddressAndThePublicOne()
    {
        AdapterSnapshot home = new(DateTimeOffset.Now, [Adapter("Ethernet", AdapterKind.Ethernet, 0, "192.168.0.1", "192.168.0.20")]);

        Assert.Equal(CarrierNatState.Unknown, CarrierNatJudge.Judge(home).State);
        Assert.Equal(CarrierNatState.Yes, CarrierNatJudge.Judge(home, IPAddress.Parse("100.100.1.1")).State);
        Assert.Equal(CarrierNatState.DoubleNat, CarrierNatJudge.Judge(home, IPAddress.Parse("10.0.0.5")).State);
        Assert.Equal(CarrierNatState.No, CarrierNatJudge.Judge(home, IPAddress.Parse("81.2.69.160")).State);
        Assert.Equal(CarrierNatState.Upstream, CarrierNatJudge.Judge(home, IPAddress.Parse("81.2.69.160"), IPAddress.Parse("81.2.69.200")).State);
    }

    [Fact]
    public void CarrierNat_ATraceHopInTheSharedRange_IsLikely()
    {
        AdapterSnapshot home = new(DateTimeOffset.Now, [Adapter("Ethernet", AdapterKind.Ethernet, 0, "192.168.0.1", "192.168.0.20")]);
        PathHop[] route =
        [
            new(1, "192.168.0.1", TimeSpan.FromMilliseconds(1), false),
            new(2, "100.65.0.1", TimeSpan.FromMilliseconds(6), false),
            new(3, "203.0.113.1", TimeSpan.FromMilliseconds(9), false),
        ];

        Assert.Equal(CarrierNatState.Likely, CarrierNatJudge.Judge(home, route: route).State);
    }

    [Fact]
    public void SupportText_NamesTheCarrierAndLeavesMacsOutByDefault()
    {
        AdapterDetails wifi = Adapter("Wi-Fi", AdapterKind.WiFi, 0, "192.168.0.1", "192.168.0.20") with
        {
            HardwareAddress = "00-1A-2B-3C-4D-5E",
            DnsServers = [new DnsServer(IPAddress.Parse("192.168.0.1"), DnsServerOrigin.Automatic, DnsEncryption.NotAvailable, null)],
            Cost = new AdapterCost(NetworkCost.Fixed, false, false, false, false),
        };

        AdapterReading reading = new(new AdapterSnapshot(new DateTimeOffset(2026, 10, 5, 9, 30, 0, TimeSpan.FromHours(6)), [wifi]), new WindowsConnectivity(0x42), Ipv6State.NotAvailable);

        string text = NetworkSupportText.Write(new SupportFacts(reading));

        Assert.Contains("Wi-Fi (carries internet traffic)", text, StringComparison.Ordinal);
        Assert.Contains("IPv4: 192.168.0.20/24 (DHCP)", text, StringComparison.Ordinal);
        Assert.Contains("Gateway: 192.168.0.1", text, StringComparison.Ordinal);
        Assert.Contains("DNS: 192.168.0.1 (automatic, plain)", text, StringComparison.Ordinal);
        Assert.Contains("Metered: yes (Fixed)", text, StringComparison.Ordinal);
        Assert.DoesNotContain("00-1A-2B", text, StringComparison.Ordinal);

        Assert.Contains("MAC: 00-1A-2B-3C-4D-5E", NetworkSupportText.Write(new SupportFacts(reading), new SupportTextOptions(HardwareAddresses: true)), StringComparison.Ordinal);
    }

    [Fact]
    public void Cost_IsMeteredForFixedVariableOrRoaming()
    {
        Assert.False(new AdapterCost(NetworkCost.Unrestricted, false, false, false, false).IsMetered);
        Assert.True(new AdapterCost(NetworkCost.Fixed, false, false, false, false).IsMetered);
        Assert.True(new AdapterCost(NetworkCost.Variable, false, false, false, false).IsMetered);
        Assert.True(new AdapterCost(NetworkCost.Unrestricted, true, false, false, false).IsMetered);
    }

#if !STORE
    [Fact]
    public void FixCommands_NeverPutTypedTextOnACommandLine()
    {
        AdapterDetails wifi = Adapter("Wi-Fi", AdapterKind.WiFi, 0, "192.168.0.1", "192.168.0.20");

        Assert.Equal("/flushdns", NetworkFixCommands.For(NetworkFix.FlushDns)!.Arguments);
        Assert.Equal("/renew \"Wi-Fi\"", NetworkFixCommands.For(NetworkFix.RenewAddress, wifi)!.Arguments);
        Assert.Contains("-InterfaceIndex 7 ", NetworkFixCommands.For(NetworkFix.ResetAdapter, wifi)!.Arguments, StringComparison.Ordinal);
        Assert.Null(NetworkFixCommands.For(NetworkFix.RenewAddress, wifi with { Name = "Evil\" & calc" }));
        Assert.Null(NetworkFixCommands.For(NetworkFix.ResetAdapter, wifi with { Index = 0 }));
        Assert.True(NetworkFixCommands.For(NetworkFix.ResetAdapter, wifi)!.DropsConnection);
    }
#endif

    internal static AdapterDetails Adapter(string name, AdapterKind kind, int order, string gateway, string address) => new(
        "{" + Guid.NewGuid().ToString() + "}",
        (ulong)(order + 1),
        7,
        name,
        name + " adapter",
        kind,
        kind == AdapterKind.WiFi ? 71u : 6u,
        0,
        AdapterState.Up,
        null,
        1500,
        866_000_000,
        866_000_000,
        [new AdapterAddress(IPAddress.Parse(address), 24, AddressScopes.Of(IPAddress.Parse(address)), true, false, true)],
        [IPAddress.Parse(gateway)],
        [],
        string.Empty,
        true,
        IPAddress.Parse(gateway),
        25,
        25,
        order);
}
