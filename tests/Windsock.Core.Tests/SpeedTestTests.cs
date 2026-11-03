using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;
using Windsock.Core.Adapters;
using Windsock.Core.Connectivity;
using Windsock.Core.History;
using Windsock.Core.Networking;
using Windsock.Core.Settings;
using Windsock.Core.SpeedTests;
using Xunit;

namespace Windsock.Core.Tests;

/// <summary>NDT7's messages, the schedule's limits, the grades, the plan comparison and the connection log's new tables.</summary>
public sealed class SpeedTestTests
{
    private static readonly DateTimeOffset Noon = new(2026, 10, 5, 12, 0, 0, TimeSpan.FromHours(6));

    [Fact]
    public void Locate_ReadsServersAndTheirSecureAddresses()
    {
        const string Json = """
            {"results":[
              {"machine":"mlab1-dac01.mlab-oti.measurement-lab.org","location":{"city":"Dhaka","country":"BD"},
               "urls":{"ws:///ndt/v7/download":"ws://a/ndt/v7/download?access_token=x","ws:///ndt/v7/upload":"ws://a/ndt/v7/upload?access_token=x",
                       "wss:///ndt/v7/download":"wss://a/ndt/v7/download?access_token=x","wss:///ndt/v7/upload":"wss://a/ndt/v7/upload?access_token=x"}},
              {"machine":"plain-only","urls":{"ws:///ndt/v7/download":"ws://b/d","ws:///ndt/v7/upload":"ws://b/u"}}
            ]}
            """;

        Ndt7Server server = Assert.Single(Ndt7Protocol.ReadLocate(Json));

        Assert.Equal("wss", server.Download.Scheme);
        Assert.Equal("mlab1-dac01.mlab-oti.measurement-lab.org (Dhaka, BD)", server.Describe());
        Assert.Empty(Ndt7Protocol.ReadLocate("not json"));
    }

    [Fact]
    public void Measurement_ReadsAppInfoAndTcpInfo()
    {
        const string Json = """
            {"AppInfo":{"ElapsedTime":2500000,"NumBytes":31250000},
             "ConnectionInfo":{"Client":"203.0.113.9:5678","Server":"[::1]:443","UUID":"u"},
             "Origin":"server","Test":"upload",
             "TCPInfo":{"BusyTime":1,"MinRTT":12345,"RTT":48000,"RTTVar":2000}}
            """;

        Ndt7Measurement measured = Ndt7Protocol.ReadMeasurement(Json)!.Value;

        Assert.Equal(2_500_000, measured.ElapsedMicroseconds);
        Assert.Equal(31_250_000, measured.Bytes);
        Assert.Equal(100e6, measured.BitsPerSecond!.Value, 3);
        Assert.Equal(48000, measured.RttMicroseconds);
        Assert.Equal(12345, measured.MinRttMicroseconds);
        Assert.Equal("upload", measured.Test);
        Assert.Null(Ndt7Protocol.ReadMeasurement("{"));
        Assert.Null(Ndt7Protocol.ReadMeasurement("{}")!.Value.BitsPerSecond);
    }

    [Theory]
    [InlineData(3d, BloatGrade.APlus)]
    [InlineData(29d, BloatGrade.A)]
    [InlineData(59d, BloatGrade.B)]
    [InlineData(150d, BloatGrade.C)]
    [InlineData(399d, BloatGrade.D)]
    [InlineData(400d, BloatGrade.F)]
    [InlineData(null, BloatGrade.Unknown)]
    public void Bloat_FollowsTheWaveformStyleTable(double? added, BloatGrade grade)
    {
        Assert.Equal(grade, SpeedGrades.Bloat(added));
    }

    [Fact]
    public void LoadedIncrease_IsTheBusierDirectionOverTheQuiet()
    {
        SpeedTestResult result = Result(Noon, 95e6, 20e6) with { IdleMs = 18, LoadedDownMs = 60, LoadedUpMs = 140 };

        Assert.Equal(122, result.LoadedIncreaseMs);
        Assert.Equal(BloatGrade.C, result.Bloat);
        Assert.Equal(95, result.DownloadMbps);
        Assert.Null((result with { IdleMs = null }).LoadedIncreaseMs);
        Assert.Equal(0, (result with { LoadedDownMs = 10, LoadedUpMs = null }).LoadedIncreaseMs);
    }

