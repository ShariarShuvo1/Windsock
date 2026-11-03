using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using Windsock.Core.Feed;
using Windsock.Core.History;
using Windsock.Core.Settings;
using Xunit;

namespace Windsock.Core.Tests;

public sealed class LocalFeedTests : IDisposable
{
    private const string Token = "0123456789abcdefghijklmnopqrstuvwxyzABCDEFG";
    private static readonly IPAddress Loopback = IPAddress.Loopback;

    private readonly string _folder = Path.Combine(Path.GetTempPath(), "windsock-feed-" + Guid.NewGuid().ToString("N"));

    [Fact]
    public void Guard_LetsTheRightRequestThrough()
    {
        Assert.Equal(FeedVerdict.Allowed, Check(authorization: "Bearer " + Token));
        Assert.Equal(FeedVerdict.Allowed, Check(host: "localhost:40406", authorization: "bearer " + Token));
        Assert.Equal(FeedVerdict.Allowed, Check(host: "[::1]:40406", remote: IPAddress.IPv6Loopback, query: Token));
    }

    /// <summary>DNS rebinding: a page whose own name points at 127.0.0.1 sends its own Host. Measured to reach the handler.</summary>
    [Theory]
    [InlineData("evil.example")]
    [InlineData("evil.example:40406")]
    [InlineData("127.0.0.1")]
    [InlineData("127.0.0.1:40407")]
    [InlineData("localhost.evil.example:40406")]
    [InlineData("")]
    [InlineData(null)]
    public void Guard_RefusesAForeignHost(string? host)
    {
        Assert.Equal(FeedVerdict.WrongHost, Check(host: host, authorization: "Bearer " + Token));
    }

    [Fact]
    public void Guard_RefusesOtherSitesPages()
    {
        Assert.Equal(FeedVerdict.ForeignPage, Check(origin: "https://evil.example", authorization: "Bearer " + Token));
        Assert.Equal(FeedVerdict.ForeignPage, Check(origin: "null", authorization: "Bearer " + Token));
        Assert.Equal(FeedVerdict.ForeignPage, Check(fetchSite: "cross-site", fetchMode: "no-cors", query: Token));
        Assert.Equal(FeedVerdict.Allowed, Check(origin: "http://127.0.0.1:40406", fetchSite: "same-origin", fetchMode: "cors", query: Token));
        Assert.Equal(FeedVerdict.Allowed, Check(fetchSite: "none", fetchMode: "navigate", query: Token));
        Assert.Equal(FeedVerdict.Allowed, Check(fetchSite: "cross-site", fetchMode: "navigate", query: Token));
    }

    [Fact]
    public void Guard_RefusesWithoutTheToken()
    {
        Assert.Equal(FeedVerdict.NoToken, Check());
        Assert.Equal(FeedVerdict.NoToken, Check(authorization: "Bearer wrong"));
        Assert.Equal(FeedVerdict.NoToken, Check(authorization: "Basic " + Token));
        Assert.Equal(FeedVerdict.NoToken, Check(query: Token, tokenInAddress: false));
    }

    [Fact]
    public void Guard_RefusesWritesAndStrangers()
    {
        Assert.Equal(FeedVerdict.WrongMethod, Check(method: "POST", authorization: "Bearer " + Token));
        Assert.Equal(FeedVerdict.WrongMethod, Check(method: "OPTIONS"));
        Assert.Equal(FeedVerdict.NotLocal, Check(remote: IPAddress.Parse("192.168.0.50"), authorization: "Bearer " + Token));
        Assert.Equal(FeedVerdict.NotLocal, Check(remoteGiven: false, authorization: "Bearer " + Token));
        Assert.Equal(421, FeedGuard.StatusOf(FeedVerdict.WrongHost));
        Assert.Equal(401, FeedGuard.StatusOf(FeedVerdict.NoToken));
    }

