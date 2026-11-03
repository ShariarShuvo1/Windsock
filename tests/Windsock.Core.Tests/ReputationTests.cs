using System.Net;
using Windsock.Core.Security;
using Xunit;

namespace Windsock.Core.Tests;

public sealed class ReputationTests : IDisposable
{
    private const string Sha = "275a021bbfb6489e54d471899f7db9d1663fc695ec2fe2a2c4538aabf651fd0f";

    // Shaped like a VirusTotal key: 64 hex characters. Not a real one.
    private const string Key = "0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef";

    // The shape of a GET /api/v3/files/{id} answer in VirusTotal's API v3 reference, trimmed.
    private const string Answer =
        """
        {
          "data": {
            "type": "file",
            "id": "275a021bbfb6489e54d471899f7db9d1663fc695ec2fe2a2c4538aabf651fd0f",
            "attributes": {
              "last_analysis_date": 1790000000,
              "last_analysis_stats": { "harmless": 0, "type-unsupported": 3, "suspicious": 1, "confirmed-timeout": 0, "timeout": 0, "failure": 0, "malicious": 60, "undetected": 7 },
              "meaningful_name": "eicar.com",
              "reputation": 3000,
              "popular_threat_classification": { "suggested_threat_label": "virus.eicar/test" }
            }
          }
        }
        """;

    private readonly string _folder = Path.Combine(Path.GetTempPath(), "windsock-reputation-" + Guid.NewGuid().ToString("N"));

    [Fact]
    public void AnAnswerGivesTheEnginesVerdicts()
    {
        ReputationReport? report = VirusTotalAnswers.Read(Sha, Answer, DateTimeOffset.UnixEpoch);

        Assert.NotNull(report);
        Assert.Equal(ReputationOutcome.Known, report.Outcome);
        Assert.Equal(60, report.Malicious);
        Assert.Equal(1, report.Suspicious);
        Assert.Equal(68, report.Engines);
        Assert.Equal(DateTimeOffset.FromUnixTimeSeconds(1790000000), report.AnalysedAt);
        Assert.Equal("virus.eicar/test", report.ThreatLabel);
        Assert.StartsWith("61 of 68 engines flag it (virus.eicar/test)", report.Text, StringComparison.Ordinal);
        Assert.Equal("https://www.virustotal.com/gui/file/" + Sha, report.Page.ToString());

        Assert.Null(VirusTotalAnswers.Read(Sha, "{\"error\":{\"code\":\"NotFoundError\"}}", DateTimeOffset.UnixEpoch));
        Assert.Null(VirusTotalAnswers.Read(Sha, "not json", DateTimeOffset.UnixEpoch));
    }

    [Theory]
    [InlineData("[1,2,3]")]
    [InlineData("\"just a string\"")]
    [InlineData("{\"data\":5}")]
    [InlineData("{\"data\":{\"attributes\":[]}}")]
    [InlineData("{\"data\":{\"attributes\":{\"last_analysis_date\":\"yesterday\"}}}")]
    [InlineData("{\"data\":{\"attributes\":{\"last_analysis_date\":null}}}")]
    [InlineData("{\"data\":{\"attributes\":{\"last_analysis_date\":1e300}}}")]
    [InlineData("{\"data\":{\"attributes\":{\"last_analysis_date\":99999999999999}}}")]
    [InlineData("{\"data\":{\"attributes\":{\"last_analysis_stats\":{\"malicious\":\"60\",\"harmless\":true}}}}")]
    [InlineData("{\"data\":{\"attributes\":{\"popular_threat_classification\":{\"suggested_threat_label\":7}}}}")]
    public void AnAnswerOfTheWrongShapeIsReadWithoutThrowing(string json)
    {
        // A value of the wrong kind throws InvalidOperationException, not
        // JsonException; neither may reach the window that asked.
        ReputationReport? report = VirusTotalAnswers.Read(Sha, json, DateTimeOffset.UnixEpoch);

        if (report is not null)
        {
            Assert.Equal(ReputationOutcome.Known, report.Outcome);
            Assert.Null(report.AnalysedAt);
            Assert.Equal(0, report.Malicious);
        }
    }

