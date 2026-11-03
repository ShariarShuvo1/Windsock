using System.Net;
using Windsock.Core.Home;
using Xunit;

namespace Windsock.Core.Tests;

public sealed class RouterTests
{
    private static readonly IPAddress Gateway = IPAddress.Parse("192.168.0.1");

    private static HomeLink Link => new("net", "Home", "{A}", 7, IPAddress.Parse("192.168.0.103"), 24, Gateway);

    /// <summary>The answer the development machine's TP-Link TL-WR840N gave to a unicast search.</summary>
    private const string SearchAnswer =
        "HTTP/1.1 200 OK\r\nCACHE-CONTROL: max-age=300\r\nEXT:\r\nLOCATION: http://192.168.0.1:1900/gatedesc.xml\r\n"
        + "SERVER: Linux/2.6.36, UPnP/1.0, Portable SDK for UPnP devices/1.6.19\r\n"
        + "ST: urn:schemas-upnp-org:device:InternetGatewayDevice:1\r\n\r\n";

    private const string Description = """
        <?xml version="1.0"?>
        <root xmlns="urn:schemas-upnp-org:device-1-0">
          <specVersion><major>1</major><minor>0</minor></specVersion>
          <URLBase></URLBase>
          <device>
            <deviceType>urn:schemas-upnp-org:device:InternetGatewayDevice:1</deviceType>
            <friendlyName>Wireless N Router TL-WR840N</friendlyName>
            <manufacturer>TP-Link</manufacturer>
            <modelName>TL-WR840N</modelName>
            <modelNumber>6.0</modelNumber>
            <serviceList>
              <service>
                <serviceType>urn:schemas-upnp-org:service:Layer3Forwarding:1</serviceType>
                <controlURL>/upnp/control/dummy</controlURL>
              </service>
            </serviceList>
            <deviceList>
              <device>
                <deviceType>urn:schemas-upnp-org:device:WANDevice:1</deviceType>
                <serviceList>
                  <service>
                    <serviceType>urn:schemas-upnp-org:service:WANCommonInterfaceConfig:1</serviceType>
                    <controlURL>/upnp/control/WANCommonIFC1</controlURL>
                  </service>
                </serviceList>
                <deviceList>
                  <device>
                    <serviceList>
                      <service>
                        <serviceType>urn:schemas-upnp-org:service:WANIPConnection:1</serviceType>
                        <controlURL>/upnp/control/WANIPConn1</controlURL>
                      </service>
                      <service>
                        <serviceType>urn:schemas-upnp-org:service:Evil:1</serviceType>
                        <controlURL>http://192.168.0.66/steal</controlURL>
                      </service>
                    </serviceList>
                  </device>
                </deviceList>
              </device>
            </deviceList>
          </device>
        </root>
        """;

    [Fact]
    public void SearchAnswer_FromTheRouter_GivesItsDescription()
    {
        Assert.Equal(new Uri("http://192.168.0.1:1900/gatedesc.xml"), UpnpWire.Location(SearchAnswer, Gateway));
    }

    /// <summary>An answer pointing anywhere but the router - another host, loopback, a name, HTTPS - is ignored.</summary>
    [Theory]
    [InlineData("http://192.168.0.66:1900/gatedesc.xml")]
    [InlineData("http://127.0.0.1:8080/")]
    [InlineData("http://router.lan:1900/gatedesc.xml")]
    [InlineData("https://192.168.0.1/gatedesc.xml")]
    [InlineData("http://user@192.168.0.1/gatedesc.xml")]
    [InlineData("file:///C:/Windows/win.ini")]
    public void SearchAnswer_PointingElsewhere_IsIgnored(string location)
    {
        Assert.Null(UpnpWire.Location(SearchAnswer.Replace("http://192.168.0.1:1900/gatedesc.xml", location, StringComparison.Ordinal), Gateway));
    }

    [Fact]
    public void SearchAnswer_ThatIsNotASuccess_IsIgnored()
    {
        Assert.Null(UpnpWire.Location(SearchAnswer.Replace("200 OK", "404 Not Found", StringComparison.Ordinal), Gateway));
    }

