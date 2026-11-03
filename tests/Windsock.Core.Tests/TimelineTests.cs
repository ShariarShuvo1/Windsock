#if !STORE
using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Xml.Linq;
using Microsoft.Diagnostics.Tracing.Parsers;
using Microsoft.Extensions.Logging.Abstractions;
using Windsock.Core.Control;
using Windsock.Core.History;
using Windsock.Core.Insight;
using Windsock.Core.Processes;
using Windsock.Core.Timeline;
using Xunit;

namespace Windsock.Core.Tests;

public sealed class TimelineTests : IDisposable
{
    private const string Browser = @"\device\harddiskvolume3\program files\browser\browser.exe";
    private const string Torrent = @"\device\harddiskvolume3\program files\torrent\torrent.exe";

    private static readonly DateTimeOffset Noon = new(2026, 10, 5, 12, 0, 0, TimeSpan.Zero);

    private readonly string _folder = Path.Combine(Path.GetTempPath(), "windsock-timeline-" + Guid.NewGuid().ToString("N"));

    public TimelineTests() => Directory.CreateDirectory(_folder);

    private string File_ => Path.Combine(_folder, "Timeline.db");

    [Theory]
    [InlineData("192.168.1.20", true)]
    [InlineData("10.0.0.5", true)]
    [InlineData("172.20.1.1", true)]
    [InlineData("169.254.3.4", true)]
    [InlineData("224.0.0.251", true)]
    [InlineData("255.255.255.255", true)]
    [InlineData("fe80::1", true)]
    [InlineData("fd12:3456::1", true)]
    [InlineData("ff02::fb", true)]
    [InlineData("::ffff:192.168.1.20", true)]
    [InlineData("100.64.0.1", false)]
    [InlineData("142.250.187.196", false)]
    [InlineData("2606:4700::6810:84e5", false)]
    public void LocalMeansAPrivateNetwork(string address, bool local) =>
        Assert.Equal(local, TrafficReach.IsLocal(IPAddress.Parse(address)));

    [Fact]
    public void AnAddressOnThePcsOwnNetworkIsLocalEvenWhenItIsGlobal()
    {
        // A home's IPv6 devices are reached by global addresses on the
        // adapter's own /64; a few ISPs hand out a public IPv4 block too.
        OnLinkPrefix[] home =
        [
            OnLinkPrefix.Of(IPAddress.Parse("2a02:1234:5678:9a00:1c2d:3e4f:5a6b:7c8d"), 64)!.Value,
            OnLinkPrefix.Of(IPAddress.Parse("203.0.113.10"), 29)!.Value,
        ];

        Assert.True(TrafficReach.IsLocal(IPAddress.Parse("2a02:1234:5678:9a00::42"), home));
        Assert.False(TrafficReach.IsLocal(IPAddress.Parse("2a02:1234:5678:9a01::42"), home));
        Assert.True(TrafficReach.IsLocal(IPAddress.Parse("203.0.113.12"), home));
        Assert.True(TrafficReach.IsLocal(IPAddress.Parse("::ffff:203.0.113.12"), home));
        Assert.False(TrafficReach.IsLocal(IPAddress.Parse("203.0.113.20"), home));
        Assert.False(TrafficReach.IsLocal(IPAddress.Parse("2606:4700::6810:84e5"), home));

        // A prefix too short to be a home network, or a single address, names none.
        Assert.Null(OnLinkPrefix.Of(IPAddress.Parse("2a02::1"), 16));
        Assert.Null(OnLinkPrefix.Of(IPAddress.Parse("2a02::1"), 128));
        Assert.Null(OnLinkPrefix.Of(IPAddress.Parse("8.8.8.8"), 8));
    }

    [Fact]
    public void AMinutesLocalShareIsKeptOnlyWhenWatchedWhole()
    {
        LocalMinuteTally tally = new();
        DateTimeOffset start = Noon.AddSeconds(10);
        long local = 0;
        long internet = 0;
        List<LocalMinute> kept = [];

        // Watching begins at 12:00:10; 12:00 is partly unwatched and 12:01
        // falls in the first minute of watching, so 12:02 is the first kept.
        for (int step = 0; step <= 24; step++)
        {
            local += 1000;
            internet += 5000;

            if (tally.Observe(start.AddSeconds(step * 10), measuring: true, new TrafficSplit(local / 2, local / 2, internet / 2, internet / 2)) is { } minute)
            {
                kept.Add(minute);
            }
        }

        Assert.NotEmpty(kept);
        Assert.Equal(EpochMinute.From(Noon.AddMinutes(2)), kept[0].Minute);
        Assert.All(kept, minute => Assert.Equal(3000, minute.BytesDown));
        Assert.All(kept, minute => Assert.Equal(3000, minute.BytesUp));
    }

