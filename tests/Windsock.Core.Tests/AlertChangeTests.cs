using System.Net;
using System.Text.Json;
using Windsock.Core.Adapters;
using Windsock.Core.Alerts;
using Windsock.Core.Connectivity;
using Windsock.Core.Networking;
using Windsock.Core.Notifications;
using Windsock.Core.Plans;
using Windsock.Core.Settings;
using Xunit;

namespace Windsock.Core.Tests;

public sealed class NetworkPrintTests
{
    private static readonly DateTimeOffset Now = new(2026, 6, 10, 12, 0, 0, TimeSpan.Zero);
    private static readonly NetworkTag Home = new("guid-home", "Home", NetworkKind.WiFi);

    private static AdapterDetails Adapter(
        string id,
        AdapterKind kind,
        string? address,
        string[] dns,
        string? gateway,
        int order = 0) => new(
        id,
        1,
        7,
        id,
        id,
        kind,
        71,
        0,
        AdapterState.Up,
        "00-11-22-33-44-55",
        1500,
        null,
        null,
        address is null ? [] : [new AdapterAddress(IPAddress.Parse(address), 24, AddressScopes.Of(IPAddress.Parse(address)), true, false, true)],
        gateway is null ? [] : [IPAddress.Parse(gateway)],
        [.. dns.Select(each => new DnsServer(IPAddress.Parse(each), DnsServerOrigin.Automatic, DnsEncryption.Unknown, null))],
        string.Empty,
        true,
        null,
        25,
        25,
        order);

    [Fact]
    public void APrint_TakesTheCarriersAddressAndDns_AndThePhysicalRouter()
    {
        AdapterSnapshot snapshot = new(Now, [Adapter("wifi", AdapterKind.WiFi, "192.168.1.20", ["9.9.9.9", "1.1.1.1", "fec0:0:0:ffff::1"], "192.168.1.1")]);

        NetworkPrint print = NetworkPrint.From(snapshot, Home, (_, router) => router.ToString() == "192.168.1.1" ? "aa:bb:cc:dd:ee:ff" : null, Now)!;

        Assert.Equal("guid-home", print.Key);
        Assert.Equal("192.168.1.20", print.Address);
        Assert.Equal(["1.1.1.1", "9.9.9.9"], print.Dns);
        Assert.Equal("192.168.1.1", print.Gateway);
        Assert.Equal("AA-BB-CC-DD-EE-FF", print.RouterMac);
    }

    [Fact]
    public void ThroughAVpn_TheNetworkIsRememberedApart()
    {
        AdapterSnapshot snapshot = new(Now,
        [
            Adapter("vpn", AdapterKind.Vpn, "10.8.0.2", ["10.8.0.1"], "10.8.0.1", order: 0),
            Adapter("wifi", AdapterKind.WiFi, "192.168.1.20", ["192.168.1.1"], "192.168.1.1", order: 1),
        ]);

        NetworkPrint print = NetworkPrint.From(snapshot, Home, (adapter, _) => adapter.Id == "wifi" ? "AA-BB-CC-DD-EE-FF" : null, Now)!;

        Assert.Equal("guid-home|vpn", print.Key);
        Assert.Equal("10.8.0.2", print.Address);
        Assert.Equal(["10.8.0.1"], print.Dns);
        Assert.Equal("192.168.1.1", print.Gateway);
        Assert.Equal("AA-BB-CC-DD-EE-FF", print.RouterMac);
    }

    [Fact]
    public void NoNetwork_IsNoPrint()
    {
        AdapterSnapshot snapshot = new(Now, [Adapter("wifi", AdapterKind.WiFi, "192.168.1.20", [], "192.168.1.1")]);

        Assert.Null(NetworkPrint.From(snapshot, null, (_, _) => null, Now));
        Assert.Null(NetworkPrint.From(new AdapterSnapshot(Now, []), Home, (_, _) => null, Now));
    }