    [Fact]
    public void Tokens_AreLongAndUrlSafe()
    {
        string token = FeedGuard.NewToken();

        Assert.Equal(43, token.Length);
        Assert.All(token, each => Assert.True(char.IsAsciiLetterOrDigit(each) || each is '-' or '_'));
        Assert.NotEqual(token, FeedGuard.NewToken());
    }

    [Theory]
    [InlineData("/", FeedRoute.Index)]
    [InlineData("/v1/", FeedRoute.Index)]
    [InlineData("/v1/STATUS", FeedRoute.Status)]
    [InlineData("/v1/events", FeedRoute.Events)]
    [InlineData("/overlay/", FeedRoute.Overlay)]
    [InlineData("/v1/../etc", FeedRoute.NotFound)]
    [InlineData(null, FeedRoute.Index)]
    public void Routes_AreFound(string? path, FeedRoute expected) => Assert.Equal(expected, FeedRoutes.Find(path));

    [Fact]
    public void Periods_ReadByNameMonthDayAndDates()
    {
        DateTimeOffset now = new(2026, 10, 5, 12, 0, 0, TimeSpan.Zero);
        TimeZoneInfo utc = TimeZoneInfo.Utc;

        UsagePeriod month = Period(now, utc, month: "2026-09");
        Assert.Equal(new DateTimeOffset(2026, 9, 1, 0, 0, 0, TimeSpan.Zero), month.From);
        Assert.Equal(new DateTimeOffset(2026, 10, 1, 0, 0, 0, TimeSpan.Zero), month.Until);
        Assert.Equal(UsageGranularity.Day, month.Granularity);

        UsagePeriod today = Period(now, utc);
        Assert.Equal(new DateTimeOffset(2026, 10, 5, 0, 0, 0, TimeSpan.Zero), today.From);
        Assert.Equal(UsageGranularity.Hour, today.Granularity);

        UsagePeriod range = Period(now, utc, from: "2026-09-01", until: "2026-09-07", by: "hour");
        Assert.Equal(new DateTimeOffset(2026, 9, 8, 0, 0, 0, TimeSpan.Zero), range.Until);

        UsagePeriod week = Period(now, utc, name: "last-week");
        Assert.Equal(DayOfWeek.Monday, week.From.DayOfWeek);
        Assert.Equal(TimeSpan.FromDays(7), week.Until - week.From);
    }

    [Theory]
    [InlineData(null, "2026-13", null, null, null, null)]
    [InlineData("forever", null, null, null, null, null)]
    [InlineData(null, null, null, "2025-01-01", "2026-09-01", null)]
    [InlineData(null, "2026-09", null, null, null, "minute")]
    [InlineData(null, null, null, "2026-09-02", "2026-09-01", null)]
    [InlineData("today", "2026-09", null, null, null, null)]
    [InlineData(null, null, null, "2026-09-01", null, null)]
    [InlineData(null, null, "2026-09-01", null, null, "week")]
    [InlineData(null, "9999-12", null, null, null, null)]
    [InlineData(null, "0001-01", null, null, null, null)]
    [InlineData(null, null, "0001-01-01", null, null, null)]
    [InlineData(null, null, "9999-12-31", null, null, null)]
    [InlineData(null, null, null, "0001-01-01", "2026-09-01", null)]
    [InlineData(null, null, null, "2026-09-01", "9999-12-31", null)]
    [InlineData(null, null, null, "06/01/9999 00:00 +00:00", "06/02/9999 00:00 +00:00", null)]
    [InlineData(null, null, null, "01/01/0001 00:00 +14:00", "01/02/0001 00:00 +14:00", null)]
    public void Periods_ThatCannotBeRead_SayWhy(string? name, string? month, string? from, string? fromDate, string? until, string? by)
    {
        UsagePeriod? period = UsagePeriod.Read(name, month, from, fromDate, until, by, DateTimeOffset.UtcNow, TimeZoneInfo.Utc, out string? problem);

        Assert.Null(period);
        Assert.False(string.IsNullOrWhiteSpace(problem));
    }