    [Fact]
    public void AGapOrAPauseKeepsNeitherMinuteAroundIt()
    {
        LocalMinuteTally tally = new();
        List<LocalMinute> kept = [];
        DateTimeOffset at = Noon;
        long bytes = 0;

        void Read(bool measuring)
        {
            bytes += 600;

            if (tally.Observe(at, measuring, new TrafficSplit(bytes, bytes, 0, 0)) is { } minute)
            {
                kept.Add(minute);
            }
        }

        for (int step = 0; step < 18; step++)
        {
            Read(true);
            at += TimeSpan.FromSeconds(10);
        }

        int before = kept.Count;

        // Recording off for a reading: the open minute goes.
        Read(false);
        at += TimeSpan.FromSeconds(10);
        Read(true);
        at += TimeSpan.FromSeconds(10);

        // A 40-second gap: neither side is whole.
        at += TimeSpan.FromSeconds(40);
        Read(true);

        Assert.Equal(before, kept.Count);
    }

    [Fact]
    public async Task TheRecorderOffersAShareUntilHistoryTakesIt()
    {
        Split split = new();
        Takes store = new();
        InsightClock clock = new(Noon);
        LocalTrafficRecorder recorder = new(split, store, () => true, clock, NullLogger<LocalTrafficRecorder>.Instance);

        for (int step = 0; step < 30; step++)
        {
            split.Totals = new TrafficSplit(step * 100, step * 200, step * 1000, step * 2000);
            recorder.Tick();
            clock.Advance(TimeSpan.FromSeconds(10));
        }

        Assert.NotEmpty(store.Offered);

        // History took none (rows not written yet), so they are offered again.
        int offeredOnce = store.Offered.Count;
        store.Accept = true;
        recorder.Tick();
        Assert.True(store.Offered.Count > offeredOnce);
        Assert.NotEmpty(store.Taken);

        await Task.CompletedTask;
    }

    [Fact]
    public void FlowsJoinTheConversationTheyContinue()
    {
        using SqliteTimelineStore store = new(File_);

        store.Record([Flow(Browser, "142.250.187.196", 443, Noon, Noon.AddSeconds(5), host: "www.google.com", country: "US")]);
        store.Record([Flow(Browser, "142.250.187.196", 443, Noon.AddMinutes(1), Noon.AddMinutes(1).AddSeconds(3))]);

        // Six minutes quiet: a new conversation.
        store.Record([Flow(Browser, "142.250.187.196", 443, Noon.AddMinutes(8), Noon.AddMinutes(8).AddSeconds(1))]);

        IReadOnlyList<TimelineEntry> all = store.Search(new TimelineQuery(Noon.AddHours(-1), Noon.AddHours(1)));

        Assert.Equal(2, all.Count);
        TimelineEntry first = all[1];
        Assert.Equal(2000, first.Received);
        Assert.Equal(200, first.Sent);
        Assert.Equal("www.google.com", first.Host);
        Assert.Equal("US", first.Country);
        Assert.Equal(Noon.AddMinutes(1).AddSeconds(3), first.Last);
    }

    [Fact]
    public void TheTimelineIsSearchedByAnythingItHolds()
    {
        using SqliteTimelineStore store = new(File_);
        store.Record(
        [
            Flow(Browser, "142.250.187.196", 443, Noon, Noon.AddSeconds(1), host: "www.google.com", country: "US"),
            Flow(Torrent, "5.6.7.8", 51413, Noon, Noon.AddSeconds(1), country: "DE", organisation: "Hetzner Online GmbH"),
            Flow(Torrent, "192.168.1.30", 445, Noon, Noon.AddSeconds(1), local: true),
        ]);

        TimelineQuery all = new(Noon.AddHours(-1), Noon.AddHours(1));

        Assert.Single(store.Search(all with { Text = "google" }));
        Assert.Single(store.Search(all with { Text = "hetzner" }));
        Assert.Single(store.Search(all with { Text = "5.6.7" }));
        Assert.Equal(2, store.Search(all with { AttributionId = Torrent }).Count);
        Assert.Equal(2, store.Search(all with { Text = "torrent" }).Count);
        Assert.Empty(store.Search(all with { Text = "100%" }));
        Assert.Equal(3, store.Count(all));
        Assert.Single(store.Search(all with { Limit = 1, Offset = 2 }));
        Assert.Empty(store.Search(new TimelineQuery(Noon.AddHours(1), Noon.AddHours(2))));
    }