    [Theory]
    [InlineData("aa:bb:cc:dd:ee:ff", "AA-BB-CC-DD-EE-FF")]
    [InlineData("AA-BB-CC-DD-EE-FF", "AA-BB-CC-DD-EE-FF")]
    [InlineData("00-00-00-00-00-00", null)]
    [InlineData("ff-ff-ff-ff-ff-ff", null)]
    [InlineData("AA-BB-CC", null)]
    [InlineData("zz-bb-cc-dd-ee-ff", null)]
    [InlineData(null, null)]
    public void HardwareAddresses_AreWrittenOneWay(string? mac, string? expected) =>
        Assert.Equal(expected, NetworkPrint.Normalise(mac));

    private static NetworkPrint Print(string? address = "192.168.1.20", string[]? dns = null, string? mac = "AA-BB-CC-DD-EE-FF", string key = "guid-home") =>
        new(key, "Home", address, dns ?? ["1.1.1.1"], "192.168.1.1", mac);

    [Fact]
    public void OnlyIPv4DnsServers_AreTaken()
    {
        AdapterSnapshot snapshot = new(Now, [Adapter("wifi", AdapterKind.WiFi, "192.168.1.20", ["2001:db8:1::1", "192.168.1.1"], "192.168.1.1")]);

        NetworkPrint print = NetworkPrint.From(snapshot, Home, (_, _) => null, Now)!;

        Assert.Equal(["192.168.1.1"], print.Dns);
    }

    [Fact]
    public void ThroughAVpn_ANewAddressIsTheVpnReconnecting()
    {
        NetworkPrint was = Print(key: "guid-home|vpn", address: "10.8.0.2");
        NetworkPrint now = Print(key: "guid-home|vpn", address: "10.8.0.7");

        Assert.Empty(NetworkPrintJudge.Compare(was, now));
        Assert.True(NetworkPrintJudge.IsTunnelled(now.Key));
        Assert.Single(NetworkPrintJudge.Compare(was with { Key = "guid-home" }, now with { Key = "guid-home" }));
    }

    [Fact]
    public void ANetworkNotSeenBefore_OrADifferentOne_IsNoChange()
    {
        Assert.Empty(NetworkPrintJudge.Compare(null, Print()));
        Assert.Empty(NetworkPrintJudge.Compare(Print(key: "other"), Print(address: "10.0.0.5")));
    }

    [Fact]
    public void EachChange_IsSaid_WithWhatItWasAndIsNow()
    {
        IReadOnlyList<NetworkPrintDifference> changes = NetworkPrintJudge.Compare(
            Print(),
            Print(address: "192.168.1.77", dns: ["8.8.8.8"], mac: "11-22-33-44-55-66"));

        Assert.Equal([NetworkPrintChange.Address, NetworkPrintChange.Dns, NetworkPrintChange.Router], changes.Select(each => each.Change));
        Assert.Equal("192.168.1.20", changes[0].Was);
        Assert.Equal("8.8.8.8", changes[1].Now);
        Assert.Contains("11-22-33-44-55-66", changes[2].Now, StringComparison.Ordinal);
        Assert.All(changes, each => Assert.Equal("Home", each.Network));
    }

    [Fact]
    public void TheSameDnsServersInAnotherOrder_AreNoChange() =>
        Assert.Empty(NetworkPrintJudge.Compare(Print(dns: ["1.1.1.1", "9.9.9.9"]), Print(dns: ["9.9.9.9", "1.1.1.1"])));

    [Fact]
    public void OnlyTwoReadingsThatAgree_AreCompared()
    {
        PrintConfirmation confirmation = new();

        Assert.Null(confirmation.Offer(Print()));
        Assert.NotNull(confirmation.Offer(Print()));

        // Mid-move: the new network's name with the old router behind it, then settled.
        Assert.Null(confirmation.Offer(Print(mac: "11-22-33-44-55-66", key: "guid-cafe")));
        Assert.Null(confirmation.Offer(Print(mac: "99-88-77-66-55-44", key: "guid-cafe")));
        Assert.Equal("99-88-77-66-55-44", confirmation.Offer(Print(mac: "99-88-77-66-55-44", key: "guid-cafe"))?.RouterMac);

        confirmation.Reset();
        Assert.Null(confirmation.Offer(Print()));
        Assert.Null(confirmation.Offer(null));
    }

