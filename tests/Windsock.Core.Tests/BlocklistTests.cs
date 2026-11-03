using System.Net;
using Microsoft.Extensions.Logging.Abstractions;
using Windsock.Core.Security;
using Windsock.Core.Settings;
using Xunit;

namespace Windsock.Core.Tests;

public sealed class BlocklistTests : IDisposable
{
    // Lines in the shape of spamhaus.org/drop/drop_v4.json and drop_v6.json, 2026-10-05.
    private const string DropFour =
        """
        {"cidr":"1.10.16.0/20","sblid":"SBL256894","rir":"apnic"}
        {"cidr":"2.56.192.0/22","sblid":"SBL459831","rir":"ripencc"}
        {"cidr":"100.64.0.0/10","sblid":"SBL000001","rir":"test"}
        {"type":"metadata","timestamp":1791150242,"size":103416,"records":3,"copyright":"(c) 2026 The Spamhaus Project SLU","terms":"https://www.spamhaus.org/drop/terms/"}
        """;

    private const string DropSix =
        """
        {"cidr":"2001:678:254::/48","sblid":"SBL697648","rir":"ripencc"}
        {"type":"metadata","timestamp":1791150242,"size":5759,"records":1,"copyright":"(c) 2026 The Spamhaus Project SLU","terms":"https://www.spamhaus.org/drop/terms/"}
        """;

    // The shape of feodotracker.abuse.ch/downloads/ipblocklist_recommended.json, 2026-10-05.
    private const string Feodo =
        """
        [
            {
                "ip_address": "50.16.16.211",
                "port": 443,
                "status": "online",
                "hostname": "ec2-50-16-16-211.compute-1.amazonaws.com",
                "as_number": 14618,
                "as_name": "AMAZON-AES",
                "country": "US",
                "first_seen": "2025-12-30 13:56:31",
                "last_online": "2026-03-12",
                "malware": "QakBot"
            }
        ]
        """;

    private readonly string _folder = Path.Combine(Path.GetTempPath(), "windsock-lists-" + Guid.NewGuid().ToString("N"));

    [Fact]
    public void SpamhausLinesAreReadWithTheirMetadata()
    {
        BlocklistFile file = BlocklistFormats.ReadSpamhaus(DropFour);

        Assert.Equal(3, file.Entries.Count);
        Assert.Equal(new BlocklistEntry("1.10.16.0/20", "SBL256894"), file.Entries[0]);
        Assert.Equal("(c) 2026 The Spamhaus Project SLU", file.Copyright);
        Assert.Equal(DateTimeOffset.FromUnixTimeSeconds(1791150242), file.PublishedAt);
    }

    [Fact]
    public void ABrokenLineCostsOnlyThatLine()
    {
        BlocklistFile file = BlocklistFormats.ReadSpamhaus("{\"cidr\":\"1.2.3.0/24\"}\n{broken\n\n{\"cidr\":\"5.6.7.0/24\",\"sblid\":\"SBL1\"}");

        Assert.Equal(["1.2.3.0/24", "5.6.7.0/24"], file.Entries.Select(each => each.Cidr));
    }

    [Fact]
    public void FeodoEntriesSayWhatTheAddressDoes()
    {
        BlocklistEntry entry = Assert.Single(BlocklistFormats.ReadFeodo(Feodo).Entries);

        Assert.Equal("50.16.16.211", entry.Cidr);
        Assert.Equal("QakBot botnet control server, port 443", entry.Reference);
        Assert.Empty(BlocklistFormats.ReadFeodo("{\"not\":\"an array\"}").Entries);
        Assert.Empty(BlocklistFormats.ReadFeodo("not json").Entries);
    }