    [Fact]
    public void Description_NamesTheRouterAndItsServices()
    {
        GatewayDescription? description = UpnpWire.Describe(Description, new Uri("http://192.168.0.1:1900/gatedesc.xml"), Gateway);

        Assert.NotNull(description);
        Assert.Equal("TP-Link", description.Manufacturer);
        Assert.Equal("TL-WR840N", description.ModelName);
        Assert.Equal(new Uri("http://192.168.0.1:1900/upnp/control/WANIPConn1"), description.Find("WANIPConnection")!.ControlUrl);
        Assert.Equal(new Uri("http://192.168.0.1:1900/upnp/control/WANCommonIFC1"), description.Find("WANCommonInterfaceConfig")!.ControlUrl);
        Assert.Null(description.Find("Evil"));
        Assert.Equal("TP-Link TL-WR840N", RouterReport.Describe(description.Manufacturer, description.ModelName, description.FriendlyName));
    }

    [Fact]
    public void Description_WithADtd_IsRefused()
    {
        const string Hostile = """
            <?xml version="1.0"?>
            <!DOCTYPE root [<!ENTITY lol "lol">]>
            <root xmlns="urn:schemas-upnp-org:device-1-0"><device><manufacturer>&lol;</manufacturer></device></root>
            """;

        Assert.Null(UpnpWire.Describe(Hostile, new Uri("http://192.168.0.1/"), Gateway));
    }

    [Fact]
    public void Envelope_IsTheSpecsShape()
    {
        string envelope = UpnpWire.Envelope(
            "urn:schemas-upnp-org:service:WANIPConnection:1",
            "GetGenericPortMappingEntry",
            [("NewPortMappingIndex", "0")]);

        Assert.Contains("<u:GetGenericPortMappingEntry xmlns:u=\"urn:schemas-upnp-org:service:WANIPConnection:1\"><NewPortMappingIndex>0</NewPortMappingIndex></u:GetGenericPortMappingEntry>", envelope, StringComparison.Ordinal);
        Assert.Equal("\"urn:schemas-upnp-org:service:WANIPConnection:1#GetExternalIPAddress\"", UpnpWire.ActionHeader("urn:schemas-upnp-org:service:WANIPConnection:1", "GetExternalIPAddress"));
    }

    [Fact]
    public void Answers_ReadValuesAndFaults()
    {
        SoapAnswer ok = UpnpWire.Answer(
            """
            <s:Envelope xmlns:s="http://schemas.xmlsoap.org/soap/envelope/"><s:Body>
            <u:GetTotalBytesReceivedResponse xmlns:u="urn:schemas-upnp-org:service:WANCommonInterfaceConfig:1">
            <NewTotalBytesReceived>2524692590</NewTotalBytesReceived></u:GetTotalBytesReceivedResponse></s:Body></s:Envelope>
            """,
            "GetTotalBytesReceived");

        Assert.True(ok.Succeeded);
        Assert.Equal("2524692590", ok["NewTotalBytesReceived"]);

        SoapAnswer fault = UpnpWire.Answer(Fault(713, "SpecifiedArrayIndexInvalid"), "GetGenericPortMappingEntry");
        Assert.False(fault.Succeeded);
        Assert.Equal(713, fault.ErrorCode);
        Assert.Equal("SpecifiedArrayIndexInvalid", fault.ErrorDescription);

        Assert.Same(SoapAnswer.Garbled, UpnpWire.Answer("not xml", "GetStatusInfo"));
    }

    /// <summary>Two mappings, then 713 ends the list as complete, as the development machine's router did.</summary>
    [Fact]
    public async Task Check_ReadsMappingsUntilTheListEnds()
    {
        FakeRouter router = new(stopWith: 713);
        RouterReport report = await RouterCheck.RunAsync(router, Link, DateTimeOffset.UnixEpoch, TestContext.Current.CancellationToken);

        Assert.Equal(RouterAnswer.Answered, report.Answer);
        Assert.Equal("TP-Link TL-WR840N", report.MakeAndModel);
        Assert.Equal("100.70.1.2", report.ExternalAddress);
        Assert.Equal(ExternalAddressKind.Shared, report.ExternalKind);
        Assert.True(report.CountersOffered);
        Assert.True(report.MappingsComplete);
        Assert.Equal(2, report.PortMappings.Count);
        Assert.Equal(new PortMapping("UDP", 3074, string.Empty, "192.168.0.50", 3074, true, "Xbox", 0), report.PortMappings[0]);
        Assert.All(router.Actions, action => Assert.StartsWith("Get", action, StringComparison.Ordinal));
    }

