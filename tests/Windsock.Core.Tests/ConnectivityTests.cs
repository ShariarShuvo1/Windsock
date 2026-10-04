using System.Globalization;
using System.Net.NetworkInformation;
using Microsoft.Extensions.Logging.Abstractions;
using Windsock.Core.Connectivity;
using Windsock.Core.Formatting;
using Windsock.Core.Hardware;
using Windsock.Core.Networking;
using Windsock.Core.Notifications;
using Windsock.Core.Settings;
using Xunit;

namespace Windsock.Core.Tests;

/// <summary>Builds checks for the connection tests.</summary>
internal static class Checks
{
    public static DateTimeOffset Noon { get; } = new(new DateTime(2026, 3, 14, 12, 0, 0, DateTimeKind.Local));

    public static NetworkIdentity Home { get; } = new("Home", NetworkKind.WiFi);

    public static NetworkIdentity Office { get; } = new("Office", NetworkKind.Ethernet);

    public static TrackerRules Rules { get; } = TrackerRules.For(TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(15));

    /// <summary>A check where everything answers.</summary>
    public static ConnectionSample Good(double seconds, NetworkIdentity? network = null, bool vpn = false) => new(
        Noon.AddSeconds(seconds),
        new ConnectionFacts(WindowsReach.Internet, network ?? Home, "192.168.1.1", vpn),
        TimeSpan.FromMilliseconds(2),
        TimeSpan.FromMilliseconds(20),
        InternetChecked: true);

    /// <summary>A check where the router answers and the internet does not.</summary>
    public static ConnectionSample ProviderDown(double seconds) => new(
        Noon.AddSeconds(seconds),
        new ConnectionFacts(WindowsReach.LocalOnly, Home, "192.168.1.1", VpnActive: false),
        TimeSpan.FromMilliseconds(2),
        null,
        InternetChecked: true);

    /// <summary>A check where nothing answers, the router included.</summary>
    public static ConnectionSample RouterDown(double seconds) => new(
        Noon.AddSeconds(seconds),
        new ConnectionFacts(WindowsReach.LocalOnly, Home, "192.168.1.1", VpnActive: false),
        null,
        null,
        InternetChecked: true);

    /// <summary>
    /// Feeds checks every five seconds from <paramref name="from"/> to
    /// <paramref name="to"/>, and returns every event they settled.
    /// </summary>
    public static List<ConnectionEvent> Run(OutageTracker tracker, double from, double to, Func<double, ConnectionSample> check)
    {
        List<ConnectionEvent> events = [];

        for (double at = from; at <= to; at += 5)
        {
            events.AddRange(tracker.Observe(check(at), Rules));
        }

        return events;
    }
}

public sealed class ConnectionJudgeTests
{
    [Fact]
    public void AnInternetAnswer_IsOnline_WhateverWindowsSays()
    {
        ConnectionSample sample = Checks.Good(0) with
        {
            Facts = new ConnectionFacts(WindowsReach.LocalOnly, Checks.Home, "192.168.1.1", false),
        };

        Assert.Null(ConnectionJudge.Judge(sample, routerAnswersPings: true));
    }

    [Fact]
    public void WithoutATarget_WindowsIsTrustedForOnline_EvenWithASilentRouter()
    {
        ConnectionSample sample = new(
            Checks.Noon,
            new ConnectionFacts(WindowsReach.Internet, Checks.Home, "192.168.1.1", false),
            null,
            null,
            InternetChecked: false);

        Assert.Null(ConnectionJudge.Judge(sample, routerAnswersPings: true));
    }

    [Fact]
    public void NoNetworkAtAll_IsNoNetwork()
    {
        ConnectionSample sample = new(Checks.Noon, new ConnectionFacts(WindowsReach.None, null, null, false), null, null, true);

        Assert.Equal(OutageCause.NoNetwork, ConnectionJudge.Judge(sample, routerAnswersPings: false));
    }

    [Fact]
    public void ARouterThatAnswers_BlamesTheProvider()
    {
        Assert.Equal(OutageCause.Provider, ConnectionJudge.Judge(Checks.ProviderDown(0), routerAnswersPings: true));
    }

    [Fact]
    public void ASilentRouter_IsBlamedOnlyWhereItHasAnsweredBefore()
    {
        Assert.Equal(OutageCause.Router, ConnectionJudge.Judge(Checks.RouterDown(0), routerAnswersPings: true));
        Assert.Equal(OutageCause.Unknown, ConnectionJudge.Judge(Checks.RouterDown(0), routerAnswersPings: false));
    }

    [Fact]
    public void ASignInPage_IsSaidToBeOne()
    {
        ConnectionSample sample = Checks.ProviderDown(0) with
        {
            Facts = new ConnectionFacts(WindowsReach.SignInRequired, Checks.Home, "192.168.1.1", false),
        };

        Assert.Equal(OutageCause.SignIn, ConnectionJudge.Judge(sample, routerAnswersPings: true));
    }

    [Fact]
    public void WindowsUnreachable_AndNothingElse_IsNotCalledAnOutage()
    {
        ConnectionSample sample = new(Checks.Noon, ConnectionFacts.Unknown, null, null, InternetChecked: false);

        Assert.Null(ConnectionJudge.Judge(sample, routerAnswersPings: true));
    }
}