    [Theory]
    [InlineData("1.10.16.0", true)]
    [InlineData("1.10.31.255", true)]
    [InlineData("1.10.32.0", false)]
    [InlineData("1.10.15.255", false)]
    [InlineData("2001:678:254:1::5", true)]
    [InlineData("2001:678:255::1", false)]
    [InlineData("::ffff:1.10.20.1", true)]
    public void ANetworkHoldsExactlyItsAddresses(string address, bool held)
    {
        CidrSet set = CidrSet.From(
        [
            new BlocklistEntry("1.10.16.0/20", "a"),
            new BlocklistEntry("2001:678:254::/48", "b"),
        ]);

        Assert.Equal(held, set.Find(IPAddress.Parse(address)) is not null);
    }

    [Fact]
    public void OverlappingNetworksAnswerOnce()
    {
        CidrSet set = CidrSet.From(
        [
            new BlocklistEntry("10.0.0.0/8", "wide"),
            new BlocklistEntry("10.1.0.0/16", "inside"),
            new BlocklistEntry("11.0.0.0/16", "next"),
            new BlocklistEntry("11.0.0.0/24", "same start"),
            new BlocklistEntry("8.8.8.8", "one"),
            new BlocklistEntry("not a network", "skipped"),
            new BlocklistEntry("1.2.3.4/33", "skipped"),
        ]);

        Assert.Equal(3, set.Count);
        Assert.Equal("wide", set.Find(IPAddress.Parse("10.1.2.3"))!.Reference);
        Assert.Equal("next", set.Find(IPAddress.Parse("11.0.0.9"))!.Reference);
        Assert.Equal("one", set.Find(IPAddress.Parse("8.8.8.8"))!.Reference);
        Assert.Null(set.Find(IPAddress.Parse("8.8.8.9")));
        Assert.Null(set.Find(null));
    }

    [Theory]
    [InlineData("1.2.3.4", true, 32)]
    [InlineData("1.2.3.0/24", true, 24)]
    [InlineData("::/0", true, 0)]
    [InlineData("2001:db8::/129", false, 0)]
    [InlineData("1.2.3.0/", false, 0)]
    [InlineData("1.2.3.0/-1", false, 0)]
    [InlineData("", false, 0)]
    public void NetworksAreReadStrictly(string text, bool valid, int prefix)
    {
        Assert.Equal(valid, CidrSet.TryParse(text, out _, out int read));

        if (valid)
        {
            Assert.Equal(prefix, read);
        }
    }

    [Fact]
    public void AListIsFetchedOnlyOnceSwitchedOnAndNeverInsideTheHour()
    {
        DateTimeOffset now = new(2026, 10, 5, 12, 0, 0, TimeSpan.Zero);

        Assert.True(Blocklists.IsDue(now, fetchedAt: null, attemptedAt: null));
        Assert.False(Blocklists.IsDue(now, fetchedAt: null, attemptedAt: now.AddMinutes(-59)));
        Assert.True(Blocklists.IsDue(now, fetchedAt: null, attemptedAt: now.AddMinutes(-61)));
        Assert.False(Blocklists.IsDue(now, fetchedAt: now.AddHours(-11), attemptedAt: now.AddHours(-11)));
        Assert.True(Blocklists.IsDue(now, fetchedAt: now.AddHours(-12), attemptedAt: now.AddHours(-12)));

        // A clock moved back does not leave a list stuck.
        Assert.True(Blocklists.IsDue(now, fetchedAt: now.AddDays(3), attemptedAt: now.AddDays(3)));
    }

    [Fact]
    public async Task NothingIsFetchedWhileTheListsAreOff()
    {
        Publisher publisher = new();
        WindsockSettings settings = new();
        using Blocklists lists = Lists(settings, publisher, new InsightClock(DateTimeOffset.UtcNow));

        await lists.SettleAsync(TestContext.Current.CancellationToken);

        Assert.Empty(publisher.Asked);
        Assert.Null(lists.Check("1.10.16.1"));
    }