    [Fact]
    public void ARouterMissingFromTheNeighbourTable_IsNotANewRouter_AndStaysKnown()
    {
        NetworkPrint known = Print();
        NetworkPrint blind = Print(mac: null);

        Assert.Empty(NetworkPrintJudge.Compare(known, blind));
        Assert.Equal("AA-BB-CC-DD-EE-FF", NetworkPrintJudge.Remember(known, blind).RouterMac);
    }
}

public sealed class ProxyJudgeTests
{
    [Fact]
    public void TheAutomaticDetectionFlag_IsReadFromTheConnectionBlob()
    {
        // Version 0x46, change counter 12, flags 0x09 (direct, automatically detect).
        byte[] on = [0x46, 0, 0, 0, 12, 0, 0, 0, 0x09, 0, 0, 0, 0, 0, 0, 0];
        byte[] off = [0x46, 0, 0, 0, 13, 0, 0, 0, 0x01, 0, 0, 0, 0, 0, 0, 0];

        Assert.True(ProxyJudge.AutoDetect(on));
        Assert.False(ProxyJudge.AutoDetect(off));
        Assert.Null(ProxyJudge.AutoDetect([0x46, 0, 0]));
        Assert.Null(ProxyJudge.AutoDetect(null));
    }

    [Fact]
    public void OnlyWhatTakesEffect_IsAChange()
    {
        ProxyPrint none = ProxyPrint.None;

        Assert.False(ProxyJudge.Differ(none, none with { Server = "10.0.0.1:3128" }));
        Assert.True(ProxyJudge.Differ(none, none with { Manual = true, Server = "10.0.0.1:3128" }));
        Assert.True(ProxyJudge.Differ(none, none with { Script = "http://wpad/proxy.pac" }));
        Assert.True(ProxyJudge.Differ(none, none with { AutoDetect = true }));
        Assert.False(ProxyJudge.Differ(none with { Manual = true, Server = "A:1" }, none with { Manual = true, Server = " a:1 " }));
    }

    [Fact]
    public void TheProxy_IsSaidInWords()
    {
        Assert.Equal("no proxy", ProxyJudge.Describe(ProxyPrint.None));
        Assert.Equal("a proxy at 127.0.0.1:8080", ProxyJudge.Describe(ProxyPrint.None with { Manual = true, Server = "127.0.0.1:8080" }));
        Assert.Equal(
            "a proxy at p:1, the setup script at http://x/p.pac and automatic detection",
            ProxyJudge.Describe(new ProxyPrint(true, "p:1", string.Empty, "http://x/p.pac", true)));
    }

    [Theory]
    [InlineData("SystemSettings", true)]
    [InlineData("rundll32", true)]
    [InlineData("chrome", false)]
    [InlineData(null, false)]
    public void WindowsOwnSettings_AreKnown(string? process, bool expected) =>
        Assert.Equal(expected, ProxyJudge.IsSettingsApp(process));

    [Fact]
    public void ReadingTheRealSettings_NeverThrows() => Assert.NotNull(ProxyJudge.Read());
}

/// <summary>
/// The proxy watch against keys of the test's own, written as Windows' proxy
/// settings would be: the registry really signals, and nothing touches the
/// real proxy settings.
/// </summary>
public sealed class ProxyWatchTests : IDisposable
{
    private readonly string _root = @"Software\WindsockTests\" + Guid.NewGuid().ToString("N");

