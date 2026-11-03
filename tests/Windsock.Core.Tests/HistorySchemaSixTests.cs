using Microsoft.Extensions.Logging.Abstractions;
using Windsock.Core.Apps;
using Windsock.Core.Connectivity;
using Windsock.Core.History;
using Windsock.Core.Networking;
using Windsock.Core.Settings;
using Xunit;

namespace Windsock.Core.Tests;

/// <summary>
/// Schema 6: the network each minute was on, quarter-hour totals the database
/// keeps itself, the local-network share, and the marks that keep a file safe
/// from older and newer versions.
/// </summary>
public sealed class HistorySchemaSixTests : IDisposable
{
    private static readonly NetworkTag Home = new("0d1170d5-35db-4491-b1ea-2e569fc298c4", "Home", NetworkKind.Ethernet, false);
    private static readonly NetworkTag Phone = new("a1b2c3d4-0000-4000-8000-000000000001", "Shariar's Galaxy", NetworkKind.WiFi, true);

    private readonly string _directory = Path.Combine(Path.GetTempPath(), "Windsock-tests", Guid.NewGuid().ToString("N"));

    private string File => Path.Combine(_directory, "Windsock.db");

    private static DateTimeOffset Noon { get; } = new(new DateTime(2026, 3, 14, 12, 0, 0, DateTimeKind.Local));

    private static long At(DateTimeOffset moment) => EpochMinute.From(moment);

    private SqliteUsageHistoryStore Open(DateTimeOffset? now = null) =>
        new(File, new ManualClock(now ?? Noon.AddDays(1)));

    private static UsageMinute Minute(long minute, long down, long up = 0, NetworkTag? network = null, long peak = 0) =>
        new(minute, down, up, peak, 0) { Network = network };

    [Fact]
    public void ASchemaFiveFile_IsMigratedInPlace_WithEveryMinuteKeptAndNoNetwork()
    {
        long start = At(Noon);
        OlderHistoryFiles.Create(
            File,
            OlderHistoryFiles.Schema5,
            $"INSERT INTO usage_minute VALUES ({start}, 1000, 100, 50, 5), ({start + 1}, 2000, 200, 60, 6), ({start + 20}, 3000, 300, 70, 7);",
            $"INSERT INTO coverage VALUES ({start}, {start + 20});",
            "INSERT INTO app (attribution) VALUES ('chrome');",
            $"INSERT INTO app_usage VALUES ({start}, 60, 1, 5000, 500);");

        using (SqliteUsageHistoryStore store = Open())
        {
            Assert.Equal(HistoryFileState.Ready, store.State);
            Assert.Equal(6600, store.Summarise(Noon, Noon.AddHours(1)).BytesTotal);
            Assert.All(store.ReadMinuteRows(start, start + 60), row => Assert.Null(row.Network));
            Assert.All(store.ReadQuarters(start, start + 60), quarter => Assert.Equal(0, quarter.NetworkId));
            Assert.Equal(6600, store.ReadQuarters(start, start + 60).Sum(quarter => quarter.BytesTotal));
            Assert.Single(store.ReadAppSlices(start, start + 60));
            Assert.Empty(store.ReadNetworks());
        }

        Assert.Equal(6L, OlderHistoryFiles.Ask(File, "PRAGMA user_version;"));
        Assert.Equal((long)SqliteUsageHistoryStore.ApplicationId, OlderHistoryFiles.Ask(File, "PRAGMA application_id;"));
        Assert.Equal((long)SqliteUsageHistoryStore.FormatFloor, OlderHistoryFiles.Ask(File, "SELECT value FROM app_state WHERE name = 'format_floor';"));
    }

    [Fact]
    public void AFileFromVersionZeroPointOne_IsMigratedToo()
    {
        long start = At(Noon);
        OlderHistoryFiles.Create(
            File,
            OlderHistoryFiles.Schema1,
            $"INSERT INTO usage_minute VALUES ({start}, 1000, 100, 50, 5), ({start + 90}, 4000, 400, 80, 8);");

        using SqliteUsageHistoryStore store = Open();

        Assert.Equal(HistoryFileState.Ready, store.State);
        Assert.Equal(5500, store.Summarise(Noon, Noon.AddHours(3)).BytesTotal);
        Assert.Equal(2, store.Summarise(Noon, Noon.AddHours(3)).Minutes);
    }

    [Fact]
    public void Minutes_KeepTheNetworkTheyWereMeasuredOn()
    {
        long start = At(Noon);

        using SqliteUsageHistoryStore store = Open();
        store.Write([Minute(start, 1000, 10, Home), Minute(start + 1, 2000, 20, Phone), Minute(start + 2, 3000, 30)]);

        IReadOnlyList<UsageMinute> rows = store.ReadMinuteRows(start, start + 3);
        Assert.Equal(Home, rows[0].Network);
        Assert.Equal(Phone, rows[1].Network);
        Assert.Null(rows[2].Network);

        IReadOnlyList<NetworkRecord> networks = store.ReadNetworks();
        Assert.Equal(2, networks.Count);
        NetworkRecord phone = Assert.Single(networks, network => network.Key == Phone.Key);
        Assert.Equal(2020, phone.BytesTotal);
        Assert.Equal(1, phone.Minutes);
        Assert.True(phone.Metered);
        Assert.Equal(NetworkKind.WiFi, phone.Kind);
    }