    [Fact]
    public async Task ASwitchedOnListMarksItsAddressesButNeverAPrivateOne()
    {
        Publisher publisher = new();
        WindsockSettings settings = new();
        settings.Insight.SpamhausDrop = true;
        InsightClock clock = new(new DateTimeOffset(2026, 10, 5, 12, 0, 0, TimeSpan.Zero));
        using Blocklists lists = Lists(settings, publisher, clock);

        await lists.SettleAsync(TestContext.Current.CancellationToken);

        Assert.Equal(2, publisher.Asked.Count);
        Assert.All(publisher.UserAgents, agent => Assert.StartsWith("Windsock/", agent, StringComparison.Ordinal));

        BlocklistHit? hit = lists.Check("1.10.16.1");
        Assert.NotNull(hit);
        Assert.Equal("SBL256894", hit.Entry.Reference);
        Assert.Equal("(c) 2026 The Spamhaus Project SLU", hit.Copyright);
        Assert.StartsWith("Listed in Spamhaus DROP (SBL256894)", hit.Text, StringComparison.Ordinal);
        Assert.NotNull(lists.Check("2001:678:254::9"));

        // The list carries carrier-grade NAT; the person's own provider is never marked.
        Assert.Null(lists.Check("100.64.0.1"));
        Assert.Null(lists.Check("192.168.1.1"));

        // Within the hour nothing is asked again, however often it looks.
        clock.Advance(TimeSpan.FromMinutes(30));
        await lists.SettleAsync(TestContext.Current.CancellationToken);
        Assert.Equal(2, publisher.Asked.Count);

        BlocklistStatus status = Assert.Single(lists.Status(), each => each.Source == BlocklistSources.SpamhausDrop);
        Assert.True(status.Wanted);
        Assert.Equal(4, status.Networks);
    }

    [Fact]
    public async Task AnUnchangedListCostsANotModifiedAndAReloadReadsTheCopy()
    {
        Publisher publisher = new();
        WindsockSettings settings = new();
        settings.Insight.SpamhausDrop = true;
        InsightClock clock = new(new DateTimeOffset(2026, 10, 5, 12, 0, 0, TimeSpan.Zero));

        using (Blocklists first = Lists(settings, publisher, clock))
        {
            await first.SettleAsync(TestContext.Current.CancellationToken);
        }

        // A later run reads the copy without asking.
        clock.Advance(TimeSpan.FromHours(2));
        using Blocklists second = Lists(settings, publisher, clock);
        await second.SettleAsync(TestContext.Current.CancellationToken);
        Assert.Equal(2, publisher.Asked.Count);
        Assert.NotNull(second.Check("1.10.16.1"));

        // Twelve hours on it asks, saying what it holds, and keeps it on "not modified".
        clock.Advance(TimeSpan.FromHours(12));
        publisher.Unchanged = true;
        await second.SettleAsync(TestContext.Current.CancellationToken);
        Assert.Equal(4, publisher.Asked.Count);
        Assert.Equal(2, publisher.Conditional);
        Assert.NotNull(second.Check("1.10.16.1"));
    }

    [Fact]
    public async Task AFailedFetchKeepsTheCopyAndSaysWhy()
    {
        Publisher publisher = new();
        WindsockSettings settings = new();
        settings.Insight.SpamhausDrop = true;
        InsightClock clock = new(new DateTimeOffset(2026, 10, 5, 12, 0, 0, TimeSpan.Zero));
        using Blocklists lists = Lists(settings, publisher, clock);
        await lists.SettleAsync(TestContext.Current.CancellationToken);

        clock.Advance(TimeSpan.FromHours(13));
        publisher.Failing = true;
        await lists.SettleAsync(TestContext.Current.CancellationToken);

        Assert.NotNull(lists.Check("1.10.16.1"));
        Assert.Contains("503", Assert.Single(lists.Status(), each => each.Source == BlocklistSources.SpamhausDrop).Problem, StringComparison.Ordinal);
    }