    /// <summary>A router that refuses to list (606) leaves the list marked incomplete.</summary>
    [Fact]
    public async Task Check_MarksARefusedListIncomplete()
    {
        RouterReport report = await RouterCheck.RunAsync(new FakeRouter(stopWith: 606), Link, DateTimeOffset.UnixEpoch, TestContext.Current.CancellationToken);

        Assert.False(report.MappingsComplete);
    }

    [Fact]
    public async Task Check_WithoutUpnp_SaysSo()
    {
        RouterReport report = await RouterCheck.RunAsync(new FakeRouter(stopWith: 713) { Silent = true }, Link, DateTimeOffset.UnixEpoch, TestContext.Current.CancellationToken);

        Assert.Equal(RouterAnswer.NoUpnp, report.Answer);
        Assert.Equal(RouterAnswer.NotHome, (await RouterCheck.RunAsync(new FakeRouter(713), null, DateTimeOffset.UnixEpoch, TestContext.Current.CancellationToken)).Answer);
    }

    [Fact]
    public async Task Client_RefusesAnythingButAGet()
    {
        using RouterClient client = new();
        GatewayService service = new("urn:schemas-upnp-org:service:WANIPConnection:1", new Uri("http://192.168.0.1/ctl"));

        await Assert.ThrowsAsync<ArgumentException>(() => client.CallAsync(service, "AddPortMapping", null, TestContext.Current.CancellationToken));
    }

    [Fact]
    public void Report_RoundTripsThroughJson()
    {
        RouterReport report = new(
            DateTimeOffset.Parse("2026-10-05T10:00:00+06:00", System.Globalization.CultureInfo.InvariantCulture),
            RouterAnswer.Answered,
            "192.168.0.1",
            "TP-Link",
            "TL-WR840N",
            null,
            null,
            "10.20.30.40",
            ExternalAddressKind.Private,
            "Connected",
            true,
            [new PortMapping("TCP", 8080, string.Empty, "192.168.0.10", 80, true, "Web", 3600)],
            true);

        RouterReport? back = RouterReport.FromJson(report.ToJson());

        Assert.NotNull(back);
        Assert.Equal(report.PortMappings, back.PortMappings);
        Assert.Equal(report with { PortMappings = back.PortMappings }, back);
        Assert.Null(RouterReport.FromJson("{nope"));
    }

    [Theory]
    [InlineData("192.168.1.2", ExternalAddressKind.Private)]
    [InlineData("10.0.0.1", ExternalAddressKind.Private)]
    [InlineData("172.20.0.1", ExternalAddressKind.Private)]
    [InlineData("100.64.0.1", ExternalAddressKind.Shared)]
    [InlineData("100.127.255.254", ExternalAddressKind.Shared)]
    [InlineData("103.4.5.6", ExternalAddressKind.Public)]
    [InlineData("0.0.0.0", ExternalAddressKind.Unknown)]
    [InlineData(null, ExternalAddressKind.Unknown)]
    public void ExternalAddresses_AreClassed(string? address, ExternalAddressKind expected) =>
        Assert.Equal(expected, RouterReport.Classify(address));

    /// <summary>The v1.01 template declares the counters ui4: they wrap at 2^32.</summary>
    [Fact]
    public void Counters_WrapAt32Bits()
    {
        TimeSpan five = TimeSpan.FromSeconds(5);

        Assert.Equal(1000ul, CounterDelta.Between(2_524_692_590, 2_524_693_590, five));
        Assert.Equal(1000ul + 296ul, CounterDelta.Between(uint.MaxValue - 295, 1000, five));
    }