    [Fact]
    public void AMinuteWrittenInTwoGoes_KeepsTheNetworkItWasFirstWrittenWith()
    {
        long start = At(Noon);

        using SqliteUsageHistoryStore store = Open();
        store.Write([Minute(start, 1000, 0, Home, peak: 10)]);
        store.Write([Minute(start, 500, 0, Phone, peak: 30)]);

        UsageMinute row = Assert.Single(store.ReadMinuteRows(start, start + 1));
        Assert.Equal(Home, row.Network);
        Assert.Equal(1500, row.BytesDown);

        UsageQuarter quarter = Assert.Single(store.ReadQuarters(start - (start % 15), start - (start % 15) + 15));
        Assert.Equal(1500, quarter.BytesDown);
        Assert.Equal(30, quarter.PeakDown);
        Assert.Equal(1, quarter.Minutes);
    }

    [Fact]
    public void ARenamedNetwork_KeepsItsRowAndTakesTheNewName()
    {
        long start = At(Noon);

        using SqliteUsageHistoryStore store = Open();
        store.Write([Minute(start, 1, 0, Home)]);
        store.Write([Minute(start + 1, 1, 0, Home with { Name = "Home 5G" })]);

        NetworkRecord network = Assert.Single(store.ReadNetworks());
        Assert.Equal("Home 5G", network.Name);
        Assert.Equal(2, network.Minutes);
    }

    [Fact]
    public void QuarterTotals_AlwaysAgreeWithTheMinutes()
    {
        long start = At(Noon) - (At(Noon) % 15);
        Random random = new(7);

        using SqliteUsageHistoryStore store = Open();
        List<UsageMinute> minutes = [];

        for (int each = 0; each < 600; each++)
        {
            NetworkTag? network = (each / 100) switch { 0 or 3 => Home, 1 or 4 => Phone, _ => null };
            minutes.Add(Minute(start + each, random.Next(0, 100_000), random.Next(0, 10_000), network, random.Next(0, 5000)));
        }

        store.Write(minutes);

        // Writes in two goes, an import over the top, an import beside, an erase.
        store.Write([Minute(start + 7, 999, 1, Home, peak: 99_999)]);
        store.Replace([Minute(start + 30, 5, 5), Minute(start + 900, 77, 7, Phone, 12)]);
        store.Merge([Minute(start + 31, 123_456, 0), Minute(start + 901, 88, 8)]);
        store.Delete(EpochMinute.ToUtc(start + 50), EpochMinute.ToUtc(start + 128));

        AgreeEverywhere(store, start, start + 1000);
    }

    [Fact]
    public void Replace_WithASmallerPeak_BringsTheQuarterPeakDown()
    {
        long start = At(Noon) - (At(Noon) % 15);

        using SqliteUsageHistoryStore store = Open();
        store.Write([Minute(start, 10, 0, Home, peak: 900), Minute(start + 1, 10, 0, Home, peak: 100)]);
        store.Replace([Minute(start, 10, 0, peak: 50)]);

        UsageQuarter quarter = Assert.Single(store.ReadQuarters(start, start + 15));
        Assert.Equal(100, quarter.PeakDown);
    }

    [Fact]
    public void ReadingByTheHourAndDay_FromQuarterTotals_MatchesGroupingTheMinutes()
    {
        long start = At(Noon) - 3000 + 7;
        Random random = new(11);
        List<UsageMinute> minutes = [];

        for (int each = 0; each < 5000; each += 1 + random.Next(0, 3))
        {
            minutes.Add(Minute(start + each, random.Next(1, 1_000_000), random.Next(0, 100_000), each % 2 == 0 ? Home : Phone, random.Next(0, 100_000)));
        }

        using SqliteUsageHistoryStore store = Open();
        store.Write(minutes);

        // A range starting and ending inside quarters, so the edges are read minute by minute.
        DateTimeOffset from = EpochMinute.ToUtc(start + 13);
        DateTimeOffset until = EpochMinute.ToUtc(start + 4987);

        foreach (UsageGranularity granularity in new[] { UsageGranularity.Hour, UsageGranularity.Day })
        {
            Dictionary<DateTimeOffset, (long Down, long Up, long Peak, int Count)> expected = [];

            foreach (UsageMinute row in minutes.Where(row => row.Minute >= start + 13 && row.Minute < start + 4987))
            {
                DateTimeOffset bucket = HistoryFill.BucketStart(row.Minute, granularity);
                (long down, long up, long peak, int count) = expected.GetValueOrDefault(bucket);
                expected[bucket] = (down + row.BytesDown, up + row.BytesUp, Math.Max(peak, row.PeakDown), count + 1);
            }

            IReadOnlyList<UsageBucket> read = store.Read(from, until, granularity);
            Assert.Equal(expected.Count, read.Count);

            foreach (UsageBucket bucket in read)
            {
                (long down, long up, long peak, int count) = expected[bucket.Start];
                Assert.Equal(down, bucket.BytesDown);
                Assert.Equal(up, bucket.BytesUp);
                Assert.Equal(peak, bucket.PeakDown);
                Assert.Equal(count, bucket.Minutes);
            }
        }

        UsageSummary summary = store.Summarise(from, until);
        Assert.Equal(minutes.Where(row => row.Minute >= start + 13 && row.Minute < start + 4987).Sum(row => row.BytesTotal), summary.BytesTotal);
    }