    [Fact]
    public async Task SwitchingAListOffDeletesItsCopy()
    {
        Publisher publisher = new();
        WindsockSettings settings = new();
        settings.Insight.SpamhausDrop = true;
        settings.Insight.FeodoTracker = true;
        using Blocklists lists = Lists(settings, publisher, new InsightClock(new DateTimeOffset(2026, 10, 5, 12, 0, 0, TimeSpan.Zero)));
        await lists.SettleAsync(TestContext.Current.CancellationToken);

        Assert.NotNull(lists.Check("50.16.16.211"));
        Assert.Contains(Directory.EnumerateFiles(_folder), file => Path.GetFileName(file).StartsWith("spamhaus-drop.", StringComparison.Ordinal));

        settings.Insight.SpamhausDrop = false;
        await lists.SettleAsync(TestContext.Current.CancellationToken);

        Assert.Null(lists.Check("1.10.16.1"));
        Assert.NotNull(lists.Check("50.16.16.211"));

        // Only when it was last asked for stays, for the hour: nothing of the
        // publisher's - no copy, no copyright, no tags.
        string kept = Assert.Single(Directory.EnumerateFiles(_folder), file => Path.GetFileName(file).StartsWith("spamhaus-drop.", StringComparison.Ordinal));
        Assert.Equal("spamhaus-drop.meta.json", Path.GetFileName(kept));
        string meta = await File.ReadAllTextAsync(kept, TestContext.Current.CancellationToken);
        Assert.DoesNotContain("Spamhaus", meta, StringComparison.Ordinal);
        Assert.DoesNotContain("v1", meta, StringComparison.Ordinal);
    }

    [Fact]
    public async Task SwitchingAListOffAndOnAgainStillAsksAtMostHourly()
    {
        Publisher publisher = new();
        WindsockSettings settings = new();
        settings.Insight.SpamhausDrop = true;
        InsightClock clock = new(new DateTimeOffset(2026, 10, 5, 12, 0, 0, TimeSpan.Zero));
        using Blocklists lists = Lists(settings, publisher, clock);
        await lists.SettleAsync(TestContext.Current.CancellationToken);
        int asked = publisher.Asked.Count;

        settings.Insight.SpamhausDrop = false;
        await lists.SettleAsync(TestContext.Current.CancellationToken);
        settings.Insight.SpamhausDrop = true;
        clock.Advance(TimeSpan.FromMinutes(5));
        await lists.SettleAsync(TestContext.Current.CancellationToken);

        Assert.Equal(asked, publisher.Asked.Count);
        Assert.Contains("hour", Assert.Single(lists.Status(), each => each.Source == BlocklistSources.SpamhausDrop).Problem, StringComparison.Ordinal);

        clock.Advance(TimeSpan.FromHours(1));
        await lists.SettleAsync(TestContext.Current.CancellationToken);

        Assert.True(publisher.Asked.Count > asked);
        Assert.NotNull(lists.Check("1.10.16.1"));
    }

    [Fact]
    public async Task AListLargerThanAnyRealOneIsRefusedAsItArrives()
    {
        Publisher publisher = new() { Huge = true };
        WindsockSettings settings = new();
        settings.Insight.FeodoTracker = true;
        using Blocklists lists = Lists(settings, publisher, new InsightClock(new DateTimeOffset(2026, 10, 5, 12, 0, 0, TimeSpan.Zero)));

        await lists.SettleAsync(TestContext.Current.CancellationToken);

        Assert.Null(lists.Check("50.16.16.211"));
        Assert.NotNull(Assert.Single(lists.Status(), each => each.Source == BlocklistSources.FeodoTracker).Problem);
    }