public sealed class OutageTrackerTests
{
    [Fact]
    public void ABlipShorterThanTheDelay_IsNotAnOutage()
    {
        OutageTracker tracker = new();
        Checks.Run(tracker, 0, 60, at => Checks.Good(at));

        List<ConnectionEvent> events = [];
        events.AddRange(tracker.Observe(Checks.ProviderDown(65), Checks.Rules));
        events.AddRange(tracker.Observe(Checks.ProviderDown(70), Checks.Rules));
        events.AddRange(Checks.Run(tracker, 75, 90, at => Checks.Good(at)));

        Assert.Empty(events);
        Assert.Equal(ConnectionStatus.Online, tracker.State.Status);
    }

    [Fact]
    public void AnOutage_StartsFromTheFirstMissedCheck_NotFromWhenItWasCalled()
    {
        OutageTracker tracker = new();
        Checks.Run(tracker, 0, 60, at => Checks.Good(at));

        List<ConnectionEvent> events = Checks.Run(tracker, 65, 90, Checks.ProviderDown);

        OutageBegan began = Assert.IsType<OutageBegan>(Assert.Single(events));
        Assert.Equal(Checks.Noon.AddSeconds(65), began.Outage.Start);
        Assert.Equal(OutageCause.Provider, began.Outage.Cause);
        Assert.Equal("Home", began.Outage.Network);
        Assert.Equal(ConnectionStatus.Offline, tracker.State.Status);
        Assert.Equal(Checks.Noon.AddSeconds(65), tracker.State.Since);
    }

    [Fact]
    public void AnOutage_EndsAtTheFirstGoodCheck_OnceASecondAgrees()
    {
        OutageTracker tracker = new();
        Checks.Run(tracker, 0, 60, at => Checks.Good(at));
        Checks.Run(tracker, 65, 120, Checks.ProviderDown);

        Assert.Empty(tracker.Observe(Checks.Good(125), Checks.Rules));

        OutageEnded ended = Assert.IsType<OutageEnded>(Assert.Single(tracker.Observe(Checks.Good(130), Checks.Rules)));

        Assert.Equal(Checks.Noon.AddSeconds(125), ended.Outage.End);
        Assert.Equal(TimeSpan.FromSeconds(60), ended.Outage.Duration);
        Assert.False(ended.Outage.Interrupted);
        Assert.Equal(ConnectionStatus.Online, tracker.State.Status);
    }

    [Fact]
    public void OneLuckyAnswer_DoesNotSplitAnOutageInTwo()
    {
        OutageTracker tracker = new();
        Checks.Run(tracker, 0, 60, at => Checks.Good(at));
        Checks.Run(tracker, 65, 100, Checks.ProviderDown);

        List<ConnectionEvent> events = [.. tracker.Observe(Checks.Good(105), Checks.Rules)];
        events.AddRange(Checks.Run(tracker, 110, 150, Checks.ProviderDown));

        Assert.Empty(events);
        Assert.NotNull(tracker.Current);
    }

    [Fact]
    public void ALongSilence_ClosesTheOutageWhereWatchingStopped()
    {
        OutageTracker tracker = new();
        Checks.Run(tracker, 0, 60, at => Checks.Good(at));
        Checks.Run(tracker, 65, 100, Checks.ProviderDown);

        // The PC slept for an hour.
        IReadOnlyList<ConnectionEvent> events = tracker.Observe(Checks.ProviderDown(3700), Checks.Rules);

        OutageEnded ended = Assert.IsType<OutageEnded>(Assert.Single(events));
        Assert.True(ended.Outage.Interrupted);
        Assert.Equal(Checks.Noon.AddSeconds(100), ended.Outage.End);
    }

    [Fact]
    public void AfterWaking_WiFiGetsItsGraceBeforeAnythingIsCalled()
    {
        OutageTracker tracker = new();
        Checks.Run(tracker, 0, 60, at => Checks.Good(at));

        // Back from sleep with Wi-Fi still joining: 30 seconds of nothing is inside the grace.
        List<ConnectionEvent> early = Checks.Run(tracker, 3600, 3630, Checks.RouterDown);
        Assert.Empty(early);

        List<ConnectionEvent> late = Checks.Run(tracker, 3635, 3650, Checks.RouterDown);
        OutageBegan began = Assert.IsType<OutageBegan>(Assert.Single(late));
        Assert.Equal(Checks.Noon.AddSeconds(3600), began.Outage.Start);
    }

    [Fact]
    public void Starting_WithTheInternetAlreadyGone_WaitsOutTheGraceToo()
    {
        OutageTracker tracker = new();

        Assert.Empty(Checks.Run(tracker, 0, 40, Checks.ProviderDown));
        Assert.Single(Checks.Run(tracker, 45, 45, Checks.ProviderDown));
    }

    [Fact]
    public void ARouterThatHasAnswered_IsBlamedWhenItStops()
    {
        OutageTracker tracker = new();
        Checks.Run(tracker, 0, 60, at => Checks.Good(at));

        OutageBegan began = Assert.IsType<OutageBegan>(Assert.Single(Checks.Run(tracker, 65, 90, Checks.RouterDown)));

        Assert.Equal(OutageCause.Router, began.Outage.Cause);
    }