    [Fact]
    public void EachDaysPlacesAddUpBySite()
    {
        using SqliteTimelineStore store = new(File_);
        store.Record(
        [
            Flow(Browser, "142.250.187.196", 443, Noon, Noon.AddSeconds(1), host: "rr3---sn-q4f.googlevideo.com", country: "US"),
            Flow(Browser, "142.250.187.197", 443, Noon, Noon.AddSeconds(1), host: "rr5---sn-q4f.googlevideo.com", country: "US"),
            Flow(Torrent, "5.6.7.8", 51413, Noon, Noon.AddSeconds(1), country: "DE", organisation: "Hetzner Online GmbH"),
        ]);

        DateOnly day = DateOnly.FromDateTime(Noon.ToLocalTime().DateTime);
        IReadOnlyList<PlaceDay> places = store.ReadPlaces(day, day);

        PlaceDay video = Assert.Single(places, each => each.Site == "googlevideo.com");
        Assert.Equal(2000, video.Received);
        Assert.Equal(2, video.Conversations);
        Assert.Equal("US", video.Country);
        Assert.Contains(places, each => each.Site == "Hetzner Online GmbH" && each.Country == "DE");
    }

    [Fact]
    public void ServiceDaysAddUpAndAreReadByPeriod()
    {
        using SqliteTimelineStore store = new(File_);
        DateOnly today = DateOnly.FromDateTime(Noon.ToLocalTime().DateTime);

        store.AddServiceDays([new ServiceDayBytes(today, "DoSvc", 1000, 10), new ServiceDayBytes(today, "wuauserv", 500, 5)]);
        store.AddServiceDays([new ServiceDayBytes(today, "DoSvc", 1000, 10), new ServiceDayBytes(today.AddDays(-40), "DoSvc", 9, 9)]);

        IReadOnlyList<Windsock.Core.Insight.ServiceBytes> month = store.ReadServiceTotals(Noon.AddDays(-1), Noon);

        Assert.Equal(new Windsock.Core.Insight.ServiceBytes("DoSvc", 2000, 20), month[0]);
        Assert.Equal(2, month.Count);
    }

    [Fact]
    public void PruningAndErasingTakeWhatTheyShould()
    {
        using SqliteTimelineStore store = new(File_);
        store.Record(
        [
            Flow(Browser, "1.1.1.1", 443, Noon.AddDays(-40), Noon.AddDays(-40)),
            Flow(Browser, "1.1.1.2", 443, Noon.AddDays(-2), Noon.AddDays(-2)),
            Flow(Browser, "1.1.1.3", 443, Noon, Noon),
        ]);

        TimelineQuery ever = new(DateTimeOffset.UnixEpoch, Noon.AddDays(1));

        Assert.True(store.Prune(Noon.AddDays(-30)) > 0);
        Assert.Equal(2, store.Count(ever));

        store.Delete(Noon.AddDays(-3), Noon.AddDays(-1));
        Assert.Equal("1.1.1.3", Assert.Single(store.Search(ever)).Address);

        store.RemoveEverything();
        Assert.Equal(0, store.Count(ever));

        // Still usable after.
        store.Record([Flow(Browser, "1.1.1.4", 443, Noon, Noon)]);
        Assert.Equal(1, store.Count(ever));
    }

    [Fact]
    public void ErasingAPeriodOfHistoryReachesTheTimeline()
    {
        // History's erase calls every IErasedWithHistory; the timeline is one.
        using SqliteTimelineStore store = new(File_);
        store.Record(
        [
            Flow(Browser, "1.1.1.1", 443, Noon.AddHours(-3), Noon.AddHours(-3)),
            Flow(Browser, "1.1.1.2", 443, Noon.AddHours(-1), Noon.AddHours(-1)),
        ]);

        IErasedWithHistory erased = Assert.IsAssignableFrom<IErasedWithHistory>(store);
        Assert.True(erased.Delete(Noon.AddHours(-2), Noon) > 0);

        TimelineQuery ever = new(DateTimeOffset.UnixEpoch, Noon.AddDays(1));
        Assert.Equal("1.1.1.1", Assert.Single(store.Search(ever)).Address);
    }