    public ProxyWatchTests()
    {
        using Microsoft.Win32.RegistryKey settings = Microsoft.Win32.Registry.CurrentUser.CreateSubKey(_root + @"\Internet Settings");
        using Microsoft.Win32.RegistryKey connections = Microsoft.Win32.Registry.CurrentUser.CreateSubKey(_root + @"\Internet Settings\Connections");
        settings.SetValue("ProxyEnable", 0, Microsoft.Win32.RegistryValueKind.DWord);
    }

    public void Dispose() => Microsoft.Win32.Registry.CurrentUser.DeleteSubKeyTree(_root, throwOnMissingSubKey: false);

    [Fact]
    public async Task AProxySwitchedOn_IsHeard_AndTheChangeSaysWhatItIs()
    {
        string settingsKey = _root + @"\Internet Settings";
        using ProxyWatch watch = new(Microsoft.Extensions.Logging.Abstractions.NullLogger<ProxyWatch>.Instance, settingsKey, settingsKey + @"\Connections");
        TaskCompletionSource<ProxyChange> heard = new(TaskCreationOptions.RunContinuationsAsynchronously);
        watch.Changed += (_, change) => heard.TrySetResult(change);
        watch.Start();

        Assert.True(watch.IsWatching);

        using (Microsoft.Win32.RegistryKey settings = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(settingsKey, writable: true)!)
        {
            settings.SetValue("ProxyServer", "127.0.0.1:8888");
            settings.SetValue("ProxyEnable", 1, Microsoft.Win32.RegistryValueKind.DWord);
        }

        Task finished = await Task.WhenAny(heard.Task, Task.Delay(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken));

        Assert.Same(heard.Task, finished);
        ProxyChange change = await heard.Task;
        Assert.False(change.Was.Effective.Manual);
        Assert.Equal("127.0.0.1:8888", change.Now.Effective.Server);
    }

    [Fact]
    public async Task AWriteThatChangesNothing_IsNotAChange()
    {
        string settingsKey = _root + @"\Internet Settings";
        using ProxyWatch watch = new(Microsoft.Extensions.Logging.Abstractions.NullLogger<ProxyWatch>.Instance, settingsKey, settingsKey + @"\Connections");
        int heard = 0;
        watch.Changed += (_, _) => Interlocked.Increment(ref heard);
        watch.Start();

        using (Microsoft.Win32.RegistryKey settings = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(settingsKey, writable: true)!)
        {
            // A server written while the proxy stays off takes no effect.
            settings.SetValue("ProxyServer", "10.0.0.1:3128");
            settings.SetValue("ProxyEnable", 0, Microsoft.Win32.RegistryValueKind.DWord);
        }

        await Task.Delay(TimeSpan.FromSeconds(3), TestContext.Current.CancellationToken);

        Assert.Equal(0, heard);
    }
}

public sealed class ForwardingTests
{
    [Theory]
    [InlineData("https://hooks.example.com/abc", null)]
    [InlineData("http://hooks.example.com/abc", "https")]
    [InlineData("http://192.168.1.10:8080/hook", null)]
    [InlineData("http://localhost:9000/hook", null)]
    [InlineData("http://homeassistant.local/api", null)]
    [InlineData("http://nas/hook", null)]
    [InlineData("ftp://example.com/x", "https://")]
    [InlineData("", "Enter")]
    public void AWebhookAddress_MustBeSafeToSendTo(string url, string? problem)
    {
        ForwardSettings forward = new() { To = ForwardKind.Webhook, WebhookUrl = url };

        string? said = ForwardTarget.Problem(forward);

        if (problem is null)
        {
            Assert.Null(said);
        }
        else
        {
            Assert.Contains(problem, said, StringComparison.Ordinal);
        }
    }

    [Theory]
    [InlineData("my-alerts_1", true)]
    [InlineData("", false)]
    [InlineData("has space", false)]
    [InlineData("slash/topic", false)]
    public void AnNtfyTopic_IsLettersDigitsHyphensAndUnderscores(string topic, bool valid) =>
        Assert.Equal(valid, ForwardTarget.IsTopic(topic));