    [Fact]
    public void Schedule_WaitsAverageTheDayOverTheTestsAndStayInBounds()
    {
        Assert.Equal(TimeSpan.FromHours(0.6), SpeedSchedule.NextWait(4, 0));
        Assert.Equal(TimeSpan.FromHours(15), SpeedSchedule.NextWait(4, 0.999999));
        Assert.Equal(-Math.Log(0.5) * 6, SpeedSchedule.NextWait(4, 0.5).TotalHours, 6);
        Assert.Equal(-Math.Log(0.5) * 24, SpeedSchedule.NextWait(1, 0.5).TotalHours, 6);

        // Asked for more than four, it still plans four.
        Assert.Equal(SpeedSchedule.NextWait(4, 0.3), SpeedSchedule.NextWait(10, 0.3));

        Random random = new(1);
        double mean = Enumerable.Range(0, 20_000).Average(_ => SpeedSchedule.NextWait(4, random.NextDouble()).TotalHours);
        Assert.InRange(mean, 5.0, 6.5);
    }

    [Fact]
    public void Schedule_CapsAutomaticTestsInAnyDay()
    {
        DateTimeOffset[] earlier = [Noon.AddHours(-23), Noon.AddHours(-12), Noon.AddHours(-6), Noon.AddHours(-1)];

        Assert.False(SpeedSchedule.MayRun(earlier, 4, Noon));
        Assert.True(SpeedSchedule.MayRun(earlier, 4, Noon.AddHours(1.5)));
        Assert.True(SpeedSchedule.MayRun(earlier[1..], 4, Noon));
        Assert.False(SpeedSchedule.MayRun(earlier[3..], 1, Noon));
    }

    [Fact]
    public void TextFrames_JoinAMessage_AndDropOneThatNeverEnds()
    {
        TextFrames frames = new();
        byte[] half = "{\"AppInfo\":"u8.ToArray();
        byte[] rest = "{\"NumBytes\":10}}"u8.ToArray();

        Assert.Null(frames.Add(half, endOfMessage: false));
        Assert.Equal("{\"AppInfo\":{\"NumBytes\":10}}", frames.Add(rest, endOfMessage: true));

        byte[] chunk = new byte[16 * 1024];

        for (int each = 0; each < 10; each++)
        {
            Assert.Null(frames.Add(chunk, endOfMessage: false));
        }

        // The overlong message is dropped whole, and the next one reads as usual.
        Assert.Null(frames.Add(chunk, endOfMessage: true));
        Assert.Equal("{}", frames.Add("{}"u8, endOfMessage: true));
    }

    [Fact]
    public void Schedule_CountsRunsStampedAfterNow_SoSettingTheClockBackFreesNothing()
    {
        DateTimeOffset[] later = [Noon.AddHours(2), Noon.AddHours(5), Noon.AddDays(3), Noon.AddDays(30)];

        Assert.False(SpeedSchedule.MayRun(later, 4, Noon));
        Assert.True(SpeedSchedule.MayRun(later[1..], 4, Noon));
    }