    [Fact]
    public void ReadingPruningAndErasingNeverCreateTheFile()
    {
        using SqliteTimelineStore store = new(File_);
        TimelineQuery ever = new(DateTimeOffset.UnixEpoch, Noon.AddDays(1));

        Assert.Empty(store.Search(ever));
        Assert.Equal(0, store.Count(ever));
        Assert.Empty(store.ReadPlaces(DateOnly.MinValue, DateOnly.MaxValue));
        Assert.Empty(store.ReadServiceTotals(DateTimeOffset.UnixEpoch, Noon));
        Assert.Equal(0, store.Prune(Noon));
        Assert.Equal(0, store.Delete(DateTimeOffset.UnixEpoch, Noon));
        Assert.Equal(0, store.RemoveEverything());
        Assert.Equal(0, store.ForgetConversations());

        Assert.False(store.Exists);
        Assert.False(File.Exists(File_));

        // Keeping is what creates it.
        store.Record([Flow(Browser, "1.1.1.1", 443, Noon, Noon)]);
        Assert.True(store.Exists);
    }

    [Fact]
    public void WhatIsErasedIsOverwrittenNotJustUnlinked()
    {
        const string Marker = "erased-host.example";

        using (SqliteTimelineStore store = new(File_))
        {
            store.Record([Flow(Browser, "1.1.1.1", 443, Noon, Noon, host: Marker)]);
            Assert.True(store.Delete(Noon.AddMinutes(-1), Noon.AddMinutes(1)) > 0);
        }

        // Neither the file nor its write-ahead log still holds the name.
        foreach (string file in Directory.EnumerateFiles(_folder, "Timeline.db*"))
        {
            byte[] bytes = File.ReadAllBytes(file);
            Assert.DoesNotContain(Marker, System.Text.Encoding.UTF8.GetString(bytes), StringComparison.Ordinal);
        }
    }

    [Fact]
    public void SwitchingTheTimelineOffCanForgetItsConversationsButKeepServiceDays()
    {
        using SqliteTimelineStore store = new(File_);
        DateOnly today = DateOnly.FromDateTime(Noon.ToLocalTime().DateTime);
        store.Record([Flow(Browser, "1.1.1.1", 443, Noon, Noon, host: "a.example", country: "US")]);
        store.AddServiceDays([new ServiceDayBytes(today, "DoSvc", 10, 1)]);

        Assert.True(store.ForgetConversations() > 0);

        Assert.Equal(0, store.Count(new TimelineQuery(DateTimeOffset.UnixEpoch, Noon.AddDays(1))));
        Assert.Empty(store.ReadPlaces(today, today));
        Assert.Single(store.ReadServiceTotals(Noon.AddDays(-1), Noon.AddDays(1)));
    }

    [Fact]
    public void PastTheBudgetTheOldestConversationsGoFirst()
    {
        using SqliteTimelineStore store = new(File_, mostConversations: 3);
        store.Record([.. Enumerable.Range(0, 5).Select(each => Flow(Browser, "1.1.1." + each, 443, Noon.AddMinutes(each * 10), Noon.AddMinutes(each * 10)))]);

        store.Prune(Noon.AddDays(-30));

        IReadOnlyList<TimelineEntry> left = store.Search(new TimelineQuery(DateTimeOffset.UnixEpoch, Noon.AddDays(1)));
        Assert.Equal(["1.1.1.4", "1.1.1.3", "1.1.1.2"], left.Select(each => each.Address));
    }

    [Fact]
    public void TheRetentionHoldsWhileNothingIsBeingKept()
    {
        using (SqliteTimelineStore seed = new(File_))
        {
            seed.Record([Flow(Browser, "1.1.1.1", 443, Noon.AddDays(-60), Noon.AddDays(-60))]);
        }

        using SqliteTimelineStore store = new(File_);
        TimelineRecorder recorder = new(
            new NoFlows(),
            new NoTrace(),
            new ServiceHosts(new NoServices()),
            store,
            new FarEndNamer(_ => null, null, null),
            new ProcessIdentityResolver(),
            new Windsock.Core.Settings.WindsockSettings(),
            () => false,
            null,
            new InsightClock(Noon),
            NullLogger<TimelineRecorder>.Instance);

        // Not elevated, not recording: nothing is kept, but the old row goes.
        recorder.Tick();

        Assert.Equal(0, store.Count(new TimelineQuery(DateTimeOffset.UnixEpoch, Noon.AddDays(1))));
    }

    [Fact]
    public void ADamagedFileIsSetAsideAndAFreshOneStarted()
    {
        File.WriteAllText(File_, "this is not a database, and it is long enough to look like one's header....");

        using SqliteTimelineStore store = new(File_);
        store.Record([Flow(Browser, "1.1.1.1", 443, Noon, Noon)]);

        Assert.Equal(1, store.Count(new TimelineQuery(DateTimeOffset.UnixEpoch, Noon.AddDays(1))));
        Assert.Contains(Directory.EnumerateFiles(_folder), file => file.Contains(".corrupt", StringComparison.Ordinal));
    }

