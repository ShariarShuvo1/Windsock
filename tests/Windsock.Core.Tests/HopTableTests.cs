using Windsock.Core.Connectivity;
using Windsock.Core.Networking;
using Windsock.Core.Settings;
using Windsock.Core.SpeedTests;
using Xunit;

namespace Windsock.Core.Tests;

/// <summary>The hop-by-hop table's sums and its reading of loss, and the ISP report.</summary>
public sealed class HopTableTests
{
    private static readonly DateTimeOffset Noon = new(2026, 10, 5, 12, 0, 0, TimeSpan.FromHours(6));

    private static readonly string[] OtherPath = ["10.0.0.2"];

    [Fact]
    public void Tally_KeepsWinMtrsColumns()
    {
        HopTally tally = new();

        tally.Add(Noon, [Onward(1, "192.168.0.1", 1), Onward(2, "10.0.0.1", 8), Arrived(3, "1.1.1.1", 12)]);
        tally.Add(Noon.AddSeconds(1), [Onward(1, "192.168.0.1", 3), Silent(2), Arrived(3, "1.1.1.1", 14)]);

        IReadOnlyList<HopRow> rows = tally.Rows();

        Assert.Equal(3, rows.Count);
        Assert.Equal(2, tally.Rounds);
        Assert.Equal(3, tally.DestinationAt);

        HopRow first = rows[0];
        Assert.Equal("192.168.0.1", first.Address);
        Assert.Equal(2, first.Sent);
        Assert.Equal(2, first.Received);
        Assert.Equal(1, first.BestMs);
        Assert.Equal(3, first.WorstMs);
        Assert.Equal(2, first.AverageMs);
        Assert.Equal(3, first.LastMs);
        Assert.Equal(1, first.DeviationMs!.Value, 9);

        // Hop 2 lost one in two, but the far end lost nothing: the router was
        // only slow to answer probes addressed to itself.
        Assert.Equal(0.5, rows[1].Loss);
        Assert.False(rows[1].LossCarriesOn);
        Assert.True(rows[2].IsDestination);
        Assert.Null(new HopTableSnapshot("1.1.1.1", Noon, 2, rows).TroubleStartsAt);
    }

    [Fact]
    public void Tally_LossThatCarriesOn_IsWhereTroubleStarts()
    {
        HopTally tally = new();

        for (int round = 0; round < 10; round++)
        {
            bool lost = round % 2 == 0;
            tally.Add(Noon.AddSeconds(round),
            [
                Onward(1, "192.168.0.1", 1),
                lost ? Silent(2) : Onward(2, "10.0.0.1", 9),
                lost ? Silent(3) : Arrived(3, "1.1.1.1", 15),
            ]);
        }

        HopTableSnapshot snapshot = new("1.1.1.1", Noon, tally.Rounds, tally.Rows());

        Assert.True(snapshot.Rows[1].LossCarriesOn);
        Assert.Equal(2, snapshot.TroubleStartsAt!.Distance);
        Assert.Single(snapshot.Rows[1].Minutes);
        Assert.Equal(0.5, snapshot.Rows[1].Minutes[0].Loss);
    }

    [Fact]
    public void Tally_LoadBalancedHops_ListEveryAddress()
    {
        HopTally tally = new();

        tally.Add(Noon, [Onward(1, "192.168.0.1", 1), Onward(2, "10.0.0.1", 8)]);
        tally.Add(Noon, [Onward(1, "192.168.0.1", 1), Onward(2, "10.0.0.2", 8)]);
        tally.Add(Noon, [Onward(1, "192.168.0.1", 1), Onward(2, "10.0.0.1", 8)]);

        HopRow second = tally.Rows()[1];

        Assert.Equal("10.0.0.1", second.Address);
        Assert.Equal(OtherPath, second.OtherAddresses);
    }

    [Fact]
    public void Tally_BeforeTheFarEndAnswers_ProbesALittleFurther()
    {
        HopTally tally = new();
        tally.Add(Noon, [Onward(1, "192.168.0.1", 1), Onward(2, "10.0.0.1", 8), Silent(3), Silent(4)]);

        Assert.Equal(2 + PathTrace.GiveUpAfter, tally.Furthest(PathTrace.MaximumHops));
        Assert.Equal(2, tally.Rows().Count);
    }

    [Fact]
    public void Report_IsOneSelfContainedPage()
    {
        WindsockSettings settings = new();
        settings.ConnectionTools.PlanDownloadMbps = 100;
        settings.ConnectionTools.PlanUploadMbps = 20;

        DateTimeOffset from = Noon.AddDays(-7);
        Outage[] outages =
        [
            new(Noon.AddDays(-2), Noon.AddDays(-2).AddMinutes(12), OutageCause.Provider, "Home"),
            new(Noon.AddDays(-1), Noon.AddDays(-1).AddMinutes(3), OutageCause.Router, "Home") { Interrupted = true },
            new(from.AddMinutes(-30), from.AddMinutes(10), OutageCause.Provider, "Home"),
        ];

        SpeedTestResult[] tests =
        [
            SpeedTestTests.Result(Noon.AddDays(-1), 42e6, 9e6) with { IdleMs = 20, LoadedDownMs = 260, Problem = "<script>alert(1)</script>" },
        ];

        IspReportData data = new(
            from,
            Noon,
            Noon,
            "Home <Wi-Fi>",
            "Summit Communications Ltd (AS58717)",
            null,
            outages,
            [],
            QualitySummary.Empty,
            new QualitySummary(1000, 990, 2.5, 30, 1.2),
            tests,
            100,
            20,
            PlanSpeed.Compare(100, 20, tests, []),
            []);

        Assert.Equal(TimeSpan.FromMinutes(25), data.Downtime);
        Assert.Equal(TimeSpan.FromMinutes(22), data.ProviderDowntime);

        string html = IspReport.Render(data);

        Assert.StartsWith("<!doctype html>", html, StringComparison.Ordinal);
        Assert.DoesNotContain("<script", html, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("src=", html, StringComparison.Ordinal);
        Assert.DoesNotContain("http://", html, StringComparison.Ordinal);
        Assert.Contains("Home &lt;Wi-Fi&gt;", html, StringComparison.Ordinal);
        Assert.Contains("the router only", html, StringComparison.Ordinal);
        Assert.Contains("42.0 Mbit/s", html, StringComparison.Ordinal);
        Assert.Contains("(end not seen)", html, StringComparison.Ordinal);
        Assert.Contains("Against the plan", html, StringComparison.Ordinal);
    }

    private static HopReply Onward(int distance, string address, double ms) =>
        new(distance, HopOutcome.Onward, address, TimeSpan.FromMilliseconds(ms));

    private static HopReply Arrived(int distance, string address, double ms) =>
        new(distance, HopOutcome.Arrived, address, TimeSpan.FromMilliseconds(ms));

    private static HopReply Silent(int distance) => new(distance, HopOutcome.Silent, null, null);
}