    [Fact]
    public void Schedule_TreatsAnUnknownOrMeteredLinkAsMetered_AndLooksUnderAVpn()
    {
        AdapterCost free = new(NetworkCost.Unrestricted, false, false, false, false);
        AdapterCost metered = new(NetworkCost.Fixed, false, false, false, false);
        AdapterDetails wifi = AdapterDetailsTests.Adapter("Wi-Fi", AdapterKind.WiFi, 1, "192.168.43.1", "192.168.43.20");
        AdapterDetails vpn = AdapterDetailsTests.Adapter("WireGuard", AdapterKind.Vpn, 0, "10.8.0.1", "10.8.0.2");

        Assert.False(SpeedSchedule.MayBeMetered(new AdapterSnapshot(Noon, [wifi with { Cost = free }])));
        Assert.True(SpeedSchedule.MayBeMetered(new AdapterSnapshot(Noon, [wifi with { Cost = metered }])));
        Assert.True(SpeedSchedule.MayBeMetered(new AdapterSnapshot(Noon, [wifi])));
        Assert.True(SpeedSchedule.MayBeMetered(new AdapterSnapshot(Noon, [])));

        // A VPN over a phone's hotspot: the tunnel says unmetered, the link under it does not.
        Assert.True(SpeedSchedule.MayBeMetered(new AdapterSnapshot(Noon, [vpn with { Cost = free }, wifi with { Cost = metered }])));
        Assert.False(SpeedSchedule.MayBeMetered(new AdapterSnapshot(Noon, [vpn, wifi with { Cost = free }])));
    }

    [Fact]
    public void ScheduledRuns_OutliveDeletedTests_AndRunsStampedLaterAreNeverPruned()
    {
        string path = TempFile();

        try
        {
            using SqliteOutageStore store = new(path);

            store.NoteScheduledRun(Noon.AddDays(10));
            store.AddSpeedTest(Result(Noon, 1e6, 1e6) with { Scheduled = true });
            store.NoteScheduledRun(Noon);
            store.ClearSpeedTests();

            IReadOnlyList<DateTimeOffset> runs = store.ReadScheduledRuns(Noon - SpeedSchedule.Day);

            Assert.Equal([Noon, Noon.AddDays(10)], runs);
            Assert.False(SpeedSchedule.MayRun(runs, 1, Noon.AddHours(1)));

            // Two days on, the noon run is pruned; the one stamped ten days out stays.
            store.NoteScheduledRun(Noon.AddDays(3));
            Assert.Equal([Noon.AddDays(3), Noon.AddDays(10)], store.ReadScheduledRuns(DateTimeOffset.UnixEpoch));
        }
        finally
        {
            Delete(path);
        }
    }

    [Fact]
    public void PlanSpeed_ComparesByHourAndKeepsPeaksApart()
    {
        DateTimeOffset evening = new(2026, 10, 4, 21, 10, 0, TimeSpan.FromHours(6));
        DateTimeOffset morning = new(2026, 10, 5, 9, 5, 0, TimeSpan.FromHours(6));

        SpeedTestResult[] tests =
        [
            Result(evening, 40e6, 10e6),
            Result(evening.AddDays(-1), 50e6, 10e6),
            Result(morning, 95e6, 20e6),
            Result(morning, null, null),
        ];

        UsageBucket[] hourly =
        [
            new(new DateTimeOffset(2026, 10, 5, 9, 0, 0, TimeSpan.FromHours(6)), 1, 1, PeakDown: 11_875_000, PeakUp: 1_000_000, Minutes: 60),
            new(new DateTimeOffset(2026, 10, 4, 3, 0, 0, TimeSpan.FromHours(6)), 0, 0, 0, 0, Minutes: 0),
        ];

        PlanSpeedSummary summary = PlanSpeed.Compare(100, 20, tests, hourly);

        Assert.Equal(3, summary.Tests);
        Assert.Equal(21, summary.SlowestHour);
        Assert.Equal(0.45, summary.Hours[21].DownloadShare!.Value, 9);
        Assert.Equal(2, summary.Hours[21].Tests);
        Assert.Equal(0.95, summary.Hours[9].DownloadShare!.Value, 9);
        Assert.Equal(95, summary.Hours[9].PeakDownloadMbps!.Value, 9);
        Assert.Null(summary.Hours[3].PeakDownloadMbps);
        Assert.Equal(0.5, summary.DownloadShare!.Value, 9);
        Assert.Null(PlanSpeed.Compare(0, 0, tests, hourly).DownloadShare);
    }