    [Fact]
    public void PastTheFlowCapNewFarEndsAreFoldedNotDropped()
    {
        using EtwProcessTrafficSource source = new() { FlowCapacity = 2 };
        source.CollectFlows = true;
        IPAddress here = IPAddress.Parse("192.168.1.5");

        for (int each = 1; each <= 5; each++)
        {
            source.Count(1234, 100, sent: false, here, IPAddress.Parse("8.8.8." + each), 443);
        }

        source.Count(1234, 7, sent: true, here, IPAddress.Parse("8.8.8.9"), 443);

        List<FlowSample> flows = [];
        source.TakeFlows(flows);

        // Two far ends as themselves, then one entry with no address for the
        // rest: every byte is still counted.
        Assert.Equal(3, flows.Count);
        Assert.Equal(500, flows.Sum(each => each.Received));
        FlowSample folded = Assert.Single(flows, each => !each.Remote.HasAddress);
        Assert.Equal(300, folded.Received);
        Assert.Equal(7, folded.Sent);
    }

    [Fact]
    public void AThousandPeersMakeOneOtherPlacesRow()
    {
        List<FlowSample> flows = [.. Enumerable.Range(1, 1000).Select(each => new FlowSample(
            9,
            EndpointKey.From(IPAddress.Parse("5.6." + (each / 250) + "." + (each % 250)), 51413),
            each,
            each * 2,
            1,
            Noon.UtcTicks,
            Noon.UtcTicks + each,
            Torrent))];

        List<FlowSample> capped = [.. TimelineRecorder.Capped(flows)];

        Assert.Equal(TimelineRecorder.PerAppCap + 1, capped.Count);
        Assert.False(capped[^1].Remote.HasAddress);
        Assert.Equal(flows.Sum(each => each.Sent), capped.Sum(each => each.Sent));
        Assert.Equal(1000, capped[0].Sent);
    }

    [Theory]
    [InlineData("type:  5 www.example.com.cdn.net;::ffff:93.184.216.34;", new[] { "93.184.216.34" }, new[] { "www.example.com.cdn.net" })]
    [InlineData("2606:4700::6810:84e5;2606:4700::6810:85e5;", new[] { "2606:4700::6810:84e5", "2606:4700::6810:85e5" }, new string[0])]
    [InlineData("", new string[0], new string[0])]
    [InlineData("garbage;type: 28;", new string[0], new string[0])]
    public void DnsResultsAreReadLeniently(string results, string[] addresses, string[] aliases)
    {
        DnsAnswer answer = DnsAnswer.Parse(results);

        Assert.Equal(addresses, answer.Addresses.Select(each => each.ToString()));
        Assert.Equal(aliases, answer.Aliases);
    }

    [Fact]
    public void EachProcessKeepsItsOwnNames()
    {
        InsightClock clock = new(Noon);
        DnsLookupLog log = new(clock);

        log.Asked(100, "www.example.com.", 1);
        log.Answered(100, "www.example.com", 1, 0, DnsAnswer.Parse("::ffff:93.184.216.34;"));
        log.Asked(100, "www.example.com", 1);
        log.Asked(200, "nosuch.example", 28);
        log.Answered(200, "nosuch.example", 28, 9003, DnsAnswer.Empty);

        DnsLookup lookup = Assert.Single(log.For(100));
        Assert.Equal("www.example.com", lookup.Name);
        Assert.Equal(2, lookup.Count);
        Assert.Equal("Found", lookup.StatusText);
        Assert.Equal("A", lookup.TypeText);
        Assert.Equal("www.example.com", log.NameFor(100, "93.184.216.34"));
        Assert.Equal("www.example.com", log.NameFor(100, "::ffff:93.184.216.34"));
        Assert.Null(log.NameFor(200, "93.184.216.34"));
        Assert.Equal("No such name", Assert.Single(log.For(200)).StatusText);
        Assert.Equal(2, log.For([100, 200, 300]).Count);

        // Two hours after a process's last lookup its names go.
        clock.Advance(TimeSpan.FromHours(3));
        log.Asked(300, "later.example", 1);
        Assert.Empty(log.For(100));

        log.Clear();
        Assert.Empty(log.For(300));
    }

    [Fact]
    public void AFullProcessMakesRoomForItsNewestNames()
    {
        InsightClock clock = new(Noon);
        DnsLookupLog log = new(clock);

        for (int each = 0; each < 600; each++)
        {
            log.Asked(100, string.Create(CultureInfo.InvariantCulture, $"n{each}.example"), 1);
            clock.Advance(TimeSpan.FromSeconds(1));
        }

        IReadOnlyList<DnsLookup> kept = log.For(100);
        Assert.Equal(512, kept.Count);
        Assert.Equal("n599.example", kept[0].Name);
        Assert.DoesNotContain(kept, each => each.Name == "n0.example");
    }