    /// <summary>
    /// The calendar's ends are refused with a reason, in every time zone, never
    /// thrown: "/v1/usage?month=9999-12" used to throw out of the request, which
    /// then hung, and crashed the command line (found in review).
    /// </summary>
    [Theory]
    [InlineData("9999-12", null)]
    [InlineData("1969-12", null)]
    [InlineData("9001-01", null)]
    [InlineData(null, "0001-01-01")]
    [InlineData(null, "9999-12-31")]
    public void Periods_AtTheCalendarsEnds_AreRefusedNotThrown(string? month, string? day)
    {
        foreach (TimeZoneInfo zone in Zones())
        {
            UsagePeriod? period = UsagePeriod.Read(null, month, day, null, null, null, DateTimeOffset.UtcNow, zone, out string? problem);

            Assert.Null(period);
            Assert.Contains("1970", problem, StringComparison.Ordinal);
        }
    }

    [Theory]
    [InlineData("1970-01", null)]
    [InlineData("9000-12", null)]
    [InlineData(null, "1970-01-01")]
    [InlineData(null, "9000-12-31")]
    public void Periods_AtTheEndsAllowed_AreRead(string? month, string? day)
    {
        foreach (TimeZoneInfo zone in Zones())
        {
            UsagePeriod? period = UsagePeriod.Read(null, month, day, null, null, null, DateTimeOffset.UtcNow, zone, out string? problem);
            Assert.True(period is not null, problem);
        }
    }

    /// <summary>Every path the feed answers is registered as its own prefix, so nothing longer can be slipped in front of it.</summary>
    [Fact]
    public void Prefixes_CoverEveryPathTheFeedAnswers()
    {
        foreach (string path in FeedRoutes.Documented)
        {
            Assert.Contains(path + "/", FeedRoutes.Prefixes);
        }

        Assert.Contains("/", FeedRoutes.Prefixes);
        Assert.Contains("/v1/", FeedRoutes.Prefixes);
        Assert.All(FeedRoutes.Prefixes, each => Assert.True(each.StartsWith('/') && each.EndsWith('/'), each));
        Assert.All(FeedRoutes.Prefixes, each => Assert.NotEqual(FeedRoute.NotFound, FeedRoutes.Find(each)));
    }

    /// <summary>Every member is always written; a value not known is null, never a zero.</summary>
    [Fact]
    public void Json_WritesNullForWhatIsNotKnown()
    {
        using JsonDocument document = JsonDocument.Parse(FeedJson.Write(EmptySnapshot(), FeedPart.All, "0.2.0"));
        JsonElement root = document.RootElement;

        Assert.Equal(1, root.GetProperty("feed").GetInt32());
        Assert.Equal(JsonValueKind.Null, root.GetProperty("rates").GetProperty("downBytesPerSecond").ValueKind);
        Assert.Equal("checking", root.GetProperty("connection").GetProperty("status").GetString());
        Assert.Equal(JsonValueKind.Null, root.GetProperty("usage").ValueKind);
        Assert.Equal(JsonValueKind.Null, root.GetProperty("plan").ValueKind);
        Assert.Equal(JsonValueKind.Null, root.GetProperty("system").GetProperty("processorPercent").ValueKind);
        Assert.Equal(JsonValueKind.Null, root.GetProperty("household").ValueKind);

        using JsonDocument part = JsonDocument.Parse(FeedJson.Write(Snapshot(), FeedPart.Rates, "0.2.0"));
        Assert.Equal(1234.6, part.RootElement.GetProperty("rates").GetProperty("downBytesPerSecond").GetDouble());
        Assert.False(part.RootElement.TryGetProperty("connection", out _));
    }