    [Fact]
    public void ConnectionLog_KeepsSpeedTestsAndQuality()
    {
        string path = TempFile();

        try
        {
            using (SqliteOutageStore store = new(path))
            {
                int changed = 0;
                store.SpeedTestsChanged += (_, _) => changed++;

                long id = store.AddSpeedTest(Result(Noon, 94.5e6, 18.2e6) with { IdleMs = 12, LoadedDownMs = 40, Network = "Home (Wi-Fi)", Scheduled = true });
                store.AddSpeedTest(Result(Noon.AddHours(-30), null, null) with { Problem = "M-Lab is busy." });

                IReadOnlyList<SpeedTestResult> recent = store.ReadSpeedTests(Noon.AddHours(-1), Noon.AddHours(1));
                SpeedTestResult kept = Assert.Single(recent);

                Assert.Equal(id, kept.Id);
                Assert.Equal(94.5e6, kept.DownloadBitsPerSecond);
                Assert.Equal(40, kept.LoadedDownMs);
                Assert.Null(kept.LoadedUpMs);
                Assert.True(kept.Scheduled);
                Assert.Equal("Home (Wi-Fi)", kept.Network);
                Assert.Equal(LatencySource.InternetTarget, kept.Latency);
                Assert.Equal(Noon, kept.At);

                Assert.Equal(2, store.ReadSpeedTests(Noon.AddDays(-2), Noon.AddDays(1)).Count);
                Assert.True(store.DeleteSpeedTest(id));
                Assert.False(store.DeleteSpeedTest(id));
                Assert.Equal(3, changed);

                QualityQuarter quarter = new(QualityQuarter.QuarterOf(Noon), CheckTarget.Internet, 10, 9, 200, 40, 30, 8);
                store.AddQuality([quarter]);
                store.AddQuality([quarter with { WorstMs = 60 }]);

                QualityQuarter read = Assert.Single(store.ReadQuality(Noon.AddHours(-1), Noon.AddHours(1)));
                Assert.Equal(20, read.Sent);
                Assert.Equal(18, read.Answered);
                Assert.Equal(400, read.SumMs, 9);
                Assert.Equal(60, read.WorstMs, 9);
                Assert.Equal(16, read.JitterPairs);

                // Clearing the outage log leaves the tests and quality alone.
                store.Clear();
                Assert.Single(store.ReadSpeedTests(Noon.AddDays(-2), Noon.AddDays(1)));
                Assert.Equal(1, store.ClearQuality());
                Assert.Equal(1, store.ClearSpeedTests());
            }
        }
        finally
        {
            Delete(path);
        }
    }

    [Fact]
    public void ConnectionLog_OpensASchemaOneFileAndKeepsItsOutages()
    {
        string path = TempFile();

        try
        {
            using (SqliteConnection old = new(new SqliteConnectionStringBuilder { DataSource = path, Pooling = false }.ConnectionString))
            {
                old.Open();
                using SqliteCommand create = old.CreateCommand();
                create.CommandText =
                    """
                    CREATE TABLE outage (id INTEGER PRIMARY KEY AUTOINCREMENT, start_ms INTEGER NOT NULL, end_ms INTEGER,
                        seen_ms INTEGER NOT NULL, cause TEXT NOT NULL, network TEXT NOT NULL, interrupted INTEGER NOT NULL DEFAULT 0);
                    INSERT INTO outage (start_ms, end_ms, seen_ms, cause, network) VALUES (1759644000000, 1759644060000, 1759644060000, 'Provider', 'Home');
                    PRAGMA user_version=1;
                    """;
                create.ExecuteNonQuery();
            }

            using SqliteOutageStore store = new(path);

            Assert.Single(store.Read(DateTimeOffset.FromUnixTimeMilliseconds(1759643000000), DateTimeOffset.FromUnixTimeMilliseconds(1759645000000)));
            Assert.Empty(store.ReadSpeedTests(DateTimeOffset.MinValue.AddYears(2000), DateTimeOffset.Now.AddDays(1)));
            store.AddSpeedTest(Result(Noon, 1e6, 1e6));
        }
        finally
        {
            Delete(path);
        }
    }

