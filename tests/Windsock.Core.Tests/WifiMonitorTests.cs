using System.Buffers.Binary;
using Microsoft.Extensions.Logging.Abstractions;
using Windsock.Core.Wifi;
using Xunit;

namespace Windsock.Core.Tests;

/// <summary>The Wi-Fi monitor's choices, crowding and suggestions, the drop log, and the signal history.</summary>
public sealed class WifiMonitorTests
{
    private static readonly DateTimeOffset Noon = new(2026, 10, 5, 12, 0, 0, TimeSpan.FromHours(6));

    private static readonly int[] NonOverlapping = [1, 6, 11];

    [Fact]
    public void Monitor_ReadsRealtimeQualityWithoutAskingForTheName()
    {
        FakeWlan wlan = new() { Realtime = Realtime(-64, 5180, 80) };
        using WifiMonitor monitor = new(wlan, NullLogger<WifiMonitor>.Instance, TimeProvider.System);

        WifiReading reading = monitor.ReadOnce();

        Assert.Equal(WifiState.Connected, reading.State);
        Assert.Equal(-64, reading.Rssi);
        Assert.Equal(3, reading.Bars);
        Assert.Equal(36, reading.Channel);
        Assert.Equal(WifiBand.Five, reading.Band);
        Assert.Equal("Wi-Fi 6", reading.Generation);
        Assert.Equal(NameAccess.NotAsked, reading.Name);
        Assert.Null(reading.NetworkName);
        Assert.DoesNotContain(WlanClient.CurrentConnectionOpcode, wlan.Asked);
        Assert.Equal(1, monitor.History.Count);
    }

    [Fact]
    public void Monitor_AsksForTheNameOnlyWhileAWatcherWantsIt()
    {
        FakeWlan wlan = new() { Realtime = Realtime(-58, 5180, 80), Association = Association("Home") };
        using WifiMonitor monitor = new(wlan, NullLogger<WifiMonitor>.Instance, TimeProvider.System);

        IDisposable named = monitor.Watch(withName: true);
        WifiReading reading = monitor.ReadOnce();

        Assert.Equal(NameAccess.Allowed, reading.Name);
        Assert.Equal("Home", reading.NetworkName);

        named.Dispose();
        Assert.Null(monitor.ReadOnce().NetworkName);
    }

    [Fact]
    public void Monitor_LocationDenied_IsReportedAndTheFiguresStay()
    {
        FakeWlan wlan = new() { Realtime = Realtime(-70, 2437, 20), AssociationStatus = WlanStatus.Denied };
        using WifiMonitor monitor = new(wlan, NullLogger<WifiMonitor>.Instance, TimeProvider.System);

        using IDisposable named = monitor.Watch(withName: true);
        WifiReading reading = monitor.ReadOnce();

        Assert.Equal(NameAccess.Denied, reading.Name);
        Assert.Equal(-70, reading.Rssi);
        Assert.Equal(WifiBand.TwoPointFour, reading.Band);
    }

    [Fact]
    public void Monitor_OlderWindows_FallsBackToTheCurrentConnection()
    {
        FakeWlan wlan = new() { RealtimeStatus = WlanStatus.NotSupported, Association = Association("Home") };
        using WifiMonitor monitor = new(wlan, NullLogger<WifiMonitor>.Instance, TimeProvider.System, windowsBuild: 22631);

        WifiReading reading = monitor.ReadOnce();

        Assert.True(reading.Fallback);
        Assert.Equal(WifiPhy.Vht, reading.Phy);
        Assert.Equal(-60, reading.Rssi);
        Assert.Null(reading.NetworkName);
        Assert.Equal(400, reading.ReceiveMbps);

        // Three refusals in a row, and it stops asking for the realtime opcode.
        monitor.ReadOnce();
        monitor.ReadOnce();
        int asked = wlan.Asked.Count(each => each == WlanClient.RealtimeQualityOpcode);
        monitor.ReadOnce();
        Assert.Equal(asked, wlan.Asked.Count(each => each == WlanClient.RealtimeQualityOpcode));
    }