    /// <summary>The CSP names the page's own inline script and style by hash, and nothing else may run.</summary>
    [Fact]
    public void Overlay_PolicyHashesItsOwnScriptAndStyle()
    {
        string page = Encoding.UTF8.GetString(FeedOverlay.Page);
        string script = FeedOverlay.Inner(page, "script");
        string style = FeedOverlay.Inner(page, "style");

        Assert.Contains("EventSource", script, StringComparison.Ordinal);
        Assert.Contains("background: transparent", style, StringComparison.Ordinal);
        Assert.Contains("'sha256-" + Convert.ToBase64String(SHA256.HashData(Encoding.UTF8.GetBytes(script))) + "'", FeedOverlay.Policy, StringComparison.Ordinal);
        Assert.Contains("'sha256-" + Convert.ToBase64String(SHA256.HashData(Encoding.UTF8.GetBytes(style))) + "'", FeedOverlay.Policy, StringComparison.Ordinal);
        Assert.StartsWith("default-src 'none'", FeedOverlay.Policy, StringComparison.Ordinal);
        Assert.DoesNotContain("unsafe", FeedOverlay.Policy, StringComparison.Ordinal);
    }

    [Fact]
    public void Endpoint_KeepsItsTokenAcrossPorts()
    {
        string path = Path.Combine(_folder, FeedEndpoint.FileName);

        FeedEndpoint first = FeedEndpoint.Prepare(path, 40406);
        first.Write(path);
        FeedEndpoint moved = FeedEndpoint.Prepare(path, 40500);

        Assert.Equal(first.Token, moved.Token);
        Assert.Equal(40500, moved.Port);
        Assert.Equal(new Uri("http://127.0.0.1:40500/"), moved.Address);
        Assert.DoesNotContain(first.Token, first.ToString(), StringComparison.Ordinal);
        Assert.NotEqual(first.Token, first.WithNewToken().Token);
        Assert.Null(FeedEndpoint.Read(Path.Combine(_folder, "missing.json")));
    }