    [Fact]
    public void EachNameAgesOnItsOwnWhileItsProcessGoesOnAsking()
    {
        InsightClock clock = new(Noon);
        DnsLookupLog log = new(clock);

        log.Asked(200, "early.example", 1);
        log.Answered(200, "early.example", 1, 0, DnsAnswer.Parse("::ffff:93.184.216.34;"));

        for (int each = 0; each < 5; each++)
        {
            clock.Advance(TimeSpan.FromMinutes(30));
            log.Asked(200, "late.example", 1);
        }

        // Two and a half hours on, the process is still asking - but not for this.
        Assert.DoesNotContain(log.For(200), each => each.Name == "early.example");
        Assert.DoesNotContain(log.All(), each => each.Lookup.Name == "early.example");
        Assert.Null(log.NameFor(200, "93.184.216.34"));
        Assert.Contains(log.For(200), each => each.Name == "late.example");
    }

    [Fact]
    public void EveryProcesssNamesAreReadTogetherLatestFirst()
    {
        InsightClock clock = new(Noon);
        DnsLookupLog log = new(clock);

        log.Asked(100, "first.example", 1);
        clock.Advance(TimeSpan.FromSeconds(1));
        log.Asked(200, "second.example", 1);
        clock.Advance(TimeSpan.FromSeconds(1));
        log.Asked(100, "third.example", 28);

        IReadOnlyList<(int ProcessId, DnsLookup Lookup)> all = log.All();
        Assert.Equal(["third.example", "second.example", "first.example"], all.Select(each => each.Lookup.Name));
        Assert.Equal([100, 200, 100], all.Select(each => each.ProcessId));

        Assert.Equal("third.example", Assert.Single(log.All(limit: 1)).Lookup.Name);
        Assert.Empty(log.All(limit: -5));
    }

    [Fact]
    public void TheDnsClientsManifestCarriesTheFieldsRead()
    {
        // The provider's manifest as registered on this machine: which events
        // carry ClientPID is read from it, not assumed.
        string manifest = RegisteredTraceEventParser.GetManifestForRegisteredProvider(EtwDnsLookupSource.Provider);
        XDocument document = XDocument.Parse(manifest);
        XNamespace ns = document.Root!.GetDefaultNamespace();

        Dictionary<string, HashSet<string>> fields = [];

        foreach (XElement template in document.Descendants(ns + "template"))
        {
            fields[(string)template.Attribute("tid")!] = [.. template.Elements(ns + "data").Select(each => (string)each.Attribute("name")!)];
        }

        HashSet<string> FieldsOf(int id, int version) =>
            document.Descendants(ns + "event")
                .Where(each => (string?)each.Attribute("value") == id.ToString(System.Globalization.CultureInfo.InvariantCulture)
                    && ((string?)each.Attribute("version") ?? "0") == version.ToString(System.Globalization.CultureInfo.InvariantCulture))
                .Select(each => (string?)each.Attribute("template"))
                .Where(each => each is not null && fields.ContainsKey(each))
                .Select(each => fields[each!])
                .FirstOrDefault() ?? [];

        Assert.Contains("QueryName", FieldsOf(EtwDnsLookupSource.QueryStarted, 0));
        Assert.Contains("QueryType", FieldsOf(EtwDnsLookupSource.QueryStarted, 0));
        Assert.DoesNotContain("ClientPID", FieldsOf(EtwDnsLookupSource.QueryStarted, 0));
        Assert.Contains("QueryResults", FieldsOf(EtwDnsLookupSource.QueryFinished, 0));
        Assert.Contains("QueryStatus", FieldsOf(EtwDnsLookupSource.QueryFinished, 0));
        Assert.Contains("ClientPID", FieldsOf(EtwDnsLookupSource.CacheAnswered, 1));
    }

    [Fact]
    public void ADnsEventIsPutToTheProcessItIsAbout()
    {
        EtwDnsLookupSource source = new(new NoTrace(), new DnsLookupLog(), processId => processId == 2000, NullLogger<EtwDnsLookupSource>.Instance);

        Assert.Equal(500, source.Whose(sender: 2000, clientProcessId: 500));
        Assert.Equal(0, source.Whose(sender: 2000, clientProcessId: null));
        Assert.Equal(700, source.Whose(sender: 700, clientProcessId: null));
        Assert.Equal(700, source.Whose(sender: 700, clientProcessId: 0));
        Assert.Equal(0, source.Whose(sender: 4, clientProcessId: null));
    }