    [Fact]
    public void ThePeak_IsFoundToTheMinute()
    {
        long start = At(Noon);

        using SqliteUsageHistoryStore store = Open();
        store.Write([Minute(start, 1, 0, peak: 10), Minute(start + 37, 1, 0, peak: 7000), Minute(start + 38, 1, 0, peak: 6999), Minute(start + 400, 1, 0, peak: 50)]);

        Assert.Equal(new PeakMoment(start + 37, 7000), store.ReadPeak(start, start + 500, upload: false));
        Assert.Equal(new PeakMoment(start + 38, 6999), store.ReadPeak(start + 38, start + 500, upload: false));
        Assert.Null(store.ReadPeak(start, start + 500, upload: true));
    }

    [Fact]
    public void Erasing_TakesTheNetworksNoMinuteIsLeftOn()
    {
        long start = At(Noon);

        using SqliteUsageHistoryStore store = Open();
        store.Write([Minute(start, 1, 0, Home), Minute(start + 600, 1, 0, Phone)]);
        store.Delete(Noon.AddHours(5), Noon.AddHours(12));

        NetworkRecord left = Assert.Single(store.ReadNetworks());
        Assert.Equal(Home.Key, left.Key);
    }

    [Fact]
    public void Erasing_DoesNotLeaveNotesOfItsOwnForTheNextRun()
    {
        long start = At(Noon);

        using (SqliteUsageHistoryStore store = Open())
        {
            store.Write([Minute(start, 1), Minute(start + 1, 2)]);
            store.Delete(Noon, Noon.AddMinutes(1));
        }

        Assert.Equal(0L, OlderHistoryFiles.Ask(File, "SELECT COUNT(*) FROM erase_pending;"));
        Assert.Equal(0L, OlderHistoryFiles.Ask(File, "SELECT COUNT(*) FROM app_state WHERE name = 'erasing';"));
    }

    [Fact]
    public void RemoveEverything_TakesNetworksAndNotes_ButKeepsBookmarksAndPlanLevels()
    {
        long start = At(Noon);

        using SqliteUsageHistoryStore store = Open();
        store.Write([Minute(start, 1, 0, Home)]);
        store.AddNote(Noon, Noon.AddHours(1), "game update");
        store.WriteMark("auto_export_day", 42);
        store.MarkCrossed("plan", start, 80, start);

        store.RemoveEverything();

        Assert.Empty(store.ReadNetworks());
        Assert.Empty(store.ReadNotes(Noon.AddDays(-1), Noon.AddDays(1)));
        Assert.Empty(store.ReadQuarters(start - 100, start + 100));
        Assert.Equal(42, store.ReadMark("auto_export_day"));
        Assert.Contains(80, store.ReadCrossedLevels("plan", start));
    }

    [Fact]
    public void AnEraseByVersionZeroPointOne_IsFinishedByTheNextRun()
    {
        long start = At(Noon);

        using (SqliteUsageHistoryStore store = Open())
        {
            store.Write([Minute(start, 1000, 0, Home), Minute(start + 1, 1000, 0, Home), Minute(start + 2, 1000, 0, Home), Minute(start + 300, 5, 0, Home)]);
            store.WriteMeasuredMinute(start + 1, [new AppBytes("chrome", 400, 4)]);
            store.WriteSystem([new SystemMinute(start + 1, 1, 100, 100, 0, 0, null, 0, 0, null, 1, 1, 1, 0, 0)]);
            store.WriteAppUsage([new UsageWindow(start, 15)], [new AppUsageSlice(start, 15, "chrome", 900, 9)], new AppUsagePosition(start + 15, start + 15));
            store.AddNote(Noon, Noon.AddMinutes(3), "inside");
        }

        // What 0.1.0 does when a row of its History is deleted: the minutes go,
        // and nothing else, since it knows of nothing else.
        OlderHistoryFiles.Run(File, $"DELETE FROM usage_minute WHERE minute >= {start} AND minute < {start + 3};");
        Assert.Equal(3L, OlderHistoryFiles.Ask(File, "SELECT COUNT(*) FROM erase_pending;"));

        using (SqliteUsageHistoryStore store = Open())
        {
            Assert.Empty(store.ReadAppSlices(start - 100, start + 100));
            Assert.Empty(store.ReadMeasuredTotals(start, start + 3));
            Assert.Empty(store.ReadSystem(Noon, Noon.AddMinutes(3), UsageGranularity.Minute));
            Assert.Empty(store.ReadNotes(Noon, Noon.AddMinutes(3)));
            Assert.Equal(5, store.ReadQuarters(start, start + 400).Sum(quarter => quarter.BytesTotal));
            Assert.True(store.Revision > 0);
        }

        Assert.Equal(0L, OlderHistoryFiles.Ask(File, "SELECT COUNT(*) FROM erase_pending;"));
        Assert.True((long)OlderHistoryFiles.Ask(File, $"SELECT COUNT(*) FROM app_erased WHERE from_minute <= {start} AND until_minute >= {start + 3};")! > 0);
    }