    [Theory]
    [InlineData("key")]
    [InlineData("0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcde")]
    [InlineData("0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdeg")]
    [InlineData("0123456789abcdef0123456789abcdef\n0123456789abcdef0123456789abcde")]
    [InlineData("0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef0")]
    public void OnlyAKeysShapeIsKept(string key)
    {
        Directory.CreateDirectory(_folder);
        ApiKeyVault vault = new(Path.Combine(_folder, "virustotal.key"));

        Assert.False(ApiKeyVault.IsWellFormed(key));
        Assert.False(vault.Store(key));
        Assert.False(vault.HasKey);
        Assert.True(ApiKeyVault.IsWellFormed(Key.ToUpperInvariant()));
    }

    [Fact]
    public void TheDaysCountSurvivesARestart()
    {
        Directory.CreateDirectory(_folder);
        string path = Path.Combine(_folder, "virustotal-usage.txt");
        InsightClock clock = new(new DateTimeOffset(2026, 10, 5, 9, 0, 0, TimeSpan.Zero));

        RequestAllowance first = new(clock, path);
        Assert.True(first.TryTake(out _));
        Assert.True(first.TryTake(out _));

        RequestAllowance second = new(clock, path);
        Assert.Equal(2, second.UsedToday);

        // A new day starts from nothing; forgetting deletes the count.
        clock.Advance(TimeSpan.FromDays(1));
        Assert.Equal(0, new RequestAllowance(clock, path).UsedToday);

        second.Forget();
        Assert.False(File.Exists(path));
    }

    [Fact]
    public async Task AnAnswerTooLargeIsRefusedNotKept()
    {
        VirusTotal site = new(HttpStatusCode.OK, new string(' ', 3 * 1024 * 1024) + Answer);
        using ReputationService service = Service(site, out _);
        Assert.True(service.SetKey(Key));

        ReputationReport report = await service.LookUpAsync(Sha, TestContext.Current.CancellationToken);

        Assert.Equal(ReputationOutcome.Failed, report.Outcome);
        Assert.Null(service.Cached(Sha));
    }

    [Fact]
    public void TheFreeAllowanceIsFourAMinuteAndFiveHundredADay()
    {
        InsightClock clock = new(new DateTimeOffset(2026, 10, 5, 23, 0, 0, TimeSpan.Zero));
        RequestAllowance allowance = new(clock);

        for (int each = 0; each < RequestAllowance.PerMinute; each++)
        {
            Assert.True(allowance.TryTake(out _));
        }

        Assert.False(allowance.TryTake(out TimeSpan wait));
        Assert.Equal(TimeSpan.FromMinutes(1), wait);

        clock.Advance(TimeSpan.FromSeconds(61));
        Assert.True(allowance.TryTake(out _));

        // A refusal from VirusTotal pauses a minute, a second one an hour.
        allowance.Refused();
        Assert.False(allowance.TryTake(out wait));
        Assert.Equal(TimeSpan.FromMinutes(1), wait);
        allowance.Refused();
        Assert.False(allowance.TryTake(out wait));
        Assert.Equal(TimeSpan.FromHours(1), wait);

        clock.Advance(TimeSpan.FromHours(2));
        allowance.Accepted();
        Assert.Equal(0, allowance.UsedToday);

        for (int each = 0; each < RequestAllowance.PerDay; each++)
        {
            clock.Advance(TimeSpan.FromSeconds(16));
            Assert.True(allowance.TryTake(out _), "request " + each);
        }

        clock.Advance(TimeSpan.FromSeconds(16));
        Assert.False(allowance.TryTake(out _));
    }

    [Fact]
    public async Task WithoutAKeyNothingIsSent()
    {
        VirusTotal site = new(HttpStatusCode.OK, Answer);
        using ReputationService service = Service(site, out _);

        ReputationReport report = await service.LookUpAsync(Sha, TestContext.Current.CancellationToken);

        Assert.Equal(ReputationOutcome.NoKey, report.Outcome);
        Assert.Empty(site.Asked);
    }