    [Fact]
    public void ARouterThatNeverAnswered_IsNotBlamed()
    {
        OutageTracker tracker = new();
        Checks.Run(tracker, 0, 60, at => Checks.Good(at) with { Router = null });

        OutageBegan began = Assert.IsType<OutageBegan>(Assert.Single(Checks.Run(tracker, 65, 90, Checks.RouterDown)));

        Assert.Equal(OutageCause.Unknown, began.Outage.Cause);
    }

    [Fact]
    public void MovingNetwork_IsSaid_ButNotOnTheFirstCheck()
    {
        OutageTracker tracker = new();

        Assert.Empty(tracker.Observe(Checks.Good(0, Checks.Office), Checks.Rules));

        NetworkChanged change = Assert.IsType<NetworkChanged>(
            Assert.Single(tracker.Observe(Checks.Good(5, Checks.Home), Checks.Rules)));

        Assert.Equal(Checks.Office, change.From);
        Assert.Equal(Checks.Home, change.To);
    }

    [Fact]
    public void LosingTheNetworkAndRejoiningIt_IsNotAChange()
    {
        OutageTracker tracker = new();
        tracker.Observe(Checks.Good(0), Checks.Rules);

        ConnectionSample gone = new(Checks.Noon.AddSeconds(5), new ConnectionFacts(WindowsReach.None, null, null, false), null, null, true);

        Assert.Empty(tracker.Observe(gone, Checks.Rules));
        Assert.Empty(tracker.Observe(Checks.Good(10), Checks.Rules));
    }

    [Fact]
    public void AVpnGoingOnAndOff_IsSaid()
    {
        OutageTracker tracker = new();
        tracker.Observe(Checks.Good(0), Checks.Rules);

        VpnChanged on = Assert.IsType<VpnChanged>(Assert.Single(tracker.Observe(Checks.Good(5, vpn: true), Checks.Rules)));
        VpnChanged off = Assert.IsType<VpnChanged>(Assert.Single(tracker.Observe(Checks.Good(10, vpn: false), Checks.Rules)));

        Assert.True(on.Active);
        Assert.False(off.Active);
    }

    [Fact]
    public void Pausing_ClosesAnOpenOutage_AndForgetsTheState()
    {
        OutageTracker tracker = new();
        Checks.Run(tracker, 0, 60, at => Checks.Good(at));
        Checks.Run(tracker, 65, 100, Checks.ProviderDown);

        OutageEnded ended = Assert.IsType<OutageEnded>(Assert.Single(tracker.Pause()));

        Assert.True(ended.Outage.Interrupted);
        Assert.Equal(ConnectionStatus.Checking, tracker.State.Status);
        Assert.Null(tracker.Current);
    }
}

public sealed class LatencyTests
{
    [Fact]
    public void TheHistory_KeepsTheNewest_OldestFirst()
    {
        LatencyHistory history = new(3);

        for (int each = 0; each < 5; each++)
        {
            history.Add(new LatencyPoint(Checks.Noon.AddSeconds(each), each, null, true, false, false));
        }

        Assert.Equal([2d, 3d, 4d], history.Since(DateTimeOffset.MinValue).Select(point => point.RouterMs!.Value));
        Assert.Equal([4d], history.Since(Checks.Noon.AddSeconds(4)).Select(point => point.RouterMs!.Value));
    }

    [Fact]
    public void Loss_CountsOnlyPingsThatWereSent()
    {
        LatencyHistory history = new(10);
        history.Add(new LatencyPoint(Checks.Noon, 2, null, RouterTried: true, InternetTried: false, false));
        history.Add(new LatencyPoint(Checks.Noon.AddSeconds(5), null, null, RouterTried: true, InternetTried: false, false));
        history.Add(new LatencyPoint(Checks.Noon.AddSeconds(10), null, null, RouterTried: false, InternetTried: false, false));

        Assert.Equal(0.5, history.RouterLoss(DateTimeOffset.MinValue));
        Assert.Null(history.InternetLoss(DateTimeOffset.MinValue));
    }

    [Theory]
    [InlineData(0, 10)]
    [InlineData(3, 10)]
    [InlineData(11, 20)]
    [InlineData(20, 20)]
    [InlineData(21, 50)]
    [InlineData(120, 200)]
    [InlineData(999, 1000)]
    [InlineData(double.NaN, 10)]
    public void TheScale_RoundsUpToAReadableTop(double peak, double top)
    {
        Assert.Equal(top, LatencyScale.Ceiling(peak));
    }

    [Fact]
    public void TheMedian_SkipsUnansweredChecks()
    {
        Assert.Equal(3, LatencyScale.Median([1, null, 3, 9]));
        Assert.Equal(2.5, LatencyScale.Median([2, 3]));
        Assert.Null(LatencyScale.Median([null, null]));
    }
}

public sealed class VpnDetectorTests
{
    [Theory]
    [InlineData(NetworkInterfaceType.Ethernet, "WireGuard Tunnel", "Home VPN", true)]
    [InlineData(NetworkInterfaceType.Ethernet, "TAP-Windows Adapter V9", "Ethernet 3", true)]
    [InlineData(NetworkInterfaceType.Ppp, "WAN Miniport (IKEv2)", "Work", true)]
    [InlineData(NetworkInterfaceType.Tunnel, "Teredo Tunneling Pseudo-Interface", "Teredo", false)]
    [InlineData(NetworkInterfaceType.Wireless80211, "Intel(R) Wi-Fi 6E AX211", "Wi-Fi", false)]
    [InlineData(NetworkInterfaceType.Ethernet, "Realtek PCIe GbE Family Controller", "Ethernet", false)]
    public void Adapters_AreToldApart(NetworkInterfaceType type, string description, string name, bool vpn)
    {
        Assert.Equal(vpn, VpnDetector.IsVpn(type, description, name));
    }
}