    /// <summary>The feed on a real loopback port: refusals, the status, a period, the overlay and the first live event.</summary>
    [Fact]
    public async Task Feed_AnswersOnLoopback()
    {
        int port = FreePort();
        WindsockSettings settings = new() { Feed = new FeedSettings { Enabled = true, Port = port } };
        string path = Path.Combine(_folder, FeedEndpoint.FileName);
        Directory.CreateDirectory(_folder);
        FakeSource source = new();

        // Made before the feed was ever on, as the AI tools server is when an
        // assistant starts it first: it reads feed.json again for each request.
        using FeedClient rereading = new(() => FeedEndpoint.Read(path));
        Assert.Equal(FeedReach.NeverSwitchedOn, (await rereading.GetAsync("/v1/status", TestContext.Current.CancellationToken)).Reach);

        using LocalFeed feed = new(settings, source, TimeProvider.System, NullLogger<LocalFeed>.Instance, path);
        await feed.StartAsync(TestContext.Current.CancellationToken);

        Assert.Equal(FeedState.Running, feed.Status.State);
        Assert.Equal(FeedReach.Answered, (await rereading.GetAsync("/v1/status", TestContext.Current.CancellationToken)).Reach);
        FeedEndpoint endpoint = FeedEndpoint.Read(path)!;
        Assert.True(endpoint.Running);
        Assert.Equal(Environment.ProcessId, endpoint.ProcessId);

        using FeedClient client = new(endpoint);
        FeedReply status = await client.GetAsync("/v1/status", TestContext.Current.CancellationToken);
        Assert.Equal(FeedReach.Answered, status.Reach);

        using (JsonDocument document = JsonDocument.Parse(status.Body!))
        {
            Assert.Equal("online", document.RootElement.GetProperty("connection").GetProperty("status").GetString());
        }

        FeedReply usage = await client.GetAsync("/v1/usage?month=2026-09", TestContext.Current.CancellationToken);
        Assert.Equal(FeedReach.Answered, usage.Reach);
        Assert.Contains("\"granularity\":\"day\"", usage.Body, StringComparison.Ordinal);

        FeedReply bad = await client.GetAsync("/v1/usage?month=September", TestContext.Current.CancellationToken);
        Assert.Equal(400, bad.Status);

        // The calendar's ends are a 400 with a reason, not a request left hanging.
        foreach (string edge in new[] { "/v1/usage?month=9999-12", "/v1/usage?day=0001-01-01", "/v1/usage?from=2026-01-01&until=9999-12-31" })
        {
            FeedReply refused = await client.GetAsync(edge, TestContext.Current.CancellationToken);
            Assert.Equal(400, refused.Status);
            Assert.Contains("1970", refused.Body, StringComparison.Ordinal);
        }

        // Anything else that goes wrong answering is a 500, and the feed carries on.
        source.Failure = new ArgumentOutOfRangeException(nameof(source));
        FeedReply failed = await client.GetAsync("/v1/usage?month=2026-08", TestContext.Current.CancellationToken);
        Assert.Equal(500, failed.Status);
        source.Failure = null;
        Assert.Equal(FeedReach.Answered, (await client.GetAsync("/v1/usage?month=2026-08", TestContext.Current.CancellationToken)).Reach);

        using FeedClient stranger = new(endpoint with { Token = "not-the-token-not-the-token" });
        Assert.Equal(FeedReach.Refused, (await stranger.GetAsync("/v1/status", TestContext.Current.CancellationToken)).Reach);

        Assert.StartsWith("HTTP/1.1 421", await RawAsync(port, "GET /v1/status?token=" + endpoint.Token + " HTTP/1.1\r\nHost: evil.example:" + port + "\r\n\r\n"), StringComparison.Ordinal);
        Assert.StartsWith("HTTP/1.1 405", await RawAsync(port, "POST /v1/status HTTP/1.1\r\nHost: 127.0.0.1:" + port + "\r\nAuthorization: Bearer " + endpoint.Token + "\r\nContent-Length: 0\r\n\r\n"), StringComparison.Ordinal);

        string overlay = await RawAsync(port, "GET /overlay?token=" + endpoint.Token + " HTTP/1.1\r\nHost: localhost:" + port + "\r\n\r\n");
        Assert.StartsWith("HTTP/1.1 200", overlay, StringComparison.Ordinal);
        Assert.Contains("Content-Security-Policy: default-src 'none'", overlay, StringComparison.Ordinal);
        Assert.DoesNotContain("Access-Control-Allow-Origin", overlay, StringComparison.OrdinalIgnoreCase);

        StrongBox<FeedReply> ended = new();
        using CancellationTokenSource follow = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        follow.CancelAfter(TimeSpan.FromSeconds(10));
        string? first = null;

        await foreach (string each in client.FollowAsync(ended, follow.Token))
        {
            first = each;
            break;
        }

        Assert.NotNull(first);
        Assert.Contains("\"rates\"", first, StringComparison.Ordinal);

        for (int wait = 0; wait < 30 && source.Watched == 0; wait++)
        {
            await Task.Delay(100, TestContext.Current.CancellationToken);
        }

        Assert.True(source.Watched > 0);

        // A reset token: a client holding the old one is refused, one that
        // reads feed.json each time carries on.
        feed.ResetToken();
        Assert.Equal(FeedReach.Refused, (await client.GetAsync("/v1/status", TestContext.Current.CancellationToken)).Reach);
        Assert.Equal(FeedReach.Answered, (await rereading.GetAsync("/v1/status", TestContext.Current.CancellationToken)).Reach);

        await feed.StopAsync(TestContext.Current.CancellationToken);
        Assert.False(FeedEndpoint.Read(path)!.Running);
        Assert.Equal(FeedReach.NotRunning, (await client.GetAsync("/v1/status", TestContext.Current.CancellationToken)).Reach);
    }