    [Fact]
    public async Task OnlyTheHashIsSentAndTheAnswerIsKept()
    {
        VirusTotal site = new(HttpStatusCode.OK, Answer);
        using ReputationService service = Service(site, out ApiKeyVault vault);
        Assert.True(service.SetKey("  " + Key + "  "));
        Assert.Equal(Key, vault.Read());

        ReputationReport report = await service.LookUpAsync(Sha.ToUpperInvariant(), TestContext.Current.CancellationToken);

        Assert.Equal(ReputationOutcome.Known, report.Outcome);
        Uri asked = Assert.Single(site.Asked);
        Assert.Equal("https://www.virustotal.com/api/v3/files/" + Sha, asked.ToString());
        Assert.Equal(Key, Assert.Single(site.Keys));

        // Asked again, it answers from what it kept.
        Assert.Equal(ReputationOutcome.Known, (await service.LookUpAsync(Sha, TestContext.Current.CancellationToken)).Outcome);
        Assert.Single(site.Asked);
        Assert.NotNull(service.Cached(Sha));

        // And forgets it all with the key.
        service.ClearKey();
        Assert.False(service.HasKey);
        Assert.Null(service.Cached(Sha));
    }

    [Theory]
    [InlineData(HttpStatusCode.NotFound, ReputationOutcome.Unknown)]
    [InlineData(HttpStatusCode.Unauthorized, ReputationOutcome.BadKey)]
    [InlineData(HttpStatusCode.TooManyRequests, ReputationOutcome.OverQuota)]
    [InlineData(HttpStatusCode.InternalServerError, ReputationOutcome.Failed)]
    public async Task EachRefusalIsSaidPlainly(HttpStatusCode status, ReputationOutcome outcome)
    {
        VirusTotal site = new(status, "{\"error\":{\"code\":\"X\",\"message\":\"x\"}}");
        using ReputationService service = Service(site, out _);
        service.SetKey(Key);

        ReputationReport report = await service.LookUpAsync(Sha, TestContext.Current.CancellationToken);

        Assert.Equal(outcome, report.Outcome);
        Assert.False(string.IsNullOrWhiteSpace(report.Text));
    }

    [Fact]
    public async Task AFileIsHashedWithoutLockingIt()
    {
        Directory.CreateDirectory(_folder);
        string path = Path.Combine(_folder, "eicar.txt");
        await File.WriteAllTextAsync(path, "hello", TestContext.Current.CancellationToken);

        await using FileStream held = new(path, FileMode.Open, FileAccess.ReadWrite, FileShare.ReadWrite);
        string? sha = await ReputationService.HashAsync(path, TestContext.Current.CancellationToken);

        Assert.Equal("2cf24dba5fb0a30e26e83b2ac5b9e29e1b161e5c1fa7425e73043362938b9824", sha);
        Assert.Null(await ReputationService.HashAsync(Path.Combine(_folder, "missing"), TestContext.Current.CancellationToken));
    }

    [Fact]
    public void AKeyProtectedHereReadsBackOnlyHere()
    {
        Directory.CreateDirectory(_folder);
        string path = Path.Combine(_folder, "key");
        ApiKeyVault vault = new(path);

        Assert.False(vault.HasKey);
        Assert.True(vault.Store(Key));
        Assert.True(vault.HasKey);
        Assert.DoesNotContain(Key, File.ReadAllText(path), StringComparison.Ordinal);
        Assert.Equal(Key, new ApiKeyVault(path).Read());

        // Bytes that are not a DPAPI blob for this account give nothing.
        File.WriteAllBytes(path, [1, 2, 3, 4]);
        Assert.Null(vault.Read());

        vault.Clear();
        Assert.False(vault.HasKey);
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

    private ReputationService Service(VirusTotal site, out ApiKeyVault vault)
    {
        vault = new ApiKeyVault(Path.Combine(_folder, "virustotal.key"));
        return new ReputationService(vault, new HttpClient(site, disposeHandler: false), new InsightClock(DateTimeOffset.UtcNow), Path.Combine(_folder, "reputation.json"));
    }

    private sealed class VirusTotal(HttpStatusCode status, string body) : HttpMessageHandler
    {
        public List<Uri> Asked { get; } = [];

        public List<string> Keys { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Asked.Add(request.RequestUri!);
            Keys.AddRange(request.Headers.GetValues("x-apikey"));
            return Task.FromResult(new HttpResponseMessage(status) { Content = new StringContent(body) });
        }
    }
}