    [Fact]
    public void EraseEverythingByVersionZeroPointOne_IsFinishedByTheNextRun_KeepingWhatItRecordedAfter()
    {
        long start = At(Noon);

        using (SqliteUsageHistoryStore store = Open())
        {
            store.Write([Minute(start, 1000, 0, Home)]);
            store.WriteCoverage(start, start);
            store.WriteMeasuredMinute(start, [new AppBytes("chrome", 400, 4)]);
            store.AddNote(Noon, Noon.AddMinutes(3), "inside");
        }

        // 0.1.0's "Delete everything", then a minute it recorded afterwards.
        long later = EpochMinute.From(DateTimeOffset.UtcNow) + 5;
        OlderHistoryFiles.Run(File, "DELETE FROM usage_minute;", "DELETE FROM coverage;", $"INSERT INTO usage_minute (minute, down, up, peak_down, peak_up) VALUES ({later}, 77, 7, 1, 1);");

        using SqliteUsageHistoryStore reopened = Open(EpochMinute.ToUtc(later + 10));

        Assert.Empty(reopened.ReadMeasuredTotals(start - 100, start + 100));
        Assert.Empty(reopened.ReadNotes(Noon.AddDays(-1), Noon.AddDays(1)));
        Assert.Empty(reopened.ReadNetworks());
        Assert.Equal(84, reopened.ReadQuarters(later - 100, later + 100).Sum(quarter => quarter.BytesTotal));
    }

    [Fact]
    public void AFileANewerVersionSaysThisOneMustNotWrite_IsReadButNeverWritten()
    {
        long start = At(Noon);

        using (SqliteUsageHistoryStore store = Open())
        {
            store.Write([Minute(start, 1000, 0, Home)]);
        }

        OlderHistoryFiles.Run(File, "PRAGMA user_version=7;", "UPDATE app_state SET value = 7 WHERE name = 'format_floor';");

        using SqliteUsageHistoryStore newer = Open();

        Assert.Equal(HistoryFileState.NewerVersion, newer.State);
        Assert.Equal(7, newer.FileSchema);
        Assert.Equal(1000, newer.Summarise(Noon, Noon.AddMinutes(1)).BytesTotal);

        Exception write = Assert.ThrowsAny<Exception>(() => newer.Write([Minute(start + 1, 1)]));
        Assert.True(HistoryFailure.IsStorage(write));
        Assert.True(HistoryFailure.IsReadOnly(write));
        Assert.True(HistoryFailure.IsStorage(Assert.ThrowsAny<Exception>(() => newer.Delete(Noon, Noon.AddDays(1)))));
        Assert.True(HistoryFailure.IsStorage(Assert.ThrowsAny<Exception>(() => newer.RemoveEverything())));
        Assert.True(HistoryFailure.IsStorage(Assert.ThrowsAny<Exception>(() => newer.AddNote(Noon, Noon.AddHours(1), "x"))));
        Assert.Equal(7L, OlderHistoryFiles.Ask(File, "PRAGMA user_version;"));
    }

    [Fact]
    public void AReadOnlyFile_IsLoggedOnce_WhileEveryOtherFailureIsLoggedEachTime()
    {
        WriteFailureLog log = new();
        HistoryReadOnlyException refused = new("newer");
        InvalidOperationException damaged = new("damaged");

        Assert.True(log.ShouldLog(refused));
        Assert.False(log.ShouldLog(refused));
        Assert.False(log.ShouldLog(new HistoryReadOnlyException("again")));
        Assert.True(log.ShouldLog(damaged));
        Assert.True(log.ShouldLog(damaged));
        Assert.False(HistoryFailure.IsReadOnly(damaged));
        Assert.True(HistoryFailure.IsStorage(refused));
    }