    [Fact]
    public void Monitor_From24H2_NeverReadsTheGatedOpcodeWithoutANameWatcher()
    {
        FakeWlan wlan = new() { RealtimeStatus = WlanStatus.NotSupported, Association = Association("Home") };
        using WifiMonitor monitor = new(wlan, NullLogger<WifiMonitor>.Instance, TimeProvider.System, windowsBuild: WifiMonitor.LocationRuleBuild);
        using IDisposable taskbar = monitor.Watch();

        for (int each = 0; each < 6; each++)
        {
            WifiReading reading = monitor.ReadOnce();
            Assert.Null(reading.Rssi);
            Assert.False(reading.Fallback);
        }

        Assert.DoesNotContain(WlanClient.CurrentConnectionOpcode, wlan.Asked);
        Assert.Equal(6, wlan.Asked.Count(each => each == WlanClient.RealtimeQualityOpcode));
    }

    [Fact]
    public void Monitor_ANameWatcherLeavingMidRead_TakesTheNameWithIt()
    {
        FakeWlan wlan = new() { Realtime = Realtime(-58, 5180, 80), Association = Association("Home") };
        using WifiMonitor monitor = new(wlan, NullLogger<WifiMonitor>.Instance, TimeProvider.System, windowsBuild: WifiMonitor.LocationRuleBuild);
        IDisposable named = monitor.Watch(withName: true);

        // The last name watcher goes while Windows is being asked for the name.
        wlan.OnQuery = opcode =>
        {
            if (opcode == WlanClient.CurrentConnectionOpcode)
            {
                named.Dispose();
            }
        };

        WifiReading reading = monitor.ReadOnce();

        Assert.Null(reading.NetworkName);
        Assert.Null(reading.Association);
        Assert.Equal(NameAccess.NotAsked, reading.Name);
        Assert.Null(monitor.ReadOnce().Association);
    }

    [Fact]
    public void Monitor_Disconnected_HasNoFigures()
    {
        FakeWlan wlan = new() { State = WlanInterface.Disconnected };
        using WifiMonitor monitor = new(wlan, NullLogger<WifiMonitor>.Instance, TimeProvider.System);

        WifiReading reading = monitor.ReadOnce();

        Assert.Equal(WifiState.Disconnected, reading.State);
        Assert.Null(reading.Rssi);
        Assert.Null(reading.Bars);
    }

    [Fact]
    public void Monitor_NoService_SaysSo()
    {
        FakeWlan wlan = new() { InterfacesStatus = WlanStatus.NoService };
        using WifiMonitor monitor = new(wlan, NullLogger<WifiMonitor>.Instance, TimeProvider.System);

        Assert.Equal(WifiState.ServiceOff, monitor.ReadOnce().State);
    }

    [Fact]
    public void SignalHistory_LinesUpWithTheThroughputSamples()
    {
        SignalPoint[] readings =
        [
            new(Noon, -60, null, null, WifiBand.Five),
            new(Noon.AddSeconds(1), -62, null, null, WifiBand.Five),
            new(Noon.AddSeconds(2), null, null, null, WifiBand.Unknown),
            new(Noon.AddSeconds(10), -70, null, null, WifiBand.Five),
        ];

        DateTimeOffset[] moments =
        [
            Noon.AddMilliseconds(-200),
            Noon.AddMilliseconds(500),
            Noon.AddMilliseconds(1100),
            Noon.AddSeconds(5),
            Noon.AddSeconds(10.4),
        ];

        int?[] aligned = SignalHistory.Align(moments, readings, TimeSpan.FromSeconds(1.5));

        Assert.Equal(new int?[] { -60, -60, -62, null, -70 }, aligned);
    }

    [Fact]
    public void SignalHistory_KeepsTheNewest()
    {
        SignalHistory history = new(3);

        for (int each = 0; each < 5; each++)
        {
            history.Add(new SignalPoint(Noon.AddSeconds(each), -50 - each, null, null, WifiBand.Five));
        }

        Assert.Equal(new int?[] { -52, -53, -54 }, history.Since(DateTimeOffset.MinValue).Select(each => each.Rssi).ToArray());
    }

    [Fact]
    public void Crowding_CountsOverlapAndSuggestsTheQuietestOfOneSixAndEleven()
    {
        NearbyNetwork[] networks =
        [
            Network(2412, -50),
            Network(2417, -60),
            Network(2437, -80),
            Network(2462, -45),
            Network(2462, -50, ssid: "Mine"),
        ];

        IReadOnlyList<ChannelLoad> loads = ChannelCrowding.Loads(networks, WifiBand.TwoPointFour, own: each => each.Name == "Mine");

        Assert.Equal(NonOverlapping, loads.Select(each => each.Channel));
        Assert.Equal(2, loads[0].Networks);
        Assert.Equal(1, loads[1].Networks);
        Assert.Equal(1, loads[2].Networks);

        ChannelSuggestion suggestion = ChannelCrowding.Suggest(loads, currentChannel: 11)!;

        Assert.Equal(6, suggestion.Channel);
        Assert.True(suggestion.IsBetter);
    }

