using System.Net;
using Windsock.Core.Connectivity;
using Windsock.Core.Feed;
using Windsock.Core.Hardware;
using Windsock.Core.Home;
using Windsock.Core.Networking;
using Windsock.Core.Notifications;
using Xunit;

namespace Windsock.Core.Tests;

public sealed class FeedSourceTests
{
    private static readonly DateTimeOffset Noon = new(2026, 10, 5, 12, 0, 0, TimeSpan.Zero);

    /// <summary>Before the first reading the feed says null, never zero.</summary>
    [Fact]
    public void NothingMeasured_IsNull()
    {
        FeedRates rates = FeedStatusSource.Rates(ThroughputSample.Empty);
        FeedSystem system = FeedStatusSource.System(SystemSnapshot.Empty);

        Assert.Null(rates.DownBytesPerSecond);
        Assert.Null(rates.At);
        Assert.Null(system.ProcessorPercent);
        Assert.Null(system.MemoryPercent);
        Assert.Null(system.GraphicsPercent);
    }

    [Fact]
    public void Connection_SaysItsStateInWords()
    {
        ConnectionSample sample = new(
            Noon,
            new ConnectionFacts(WindowsReach.LocalOnly, new NetworkIdentity("Home", NetworkKind.WiFi), "192.168.0.1", VpnActive: true),
            TimeSpan.FromMilliseconds(2.4),
            null,
            InternetChecked: false);

        FeedConnection offline = FeedStatusSource.Connection(new ConnectionState(ConnectionStatus.Offline, OutageCause.Provider, Noon), sample);

        Assert.Equal("offline", offline.Status);
        Assert.Equal("provider", offline.Cause);
        Assert.Equal("Home", offline.Network);
        Assert.Equal(2.4, offline.RouterMs!.Value, 3);
        Assert.Null(offline.InternetMs);
        Assert.True(offline.Vpn);

        Assert.Equal("checking", FeedStatusSource.Connection(ConnectionState.Checking, null).Status);
        Assert.Null(FeedStatusSource.Connection(ConnectionState.Checking, null).Vpn);
    }

    /// <summary>The new-device notice goes through the alert pipeline as a kind of its own, off until switched on.</summary>
    [Fact]
    public void NewDevices_AreAnAlertKind_OffByDefault()
    {
        Alerts.AlertKind? kind = Alerts.AlertCatalogue.Find(DeviceJoinedNotices.Topic);
        Settings.WindsockSettings settings = new();

        Assert.NotNull(kind);
        Assert.Contains(kind, Alerts.AlertCatalogue.Listed);
        Assert.False(kind.IsOn!(settings));

        kind.SetOn!(settings, true);
        Assert.True(settings.Home.NotifyNewDevices);
        Assert.False(settings.Home.FindDevices);
    }

    [Fact]
    public void NewDeviceNotice_NamesWhatIsKnown()
    {
        HomeDevice printer = new("home", new MacAddress(0x001122334455), Noon, Noon, IPAddress.Parse("192.168.0.20"), null, "HP0A1B2C", DeviceNameSource.Multicast, "Office printer", false)
        {
            Maker = "HP Inc.",
        };

        Notice notice = DeviceJoinedNotices.For(new DeviceJoined(printer, "Home"));

        Assert.Equal(DeviceJoinedNotices.Topic, notice.Topic);
        Assert.Equal("New device on Home", notice.Title);
        Assert.Equal("Office printer (HP Inc.) joined at 192.168.0.20.", notice.Message);
        Assert.Equal("device-00-11-22-33-44-55", notice.Tag);

        HomeDevice phone = new("home", new MacAddress(0xDAA119000001), Noon, Noon, null, null, null, DeviceNameSource.None, null, false);
        Assert.Equal("A device with a private address joined at DA-A1-19-00-00-01.", DeviceJoinedNotices.For(new DeviceJoined(phone, "Home")).Message);
    }
}