    [Fact]
    public void AnEraseByVersionZeroPointOne_TakesTheClosedTimeInsideIt_AndTheCopies()
    {
        // Found by review: 0.1.0 erases one unbroken range, but only its
        // recorded minutes were known afterwards, so Windows' figures and notes
        // for time inside it with nothing recorded came back as History's fill.
        DateTimeOffset first = new(Noon.Date.AddDays(-5), Noon.Offset);
        long day1 = At(first);
        long day2 = day1 + 1440;
        long day3 = day2 + 1440;
        long day4 = day3 + 1440;
        string backups = Path.Combine(_directory, "Backups");

        using (SqliteUsageHistoryStore store = Open())
        {
            List<UsageMinute> rows = [];

            for (int i = 0; i < 60; i++)
            {
                rows.Add(Minute(day1 + 600 + i, 1000, 100, Home));
                rows.Add(Minute(day3 + 600 + i, 1000, 100, Home));
            }

            store.Write(rows);
            store.WriteCoverage(day1 + 600, day1 + 659);
            store.WriteCoverage(day3 + 600, day3 + 659);
            store.WriteAppUsage([new UsageWindow(day2 + 600, 60)], [new AppUsageSlice(day2 + 600, 60, "chrome", 5000, 500)], new AppUsagePosition(day4, day4));
            store.AddNote(EpochMinute.ToUtc(day2 + 600), EpochMinute.ToUtc(day2 + 660), "between");
            Assert.False(store.SettledOlderErase);
        }

        // A copy made before the erase still holds what was erased.
        Directory.CreateDirectory(backups);
        System.IO.File.WriteAllText(Path.Combine(backups, "Windsock-2026-03-01.db"), "a copy");

        OlderHistoryFiles.Run(File, $"DELETE FROM usage_minute WHERE minute >= {day1} AND minute < {day4};");

        using (SqliteUsageHistoryStore store = Open())
        {
            Assert.True(store.SettledOlderErase);
            Assert.Empty(store.ReadAppSlices(day1, day4));
            Assert.Empty(store.ReadMissedSpans(EpochMinute.ToUtc(day1), EpochMinute.ToUtc(day4)));
            Assert.Empty(store.ReadNotes(EpochMinute.ToUtc(day1), EpochMinute.ToUtc(day4)));

            using HistoryBackup backup = new(store, store, new WindsockSettings(), backups, TimeProvider.System, NullLogger<HistoryBackup>.Instance);
            backup.RunDue(Noon);
            Assert.False(System.IO.File.Exists(Path.Combine(backups, "Windsock-2026-03-01.db")));
        }
    }

    [Fact]
    public void TwoErasesByVersionZeroPointOne_LeaveTheTimeBetweenThem()
    {
        DateTimeOffset first = new(Noon.Date.AddDays(-5), Noon.Offset);
        long day1 = At(first);
        long day2 = day1 + 1440;
        long day3 = day2 + 1440;

        using (SqliteUsageHistoryStore store = Open())
        {
            store.Write([Minute(day1 + 600, 1000), Minute(day2 + 720, 1000), Minute(day3 + 600, 1000)]);
            store.WriteAppUsage([new UsageWindow(day2 + 480, 60)], [new AppUsageSlice(day2 + 480, 60, "chrome", 5000, 500)], new AppUsagePosition(day3 + 1440, day3 + 1440));
        }

        // Two separate erases; the minute on day 2 stays, so the time around it was never erased.
        OlderHistoryFiles.Run(
            File,
            $"DELETE FROM usage_minute WHERE minute >= {day1} AND minute < {day2};",
            $"DELETE FROM usage_minute WHERE minute >= {day3} AND minute < {day3 + 1440};");

        using SqliteUsageHistoryStore reopened = Open();
        Assert.Single(reopened.ReadAppSlices(day2, day3));
        Assert.Equal(1, reopened.Summarise(EpochMinute.ToUtc(day1), EpochMinute.ToUtc(day3 + 1440)).Minutes);
    }

    [Fact]
    public void AFileANewerVersionWroteThatThisSqliteCannotRead_IsLeftAlone_NotSetAsideAsDamaged()
    {
        // Found by review: SQLite reports a schema it cannot parse as a damaged
        // file, and a damaged file is set aside for a fresh one - which, after a
        // downgrade, would have hidden all of history.
        using (SqliteUsageHistoryStore store = Open())
        {
            store.Write([Minute(At(Noon), 1000)]);
        }

        OlderHistoryFiles.Run(
            File,
            "CREATE TABLE later (a INTEGER);",
            "UPDATE app_state SET value = 7 WHERE name = 'format_floor';",
            "PRAGMA user_version=7;",
            "PRAGMA writable_schema=ON;",
            "UPDATE sqlite_master SET sql = 'CREATE TABLE later (a INTEGER) WITH SOMETHING_NEWER' WHERE name = 'later';",
            "PRAGMA writable_schema=OFF;");

        Assert.Equal(7, SqliteUsageHistoryStore.NewerByHeader(File));

        using (SqliteUsageHistoryStore store = Open())
        {
            Assert.Equal(HistoryFileState.Unavailable, store.State);
            Assert.Equal(7, store.FileSchema);
            Assert.False(store.RecoveredFromCorruption);
            Assert.True(HistoryFailure.IsStorage(Assert.ThrowsAny<Exception>(() => store.Summarise(Noon, Noon.AddHours(1)))));
        }

        Assert.Empty(Directory.GetFiles(_directory, "*corrupt*"));
        Assert.True(System.IO.File.Exists(File));
    }