    [Fact]
    public void Crowding_FiveGigahertz_PrefersChannelsWithoutRadarWhenEquallyQuiet()
    {
        NearbyNetwork[] networks = [Network(5180, -50, width: 80, centre: 42)];

        IReadOnlyList<ChannelLoad> loads = ChannelCrowding.Loads(networks, WifiBand.Five, widthMhz: 80);
        ChannelSuggestion suggestion = ChannelCrowding.Suggest(loads)!;

        Assert.True(loads.Single(each => each.Channel == 44).Networks == 1);
        Assert.Equal(0, loads.Single(each => each.Channel == 52).Networks);
        Assert.False(ChannelCrowding.IsDfs(WifiBand.Five, suggestion.Channel));
        Assert.Equal(149, suggestion.Channel);
    }

    [Fact]
    public void Scan_BuildsLoadsAndSuggestionsForBandsHeard()
    {
        WifiScan scan = WifiScanner.Build([Network(2437, -60), Network(5745, -70)], null, null, 20, Noon, fresh: true);

        Assert.Equal(2, scan.Loads.Count);
        Assert.Equal(2, scan.Suggestions.Count);
        Assert.Equal(-60, scan.Networks[0].Rssi);
    }

    [Fact]
    public void Events_AreReadByNameFromTheirXml()
    {
        WifiEvent disconnected = WifiEvents.Parse(EventXml(8003, "2026-10-04T09:19:21.0330348Z",
            ("InterfaceGuid", "{4b0a0962-1dc4-4864-8a1c-21cd6bcd1853}"),
            ("ProfileName", "Home"),
            ("SSID", "Home"),
            ("Reason", "The network is disconnected because the driver disconnected."),
            ("ReasonCode", "0x3800B")))!;

        Assert.Equal(WifiEventKind.Disconnected, disconnected.Kind);
        Assert.Equal(new DateTimeOffset(2026, 10, 4, 9, 19, 21, TimeSpan.Zero).AddTicks(330348), disconnected.At);
        Assert.Equal(0x3800B, disconnected.ReasonCode);
        Assert.Equal("Home", disconnected.Network);
        Assert.False(disconnected.SeemsDeliberate);

        WifiEvent failed = WifiEvents.Parse(EventXml(8002, "2026-10-04T09:20:00Z", ("FailureReason", "PSK mismatch suspected"), ("ReasonCode", "295060"), ("RSSI", "-71")))!;

        Assert.Equal(WifiEventKind.ConnectFailed, failed.Kind);
        Assert.Equal("PSK mismatch suspected", failed.Reason);
        Assert.Equal(-71, failed.Rssi);

        Assert.Null(WifiEvents.Parse(EventXml(11000, "2026-10-04T09:20:00Z")));
        Assert.Null(WifiEvents.Parse("<not"));
    }

    [Fact]
    public void Drops_PairEachDisconnectWithTheNextConnect_AndHideNamesWhenAsked()
    {
        Guid adapter = Guid.NewGuid();
        WifiEvent[] events =
        [
            new(Noon, WifiEventKind.Disconnected, adapter, "Home", "The driver disconnected.", 11, null, null),
            new(Noon.AddSeconds(40), WifiEventKind.Connected, adapter, "Home", null, null, null, "802.11ax"),
            new(Noon.AddMinutes(30), WifiEventKind.Disconnected, adapter, "Home", "The driver disconnected.", 0x3800B, null, null),
        ];

        IReadOnlyList<WifiDrop> drops = WifiEvents.Drops(events, withNames: false);

        Assert.Equal(2, drops.Count);
        Assert.Null(drops[0].BackAt);
        Assert.False(drops[0].Deliberate);
        Assert.Equal(TimeSpan.FromSeconds(40), drops[1].Duration);
        Assert.True(drops[1].Deliberate);
        Assert.All(drops, drop => Assert.Null(drop.Network));
    }

    [Fact]
    public void Query_AsksForTheThreeEventsInTheWindow()
    {
        Assert.Equal(
            "*[System[(EventID=8001 or EventID=8002 or EventID=8003) and TimeCreated[timediff(@SystemTime) <= 3600000]]]",
            WifiEvents.Query(TimeSpan.FromHours(1)));
    }