    [Fact]
    public void AConnectionRowIsWrittenAsTheTableKeepsIt()
    {
        ProcessConnection connection = new(42, TransportProtocol.Tcp, false, "192.168.1.5", 50443, "142.250.187.196", 443, TcpConnectionState.Established, null);

        Assert.True(TcpConnectionCloser.TryRow(connection, out TcpConnectionCloser.TcpRow row));
        Assert.Equal(12u, row.State);

        // The same encoding the connection tables are read with, the other way.
        Assert.Equal("192.168.1.5", new IPAddress(row.LocalAddr).ToString());
        Assert.Equal("142.250.187.196", new IPAddress(row.RemoteAddr).ToString());
        Assert.Equal(443, Listen(row.RemotePort));
        Assert.Equal(50443, Listen(row.LocalPort));
        Assert.Equal(20, System.Runtime.InteropServices.Marshal.SizeOf<TcpConnectionCloser.TcpRow>());

        static int Listen(uint port) => Windsock.Core.Insight.Listeners.Port(port);
    }

    [Fact]
    public void OnlyAnIpv4TcpConversationCanBeClosed()
    {
        Assert.Equal(CloseOutcome.NotOnIpv6, TcpConnectionCloser.CannotClose(
            new ProcessConnection(1, TransportProtocol.Tcp, true, "2a00::1", 50000, "2606:4700::1", 443, TcpConnectionState.Established, null))!.Outcome);
        Assert.Null(TcpConnectionCloser.CannotClose(
            new ProcessConnection(1, TransportProtocol.Tcp, true, "::ffff:192.168.1.5", 50000, "::ffff:1.1.1.1", 443, TcpConnectionState.Established, null)));
        Assert.Equal(CloseOutcome.NotAConnection, TcpConnectionCloser.CannotClose(
            new ProcessConnection(1, TransportProtocol.Udp, false, "0.0.0.0", 5353, null, 0, TcpConnectionState.Unknown, null))!.Outcome);
        Assert.Equal(CloseOutcome.NotAConnection, TcpConnectionCloser.CannotClose(
            new ProcessConnection(1, TransportProtocol.Tcp, false, "0.0.0.0", 80, "0.0.0.0", 0, TcpConnectionState.Listening, null))!.Outcome);
    }

    [Fact]
    public void ClosingNeedsAnAdministratorAndOnlyTouchesOurOwnSocket()
    {
        // Our own throwaway loopback connection: nothing else is ever aimed at.
        using TcpListener listener = new(IPAddress.Loopback, 0);
        listener.Start();
        using TcpClient client = new();
        client.Connect(IPAddress.Loopback, ((IPEndPoint)listener.LocalEndpoint).Port);
        using TcpClient accepted = listener.AcceptTcpClient();

        IPEndPoint mine = (IPEndPoint)client.Client.LocalEndPoint!;
        ProcessConnection connection = new(
            Environment.ProcessId,
            TransportProtocol.Tcp,
            false,
            mine.Address.ToString(),
            mine.Port,
            "127.0.0.1",
            ((IPEndPoint)listener.LocalEndpoint).Port,
            TcpConnectionState.Established,
            null);

        CloseResult result = TcpConnectionCloser.Close(connection);

        Assert.Equal(Environment.IsPrivilegedProcess ? CloseOutcome.Closed : CloseOutcome.NeedsAdministrator, result.Outcome);
    }

    [Fact]
    public void FirewallRulesAreReadWithoutChangingAnything()
    {
        // Reading works for a standard user; nothing here adds or removes a rule.
        IReadOnlyList<FirewallRule> rules = FirewallBlocks.List();
        Assert.All(rules, rule => Assert.StartsWith("Windsock: block ", rule.Name, StringComparison.Ordinal));
        Assert.NotNull(FirewallBlocks.Standing());

        if (!FirewallBlocks.CanChange)
        {
            string program = Path.Combine(Path.GetTempPath(), "windsock-never-blocked.exe");
            Assert.Equal(FirewallOutcome.NeedsAdministrator, FirewallBlocks.BlockProgram(program, "never").Outcome);
            Assert.Equal(FirewallOutcome.NeedsAdministrator, FirewallBlocks.BlockService("WindsockNoSuchService", "never").Outcome);
        }

        Assert.Equal(FirewallOutcome.NotFound, FirewallBlocks.Remove("Windsock: block nothing (nowhere)").Outcome);
        Assert.Equal(FirewallOutcome.Failed, FirewallBlocks.BlockProgram(@"\Device\HarddiskVolume3\x.exe", "x").Outcome);
    }