    [Fact]
    public void ADamagedFile_IsStillSetAside_AndAnOrdinaryFileIsNotNewer()
    {
        using (SqliteUsageHistoryStore store = Open())
        {
            store.Write([Minute(At(Noon), 1000)]);
        }

        Assert.Null(SqliteUsageHistoryStore.NewerByHeader(File));

        byte[] bytes = System.IO.File.ReadAllBytes(File);
        Array.Clear(bytes, 0, 100);
        System.IO.File.WriteAllBytes(File, bytes);
        Assert.Null(SqliteUsageHistoryStore.NewerByHeader(File));

        using SqliteUsageHistoryStore reopened = Open();
        Assert.True(reopened.RecoveredFromCorruption);
        Assert.Equal(HistoryFileState.Ready, reopened.State);
    }

    [Fact]
    public void EraseEverything_StandsAndIsAnnounced_WhenTheVacuumAfterItFails()
    {
        // Found by review: the erase was committed, then VACUUM threw (a full
        // disk), so Erased never fired, the copies beside the file kept the
        // erased history, and the page said the erase had failed.
        using SqliteUsageHistoryStore store = Open();
        store.VacuumWait = TimeSpan.FromSeconds(1);
        store.Write([Minute(At(Noon), 1000), Minute(At(Noon) + 1, 1000)]);

        bool erased = false;
        store.Erased += (_, _) => erased = true;

        // Another reader holding the file makes VACUUM fail; the erase does not.
        using (OlderHistoryFiles.HoldRead(File))
        {
            Assert.Equal(2, store.RemoveEverything());
        }

        Assert.True(erased);
        Assert.Equal(0, store.Summarise(Noon, Noon.AddHours(1)).Minutes);
    }

    [Fact]
    public void ANewerFileThatKeepsTheFloor_IsWrittenAsUsual()
    {
        using (SqliteUsageHistoryStore store = Open())
        {
            store.Write([Minute(At(Noon), 1)]);
        }

        OlderHistoryFiles.Run(File, "PRAGMA user_version=8;");

        using SqliteUsageHistoryStore newer = Open();
        Assert.Equal(HistoryFileState.Ready, newer.State);
        newer.Write([Minute(At(Noon) + 1, 1)]);
        Assert.Equal(8L, OlderHistoryFiles.Ask(File, "PRAGMA user_version;"));
    }

    [Fact]
    public void ANewerFileWithNoFloorAtAll_IsNotTrusted()
    {
        using (SqliteUsageHistoryStore store = Open())
        {
            store.Write([Minute(At(Noon), 1)]);
        }

        OlderHistoryFiles.Run(File, "PRAGMA user_version=9;", "DELETE FROM app_state WHERE name = 'format_floor';");

        using SqliteUsageHistoryStore newer = Open();
        Assert.Equal(HistoryFileState.NewerVersion, newer.State);
    }

    [Fact]
    public void TheLocalShare_IsKeptForRecordedMinutesOnly_AndCutToTheMinute()
    {
        long start = At(Noon) - (At(Noon) % 15);

        using SqliteUsageHistoryStore store = Open();
        store.Write([Minute(start, 1000, 100, Home), Minute(start + 1, 2000, 200, Home)]);

        IReadOnlyList<long> set = store.WriteLocalTraffic(
        [
            new LocalMinute(start, 400, 40),
            new LocalMinute(start + 1, 9999, 9999),
            new LocalMinute(start + 2, 5, 5),
        ]);

        Assert.Equal(new[] { start, start + 1 }, set.ToArray());

        UsageQuarter quarter = Assert.Single(store.ReadQuarters(start, start + 15));
        Assert.Equal(2400, quarter.LocalDown);
        Assert.Equal(240, quarter.LocalUp);
        Assert.Equal(2, quarter.LocalMinutes);
        Assert.Equal(600, quarter.InternetDown);
        Assert.Equal(60, quarter.InternetUp);

        UsageMinute row = store.ReadMinuteRows(start, start + 1)[0];
        Assert.Equal(400, row.LocalDown);

        // Set again, it replaces rather than adds.
        store.WriteLocalTraffic([new LocalMinute(start, 100, 0)]);
        Assert.Equal(2100, Assert.Single(store.ReadQuarters(start, start + 15)).LocalDown);

        UsageBucket first = store.Read(EpochMinute.ToUtc(start), EpochMinute.ToUtc(start + 15), UsageGranularity.Minute)[0];
        Assert.Equal(100, first.LocalDown);
        Assert.Equal(2100, store.Summarise(EpochMinute.ToUtc(start), EpochMinute.ToUtc(start + 15)).LocalDown);
    }

    [Fact]
    public void AMinuteWhoseLocalShareIsNotKnown_CountsWhole()
    {
        long start = At(Noon) - (At(Noon) % 15);

        using SqliteUsageHistoryStore store = Open();
        store.Write([Minute(start, 1000, 100, Home)]);

        UsageQuarter quarter = Assert.Single(store.ReadQuarters(start, start + 15));
        Assert.Equal(0, quarter.LocalMinutes);
        Assert.Equal(1100, quarter.InternetTotal);
    }