    private static string EventXml(int id, string time, params (string Name, string Value)[] data) =>
        string.Concat(
            "<Event xmlns='http://schemas.microsoft.com/win/2004/08/events/event'><System><Provider Name='Microsoft-Windows-WLAN-AutoConfig'/>",
            $"<EventID>{id}</EventID><TimeCreated SystemTime='{time}'/></System><EventData>",
            string.Concat(data.Select(each => $"<Data Name='{each.Name}'>{each.Value}</Data>")),
            "</EventData></Event>");

    private static NearbyNetwork Network(double megahertz, int rssi, string ssid = "Other", int width = 20, int centre = 0) => new(
        System.Text.Encoding.UTF8.GetBytes(ssid),
        "00:11:22:33:44:55",
        rssi,
        80,
        megahertz,
        WifiPhy.He,
        WifiPhy.He,
        width,
        centre,
        null,
        null);

    private static byte[] Realtime(int rssi, uint megahertz, uint width)
    {
        byte[] buffer = new byte[WifiDecoders.RealtimeHeader + WifiDecoders.RealtimeLink];
        BinaryPrimitives.WriteUInt32LittleEndian(buffer, 10);
        BinaryPrimitives.WriteUInt32LittleEndian(buffer.AsSpan(4), 80);
        BinaryPrimitives.WriteUInt32LittleEndian(buffer.AsSpan(8), 600_000);
        BinaryPrimitives.WriteUInt32LittleEndian(buffer.AsSpan(12), 400_000);
        BinaryPrimitives.WriteUInt32LittleEndian(buffer.AsSpan(20), 1);
        BinaryPrimitives.WriteUInt32LittleEndian(buffer.AsSpan(24 + 4), megahertz);
        BinaryPrimitives.WriteUInt32LittleEndian(buffer.AsSpan(24 + 8), width);
        BinaryPrimitives.WriteInt32LittleEndian(buffer.AsSpan(24 + 12), rssi);
        return buffer;
    }

    private static byte[] Association(string ssid)
    {
        byte[] buffer = new byte[WifiDecoders.ConnectionAttributes];
        System.Text.Encoding.Unicode.GetBytes(ssid).CopyTo(buffer, 8);
        byte[] name = System.Text.Encoding.UTF8.GetBytes(ssid);
        BinaryPrimitives.WriteUInt32LittleEndian(buffer.AsSpan(520), (uint)name.Length);
        name.CopyTo(buffer, 524);
        BinaryPrimitives.WriteUInt32LittleEndian(buffer.AsSpan(520 + 48), 8);
        BinaryPrimitives.WriteUInt32LittleEndian(buffer.AsSpan(520 + 56), 80);
        BinaryPrimitives.WriteUInt32LittleEndian(buffer.AsSpan(520 + 60), 400_000);
        BinaryPrimitives.WriteUInt32LittleEndian(buffer.AsSpan(520 + 64), 300_000);
        return buffer;
    }

    private sealed class FakeWlan : IWlanClient
    {
        public WlanStatus InterfacesStatus { get; init; } = WlanStatus.Ok;

        public uint State { get; init; } = WlanInterface.Connected;

        public byte[]? Realtime { get; init; }

        public WlanStatus RealtimeStatus { get; init; } = WlanStatus.Ok;

        public byte[]? Association { get; init; }

        public WlanStatus AssociationStatus { get; init; } = WlanStatus.Ok;

        public List<uint> Asked { get; } = [];

        public Action<uint>? OnQuery { get; set; }

        public (WlanStatus Status, IReadOnlyList<WlanInterface> Interfaces) Interfaces() =>
            (InterfacesStatus, InterfacesStatus == WlanStatus.Ok ? [new WlanInterface(Guid.Empty, "Test adapter", State)] : []);

        public (WlanStatus Status, byte[]? Data) Query(Guid adapter, uint opcode)
        {
            Asked.Add(opcode);
            OnQuery?.Invoke(opcode);

            return opcode == WlanClient.RealtimeQualityOpcode
                ? (RealtimeStatus, RealtimeStatus == WlanStatus.Ok ? Realtime : null)
                : (AssociationStatus, AssociationStatus == WlanStatus.Ok ? Association : null);
        }

        public WlanStatus Scan(Guid adapter) => WlanStatus.Ok;

        public (WlanStatus Status, byte[]? Data) NetworkList(Guid adapter) => (WlanStatus.Ok, null);

        public string? ReasonText(uint code) => null;

#if !STORE
        public (WlanStatus Status, string? Xml) Profile(Guid adapter, string name) => (WlanStatus.Denied, null);
#endif
    }
}
