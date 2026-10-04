using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;
using Windsock.Core.Apps;
using Windsock.Core.History;
using Windsock.Core.Processes;
using Windsock.Core.Settings;
using Xunit;

namespace Windsock.Core.Tests;

public sealed class AppMinuteTests : IDisposable
{
    // 2026-09-16 10:00:00 UTC, on a minute and a quarter hour.
    private static readonly DateTimeOffset Ten = new(2026, 9, 16, 10, 0, 0, TimeSpan.Zero);
    private static readonly long TenMinute = EpochMinute.From(Ten);

    private const string Chrome = @"\device\harddiskvolume3\program files\google\chrome\application\chrome.exe";
    private const string Torrent = @"\device\harddiskvolume3\program files\qbittorrent\qbittorrent.exe";

    private readonly string _directory = Path.Combine(Path.GetTempPath(), "Windsock-tests", Guid.NewGuid().ToString("N"));

    private string File => Path.Combine(_directory, "Windsock.db");

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();

        try
        {
            Directory.Delete(_directory, recursive: true);
        }
        catch (DirectoryNotFoundException)
        {
        }
        catch (IOException)
        {
        }
    }

    // ------------------------------------------------------------------ tally

    [Fact]
    public void AMinute_IsKeptOnlyWhenWatchedWhole()
    {
        Dictionary<int, string> apps = new() { [10] = Chrome };
        AppMinuteTally tally = new(id => apps[id]);
        Counters trace = new();

        // Watching starts mid-minute: that minute is not whole, and nothing is
        // kept in the first minute of watching.
        DateTimeOffset at = Ten.AddSeconds(-90);
        Assert.Null(tally.Observe(at, true, trace.Running, []));

        for (int step = 1; step <= 9; step++)
        {
            trace.Move(10, received: 1_000, sent: 100);
            Assert.Null(tally.Observe(at.AddSeconds(10 * step), true, trace.Running, []));
        }

        // 10:00:00 to 10:01:00, six readings, 600 bytes down a reading.
        at = Ten;
        MeasuredMinute? kept = null;

        for (int step = 1; step <= 7; step++)
        {
            if (step <= 6)
            {
                trace.Move(10, received: 600, sent: 60);
            }

            MeasuredMinute? finished = tally.Observe(at.AddSeconds(10 * step), true, trace.Running, []);

            if (finished is not null)
            {
                kept = finished;
            }
        }

        Assert.NotNull(kept);
        Assert.Equal(TenMinute, kept.Minute);
        AppBytes chrome = Assert.Single(kept.Apps);
        Assert.Equal(Chrome, chrome.AttributionId);
        Assert.Equal(3_600, chrome.BytesReceived);
        Assert.Equal(360, chrome.BytesSent);
    }

    [Fact]
    public void AProcessThatEndedBetweenReadings_StillCounts_UnderItsFile()
    {
        AppMinuteTally tally = new(_ => Chrome);
        Counters trace = new();

        MeasuredMinute? kept = Run(tally, trace, Ten, readings: 7, move: (step, counters) =>
        {
            if (step == 2)
            {
                counters.Move(20, received: 5_000, sent: 500);
            }

            if (step == 3)
            {
                // Moved 2,000 more, then ended before the reading.
                counters.End(20, extraReceived: 2_000, image: @"\Device\HarddiskVolume3\Program Files\qBittorrent\qbittorrent.exe");
            }
        });

        Assert.NotNull(kept);
        AppBytes gone = Assert.Single(kept.Apps);
        Assert.Equal(Chrome, gone.AttributionId); // it was seen running first, as Chrome says
        Assert.Equal(7_000, gone.BytesReceived);

        // Never seen running: named from the trace's own record of its file.
        AppMinuteTally blind = new(_ => throw new InvalidOperationException("never asked"));
        Counters quick = new();

        MeasuredMinute? second = Run(blind, quick, Ten, readings: 7, move: (step, counters) =>
        {
            if (step == 4)
            {
                counters.EndUnseen(30, received: 9_000, sent: 900, image: @"\Device\HarddiskVolume3\Program Files\qBittorrent\qbittorrent.exe");
            }
        });

        Assert.NotNull(second);
        AppBytes torrent = Assert.Single(second.Apps);
        Assert.Equal(Torrent, torrent.AttributionId);
        Assert.Equal(9_000, torrent.BytesReceived);
    }

    [Fact]
    public void AReusedProcessId_StartsAgainFromNothing()
    {
        int asked = 0;
        AppMinuteTally tally = new(id =>
        {
            asked++;
            return asked == 1 ? Chrome : Torrent;
        });
        Counters trace = new();

        MeasuredMinute? kept = Run(tally, trace, Ten, readings: 7, move: (step, counters) =>
        {
            if (step == 1)
            {
                counters.Move(10, received: 50_000, sent: 0);
            }

            if (step == 3)
            {
                // The id comes back with smaller counters, and no end was seen.
                counters.Set(10, received: 800, sent: 80);
            }
        });

        Assert.NotNull(kept);
        Assert.Equal(50_000, kept.Apps.Single(app => app.AttributionId == Chrome).BytesReceived);
        Assert.Equal(800, kept.Apps.Single(app => app.AttributionId == Torrent).BytesReceived);
    }

    [Fact]
    public void AGap_OrTheTraceStopping_DropsTheMinute()
    {
        AppMinuteTally tally = new(_ => Chrome);
        Counters trace = new();

        Assert.Null(tally.Observe(Ten, true, trace.Running, []));
        trace.Move(10, received: 1_000, sent: 0);
        Assert.Null(tally.Observe(Ten.AddSeconds(10), true, trace.Running, []));

        // Asleep from 10:00:10 to 10:00:50.
        trace.Move(10, received: 1_000, sent: 0);
        Assert.Null(tally.Observe(Ten.AddSeconds(50), true, trace.Running, []));
        Assert.Null(tally.Observe(Ten.AddSeconds(60), true, trace.Running, []));
        Assert.Null(tally.Observe(Ten.AddSeconds(70), true, trace.Running, []));

        // And a minute with the trace off in it is not kept either.
        AppMinuteTally stopped = new(_ => Chrome);
        Counters other = new();

        MeasuredMinute? kept = Run(stopped, other, Ten, readings: 7, measuringAt: step => step != 4, move: (_, counters) => counters.Move(10, 100, 0));
        Assert.Null(kept);
    }

    [Fact]
    public void AReadingLateAcrossAMinutesEdge_KeepsNeitherMinute()
    {
        AppMinuteTally tally = new(_ => Chrome);
        Counters trace = new();
        List<MeasuredMinute> kept = [];

        void Read(DateTimeOffset at)
        {
            trace.Move(10, received: 1_000, sent: 0);

            if (tally.Observe(at, true, trace.Running, []) is { } minute)
            {
                kept.Add(minute);
            }
        }

        // Watching from 9:58, every ten seconds, so 9:59 is whole.
        for (int second = -120; second <= 50; second += 10)
        {
            Read(Ten.AddSeconds(second));
        }

        // The reading that should close 10:00 at 10:01:00 comes at 10:01:04.
        Read(Ten.AddSeconds(64));

        for (int second = 70; second <= 190; second += 10)
        {
            Read(Ten.AddSeconds(second));
        }

        // 9:59 was closed on time; 10:00 took four seconds of 10:01, and 10:01
        // began four seconds late, so both are left to Windows. 10:02 is whole.
        Assert.Equal([EpochMinute.From(Ten) - 1, EpochMinute.From(Ten) + 2], kept.Select(minute => minute.Minute));
    }

    [Theory]
    [InlineData(4, null, null, null, "System")]
    [InlineData(99, "Claude_pzs8sxrjxfjjc", @"\Device\X\claude.exe", null, "Claude_pzs8sxrjxfjjc")]
    [InlineData(99, null, @"\Device\HarddiskVolume3\App.EXE", @"\Device\Other\app.exe", @"\device\harddiskvolume3\app.exe")]
    [InlineData(99, null, null, @"\Device\HarddiskVolume3\Gone.exe", @"\device\harddiskvolume3\gone.exe")]
    [InlineData(99, null, null, null, "")]
    public void AProcess_IsCountedUnderWhatWindowsCountsItUnder(int processId, string? family, string? native, string? image, string expected) =>
        Assert.Equal(expected, AppMinuteTally.CountedAs(processId, family, native, image));

    // --------------------------------------------------------------- breakdown

    [Fact]
    public void WindowsFigures_AreLeftOutWhereWindsockMeasured()
    {
        long hour = TenMinute;

        AppUsageTotal[] measured = [new(Chrome, 5_000, 300)];
        UsageWindow[] spans = [new(hour + 30, 30)];

        AppUsageSlice[] windows =
        [
            // Minute records inside the measured half hour: the same bytes again.
            new(hour + 40, 1, Chrome, 9_999, 999),

            // A minute before Windsock started: kept.
            new(hour + 10, 1, Torrent, 1_000, 100),

            // An hour record half measured: half of it kept.
            new(hour, 60, Torrent, 4_000, 400),

            // A record running past the period: cannot be cut, so left out.
            new(hour + 59, 5, Torrent, 7_000, 700),
        ];

        IReadOnlyList<AppShare> shares = AppBreakdown.Combine(measured, spans, windows, hour, hour + 60);

        AppShare chrome = shares.Single(share => share.AttributionId == Chrome);
        Assert.Equal(5_000, chrome.MeasuredDown);
        Assert.Equal(0, chrome.WindowsDown);

        AppShare torrent = shares.Single(share => share.AttributionId == Torrent);
        Assert.Equal(0, torrent.MeasuredDown);
        Assert.Equal(1_000 + 2_000, torrent.WindowsDown);
        Assert.Equal(100 + 200, torrent.WindowsUp);

        Assert.Equal(Chrome, shares[0].AttributionId);
    }

    [Fact]
    public void TrafficWindowsMovedOutOfClosedTime_IsPutBackThere_InTheChartAndEachApp()
    {
        // As run on 2026-09-16: Windsock closed for a stretch, Windows then held
        // no record for it and one far too big at two minutes after Windsock
        // started again.
        long start = TenMinute;
        using SqliteUsageHistoryStore store = new(File);

        List<UsageMinute> recorded = [];
        List<AppUsageSlice> windows = [];

        foreach (long minute in Enumerable.Range(-30, 10).Concat(Enumerable.Range(0, 30)).Select(offset => start + offset))
        {
            recorded.Add(new UsageMinute(minute, 160_000, 1_000, 160_000, 1_000));

            // Measuring warms up a minute after recording starts.
            if (minute != start)
            {
                store.WriteMeasuredMinute(minute, [new AppBytes(Torrent, 150_000, 0)]);
            }

            windows.Add(new AppUsageSlice(minute, 1, Torrent, minute == start + 2 ? 11_720_000 : 150_000, 0));
        }

        store.Write(recorded);
        store.WriteCoverage(start - 30, start - 21);
        store.WriteCoverage(start, start + 29);
        store.WriteAppUsage(
            [.. windows.Select(slice => new UsageWindow(slice.StartMinute, 1))],
            windows,
            new AppUsagePosition(start + 30, start + 29));

        IReadOnlyList<UsageSpan> fill = MovedTraffic.Fill(store, store, EpochMinute.ToUtc(start - 40), EpochMinute.ToUtc(start + 40));
        Assert.Equal(new UsageSpan(start - 20, 20, 11_720_000 - 160_000, 0), Assert.Single(fill));

        // Only where the closed stretch lies wholly inside the period.
        Assert.Empty(MovedTraffic.Fill(store, store, EpochMinute.ToUtc(start - 10), EpochMinute.ToUtc(start + 40)));

        AppHistory history = new(store, store, store);

        AppShare closed = Assert.Single(history.Breakdown(EpochMinute.ToUtc(start - 20), EpochMinute.ToUtc(start)));
        Assert.Equal(Torrent, closed.AttributionId);
        Assert.Equal(0, closed.MeasuredDown);
        Assert.Equal(11_570_000, closed.WindowsDown);

        // In a minute chart the fill is drawn in the stretch's first minute, and so are its apps.
        AppShare first = Assert.Single(history.Breakdown(EpochMinute.ToUtc(start - 20), EpochMinute.ToUtc(start - 19), EpochMinute.ToUtc(start + 40)));
        Assert.Equal(11_570_000, first.WindowsDown);
        Assert.Empty(history.Breakdown(EpochMinute.ToUtc(start - 19), EpochMinute.ToUtc(start - 18), EpochMinute.ToUtc(start + 40)));

        // The minute Windows put it in keeps Windsock's own figure alone.
        AppShare moved = Assert.Single(history.Breakdown(EpochMinute.ToUtc(start + 2), EpochMinute.ToUtc(start + 3)));
        Assert.Equal(150_000, moved.MeasuredDown);
        Assert.Equal(0, moved.WindowsDown);
    }

    [Fact]
    public void MovedTraffic_IsNotTakenForRecordsCountedAgain()
    {
        long start = TenMinute;
        UsageWindow[] kept = [new(start - 30, 10), new(start, 40)];
        AppUsageSlice[] moved = [new(start + 2, 1, Torrent, 11_720_000, 0)];
        (long, long)? Kept(long from, long until, string? app) => (150_000, 0);

        MovedSpan found = Assert.Single(MovedTraffic.Find(kept, (_, _) => moved, Kept, perApp: true));
        Assert.Equal(new MovedSpan(start - 20, 20, Torrent, 11_570_000, 0), found);

        // Windows has a record for the stretch before: nothing was moved out of it.
        AppUsageSlice[] covered = [new(start - 5, 1, Torrent, 9_000, 0), .. moved];
        Assert.Empty(MovedTraffic.Find(kept, (_, _) => covered, Kept, perApp: true));

        // Long after keeping resumed, a record is only the same bytes counted again.
        AppUsageSlice[] later = [new(start + 25, 1, Torrent, 11_720_000, 0)];
        Assert.Empty(MovedTraffic.Find(kept, (_, _) => later, Kept, perApp: true));

        // Nor are headers taken for moved traffic: a third above what was kept stays out.
        AppUsageSlice[] headers = [new(start + 2, 1, Torrent, 200_000_000, 0)];
        Assert.Empty(MovedTraffic.Find(kept, (_, _) => headers, (_, _, _) => (150_000_000, 0), perApp: true));

        // Nor where Windsock did not keep the record's minutes whole: it counts there already.
        Assert.Empty(MovedTraffic.Find(kept, (_, _) => moved, (_, _, _) => null, perApp: true));

        // With no stretch kept before within the look-behind, the missed time is that hour.
        Assert.Equal(start - MovedTraffic.LookBehind, Assert.Single(MovedTraffic.Find([new(start, 40)], (_, _) => moved, Kept, perApp: false)).StartMinute);
    }

    // ------------------------------------------------------------------ store

    [Fact]
    public void MeasuredMinutes_AreKeptOnce_AndTheirQuartersAddUp()
    {
        using SqliteUsageHistoryStore store = new(File);

        for (long minute = TenMinute; minute < TenMinute + 40; minute++)
        {
            store.WriteMeasuredMinute(minute, [new AppBytes(Chrome, 100, 10), new AppBytes(Torrent, 1_000, 0)]);
        }

        // Written again: nothing changes.
        store.WriteMeasuredMinute(TenMinute + 5, [new AppBytes(Chrome, 999_999, 0)]);

        // A quiet minute is still measured.
        store.WriteMeasuredMinute(TenMinute + 40, []);

        UsageWindow span = Assert.Single(store.ReadMeasured(TenMinute - 60, TenMinute + 120));
        Assert.Equal(TenMinute, span.StartMinute);
        Assert.Equal(41, span.Minutes);

        // 10:07 to 10:37: two whole quarters from their totals, eight and seven minutes either side.
        IReadOnlyList<AppUsageTotal> totals = store.ReadMeasuredTotals(TenMinute + 7, TenMinute + 37);
        Assert.Equal(3_000, totals.Single(total => total.AttributionId == Chrome).BytesReceived);
        Assert.Equal(30_000, totals.Single(total => total.AttributionId == Torrent).BytesReceived);

        // Inside one quarter.
        Assert.Equal(300, store.ReadMeasuredTotals(TenMinute + 1, TenMinute + 4).Single(total => total.AttributionId == Chrome).BytesReceived);
    }

    [Fact]
    public void Compaction_DropsTheMinutes_AndKeepsTheQuarters()
    {
        using SqliteUsageHistoryStore store = new(File);

        for (long minute = TenMinute; minute < TenMinute + 30; minute++)
        {
            store.WriteMeasuredMinute(minute, [new AppBytes(Chrome, 100, 10)]);
        }

        int removed = store.CompactAppDetail(TenMinute + 30);

        Assert.Equal(30, removed);
        Assert.Equal(TenMinute + 30, store.ReadAppDetailCompactedBefore());
        Assert.Equal(3_000, store.ReadMeasuredTotals(TenMinute, TenMinute + 30).Single().BytesReceived);

        // Minutes themselves are gone: a part of a quarter has nothing to read.
        Assert.Empty(store.ReadMeasuredTotals(TenMinute + 1, TenMinute + 4));
        Assert.True(store.FileBytes > 0);
    }

    [Fact]
    public void Erasing_TakesTheMinutes_RecountsTheQuarters_AndCutsTheMeasuredSpan()
    {
        using SqliteUsageHistoryStore store = new(File, new ManualClock(Ten.AddHours(1)));

        for (long minute = TenMinute; minute < TenMinute + 30; minute++)
        {
            store.WriteMeasuredMinute(minute, [new AppBytes(Chrome, 100, 10)]);
        }

        store.Delete(EpochMinute.ToUtc(TenMinute + 5), EpochMinute.ToUtc(TenMinute + 10));

        Assert.Equal(2_500, store.ReadMeasuredTotals(TenMinute, TenMinute + 30).Single().BytesReceived);
        Assert.Equal(1_000, store.ReadMeasuredTotals(TenMinute, TenMinute + 15).Single().BytesReceived);

        IReadOnlyList<UsageWindow> spans = store.ReadMeasured(TenMinute, TenMinute + 30);
        Assert.Equal([new UsageWindow(TenMinute, 5), new UsageWindow(TenMinute + 10, 20)], spans);

        // Nothing is written back into the erased stretch.
        store.WriteMeasuredMinute(TenMinute + 6, [new AppBytes(Chrome, 100, 10)]);
        Assert.Equal(2, store.ReadMeasured(TenMinute, TenMinute + 30).Count);

        store.RemoveEverything();
        Assert.Empty(store.ReadMeasured(TenMinute, TenMinute + 30));
        Assert.Empty(store.ReadMeasuredTotals(TenMinute, TenMinute + 30));
    }

    [Fact]
    public void AVersionThreeFile_GainsTheDetailTables_AndKeepsItsHistory()
    {
        Directory.CreateDirectory(_directory);

        using (SqliteConnection old = new($"Data Source={File};Pooling=False"))
        {
            old.Open();
            using SqliteCommand create = old.CreateCommand();
            create.CommandText =
                """
                CREATE TABLE usage_minute (minute INTEGER PRIMARY KEY, down INTEGER NOT NULL, up INTEGER NOT NULL, peak_down INTEGER NOT NULL, peak_up INTEGER NOT NULL) WITHOUT ROWID;
                CREATE TABLE coverage (start_minute INTEGER PRIMARY KEY, end_minute INTEGER NOT NULL) WITHOUT ROWID;
                CREATE TABLE app (id INTEGER PRIMARY KEY, attribution TEXT NOT NULL UNIQUE);
                CREATE TABLE app_usage (start_minute INTEGER NOT NULL, minutes INTEGER NOT NULL, app INTEGER NOT NULL, down INTEGER NOT NULL, up INTEGER NOT NULL, PRIMARY KEY (start_minute, app)) WITHOUT ROWID;
                CREATE TABLE app_state (name TEXT PRIMARY KEY, value INTEGER NOT NULL) WITHOUT ROWID;
                CREATE TABLE app_erased (from_minute INTEGER NOT NULL, until_minute INTEGER NOT NULL);
                INSERT INTO usage_minute VALUES (29000000, 5, 6, 7, 8);
                PRAGMA user_version = 3;
                """;
            create.ExecuteNonQuery();
        }

        using SqliteUsageHistoryStore store = new(File);

        Assert.Equal(5, store.Summarise(EpochMinute.ToUtc(29000000), EpochMinute.ToUtc(29000001)).BytesDown);
        store.WriteMeasuredMinute(TenMinute, [new AppBytes(Chrome, 1, 1)]);
        Assert.Single(store.ReadMeasured(TenMinute, TenMinute + 1));
    }

    // --------------------------------------------------------------- recorder

    [Fact]
    public void TheRecorder_ReadsOnTheClocksTenSecondMarks()
    {
        Assert.Equal(TimeSpan.FromSeconds(10), AppMinuteRecorder.UntilNextMark(Ten));
        Assert.Equal(TimeSpan.FromSeconds(3), AppMinuteRecorder.UntilNextMark(Ten.AddSeconds(7)));
        Assert.Equal(TimeSpan.FromSeconds(10.5), AppMinuteRecorder.UntilNextMark(Ten.AddSeconds(9.5)));
    }

    [Fact]
    public void TheRecorder_KeepsWholeMinutes_AndNothingWhileHistoryIsOff()
    {
        using SqliteUsageHistoryStore store = new(File, new ManualClock(Ten.AddHours(2)));
        ManualClock clock = new(Ten);
        FakeLedger ledger = new();
        bool recording = true;

        AppMinuteRecorder recorder = new(ledger, store, () => recording, new WindsockSettings(), clock, NullLogger<AppMinuteRecorder>.Instance);

        // 10:00 is the first minute of watching, so 10:01 and 10:02 are kept.
        for (int step = 0; step <= 19; step++)
        {
            ledger.Running[4] = new ProcessTraffic(step * 10, step * 100, step);
            recorder.Tick();
            clock.Advance(TimeSpan.FromSeconds(10));
        }

        Assert.Equal(1_200, store.ReadMeasuredTotals(TenMinute, TenMinute + 3).Single(total => total.AttributionId == "System").BytesReceived);

        recording = false;

        for (int step = 20; step <= 40; step++)
        {
            ledger.Running[4] = new ProcessTraffic(step * 10, step * 100, step);
            recorder.Tick();
            clock.Advance(TimeSpan.FromSeconds(10));
        }

        Assert.Equal(2, store.ReadMeasured(TenMinute, TenMinute + 10).Single().Minutes);
    }

    // ---------------------------------------------------------------- helpers

    private static MeasuredMinute? Run(
        AppMinuteTally tally,
        Counters trace,
        DateTimeOffset start,
        int readings,
        Action<int, Counters> move,
        Func<int, bool>? measuringAt = null)
    {
        MeasuredMinute? kept = null;

        // A minute of watching first, which keeps nothing.
        for (int warm = -6; warm <= 0; warm++)
        {
            Assert.Null(tally.Observe(start.AddSeconds(10 * warm), true, trace.Running, trace.TakeDeparted()));
        }

        for (int step = 1; step <= readings; step++)
        {
            if (step <= 6)
            {
                move(step, trace);
            }

            MeasuredMinute? finished = tally.Observe(
                start.AddSeconds(10 * step),
                measuringAt?.Invoke(step) ?? true,
                trace.Running,
                trace.TakeDeparted());

            kept ??= finished;
        }

        return kept;
    }

    private sealed class Counters
    {
        private readonly List<DepartedTraffic> _departed = [];

        public Dictionary<int, ProcessTraffic> Running { get; } = [];

        public void Move(int processId, long received, long sent)
        {
            ProcessTraffic now = Running.GetValueOrDefault(processId);
            Running[processId] = new ProcessTraffic(now.BytesSent + sent, now.BytesReceived + received, now.Packets + 1);
        }

        public void Set(int processId, long received, long sent) =>
            Running[processId] = new ProcessTraffic(sent, received, 1);

        public void End(int processId, long extraReceived, string image)
        {
            ProcessTraffic now = Running[processId];
            Running.Remove(processId);
            _departed.Add(new DepartedTraffic(processId, now with { BytesReceived = now.BytesReceived + extraReceived }, image));
        }

        public void EndUnseen(int processId, long received, long sent, string image) =>
            _departed.Add(new DepartedTraffic(processId, new ProcessTraffic(sent, received, 1), image));

        public List<DepartedTraffic> TakeDeparted()
        {
            List<DepartedTraffic> taken = [.. _departed];
            _departed.Clear();
            return taken;
        }
    }

    private sealed class FakeLedger : IProcessTrafficLedger
    {
        public Dictionary<int, ProcessTraffic> Running { get; } = [];

        public bool CanAttribute => true;

        public void Take(Dictionary<int, ProcessTraffic> running, List<DepartedTraffic> departed)
        {
            running.Clear();
            departed.Clear();

            foreach ((int id, ProcessTraffic traffic) in Running)
            {
                running[id] = traffic;
            }
        }

        public string? ImageOf(int processId) => null;
    }

    private sealed class ManualClock(DateTimeOffset now) : TimeProvider
    {
        private DateTimeOffset _now = now;

        public override DateTimeOffset GetUtcNow() => _now;

        public void Advance(TimeSpan by) => _now += by;
    }
}