    [Fact]
    public void ReplacingAMinuteFromAFile_TakesItsLocalShareFromTheFileToo()
    {
        long start = At(Noon) - (At(Noon) % 15);

        using SqliteUsageHistoryStore store = Open();
        store.Write([Minute(start, 1000, 100, Home)]);
        store.WriteLocalTraffic([new LocalMinute(start, 500, 50)]);

        store.Replace([Minute(start, 800, 80)]);

        UsageQuarter quarter = Assert.Single(store.ReadQuarters(start, start + 15));
        Assert.Equal(0, quarter.LocalMinutes);
        Assert.Equal(880, quarter.InternetTotal);
        Assert.Equal(Home, store.ReadMinuteRows(start, start + 1)[0].Network);
    }

    [Fact]
    public void Notes_AreAddedChangedReadAndRemoved()
    {
        using SqliteUsageHistoryStore store = Open();
        int changed = 0;
        store.NotesChanged += (_, _) => changed++;

        HistoryNote note = store.AddNote(Noon, Noon.AddHours(2), "  game\nupdate  ");
        Assert.Equal("game update", note.Text);
        Assert.Equal(Noon, note.From);

        Assert.True(store.UpdateNote(note.Id, Noon.AddHours(1), Noon.AddHours(3), "ISP outage"));
        HistoryNote read = Assert.Single(store.ReadNotes(Noon.AddHours(2), Noon.AddHours(2).AddMinutes(1)));
        Assert.Equal("ISP outage", read.Text);
        Assert.Empty(store.ReadNotes(Noon, Noon.AddHours(1)));

        Assert.True(store.RemoveNote(note.Id));
        Assert.False(store.RemoveNote(note.Id));
        Assert.Equal(3, changed);

        Assert.Throws<ArgumentException>(() => store.AddNote(Noon, Noon.AddHours(1), "   "));
        Assert.Throws<ArgumentException>(() => store.AddNote(Noon, Noon, "empty stretch"));
    }

    [Fact]
    public void ANoteLongerThanItMayBe_IsCut()
    {
        string text = new('x', HistoryNote.LongestText + 50);
        Assert.Equal(HistoryNote.LongestText, HistoryNote.Clean(text)!.Length);
        Assert.Null(HistoryNote.Clean("\r\n\t "));
    }

    [Fact]
    public void Erasing_TakesNotesInside_TrimsOnesReachingIn_AndLeavesOnesAcross()
    {
        using SqliteUsageHistoryStore store = Open();

        HistoryNote inside = store.AddNote(Noon.AddHours(1), Noon.AddHours(2), "inside");
        HistoryNote fromLeft = store.AddNote(Noon, Noon.AddHours(1.5), "from the left");
        HistoryNote toRight = store.AddNote(Noon.AddHours(1.5), Noon.AddHours(4), "to the right");
        HistoryNote across = store.AddNote(Noon.AddHours(-1), Noon.AddHours(5), "across");

        store.Delete(Noon.AddHours(1), Noon.AddHours(3));

        IReadOnlyList<HistoryNote> left = store.ReadNotes(Noon.AddDays(-1), Noon.AddDays(1));
        Assert.DoesNotContain(left, note => note.Id == inside.Id);
        Assert.Equal(Noon.AddHours(1), Assert.Single(left, note => note.Id == fromLeft.Id).Until);
        Assert.Equal(Noon.AddHours(3), Assert.Single(left, note => note.Id == toRight.Id).From);

        HistoryNote kept = Assert.Single(left, note => note.Id == across.Id);
        Assert.Equal(Noon.AddHours(-1), kept.From);
        Assert.Equal(Noon.AddHours(5), kept.Until);
    }

    [Fact]
    public void Marks_AndPlanLevels_AreKept()
    {
        using SqliteUsageHistoryStore store = Open();

        Assert.Null(store.ReadMark("x"));
        store.WriteMark("x", 5);
        store.WriteMark("x", 6);
        Assert.Equal(6, store.ReadMark("x"));

        Assert.True(store.MarkCrossed("plan", 100, 80, 120));
        Assert.False(store.MarkCrossed("plan", 100, 80, 130));
        Assert.True(store.MarkCrossed("plan", 200, 80, 220));
        Assert.Equal(new HashSet<int> { 80 }, store.ReadCrossedLevels("plan", 100));
    }