    [Theory]
    [InlineData("192.168.1.10", true)]
    [InlineData("10.0.0.5", true)]
    [InlineData("127.0.0.1", true)]
    [InlineData("fd00::5", true)]
    [InlineData("fe80::1", true)]
    [InlineData("8.8.8.8", false)]
    [InlineData("2606:4700::1111", false)]
    [InlineData("100.64.0.1", false)]
    public void PlainHttp_GoesOnlyToThisPcOrTheLocalNetwork(string address, bool allowed) =>
        Assert.Equal(allowed, ForwardTarget.IsPrivate(IPAddress.Parse(address)));

    [Fact]
    public void ATopicsWholeAddress_IsSplit()
    {
        Assert.Equal(("https://ntfy.sh", "home-pc"), ForwardTarget.SplitNtfy("https://ntfy.sh/home-pc"));
        Assert.Equal(("https://push.example.org:8443", "alerts"), ForwardTarget.SplitNtfy("https://push.example.org:8443/alerts/"));
        Assert.Equal((ForwardSettings.PublicNtfy, "just-a-topic"), ForwardTarget.SplitNtfy("just-a-topic"));
    }

    [Fact]
    public void TheDestinationInWords_HoldsNoSecret()
    {
        ForwardSettings webhook = new() { To = ForwardKind.Webhook, WebhookUrl = "https://discord.com/api/webhooks/123/secret-token?wait=true" };
        ForwardSettings ntfy = new() { NtfyServer = "https://ntfy.sh", NtfyTopic = "secret-topic-name", NtfyToken = "tk_secret" };

        Assert.Equal("discord.com", ForwardTarget.Describe(webhook));
        Assert.Equal("ntfy.sh", ForwardTarget.Describe(ntfy));
        Assert.Equal(new Uri("https://ntfy.sh/"), ForwardTarget.Address(ntfy));
    }

    private static readonly AlertRecord Record = new(9, new DateTimeOffset(2026, 6, 10, 12, 0, 0, TimeSpan.Zero), NoticeTopics.UploadHigh, "Unusual upload", "2 MB/s for 10 minutes.");

    [Fact]
    public void AWebhook_GetsTheAlertAndNothingElse()
    {
        using JsonDocument body = JsonDocument.Parse(ForwardPayload.Webhook(Record, "Desk PC"));

        string[] fields = [.. body.RootElement.EnumerateObject().Select(each => each.Name).Order(StringComparer.Ordinal)];

        Assert.Equal(["at", "content", "kind", "level", "message", "name", "sender", "source", "text", "title"], fields);
        Assert.Equal("warning", body.RootElement.GetProperty("level").GetString());
        Assert.Equal("[Desk PC] Unusual upload: 2 MB/s for 10 minutes.", body.RootElement.GetProperty("text").GetString());
        Assert.Equal("2026-06-10T12:00:00.0000000+00:00", body.RootElement.GetProperty("at").GetString());
    }

    [Fact]
    public void WithoutAName_NoSenderIsSent()
    {
        using JsonDocument body = JsonDocument.Parse(ForwardPayload.Webhook(Record, "  "));

        Assert.False(body.RootElement.TryGetProperty("sender", out _));
    }

    [Fact]
    public void Ntfy_GetsTheTopicTitleMessagePriorityAndTag()
    {
        using JsonDocument body = JsonDocument.Parse(ForwardPayload.Ntfy(Record, "home-pc", null));

        Assert.Equal("home-pc", body.RootElement.GetProperty("topic").GetString());
        Assert.Equal("Unusual upload", body.RootElement.GetProperty("title").GetString());
        Assert.Equal(3, body.RootElement.GetProperty("priority").GetInt32());
        Assert.Equal("warning", body.RootElement.GetProperty("tags")[0].GetString());
        Assert.Equal(5, body.RootElement.EnumerateObject().Count());
    }
}