    [Theory]
    [InlineData("{\"type\":1}")]
    [InlineData("{\"type\":\"metadata\",\"timestamp\":\"abc\"}")]
    [InlineData("{\"type\":\"metadata\",\"timestamp\":1e300}")]
    [InlineData("{\"type\":\"metadata\",\"timestamp\":99999999999999}")]
    [InlineData("{\"type\":\"metadata\",\"timestamp\":-5,\"copyright\":3}")]
    [InlineData("{\"cidr\":7,\"sblid\":[]}")]
    public void ASpamhausLineOfTheWrongShapeCostsOnlyThatLine(string hostile)
    {
        BlocklistFile file = BlocklistFormats.ReadSpamhaus(hostile + "\n{\"cidr\":\"1.2.3.0/24\"}");

        Assert.Equal("1.2.3.0/24", Assert.Single(file.Entries).Cidr);
        Assert.Null(file.PublishedAt);
    }

    [Theory]
    [InlineData("[{\"ip_address\":\"1.2.3.4\",\"port\":\"443\"}]", 1)]
    [InlineData("[{\"ip_address\":\"1.2.3.4\",\"port\":1e40,\"malware\":5}]", 1)]
    [InlineData("[{\"ip_address\":5},1,\"x\",null]", 0)]
    [InlineData("{\"ip_address\":\"1.2.3.4\"}", 0)]
    [InlineData("[", 0)]
    public void AFeodoFileOfTheWrongShapeIsReadWithoutThrowing(string hostile, int expected) =>
        Assert.Equal(expected, BlocklistFormats.ReadFeodo(hostile).Entries.Count);

    [Fact]
    public void ThePrivacyPagesSwitchesAreTheListsOwn()
    {
        WindsockSettings settings = new();

        Assert.All(InsightContacts.All, contact => Assert.False(contact.IsAllowed(settings)));

        InsightContacts.SpamhausDrop.Allow!(settings, true);
        Assert.True(settings.Insight.SpamhausDrop);
        Assert.True(BlocklistSources.IsWanted(BlocklistSources.SpamhausDrop, settings.Insight));
        Assert.False(BlocklistSources.IsWanted(BlocklistSources.FeodoTracker, settings.Insight));
        Assert.Equal("www.spamhaus.org", InsightContacts.SpamhausDrop.Destination(settings));
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_folder, recursive: true);
        }
        catch (DirectoryNotFoundException)
        {
        }
    }

    private Blocklists Lists(WindsockSettings settings, Publisher publisher, InsightClock clock) =>
        new(settings, _folder, new HttpClient(publisher, disposeHandler: false), clock, NullLogger<Blocklists>.Instance);

    /// <summary>Stands in for the publishers' servers.</summary>
    private sealed class Publisher : HttpMessageHandler
    {
        public List<Uri> Asked { get; } = [];

        public List<string> UserAgents { get; } = [];

        public int Conditional { get; private set; }

        public bool Unchanged { get; set; }

        public bool Failing { get; set; }

        public bool Huge { get; set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Asked.Add(request.RequestUri!);
            UserAgents.Add(request.Headers.UserAgent.ToString());

            if (request.Headers.IfNoneMatch.Count > 0 || request.Headers.IfModifiedSince is not null)
            {
                Conditional++;
            }

            if (Failing)
            {
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.ServiceUnavailable));
            }

            if (Unchanged && request.Headers.IfNoneMatch.Count > 0)
            {
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotModified));
            }

            string path = request.RequestUri!.AbsolutePath;
            string body = path.EndsWith("drop_v4.json", StringComparison.Ordinal) ? DropFour
                : path.EndsWith("drop_v6.json", StringComparison.Ordinal) ? DropSix
                : Feodo;

            if (Huge)
            {
                body = "[" + new string(' ', 9 * 1024 * 1024) + "]";
            }

            HttpResponseMessage response = new(HttpStatusCode.OK) { Content = new StringContent(body) };
            response.Headers.ETag = new System.Net.Http.Headers.EntityTagHeaderValue("\"v1\"");
            return Task.FromResult(response);
        }
    }
}