    [Fact]
    public void ARuleNameIsUniqueToWhatItBlocksAndHasNoBar() =>
        Assert.Equal(
            @"Windsock: block A/B (C:\Program Files\a.exe)",
            FirewallBlocks.RuleName("A|B", @"C:\Program Files\a.exe"));

    [Theory]
    [InlineData(@"C:\Windows\System32\svchost.exe")]
    [InlineData(@"C:\WINDOWS\system32\SVCHOST.EXE")]
    [InlineData(@"C:\Windows\SysWOW64\svchost.exe")]
    [InlineData(@"C:\Windows\System32\lsass.exe")]
    [InlineData(@"C:\Windows\System32\csrss.exe")]
    public void AServiceHostOrAPartOfWindowsIsNeverBlockedAsAProgram(string path)
    {
        // A rule on svchost.exe would block every service host at once. Refused
        // before the firewall is asked anything, elevated or not.
        Assert.NotNull(FirewallBlocks.ProgramRefusal(path));
        Assert.Equal(FirewallOutcome.NotAllowed, FirewallBlocks.BlockProgram(path, "Delivery Optimization").Outcome);
    }

    [Theory]
    [InlineData("Dnscache")]
    [InlineData("dhcp")]
    [InlineData("BFE")]
    [InlineData("NlaSvc")]
    public void AServiceWindowsNeedsToReachTheNetworkIsNeverBlocked(string service)
    {
        Assert.NotNull(FirewallBlocks.ServiceRefusal(service));
        Assert.Equal(FirewallOutcome.NotAllowed, FirewallBlocks.BlockService(service, service).Outcome);
    }

    [Fact]
    public void AnOrdinaryProgramOrServiceMayBeBlocked()
    {
        Assert.Null(FirewallBlocks.ProgramRefusal(@"C:\Program Files\App\app.exe"));
        Assert.Null(FirewallBlocks.ProgramRefusal(@"C:\Users\me\svchost-helper.exe"));
        Assert.Null(FirewallBlocks.ServiceRefusal("DoSvc"));
        Assert.Null(FirewallBlocks.ServiceRefusal(null));
    }

    [Fact]
    public void GroupPolicyOverridingLocalRulesIsSaid()
    {
        FirewallStanding ignored = new(FirewallOn: true, OutboundAllowedByDefault: true, LocalRulesIgnored: true);
        Assert.False(ignored.RulesTakeEffect);
        Assert.Contains("Group Policy", ignored.Note, StringComparison.Ordinal);

        FirewallStanding normal = new(FirewallOn: true, OutboundAllowedByDefault: true);
        Assert.True(normal.RulesTakeEffect);
        Assert.Null(normal.Note);

        Assert.Contains("off", new FirewallStanding(false, true).Note, StringComparison.Ordinal);
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_folder, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    private static TimelineFlow Flow(
        string app,
        string address,
        int port,
        DateTimeOffset first,
        DateTimeOffset last,
        string? host = null,
        string? country = null,
        string? organisation = null,
        bool local = false) =>
        new(app, Path.GetFileNameWithoutExtension(app), address, port, host, country, organisation, local, first, last, 1000, 100, 10);

    private sealed class Split : ITrafficSplitSource
    {
        public TrafficSplit Totals { get; set; }

        public bool CanAttribute => true;

        public TrafficSplit ReadSplit() => Totals;
    }

    private sealed class Takes : ILocalTrafficStore
    {
        public bool Accept { get; set; }

        public List<LocalMinute> Offered { get; } = [];

        public List<LocalMinute> Taken { get; } = [];

        public IReadOnlyList<long> WriteLocalTraffic(IReadOnlyList<LocalMinute> minutes)
        {
            Offered.AddRange(minutes);

            if (!Accept)
            {
                return [];
            }

            Taken.AddRange(minutes);
            return [.. minutes.Select(each => each.Minute)];
        }
    }

    private sealed class NoFlows : IFlowLedger
    {
        public bool CanAttribute => false;

        public bool CollectFlows { get; set; }

        public void TakeFlows(List<FlowSample> into) => into.Clear();
    }

    private sealed class NoServices : IServiceHostSource
    {
        public IReadOnlyList<HostedService> Read() => [];
    }

    private sealed class NoTrace : IProcessTrafficSource
    {
        public TrafficSourceState State => TrafficSourceState.Stopped;

        public string? Detail => null;

        public string? Explanation => null;

        public void Start()
        {
        }

        public void CopyTo(Dictionary<int, ProcessTraffic> destination) => destination.Clear();
    }
}
#endif