public sealed class AlertCatalogueTests
{
    [Fact]
    public void EveryTopic_HasAKind()
    {
        string[] topics = [.. typeof(NoticeTopics).GetFields()
            .Where(field => field.IsLiteral && field.Name != nameof(NoticeTopics.Test))
            .Select(field => (string)field.GetRawConstantValue()!)];

        foreach (string topic in topics)
        {
            AlertKind? kind = AlertCatalogue.Find(topic);

            if (kind is null)
            {
                Assert.True(AlertCatalogue.IsStoreEdition, $"{topic} has no kind");
                continue;
            }

            Assert.Equal(topic, kind.Topic);
        }
    }

    [Fact]
    public void EveryListedKind_HasASwitchThatWorks()
    {
        foreach (AlertKind kind in AlertCatalogue.Listed)
        {
            WindsockSettings settings = new();

            Assert.NotNull(kind.IsOn);
            Assert.NotNull(kind.SetOn);

            kind.SetOn!(settings, true);
            Assert.True(kind.IsOn!(settings), kind.Topic);

            kind.SetOn(settings, false);
            Assert.False(kind.IsOn(settings), kind.Topic);
        }
    }

    [Fact]
    public void TheConnectionsTwoSwitches_AreSharedByTheirPairs()
    {
        WindsockSettings settings = new();

        AlertCatalogue.Find(NoticeTopics.ConnectionLost)!.SetOn!(settings, false);
        Assert.False(AlertCatalogue.Find(NoticeTopics.ConnectionRestored)!.IsOn!(settings));
    }

    [Fact]
    public void TheDefaults_KeepTheConnectionsAndLeaveTheNewNoisyOnesOff()
    {
        WindsockSettings settings = new();

        Assert.True(AlertCatalogue.Find(NoticeTopics.ConnectionLost)!.IsOn!(settings));
        Assert.False(AlertCatalogue.Find(NoticeTopics.NetworkChanged)!.IsOn!(settings));
        Assert.True(AlertCatalogue.Find(NoticeTopics.PlanLevel)!.IsOn!(settings));

        foreach (string topic in new[] { NoticeTopics.PingHigh, NoticeTopics.SpeedDrop, NoticeTopics.UploadHigh, NoticeTopics.GraphicsHot, NoticeTopics.DriveFull, NoticeTopics.DnsChanged, NoticeTopics.RouterChanged, NoticeTopics.ProxyChanged, NoticeTopics.AwaySummary })
        {
            Assert.False(AlertCatalogue.Find(topic)!.IsOn!(settings), topic);
        }

        Assert.False(settings.Alerts.Quiet.Enabled);
        Assert.False(settings.Alerts.Forward.Enabled);
    }

    [Fact]
    public void TheStoreEdition_ListsNothingItCannotRaise()
    {
        bool offersUpdate = AlertCatalogue.All.Any(kind => kind.Topic == NoticeTopics.UpdateReady);
        bool offersProcessor = AlertCatalogue.All.Any(kind => kind.Topic == NoticeTopics.ProcessorHot);

        Assert.Equal(!AlertCatalogue.IsStoreEdition, offersUpdate);
        Assert.Equal(!AlertCatalogue.IsStoreEdition, offersProcessor);
    }
}

public sealed class AlertSettingsTests : IDisposable
{
    private readonly string _path = Path.Combine(Path.GetTempPath(), "windsock-alert-settings-" + Guid.NewGuid().ToString("N") + ".json");

    public void Dispose() => File.Delete(_path);