    /// <summary>
    /// HTTP.sys gives a request to the longest prefix registered for it, so
    /// another program holding any of the feed's paths must stop the feed from
    /// starting, never share the port with it; and once it runs, none of its
    /// paths can be taken (found in review).
    /// </summary>
    [Fact]
    public async Task Feed_FailsClosed_WhenAnyOfItsPathsIsTaken()
    {
        int port = FreePort();
        WindsockSettings settings = new() { Feed = new FeedSettings { Enabled = true, Port = port } };
        string path = Path.Combine(_folder, FeedEndpoint.FileName);
        Directory.CreateDirectory(_folder);

        using LocalFeed feed = new(settings, new FakeSource(), TimeProvider.System, NullLogger<LocalFeed>.Instance, path);

        using (HttpListener squatter = new())
        {
            squatter.Prefixes.Add(string.Create(CultureInfo.InvariantCulture, $"http://127.0.0.1:{port}/v1/status/"));
            squatter.Start();

            await feed.StartAsync(TestContext.Current.CancellationToken);

            Assert.Equal(FeedState.Failed, feed.Status.State);
            Assert.Contains("part of the feed's address", feed.Status.Problem, StringComparison.Ordinal);
            Assert.False(FeedEndpoint.Read(path)!.Running);
        }

        feed.Apply();
        Assert.Equal(FeedState.Running, feed.Status.State);

        foreach (string taken in new[] { "/v1/status/", "/v1/", "/v1/usage/", "/overlay/", "/" })
        {
            using HttpListener late = new();
            late.Prefixes.Add(string.Create(CultureInfo.InvariantCulture, $"http://127.0.0.1:{port}{taken}"));
            HttpListenerException refused = Assert.Throws<HttpListenerException>(late.Start);
            Assert.True(refused.ErrorCode is 32 or 183, refused.Message);
        }

        await feed.StopAsync(TestContext.Current.CancellationToken);
    }

    /// <summary>History read read-only beside the app's own store, which stays open and writing.</summary>
    [Fact]
    public void History_IsReadReadOnlyBesideTheStore()
    {
        Directory.CreateDirectory(_folder);
        string path = Path.Combine(_folder, "Windsock.db");
        DateTimeOffset start = new(2026, 9, 14, 10, 0, 0, TimeSpan.Zero);
        long minute = EpochMinute.From(start);

        using SqliteUsageHistoryStore store = new(path);
        store.Write([new UsageMinute(minute, 1000, 100, 50, 5), new UsageMinute(minute + 1, 2000, 200, 60, 6), new UsageMinute(minute + 90, 500, 50, 10, 1)]);

        using ReadOnlyHistory? history = ReadOnlyHistory.Open(path, out string? problem);
        Assert.Null(problem);
        Assert.NotNull(history);

        UsagePeriod hours = new(start, start.AddHours(3), UsageGranularity.Hour);
        IReadOnlyList<UsageBucket> buckets = ReadOnlyHistory.Bucket(history.ReadMinutes(hours.From, hours.Until), UsageGranularity.Hour, TimeZoneInfo.Utc);

        Assert.Equal(2, buckets.Count);
        Assert.Equal(3000, buckets[0].BytesDown);
        Assert.Equal(60, buckets[0].PeakDown);
        Assert.Equal(2, buckets[0].Minutes);

        store.Write([new UsageMinute(minute + 2, 7, 7, 7, 7)]);
        Assert.Equal(4, history.ReadMinutes(hours.From, hours.Until).Count);

        StringWriter csv = new();
        history.WriteCsv(csv, new UsagePeriod(start, start.AddHours(1), UsageGranularity.Minute));
        Assert.StartsWith(UsageCsv.Header, csv.ToString(), StringComparison.Ordinal);

        using MemoryStream json = new();
        history.WriteJson(json, hours, start);
        using JsonDocument document = JsonDocument.Parse(json.ToArray());
        Assert.Equal("windsock-usage", document.RootElement.GetProperty("format").GetString());

        Assert.Null(ReadOnlyHistory.Open(Path.Combine(_folder, "none.db"), out string? missing));
        Assert.NotNull(missing);
        Assert.False(File.Exists(Path.Combine(_folder, "none.db")));
    }