    /// <summary>A fall a wrap cannot explain - the router restarted - drops the reading rather than inventing gigabytes.</summary>
    [Fact]
    public void Counters_ThatReset_AreDropped()
    {
        TimeSpan five = TimeSpan.FromSeconds(5);

        Assert.Null(CounterDelta.Between(2_524_692_590, 1_000_000, five));
        Assert.Null(CounterDelta.Between(10_000_000_000, 5, five));
        Assert.Equal(10ul, CounterDelta.Between(10_000_000_000, 10_000_000_010, five));
        Assert.Equal(2_524_692_590ul, CounterDelta.Parse("2524692590"));
        Assert.Null(CounterDelta.Parse("-1"));
        Assert.Null(CounterDelta.Parse(null));
    }

    private static string Fault(int code, string description) =>
        $"""
        <s:Envelope xmlns:s="http://schemas.xmlsoap.org/soap/envelope/"><s:Body><s:Fault>
        <faultcode>s:Client</faultcode><faultstring>UPnPError</faultstring><detail>
        <UPnPError xmlns="urn:schemas-upnp-org:control-1-0"><errorCode>{code}</errorCode>
        <errorDescription>{description}</errorDescription></UPnPError></detail></s:Fault></s:Body></s:Envelope>
        """;

    private sealed class FakeRouter(int stopWith) : IRouterClient
    {
        public bool Silent { get; init; }

        public List<string> Actions { get; } = [];

        public Task<GatewayDescription?> DescribeAsync(HomeLink link, CancellationToken cancellationToken) =>
            Task.FromResult(Silent ? null : UpnpWire.Describe(Description, new Uri("http://192.168.0.1:1900/gatedesc.xml"), Gateway));

        public Task<SoapAnswer> CallAsync(GatewayService service, string action, IReadOnlyList<(string Name, string Value)>? arguments, CancellationToken cancellationToken)
        {
            Actions.Add(action);

            return Task.FromResult(action switch
            {
                "GetExternalIPAddress" => Values(("NewExternalIPAddress", "100.70.1.2")),
                "GetStatusInfo" => Values(("NewConnectionStatus", "Connected"), ("NewUptime", "1791150986")),
                "GetTotalBytesReceived" => Values(("NewTotalBytesReceived", "2524692590")),
                "GetGenericPortMappingEntry" => arguments![0].Value switch
                {
                    "0" => Values(("NewRemoteHost", ""), ("NewExternalPort", "3074"), ("NewProtocol", "UDP"), ("NewInternalPort", "3074"), ("NewInternalClient", "192.168.0.50"), ("NewEnabled", "1"), ("NewPortMappingDescription", "Xbox"), ("NewLeaseDuration", "0")),
                    "1" => Values(("NewRemoteHost", ""), ("NewExternalPort", "51413"), ("NewProtocol", "TCP"), ("NewInternalPort", "51413"), ("NewInternalClient", "192.168.0.103"), ("NewEnabled", "1"), ("NewPortMappingDescription", "Transmission"), ("NewLeaseDuration", "3600")),
                    _ => new SoapAnswer(null, stopWith, "Stop"),
                },
                _ => SoapAnswer.Garbled,
            });
        }

        private static SoapAnswer Values(params (string Name, string Value)[] values) =>
            new(values.ToDictionary(each => each.Name, each => each.Value), null, null);
    }

    /// <summary>
    /// The household counters are asked for only while the network and the
    /// router are the ones they were found on: a move to another network, a
    /// new router address or a new address of our own stops the asking before
    /// the next request, rather than sending SOAP every five seconds to an old
    /// gateway address that may now be somebody else's machine (found in review).
    /// </summary>
    [Fact]
    public void Household_AsksOnlyTheRouterItFound()
    {
        HomeLink home = new("net", "Home", "{A}", 7, IPAddress.Parse("192.168.0.103"), 24, IPAddress.Parse("192.168.0.1"));

        Assert.True(HouseholdTraffic.SameLink(home, home with { NetworkName = "Home, renamed", InterfaceIndex = 9 }));
        Assert.False(HouseholdTraffic.SameLink(home, null));
        Assert.False(HouseholdTraffic.SameLink(home, home with { NetworkKey = "cafe" }));
        Assert.False(HouseholdTraffic.SameLink(home, home with { Gateway = IPAddress.Parse("192.168.0.254") }));
        Assert.False(HouseholdTraffic.SameLink(home, home with { Gateway = null }));
        Assert.False(HouseholdTraffic.SameLink(home, home with { Address = IPAddress.Parse("192.168.0.104") }));
    }
}