public sealed class NetworkIdentityTests
{
    [Theory]
    [InlineData("Home", NetworkKind.WiFi, "Home (Wi-Fi)")]
    [InlineData("Network 2", NetworkKind.Ethernet, "Network 2 (Ethernet)")]
    [InlineData("Ethernet", NetworkKind.Ethernet, "Ethernet")]
    [InlineData("Office Wi-Fi", NetworkKind.WiFi, "Office Wi-Fi")]
    [InlineData("Carrier", NetworkKind.Cellular, "Carrier (mobile)")]
    [InlineData("Something", NetworkKind.Other, "Something")]
    public void ANetwork_IsNamedWithoutSayingItsKindTwice(string name, NetworkKind kind, string expected)
    {
        Assert.Equal(expected, new NetworkIdentity(name, kind).Describe());
    }
}

public sealed class OutageWordsTests
{
    [Theory]
    [InlineData(0, "0 seconds")]
    [InlineData(1, "1 second")]
    [InlineData(45, "45 seconds")]
    [InlineData(420, "7 minutes")]
    [InlineData(3600, "1 hour")]
    [InlineData(4320, "1 hour 12 minutes")]
    [InlineData(273600, "3 days 4 hours")]
    [InlineData(-5, "0 seconds")]
    public void Durations_ReadTheWayTheyAreSaid(double seconds, string expected)
    {
        Assert.Equal(expected, OutageWords.Spoken(TimeSpan.FromSeconds(seconds)));
    }

    [Theory]
    [InlineData(45, "45 s")]
    [InlineData(420, "7 min")]
    [InlineData(4320, "1 h 12 min")]
    [InlineData(273600, "3 d 4 h")]
    public void BriefDurations_FitATable(double seconds, string expected)
    {
        Assert.Equal(expected, OutageWords.Brief(TimeSpan.FromSeconds(seconds)));
    }

    [Fact]
    public void When_NamesTodayAndYesterday_AndDatesTheRest()
    {
        DateTimeOffset now = Checks.Noon.AddHours(10);
        CultureInfo culture = CultureInfo.InvariantCulture;

        Outage today = new(Checks.Noon, Checks.Noon.AddMinutes(7), OutageCause.Provider, "Home");
        Outage yesterday = today with { Start = Checks.Noon.AddDays(-1), End = Checks.Noon.AddDays(-1).AddMinutes(1) };
        Outage older = today with { Start = Checks.Noon.AddDays(-3), End = Checks.Noon.AddDays(-3).AddMinutes(1) };
        Outage ongoing = today with { End = null };

        Assert.Equal("Today, 12:00 – 12:07", OutageWords.When(today, now, culture));
        Assert.Equal("Yesterday, 12:00 – 12:01", OutageWords.When(yesterday, now, culture));
        Assert.Equal("Wed 11 Mar, 12:00 – 12:01", OutageWords.When(older, now, culture));
        Assert.Equal("Today, 12:00 – now", OutageWords.When(ongoing, now, culture));
    }

    [Fact]
    public void When_PastMidnight_GivesBothDays()
    {
        DateTimeOffset late = new(new DateTime(2026, 3, 13, 23, 50, 0, DateTimeKind.Local));
        Outage outage = new(late, late.AddMinutes(20), OutageCause.Router, "Home");

        Assert.Equal("Yesterday 23:50 – Today 00:10", OutageWords.When(outage, Checks.Noon, CultureInfo.InvariantCulture));
    }

    [Fact]
    public void TheSummary_CountsTotalsAndTheLongest()
    {
        Outage first = new(Checks.Noon, Checks.Noon.AddMinutes(9), OutageCause.Provider, "Home");
        Outage second = first with { Start = Checks.Noon.AddHours(1), End = Checks.Noon.AddHours(1).AddMinutes(5) };

        Assert.Equal("No outages in the last 7 days.", OutageWords.Summary([], "the last 7 days", Checks.Noon));
        Assert.Equal("1 outage in the last 7 days, 9 minutes in all.", OutageWords.Summary([first], "the last 7 days", Checks.Noon));
        Assert.Equal(
            "2 outages in the last 7 days, 14 minutes in all. The longest lasted 9 minutes.",
            OutageWords.Summary([first, second], "the last 7 days", Checks.Noon));
    }

    [Fact]
    public void TheSummary_CountsAnOutageStillGoing_UpToNow()
    {
        Outage ongoing = new(Checks.Noon, null, OutageCause.Provider, "Home");

        Assert.Equal(
            "1 outage in today, 3 minutes in all.",
            OutageWords.Summary([ongoing], "today", Checks.Noon.AddMinutes(3)));
    }