    private static FeedVerdict Check(
        string method = "GET",
        string? host = "127.0.0.1:40406",
        string? origin = null,
        string? fetchSite = null,
        string? fetchMode = null,
        string? authorization = null,
        string? query = null,
        IPAddress? remote = null,
        bool tokenInAddress = true,
        bool remoteGiven = true) =>
        FeedGuard.Check(remoteGiven ? remote ?? Loopback : null, method, host, origin, fetchSite, fetchMode, authorization, query, 40406, Token, tokenInAddress);

    private static UsagePeriod Period(DateTimeOffset now, TimeZoneInfo zone, string? name = null, string? month = null, string? from = null, string? until = null, string? by = null)
    {
        UsagePeriod? period = UsagePeriod.Read(name, month, null, from, until, by, now, zone, out string? problem);
        Assert.True(period is not null, problem);
        return period;
    }

    private static FeedSnapshot EmptySnapshot() => new(
        DateTimeOffset.Now,
        new FeedRates(null, null, null),
        new FeedConnection("checking", null, null, null, null, null, null),
        null,
        null,
        new FeedSystem(null, null, null, null, null, null, null),
        null);

    private static FeedSnapshot Snapshot() => new(
        DateTimeOffset.Now,
        new FeedRates(DateTimeOffset.Now, 1234.56, 78.9),
        new FeedConnection("online", DateTimeOffset.Now, null, "Home", 1.2, null, false),
        new FeedUsage(new FeedTotals(10, 20), new FeedTotals(100, 200), DateTimeOffset.Now),
        null,
        new FeedSystem(DateTimeOffset.Now, 11.5, 49.2, 19, 4, null, null),
        null);

    private static TimeZoneInfo[] Zones() =>
    [
        TimeZoneInfo.Utc,
        TimeZoneInfo.CreateCustomTimeZone("Ahead", TimeSpan.FromHours(14), "Ahead", "Ahead"),
        TimeZoneInfo.CreateCustomTimeZone("Behind", TimeSpan.FromHours(-12), "Behind", "Behind"),
    ];

    private static int FreePort()
    {
        TcpListener probe = new(IPAddress.Loopback, 0);
        probe.Start();
        int port = ((IPEndPoint)probe.LocalEndpoint).Port;
        probe.Stop();
        return port;
    }

    private static async Task<string> RawAsync(int port, string request)
    {
        using TcpClient client = new();
        await client.ConnectAsync(IPAddress.Loopback, port, TestContext.Current.CancellationToken);
        NetworkStream stream = client.GetStream();
        await stream.WriteAsync(Encoding.ASCII.GetBytes(request), TestContext.Current.CancellationToken);

        byte[] buffer = new byte[64 * 1024];
        int read = await stream.ReadAsync(buffer, TestContext.Current.CancellationToken);
        return Encoding.UTF8.GetString(buffer, 0, read);
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_folder))
            {
                Directory.Delete(_folder, recursive: true);
            }
        }
        catch (IOException)
        {
        }
    }

    private sealed class FakeSource : IFeedSource
    {
        private int _watched;

        public int Watched => Volatile.Read(ref _watched);

        /// <summary>Thrown from the next usage reads, when set.</summary>
        public Exception? Failure { get; set; }

        public FeedSnapshot Read() => Snapshot();

        public FeedUsageRange? ReadUsage(DateTimeOffset from, DateTimeOffset until, UsageGranularity granularity) =>
            Failure is { } failure
                ? throw failure
                : new(from, until, granularity, new FeedTotals(1, 2), [new FeedBucket(from, 1, 2, 60)]);

        public IDisposable Watch()
        {
            Interlocked.Increment(ref _watched);
            return new Released();
        }

        private sealed class Released : IDisposable
        {
            public void Dispose()
            {
            }
        }
    }
}