    [Fact]
    public void ABackup_IsOneWholeFileWithTheSameHistory()
    {
        long start = At(Noon);
        string copy = Path.Combine(_directory, "copy", "Windsock-copy.db");

        using (SqliteUsageHistoryStore store = Open())
        {
            store.Write([Minute(start, 1000, 0, Home), Minute(start + 1, 2000, 0, Phone)]);
            store.AddNote(Noon, Noon.AddHours(1), "kept in the copy");
            store.BackupTo(copy);
        }

        Assert.True(System.IO.File.Exists(copy));
        Assert.False(System.IO.File.Exists(copy + "-wal"));
        Assert.Equal("delete", OlderHistoryFiles.Ask(copy, "PRAGMA journal_mode;"));

        using SqliteUsageHistoryStore restored = new(copy, new ManualClock(Noon.AddDays(1)));
        Assert.Equal(3000, restored.Summarise(Noon, Noon.AddHours(1)).BytesTotal);
        Assert.Equal(2, restored.ReadNetworks().Count);
        Assert.Single(restored.ReadNotes(Noon, Noon.AddHours(1)));
    }

    [Fact]
    public void ImportingIntoAnErasedStretch_IsAllowed_AndCollectionStillKeepsOut()
    {
        long start = At(Noon);

        using SqliteUsageHistoryStore store = Open();
        store.Write([Minute(start, 1000, 0, Home)]);
        store.Delete(Noon, Noon.AddMinutes(10));

        // A person importing a file over time they erased means it.
        Assert.Equal(1, store.Merge([Minute(start, 700, 0, Phone)]));
        Assert.Equal(700, store.ReadQuarters(start, start + 10).Sum(quarter => quarter.BytesTotal));
        Assert.Equal(Phone.Key, Assert.Single(store.ReadNetworks()).Key);

        // Windows' figures for that time, read by collection, still stay out.
        store.WriteAppUsage([new UsageWindow(start, 1)], [new AppUsageSlice(start, 1, "chrome", 5, 5)], new AppUsagePosition(start + 1, start + 1));
        Assert.Empty(store.ReadAppSlices(start, start + 1));
        Assert.Equal(0L, OlderHistoryFiles.Ask(File, "SELECT COUNT(*) FROM erase_pending;"));
    }

    [Fact]
    public async Task ABackupTakenWhileTheFileIsVacuumed_WaitsItsTurn_AndIsWhole()
    {
        long start = At(Noon);
        List<UsageMinute> minutes = [.. Enumerable.Range(0, 20_000).Select(each => Minute(start + each, 1000 + each, 10, Home))];

        using SqliteUsageHistoryStore store = Open();
        store.Write(minutes);
        store.Delete(EpochMinute.ToUtc(start + 5000), EpochMinute.ToUtc(start + 15_000));

        string copy = Path.Combine(_directory, "during-vacuum.db");
        CancellationToken cancel = TestContext.Current.CancellationToken;
        await Task.WhenAll(Task.Run(store.Reclaim, cancel), Task.Run(() => store.BackupTo(copy), cancel));

        using SqliteUsageHistoryStore restored = new(copy, new ManualClock(Noon.AddDays(1)));
        Assert.Equal(10_000, restored.Summarise(EpochMinute.ToUtc(start), EpochMinute.ToUtc(start + 20_000)).Minutes);
    }

    [Fact]
    public void TheRunsOfMinutes_AreFoundForSettling()
    {
        Assert.Equal(
            new[] { (1L, 4L), (7L, 8L), (10L, 12L) },
            SqliteUsageHistoryStore.Runs([1, 2, 3, 7, 10, 11]).ToArray());
        Assert.Empty(SqliteUsageHistoryStore.Runs([]));
    }

    /// <summary>Checks the quarter totals against the minutes over many ranges, edges included.</summary>
    private static void AgreeEverywhere(SqliteUsageHistoryStore store, long from, long until)
    {
        IReadOnlyList<UsageMinute> minutes = store.ReadMinuteRows(from, until);
        IReadOnlyDictionary<string, long> ids = store.ReadNetworkIds();

        for (long start = from; start < until; start += 37)
        {
            long end = Math.Min(until, start + 211);
            IReadOnlyList<UsageQuarter> quarters = store.ReadQuarters(start, end);
            List<UsageMinute> inside = [.. minutes.Where(row => row.Minute >= start && row.Minute < end)];

            Assert.Equal(inside.Sum(row => row.BytesDown), quarters.Sum(quarter => quarter.BytesDown));
            Assert.Equal(inside.Sum(row => row.BytesUp), quarters.Sum(quarter => quarter.BytesUp));
            Assert.Equal(inside.Count, quarters.Sum(quarter => quarter.Minutes));
            Assert.Equal(inside.Select(row => row.PeakDown).DefaultIfEmpty(0).Max(), quarters.Select(quarter => quarter.PeakDown).DefaultIfEmpty(0).Max());

            foreach (IGrouping<long, UsageMinute> network in inside.GroupBy(row => row.Network is { } tag ? ids[tag.Key] : 0))
            {
                Assert.Equal(network.Sum(row => row.BytesTotal), quarters.Where(quarter => quarter.NetworkId == network.Key).Sum(quarter => quarter.BytesTotal));
            }
        }
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_directory))
            {
                Directory.Delete(_directory, recursive: true);
            }
        }
        catch (IOException)
        {
        }
    }

    private sealed class ManualClock(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now.ToUniversalTime();
    }
}