    [Fact]
    public void TheChoices_SurviveBeingSaved()
    {
        SettingsStore store = new(_path);
        WindsockSettings settings = new();
        settings.Alerts.Quiet = new QuietHoursSettings { Enabled = true, StartMinute = 23 * 60, Days = [DayOfWeek.Friday] };
        settings.Alerts.Choices = new() { [NoticeTopics.DriveFull] = new AlertChoice { Sound = NoticeSound.Mail, BreaksQuiet = true, Forward = false } };
        settings.Alerts.Ping.Milliseconds = 90;
        settings.Alerts.Forward = new ForwardSettings { Enabled = true, To = ForwardKind.Webhook, WebhookUrl = "https://example.com/hook-secret" };

        store.Save(settings);
        WindsockSettings read = store.Load();

        Assert.True(read.Alerts.Quiet.Enabled);
        Assert.Equal(23 * 60, read.Alerts.Quiet.StartMinute);
        Assert.Equal([DayOfWeek.Friday], read.Alerts.Quiet.Days);
        Assert.Equal(NoticeSound.Mail, read.Alerts.ChoiceFor(NoticeTopics.DriveFull).Sound);
        Assert.False(read.Alerts.ChoiceFor(NoticeTopics.DriveFull).Forward);
        Assert.True(read.Alerts.ChoiceFor(NoticeTopics.PingHigh).Forward);
        Assert.Equal(90, read.Alerts.Ping.Milliseconds);
    }

    [Fact]
    public void Forwarding_IsNeverInTheSettingsFile()
    {
        WindsockSettings settings = new();
        settings.Alerts.Forward = new ForwardSettings { Enabled = true, NtfyTopic = "secret-topic", NtfyToken = "tk_secret" };

        new SettingsStore(_path).Save(settings);
        string written = File.ReadAllText(_path);

        Assert.DoesNotContain("secret", written, StringComparison.Ordinal);
        Assert.DoesNotContain("Forward", written, StringComparison.Ordinal);
        Assert.DoesNotContain("secret", SettingsTransfer.Export(settings, DateTimeOffset.UnixEpoch), StringComparison.Ordinal);
    }

    [Fact]
    public void AFileSavedBeforeAlerts_OpensWithTheDefaults()
    {
        File.WriteAllText(_path, """{ "Theme": "Dark", "Alerts": null }""");

        WindsockSettings read = new SettingsStore(_path).Load();

        Assert.NotNull(read.Alerts);
        Assert.Equal(7, read.Alerts.Quiet.Days.Count);
        Assert.Equal(90, read.Alerts.KeepDays);
    }

    [Fact]
    public void AnImportsChanges_NameTheAlertSettingsAsTheAlertsPageDoes()
    {
        WindsockSettings mine = new();
        WindsockSettings file = new();
        file.Alerts.Ping.Enabled = true;
        file.Alerts.Quiet = new QuietHoursSettings { Enabled = true, Days = [DayOfWeek.Friday, DayOfWeek.Saturday] };
        file.Alerts.Choices = new()
        {
            [NoticeTopics.DriveFull] = new AlertChoice { Sound = NoticeSound.Mail },
            [NoticeTopics.PingHigh] = new AlertChoice { BreaksQuiet = true },
        };

        IReadOnlyList<SettingChange> changes = SettingsTransfer.Compare(mine, file);
        string[] names = [.. changes.Select(change => change.Name)];

        Assert.Contains("Alert on slow responses or lost pings", names);
        Assert.Contains("Quiet hours", names);
        Assert.Single(names, name => name == "Each alert's sound, quiet hours and forwarding");
        Assert.Single(names, name => name == "Days quiet hours begin on");
        Assert.DoesNotContain(names, name => name.Contains("Choices", StringComparison.Ordinal));
    }

    [Fact]
    public void AnImport_CarriesTheAlerts_ButKeepsThisMachinesForwarding()
    {
        WindsockSettings mine = new();
        mine.Alerts.Forward = new ForwardSettings { Enabled = true, NtfyTopic = "mine" };
        WindsockSettings file = new();
        file.Alerts.SpeedDrop.Enabled = true;

        mine.CopyFrom(file);

        Assert.True(mine.Alerts.SpeedDrop.Enabled);
        Assert.True(mine.Alerts.Forward.Enabled);
        Assert.Equal("mine", mine.Alerts.Forward.NtfyTopic);
    }
}