    [Fact]
    public void EveryCause_HasWordsForEveryPlace()
    {
        foreach (OutageCause cause in Enum.GetValues<OutageCause>())
        {
            Assert.False(string.IsNullOrWhiteSpace(OutageWords.Headline(cause)));
            Assert.False(string.IsNullOrWhiteSpace(OutageWords.Explain(cause)));
            Assert.False(string.IsNullOrWhiteSpace(OutageWords.Blame(cause)));
        }
    }
}

public sealed class SqliteOutageStoreTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "Windsock-tests", Guid.NewGuid().ToString("N"));

    private string File => Path.Combine(_directory, "Outages.db");

    [Fact]
    public void AnOutage_IsKeptFromStartToFinish()
    {
        using SqliteOutageStore store = new(File);
        Outage began = new(Checks.Noon, null, OutageCause.Provider, "Home, \"upstairs\"");

        long id = store.Begin(began);
        Outage open = Assert.Single(store.Read(Checks.Noon.AddHours(-1), Checks.Noon.AddHours(1)));
        Assert.Null(open.End);
        Assert.Equal(id, open.Id);

        store.Finish(id, Checks.Noon.AddMinutes(7), interrupted: false);
        Outage closed = Assert.Single(store.Read(Checks.Noon.AddHours(-1), Checks.Noon.AddHours(1)));

        Assert.Equal(Checks.Noon, closed.Start);
        Assert.Equal(Checks.Noon.AddMinutes(7), closed.End);
        Assert.Equal(OutageCause.Provider, closed.Cause);
        Assert.Equal("Home, \"upstairs\"", closed.Network);
        Assert.False(closed.Interrupted);
    }

    [Fact]
    public void AnOutageARunNeverSawEnd_IsClosedWhereItWasLastSeen()
    {
        using (SqliteOutageStore crashed = new(File))
        {
            long id = crashed.Begin(new Outage(Checks.Noon, null, OutageCause.Router, "Home"));
            crashed.Touch(id, Checks.Noon.AddMinutes(4));
        }

        using SqliteOutageStore store = new(File);

        Assert.Equal(1, store.CloseUnfinished());

        Outage closed = Assert.Single(store.Read(Checks.Noon, Checks.Noon.AddHours(1)));
        Assert.Equal(Checks.Noon.AddMinutes(4), closed.End);
        Assert.True(closed.Interrupted);
    }

    [Fact]
    public void Reading_FindsOutagesThatOverlapThePeriod_NewestFirst()
    {
        using SqliteOutageStore store = new(File);
        long early = store.Begin(new Outage(Checks.Noon.AddHours(-2), null, OutageCause.Provider, "Home"));
        store.Finish(early, Checks.Noon.AddMinutes(-90), false);
        long straddling = store.Begin(new Outage(Checks.Noon.AddMinutes(-5), null, OutageCause.Provider, "Home"));
        store.Finish(straddling, Checks.Noon.AddMinutes(5), false);
        long late = store.Begin(new Outage(Checks.Noon.AddMinutes(30), null, OutageCause.Router, "Home"));
        store.Finish(late, Checks.Noon.AddMinutes(31), false);

        IReadOnlyList<Outage> found = store.Read(Checks.Noon, Checks.Noon.AddHours(1));

        Assert.Equal([late, straddling], found.Select(outage => outage.Id));
    }

    [Fact]
    public void Clearing_ForgetsEverything_AndSaysSo()
    {
        using SqliteOutageStore store = new(File);
        int changes = 0;
        store.Changed += (_, _) => changes++;

        store.Begin(new Outage(Checks.Noon, Checks.Noon.AddMinutes(1), OutageCause.Provider, "Home"));

        Assert.Equal(1, store.Clear());
        Assert.Empty(store.Read(DateTimeOffset.MinValue.AddYears(1970), DateTimeOffset.MaxValue.AddYears(-1)));
        Assert.Equal(2, changes);
    }

    [Fact]
    public void ADamagedFile_IsSetAside_AndANewLogStarted()
    {
        Directory.CreateDirectory(_directory);
        System.IO.File.WriteAllText(File, "this is not a database, it is a sentence that is long enough to look like one");

        using SqliteOutageStore store = new(File);

        store.Begin(new Outage(Checks.Noon, null, OutageCause.Provider, "Home"));
        Assert.Single(store.Read(Checks.Noon.AddMinutes(-1), Checks.Noon.AddMinutes(1)));
        Assert.Contains(Directory.GetFiles(_directory), path => path.Contains(".corrupt", StringComparison.Ordinal));
    }

    [Fact]
    public void AFileThatCannotBeOpened_FailsEachCall_InAWayCallersRecognise()
    {
        Directory.CreateDirectory(File);

        using SqliteOutageStore store = new(File);

        Exception error = Assert.ThrowsAny<Exception>(() => store.Read(Checks.Noon, Checks.Noon.AddHours(1)));
        Assert.True(History.HistoryFailure.IsStorage(error));
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_directory, recursive: true);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }
}