    [Fact]
    public void ConnectionLog_BringsASchemaTwoFileToThree()
    {
        string path = TempFile();

        try
        {
            using (SqliteOutageStore first = new(path))
            {
                first.AddSpeedTest(Result(Noon, 1e6, 1e6));
            }

            // As a schema 2 file was: no scheduled_run table, version 2.
            using (SqliteConnection old = new(new SqliteConnectionStringBuilder { DataSource = path, Pooling = false }.ConnectionString))
            {
                old.Open();
                using SqliteCommand undo = old.CreateCommand();
                undo.CommandText = "DROP TABLE scheduled_run; PRAGMA user_version=2;";
                undo.ExecuteNonQuery();
            }

            using SqliteOutageStore store = new(path);

            store.NoteScheduledRun(Noon);
            Assert.Single(store.ReadScheduledRuns(Noon.AddHours(-1)));
            Assert.Single(store.ReadSpeedTests(Noon.AddHours(-1), Noon.AddHours(1)));
        }
        finally
        {
            Delete(path);
        }
    }

    [Fact]
    public void Service_RefusesWithoutAgreementOrServer()
    {
        string path = TempFile();

        try
        {
            using SqliteOutageStore store = new(path);
            WindsockSettings settings = new();
            using ConnectionMonitor monitor = new(new QuietFacts(), new SilentProbe(), store, settings, NullLogger<ConnectionMonitor>.Instance, TimeProvider.System);
            using SpeedTestService service = new(settings, store, monitor, new SilentProbe(), TimeProvider.System, NullLogger<SpeedTestService>.Instance);

            Assert.Equal(SpeedTestRefusal.NotAgreed, service.CanRun());
            Assert.Contains("publishes every result", service.Destination(), StringComparison.Ordinal);

            settings.ConnectionTools.MeasurementLabConsentAt = Noon;
            Assert.Equal(SpeedTestRefusal.None, service.CanRun());

            settings.ConnectionTools.SpeedTestServer = SpeedTestServer.LibreSpeed;
            Assert.Equal(SpeedTestRefusal.NoServer, service.CanRun());

            settings.ConnectionTools.LibreSpeedUrl = "https://speed.example.lan/index.html";
            Assert.Equal(SpeedTestRefusal.None, service.CanRun());
            Assert.Equal("your LibreSpeed server at speed.example.lan", service.Destination());
        }
        finally
        {
            Delete(path);
        }
    }

    [Theory]
    [InlineData("https://speed.example.lan", "https://speed.example.lan/")]
    [InlineData("https://speed.example.lan/backend/empty.php", "https://speed.example.lan/backend/")]
    [InlineData("http://192.168.0.5:8080/speed?x=1", "http://192.168.0.5:8080/speed/")]
    [InlineData("ftp://speed.example.lan", null)]
    [InlineData("speed.example.lan", null)]
    public void LibreSpeedAddress_IsMadeAFolder(string typed, string? expected)
    {
        Assert.Equal(expected, LibreSpeedClient.ParseRoot(typed)?.ToString());
    }

    internal static SpeedTestResult Result(DateTimeOffset at, double? down, double? up) =>
        new(0, at, SpeedTestBackend.MeasurementLab, "mlab1-dac01", false, down, up, null, null, null, null, LatencySource.InternetTarget, null, null);

    private static string TempFile() => Path.Combine(Path.GetTempPath(), "windsock-connection-" + Guid.NewGuid().ToString("N") + ".db");

    private static void Delete(string path)
    {
        SqliteConnection.ClearAllPools();

        foreach (string suffix in new[] { string.Empty, "-wal", "-shm" })
        {
            try
            {
                File.Delete(path + suffix);
            }
            catch (IOException)
            {
            }
        }
    }

    private sealed class QuietFacts : IConnectionFactsSource
    {
        public event EventHandler? Changed
        {
            add { }
            remove { }
        }

        public ConnectionFacts Read() => ConnectionFacts.Unknown;
    }

    private sealed class SilentProbe : INetworkPathProbe
    {
        public IAsyncEnumerable<PathHop> TraceAsync(string address, CancellationToken cancellationToken) => AsyncEnumerable.Empty<PathHop>();

        public Task<TimeSpan?> PingAsync(string address, CancellationToken cancellationToken) => Task.FromResult<TimeSpan?>(null);
    }
}