public sealed class AlertNoticeTests
{
    private static readonly DateTimeOffset Noon = new(2026, 6, 10, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void APlanLevel_SaysWhatIsLeft_AndAFirstLookIsQuiet()
    {
        DataPlan plan = new() { Id = "p1", Name = "Home fibre", LimitBytes = 100_000_000_000 };
        PlanStatus status = new(
            plan,
            new BillingPeriod(Noon.AddDays(-13), Noon.AddDays(17)),
            PlanState.Running,
            Noon,
            0,
            PlanTally.Empty with { Counted = 80_000_000_000 },
            new PlanOutlook(null, null, 17, TimeSpan.FromDays(17), null, true, DateOnly.FromDateTime(Noon.AddDays(17).Date)));

        Notice eighty = AlertNotices.Plan(new PlanLevelCrossing(plan, 80, status, FirstLook: false));
        Notice full = AlertNotices.Plan(new PlanLevelCrossing(plan, 100, status, FirstLook: true));

        Assert.Equal("Home fibre: 80% used", eighty.Title);
        Assert.Contains("left", eighty.Message, StringComparison.Ordinal);
        Assert.Equal("plan:p1", eighty.Tag);
        Assert.False(eighty.Quiet);
        Assert.Equal("Home fibre: allowance used up", full.Title);
        Assert.True(full.Quiet);
    }

    [Fact]
    public void TheAwaySummary_CountsOnlyTheOfflineTimeWhileAway()
    {
        AwayReport report = new(Noon, Noon.AddHours(2), 1_500_000_000, 120_000_000);
        Outage[] outages = [new(Noon.AddMinutes(-10), Noon.AddMinutes(5), OutageCause.Unknown, "Home")];

        Notice away = AlertNotices.Away(report, outages, 3);

        Assert.Equal("While you were away", away.Title);
        Assert.Contains("Locked for 2 hours.", away.Message, StringComparison.Ordinal);
        Assert.Contains("1 outage, 5 minutes offline.", away.Message, StringComparison.Ordinal);
        Assert.Contains("3 alerts.", away.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void AProxyChange_SaysWhatItIsNow()
    {
        Notice notice = AlertNotices.Proxy(new ProxyChange(ProxyPrint.None, ProxyPrint.None with { Manual = true, Server = "127.0.0.1:8080" }, false));

        Assert.StartsWith("This PC now uses a proxy at 127.0.0.1:8080, where it used no proxy.", notice.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ARouterChange_IsTaggedByItsNetwork()
    {
        Notice notice = AlertNotices.NetworkChange(new NetworkPrintDifference(NetworkPrintChange.Router, "Home", "192.168.1.1 (AA)", "192.168.1.1 (BB)"));

        Assert.Equal(NoticeTopics.RouterChanged, notice.Topic);
        Assert.Equal("router:Home", notice.Tag);
        Assert.Equal("Different router on Home", notice.Title);
    }
}

public sealed class ToastSoundTests
{
    private static readonly Notice Plain = new("t", "Title", "Message");

    [Theory]
    [InlineData(NoticeSound.Default, false, true, true)]
    [InlineData(NoticeSound.Default, true, true, false)]
    [InlineData(NoticeSound.Silent, false, true, false)]
    [InlineData(NoticeSound.Reminder, true, true, true)]
    [InlineData(NoticeSound.Reminder, false, false, false)]
    public void TheSoundChosen_WinsOverTheNotice_ButNeverOverTheSwitch(NoticeSound chosen, bool quiet, bool sound, bool audible) =>
        Assert.Equal(audible, ToastXml.Audible(Plain with { Sound = chosen, Quiet = quiet }, sound));

    [Fact]
    public void ANamedSound_IsWrittenIntoTheMarkup()
    {
        string markup = ToastXml.Build(Plain with { Sound = NoticeSound.Mail }, sound: true);

        Assert.Contains("ms-winsoundevent:Notification.Mail", markup, StringComparison.Ordinal);
    }
}