public sealed class OutageCsvTests
{
    [Fact]
    public void AnOutage_IsOneRow_UnderTheHeader()
    {
        DateTimeOffset start = new(2026, 3, 14, 12, 0, 0, TimeSpan.Zero);
        Outage outage = new(start, start.AddSeconds(425), OutageCause.Provider, "Home");

        StringWriter writer = new();
        OutageCsv.Write(writer, [outage]);
        string[] lines = writer.ToString().Split('\n', StringSplitOptions.RemoveEmptyEntries);

        Assert.Equal(OutageCsv.Header, lines[0]);
        string[] cells = lines[1].Split(',');
        Assert.Equal("2026-03-14T12:00:00Z", cells[0]);
        Assert.Equal("2026-03-14T12:07:05Z", cells[2]);
        Assert.Equal("425", cells[4]);
        Assert.Equal("provider", cells[5]);
        Assert.Equal("Home", cells[6]);
        Assert.Equal("no", cells[7]);
    }

    [Fact]
    public void AnOutageStillGoing_HasNoMadeUpEnd()
    {
        Outage outage = new(Checks.Noon, null, OutageCause.Router, "Home");

        StringWriter writer = new();
        OutageCsv.Write(writer, [outage]);
        string[] cells = writer.ToString().Split('\n')[1].Split(',');

        Assert.Equal(string.Empty, cells[2]);
        Assert.Equal(string.Empty, cells[3]);
        Assert.Equal(string.Empty, cells[4]);
    }

    [Theory]
    [InlineData("Home", "Home")]
    [InlineData("Home, upstairs", "\"Home, upstairs\"")]
    [InlineData("Say \"hi\"", "\"Say \"\"hi\"\"\"")]
    [InlineData("=HYPERLINK(\"x\")", "\"'=HYPERLINK(\"\"x\"\")\"")]
    [InlineData("+1", "'+1")]
    [InlineData("@me", "'@me")]
    [InlineData("", "")]
    public void NetworkNames_CannotBreakTheFile_OrBecomeFormulas(string name, string expected)
    {
        Assert.Equal(expected, OutageCsv.Field(name));
    }
}

public sealed class ConnectionNoticesTests
{
    [Theory]
    [InlineData(OutageCause.NoNetwork, "Disconnected from the network")]
    [InlineData(OutageCause.Provider, "Internet connection lost")]
    [InlineData(OutageCause.Router, "Internet connection lost")]
    [InlineData(OutageCause.SignIn, "This network wants you to sign in")]
    public void Lost_SaysWhatHappened(OutageCause cause, string title)
    {
        Notice notice = ConnectionNotices.Lost(new Outage(Checks.Noon, null, cause, "Home"));

        Assert.Equal(title, notice.Title);
        Assert.Equal(OutageWords.Explain(cause), notice.Message);
        Assert.Equal(ConnectionNotices.Section, notice.Opens);
        Assert.False(notice.Quiet);
    }

    [Fact]
    public void Restored_TakesThePlaceOfLost_Quietly_AndSaysHowLong()
    {
        Outage outage = new(Checks.Noon, Checks.Noon.AddMinutes(7), OutageCause.Provider, "Home");

        Notice lost = ConnectionNotices.Lost(outage);
        Notice back = ConnectionNotices.Restored(outage);

        Assert.Equal(lost.Tag, back.Tag);
        Assert.True(back.Quiet);
        Assert.Contains("7 minutes", back.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void NetworkAndVpnNotices_NameWhatChanged()
    {
        Notice moved = ConnectionNotices.NetworkSwitched(new NetworkChanged(Checks.Office, Checks.Home));
        Notice vpnOff = ConnectionNotices.VpnSwitched(new VpnChanged(false));
        Notice vpnOn = ConnectionNotices.VpnSwitched(new VpnChanged(true));

        Assert.Equal("Switched to Home (Wi-Fi)", moved.Title);
        Assert.Contains("Office (Ethernet)", moved.Message, StringComparison.Ordinal);
        Assert.False(vpnOff.Quiet);
        Assert.True(vpnOn.Quiet);
    }
}

public sealed class ConnectionSettingsTests
{
    [Theory]
    [InlineData("1.1.1.1", true)]
    [InlineData(" 8.8.8.8 ", true)]
    [InlineData("2606:4700:4700::1111", true)]
    [InlineData("example.com", true)]
    [InlineData("router.local", true)]
    [InlineData("", false)]
    [InlineData("   ", false)]
    [InlineData("http://example.com", false)]
    [InlineData("example.com; calc", false)]
    [InlineData("-bad.example", false)]
    [InlineData("exa mple.com", false)]
    public void OnlyAddresses_AreTakenAsTargets(string text, bool accepted)
    {
        Assert.Equal(accepted, ConnectionSettings.IsTarget(text));
    }

    [Fact]
    public void ACustomTargetThatIsNotAnAddress_IsNotPinged()
    {
        ConnectionSettings settings = new() { InternetCheck = InternetCheck.Custom, CustomTarget = "not an address" };

        Assert.Null(settings.InternetTarget);

        settings.CustomTarget = " 9.9.9.9 ";
        Assert.Equal("9.9.9.9", settings.InternetTarget);
    }

    [Fact]
    public void TheDefaults_AreTheAgreedOnes()
    {
        ConnectionSettings settings = new();

        Assert.True(settings.WatchInBackground);
        Assert.True(settings.NotifyLostAndRestored);
        Assert.False(settings.NotifyNetworkChanges);
        Assert.Equal(InternetCheck.WindowsOnly, settings.InternetCheck);
        Assert.Null(settings.InternetTarget);
        Assert.Equal(TimeSpan.FromSeconds(5), settings.CheckEvery);
        Assert.Equal(TimeSpan.FromSeconds(15), settings.OutageAfter);
    }

    [Fact]
    public void TimingsFromAnEditedFile_AreHeldToWhatIsSensible()
    {
        ConnectionSettings settings = new() { CheckEverySeconds = 0, OutageAfterSeconds = 100000 };

        Assert.Equal(TimeSpan.FromSeconds(ConnectionSettings.FastestCheck), settings.CheckEvery);
        Assert.Equal(TimeSpan.FromSeconds(ConnectionSettings.LongestOutage), settings.OutageAfter);
    }

    [Fact]
    public void TheChoices_SurviveARestart()
    {
        string directory = Path.Combine(Path.GetTempPath(), "Windsock-tests", Guid.NewGuid().ToString("N"));
        string path = Path.Combine(directory, "settings.json");

        try
        {
            Directory.CreateDirectory(directory);
            SettingsStore store = new(path);
            WindsockSettings settings = new();
            settings.Connection.InternetCheck = InternetCheck.Quad9;
            settings.Connection.NotifyNetworkChanges = true;
            settings.Connection.OutageAfterSeconds = 60;
            store.Save(settings);

            ConnectionSettings loaded = new SettingsStore(path).Load().Connection;

            Assert.Equal(InternetCheck.Quad9, loaded.InternetCheck);
            Assert.True(loaded.NotifyNetworkChanges);
            Assert.Equal(60, loaded.OutageAfterSeconds);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }
}

public sealed class OfflineMeterTests
{
    private static MeterReading Reading(double down, double up, ConnectionState state) =>
        new MeterReading(down, up, SystemSnapshot.Empty) { Connection = state };

    private static ConnectionState Offline { get; } = new(ConnectionStatus.Offline, OutageCause.Provider, Checks.Noon);

    [Fact]
    public void WhileTheInternetIsGone_TheNetworkFiguresAreADash()
    {
        MeterReading reading = Reading(120, 40, Offline);

        Assert.Equal(MeterReadout.Unknown, MeterReadout.Compose(MeterField.Download, reading, new TaskbarSettings()).Value);
        Assert.Equal(MeterReadout.Unknown, MeterReadout.Compose(MeterField.Upload, reading, new TaskbarSettings()).Value);
        Assert.Equal(MeterReadout.Unknown, MeterReadout.Compose(MeterField.NetworkTotal, reading, new TaskbarSettings()).Value);
    }

    [Fact]
    public void ARealTransferOnTheLocalNetwork_StillShows()
    {
        MeterReading reading = Reading(5 * 1024 * 1024, 0, Offline);

        Assert.NotEqual(MeterReadout.Unknown, MeterReadout.Compose(MeterField.Download, reading, new TaskbarSettings()).Value);
    }

    [Fact]
    public void Online_OrNotWatched_ChangesNothing()
    {
        MeterReading online = Reading(0, 0, new ConnectionState(ConnectionStatus.Online, null, Checks.Noon));
        MeterReading unwatched = Reading(0, 0, default);

        Assert.NotEqual(MeterReadout.Unknown, MeterReadout.Compose(MeterField.Download, online, new TaskbarSettings()).Value);
        Assert.NotEqual(MeterReadout.Unknown, MeterReadout.Compose(MeterField.Download, unwatched, new TaskbarSettings()).Value);
        Assert.Null(MeterReadout.Offline(online));
    }

    [Fact]
    public void TheTooltip_SaysWhyFirst()
    {
        MeterReading reading = Reading(0, 0, Offline);
        MeterSlot slot = new() { Field = MeterField.Download };

        string tooltip = MeterReadout.Tooltip([slot], reading, new TaskbarSettings());

        Assert.StartsWith("Internet unreachable since ", tooltip, StringComparison.Ordinal);
        Assert.Contains("Download rate  —", tooltip, StringComparison.Ordinal);
    }
}

public sealed class ConnectionMonitorTests
{
    [Fact]
    public async Task Checks_BecomeOutages_InTheLog_AndInEvents()
    {
        FakeFacts facts = new() { Facts = new ConnectionFacts(WindowsReach.Internet, Checks.Home, "192.168.1.1", false) };
        FakeProbe probe = new();
        FakeStore store = new();
        FakeTime time = new(Checks.Noon);
        WindsockSettings settings = new();
        settings.Connection.InternetCheck = InternetCheck.Cloudflare;

        using ConnectionMonitor monitor = new(facts, probe, store, settings, NullLogger<ConnectionMonitor>.Instance, time);
        List<ConnectionEvent> events = [];
        List<ConnectionState> states = [];
        monitor.Happened += (_, happened) => events.Add(happened);
        monitor.StateChanged += (_, state) => states.Add(state);

        async Task Step(bool internet)
        {
            probe.Answers["1.1.1.1"] = internet ? TimeSpan.FromMilliseconds(20) : null;
            await monitor.SampleOnceAsync(CancellationToken.None);
            time.Advance(TimeSpan.FromSeconds(5));
        }

        for (int each = 0; each < 12; each++)
        {
            await Step(internet: true);
        }

        Assert.Equal(ConnectionStatus.Online, monitor.State.Status);

        for (int each = 0; each < 6; each++)
        {
            await Step(internet: false);
        }

        Assert.Equal(ConnectionStatus.Offline, monitor.State.Status);
        OutageBegan began = Assert.IsType<OutageBegan>(Assert.Single(events));
        Assert.NotEqual(0, began.Outage.Id);
        Assert.Equal(began.Outage.Id, monitor.Current?.Id);
        Assert.Single(store.Begun);

        await Step(internet: true);
        await Step(internet: true);

        OutageEnded ended = Assert.IsType<OutageEnded>(events[^1]);
        Assert.Equal(began.Outage.Id, ended.Outage.Id);
        Assert.Equal(began.Outage.Id, Assert.Single(store.Finished).Id);
        Assert.Equal(ConnectionStatus.Online, monitor.State.Status);
        Assert.Equal([ConnectionStatus.Online, ConnectionStatus.Offline, ConnectionStatus.Online], states.Select(state => state.Status));
        Assert.Equal(20, monitor.History.Since(DateTimeOffset.MinValue).Length);
    }

    [Fact]
    public async Task ALogThatCannotBeWritten_DoesNotStopTheChecks()
    {
        FakeFacts facts = new() { Facts = new ConnectionFacts(WindowsReach.LocalOnly, Checks.Home, "192.168.1.1", false) };
        FakeProbe probe = new();
        FakeStore store = new() { Broken = true };
        FakeTime time = new(Checks.Noon);
        WindsockSettings settings = new();

        using ConnectionMonitor monitor = new(facts, probe, store, settings, NullLogger<ConnectionMonitor>.Instance, time);
        List<ConnectionEvent> events = [];
        monitor.Happened += (_, happened) => events.Add(happened);

        for (int each = 0; each < 20; each++)
        {
            await monitor.SampleOnceAsync(CancellationToken.None);
            time.Advance(TimeSpan.FromSeconds(5));
        }

        Assert.IsType<OutageBegan>(Assert.Single(events));
        Assert.Equal(ConnectionStatus.Offline, monitor.State.Status);
    }

    [Fact]
    public async Task AFactsSourceThatThrows_IsAnUnknownReading()
    {
        FakeFacts facts = new() { Throws = true };
        FakeTime time = new(Checks.Noon);

        using ConnectionMonitor monitor = new(facts, new FakeProbe(), new FakeStore(), new WindsockSettings(), NullLogger<ConnectionMonitor>.Instance, time);

        await monitor.SampleOnceAsync(CancellationToken.None);

        Assert.Equal(WindowsReach.Unknown, monitor.Latest?.Facts.Reach);
    }

    [Fact]
    public void Watching_IsCounted()
    {
        using ConnectionMonitor monitor = new(new FakeFacts(), new FakeProbe(), new FakeStore(), new WindsockSettings(), NullLogger<ConnectionMonitor>.Instance, new FakeTime(Checks.Noon));

        IDisposable first = monitor.Watch();
        IDisposable second = monitor.Watch();
        first.Dispose();
        first.Dispose();

        Assert.True(monitor.IsWatched);

        second.Dispose();

        Assert.False(monitor.IsWatched);
    }

    private sealed class FakeFacts : IConnectionFactsSource
    {
        public ConnectionFacts Facts { get; set; } = ConnectionFacts.Unknown;

        public bool Throws { get; set; }

        public event EventHandler? Changed
        {
            add { }
            remove { }
        }

        public ConnectionFacts Read() => Throws ? throw new InvalidOperationException("No.") : Facts;
    }

    private sealed class FakeProbe : INetworkPathProbe
    {
        public Dictionary<string, TimeSpan?> Answers { get; } = new(StringComparer.Ordinal);

        public IAsyncEnumerable<PathHop> TraceAsync(string address, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<TimeSpan?> PingAsync(string address, CancellationToken cancellationToken) =>
            Task.FromResult(Answers.TryGetValue(address, out TimeSpan? answer) ? answer : TimeSpan.FromMilliseconds(2));
    }

    private sealed class FakeStore : IOutageStore
    {
        private long _next = 1;

        public bool Broken { get; set; }

        public List<Outage> Begun { get; } = [];

        public List<(long Id, DateTimeOffset At, bool Interrupted)> Finished { get; } = [];

        public event EventHandler? Changed
        {
            add { }
            remove { }
        }

        public long Begin(Outage outage)
        {
            Fail();
            Begun.Add(outage);
            return _next++;
        }

        public void Touch(long id, DateTimeOffset stillOut) => Fail();

        public void Finish(long id, DateTimeOffset finishedAt, bool interrupted)
        {
            Fail();
            Finished.Add((id, finishedAt, interrupted));
        }

        public int CloseUnfinished()
        {
            Fail();
            return 0;
        }

        public IReadOnlyList<Outage> Read(DateTimeOffset from, DateTimeOffset until) => [];

        public int Clear() => 0;

        private void Fail()
        {
            if (Broken)
            {
                throw new IOException("The disk is full.");
            }
        }
    }

    private sealed class FakeTime(DateTimeOffset start) : TimeProvider
    {
        private DateTimeOffset _now = start;

        public override TimeZoneInfo LocalTimeZone => TimeZoneInfo.Local;

        public override DateTimeOffset GetUtcNow() => _now.ToUniversalTime();

        public void Advance(TimeSpan by) => _now += by;
    }
}
