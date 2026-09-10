using Windsock.Core.History;
using Xunit;

namespace Windsock.Core.Tests;

public sealed class SqliteUsageHistoryStoreTests : IDisposable
{
    private readonly string _directory;
    private readonly string _file;

    public SqliteUsageHistoryStoreTests()
    {
        _directory = Path.Combine(Path.GetTempPath(), "Windsock-tests", Guid.NewGuid().ToString("N"));
        _file = Path.Combine(_directory, "Windsock.db");
    }
    private static DateTimeOffset Noon { get; } = new DateTimeOffset(
        new DateTime(2026, 3, 14, 12, 0, 0, DateTimeKind.Local));

    private static long MinuteOf(DateTimeOffset moment) => EpochMinute.From(moment);

    [Fact]
    public void WrittenMinutes_ComeBack()
    {
        using var store = new SqliteUsageHistoryStore(_file);

        store.Write([new UsageMinute(MinuteOf(Noon), 1000, 200, 400, 90)]);

        UsageBucket row = Assert.Single(
            store.Read(Noon, Noon.AddMinutes(1), UsageGranularity.Minute));

        Assert.Equal(1000, row.BytesDown);
        Assert.Equal(200, row.BytesUp);
        Assert.Equal(400, row.PeakDown);
        Assert.Equal(90, row.PeakUp);
        Assert.Equal(1200, row.BytesTotal);
    }

    [Fact]
    public void TheSameMinuteWrittenTwice_AddsUp()
    {
        using var store = new SqliteUsageHistoryStore(_file);
        long minute = MinuteOf(Noon);
        store.Write([new UsageMinute(minute, 1000, 100, 500, 50)]);
        store.Write([new UsageMinute(minute, 400, 20, 200, 10)]);

        UsageBucket row = Assert.Single(
            store.Read(Noon, Noon.AddMinutes(1), UsageGranularity.Minute));

        Assert.Equal(1400, row.BytesDown);
        Assert.Equal(120, row.BytesUp);
        Assert.Equal(500, row.PeakDown);
        Assert.Equal(50, row.PeakUp);
    }

    [Fact]
    public void Merge_KeepsWhatIsAlreadyRecorded()
    {
        using var store = new SqliteUsageHistoryStore(_file);
        long minute = MinuteOf(Noon);

        store.Write([new UsageMinute(minute, 1000, 100, 500, 50)]);
        int written = store.Merge([new UsageMinute(minute, 7777, 8888, 1, 1)]);

        UsageBucket row = Assert.Single(
            store.Read(Noon, Noon.AddMinutes(1), UsageGranularity.Minute));

        Assert.Equal(0, written);
        Assert.Equal(1000, row.BytesDown);
    }

    [Fact]
    public void ImportingTheSameFileTwice_ChangesNothingTheSecondTime()
    {
        using var store = new SqliteUsageHistoryStore(_file);

        UsageMinute[] file =
        [
            new(MinuteOf(Noon), 1000, 100, 500, 50),
            new(MinuteOf(Noon.AddMinutes(1)), 2000, 200, 600, 60),
        ];

        Assert.Equal(2, store.Merge(file));
        Assert.Equal(0, store.Merge(file));

        // The whole point: a double-click on Import does not double the month.
        Assert.Equal(3300, store.Summarise(Noon, Noon.AddHours(1)).BytesTotal);
    }

    [Fact]
    public void Replace_TakesTheFileOverWhatWasRecorded()
    {
        using var store = new SqliteUsageHistoryStore(_file);
        long minute = MinuteOf(Noon);

        store.Write([new UsageMinute(minute, 1000, 100, 500, 50)]);
        int written = store.Replace([new UsageMinute(minute, 7, 8, 9, 10)]);

        UsageBucket row = Assert.Single(
            store.Read(Noon, Noon.AddMinutes(1), UsageGranularity.Minute));

        // Replacing, not adding: the file is being believed over the database.
        Assert.Equal(1, written);
        Assert.Equal(7, row.BytesDown);
        Assert.Equal(8, row.BytesUp);
        Assert.Equal(9, row.PeakDown);
        Assert.Equal(10, row.PeakUp);
    }

    [Fact]
    public void Replace_StillAddsMinutesThatAreNew()
    {
        using var store = new SqliteUsageHistoryStore(_file);

        store.Write([new UsageMinute(MinuteOf(Noon), 1000, 100, 1, 1)]);

        Assert.Equal(2, store.Replace(
        [
            new UsageMinute(MinuteOf(Noon), 5, 5, 1, 1),
            new UsageMinute(MinuteOf(Noon.AddMinutes(1)), 20, 20, 1, 1),
        ]));

        Assert.Equal(50, store.Summarise(Noon, Noon.AddHours(1)).BytesTotal);
    }

    [Fact]
    public void CountExisting_ReportsTheOverlapAndNothingElse()
    {
        using var store = new SqliteUsageHistoryStore(_file);

        store.Write(
        [
            new UsageMinute(MinuteOf(Noon), 1, 1, 1, 1),
            new UsageMinute(MinuteOf(Noon.AddMinutes(2)), 1, 1, 1, 1),
        ]);
        int overlap = store.CountExisting(
        [
            new UsageMinute(MinuteOf(Noon), 9, 9, 9, 9),
            new UsageMinute(MinuteOf(Noon.AddMinutes(1)), 9, 9, 9, 9),
            new UsageMinute(MinuteOf(Noon.AddMinutes(2)), 9, 9, 9, 9),
            new UsageMinute(MinuteOf(Noon.AddDays(5)), 9, 9, 9, 9),
        ]);

        Assert.Equal(2, overlap);
    }

    [Fact]
    public void CountExisting_IsZeroWhenNothingIsRecordedYet()
    {
        using var store = new SqliteUsageHistoryStore(_file);

        Assert.Equal(0, store.CountExisting([new UsageMinute(MinuteOf(Noon), 1, 1, 1, 1)]));
        Assert.Equal(0, store.CountExisting([]));
    }

    [Fact]
    public void Read_GroupsIntoLocalHours()
    {
        using var store = new SqliteUsageHistoryStore(_file);

        store.Write(
        [
            new UsageMinute(MinuteOf(Noon), 100, 10, 60, 6),
            new UsageMinute(MinuteOf(Noon.AddMinutes(30)), 200, 20, 90, 9),
            new UsageMinute(MinuteOf(Noon.AddHours(1)), 400, 40, 30, 3),
        ]);

        IReadOnlyList<UsageBucket> hours =
            store.Read(Noon, Noon.AddHours(2), UsageGranularity.Hour);

        Assert.Equal(2, hours.Count);
        Assert.Equal(300, hours[0].BytesDown);
        Assert.Equal(2, hours[0].Minutes);
        Assert.Equal(90, hours[0].PeakDown);
        Assert.Equal(Noon, hours[0].Start);
        Assert.Equal(400, hours[1].BytesDown);
        Assert.Equal(Noon.AddHours(1), hours[1].Start);
    }

    [Fact]
    public void Read_GroupsIntoLocalDays()
    {
        using var store = new SqliteUsageHistoryStore(_file);

        store.Write(
        [
            new UsageMinute(MinuteOf(Noon), 100, 10, 60, 6),
            new UsageMinute(MinuteOf(Noon.AddHours(6)), 200, 20, 70, 7),
            new UsageMinute(MinuteOf(Noon.AddDays(1)), 400, 40, 80, 8),
        ]);

        IReadOnlyList<UsageBucket> days =
            store.Read(Noon.AddHours(-12), Noon.AddDays(2), UsageGranularity.Day);

        Assert.Equal(2, days.Count);
        Assert.Equal(300, days[0].BytesDown);
        Assert.Equal(400, days[1].BytesDown);

        // Days start at local midnight, not at the first reading in them.
        Assert.Equal(0, days[0].Start.Hour);
        Assert.Equal(0, days[0].Start.Minute);
    }

    [Fact]
    public void TheEndOfARange_IsExcluded()
    {
        using var store = new SqliteUsageHistoryStore(_file);

        store.Write(
        [
            new UsageMinute(MinuteOf(Noon), 100, 10, 1, 1),
            new UsageMinute(MinuteOf(Noon.AddMinutes(1)), 200, 20, 1, 1),
        ]);

        // Half-open, so consecutive ranges tile without counting the seam twice.
        Assert.Equal(110, store.Summarise(Noon, Noon.AddMinutes(1)).BytesTotal);
        Assert.Equal(330, store.Summarise(Noon, Noon.AddMinutes(2)).BytesTotal);
    }

    [Fact]
    public void Summarise_TakesThePeakAcrossTheRange()
    {
        using var store = new SqliteUsageHistoryStore(_file);

        store.Write(
        [
            new UsageMinute(MinuteOf(Noon), 100, 10, 500, 50),
            new UsageMinute(MinuteOf(Noon.AddMinutes(1)), 200, 20, 900, 20),
        ]);

        UsageSummary summary = store.Summarise(Noon, Noon.AddHours(1));

        Assert.Equal(300, summary.BytesDown);
        Assert.Equal(30, summary.BytesUp);
        Assert.Equal(900, summary.PeakDown);
        Assert.Equal(50, summary.PeakUp);
        Assert.Equal(2, summary.Minutes);
    }

    [Fact]
    public void AnEmptyRange_SummarisesToZeroRatherThanNothing()
    {
        using var store = new SqliteUsageHistoryStore(_file);

        UsageSummary summary = store.Summarise(Noon, Noon.AddHours(1));

        Assert.Equal(0, summary.BytesTotal);
        Assert.Equal(0, summary.Minutes);
    }

    [Fact]
    public void Extent_ReportsTheOldestAndNewestMinutes()
    {
        using var store = new SqliteUsageHistoryStore(_file);

        Assert.False(store.Extent().HasData);

        store.Write(
        [
            new UsageMinute(MinuteOf(Noon), 1, 1, 1, 1),
            new UsageMinute(MinuteOf(Noon.AddDays(3)), 1, 1, 1, 1),
        ]);

        UsageExtent extent = store.Extent();

        Assert.True(extent.HasData);
        Assert.Equal(Noon, extent.First);
        Assert.Equal(Noon.AddDays(3), extent.Last);
    }

    [Fact]
    public void Coverage_OnlyEverExtends()
    {
        using var store = new SqliteUsageHistoryStore(_file);
        long start = MinuteOf(Noon);

        store.WriteCoverage(start, start + 5);
        store.WriteCoverage(start, start + 20);
        store.WriteCoverage(start, start + 10);

        CoverageWindow window = Assert.Single(
            store.ReadCoverage(Noon, Noon.AddHours(1)));

        Assert.Equal(Noon, window.Start);
        Assert.Equal(Noon.AddMinutes(20), window.End);
        Assert.Equal(TimeSpan.FromMinutes(21), window.Duration);
    }

    [Fact]
    public void Coverage_IsFoundWhenItOverlapsTheRange()
    {
        using var store = new SqliteUsageHistoryStore(_file);
        long start = MinuteOf(Noon);

        store.WriteCoverage(start, start + 120);

        // A run that began before the window being asked about still covers it.
        Assert.Single(store.ReadCoverage(Noon.AddMinutes(30), Noon.AddMinutes(40)));
        Assert.Empty(store.ReadCoverage(Noon.AddHours(5), Noon.AddHours(6)));
    }

    /// <summary>
    /// Deleting everything is not deleting a range that happens to cover
    /// everything: the record of having watched goes too. Somebody asking for
    /// their history to be gone is not helped by a note saying Windsock was
    /// watching them from March to September.
    /// </summary>
    [Fact]
    public void RemoveEverything_TakesTheReadingsAndTheRecordOfWatching()
    {
        using var store = new SqliteUsageHistoryStore(_file);

        store.Write(
        [
            new UsageMinute(MinuteOf(Noon), 200, 20, 1, 1),
            new UsageMinute(MinuteOf(Noon.AddMinutes(1)), 400, 40, 1, 1),
        ]);

        store.WriteCoverage(MinuteOf(Noon), MinuteOf(Noon.AddHours(2)));

        Assert.True(store.Extent().HasData);
        Assert.NotEmpty(store.ReadCoverage(Noon, Noon.AddHours(2)));

        int removed = store.RemoveEverything();

        Assert.Equal(2, removed);
        Assert.False(store.Extent().HasData);
        Assert.Equal(0, store.Summarise(Noon.AddYears(-1), Noon.AddYears(1)).BytesTotal);
        Assert.Empty(store.ReadCoverage(Noon.AddYears(-1), Noon.AddYears(1)));
    }

    /// <summary>
    /// Anything showing history has to be told, and told by something other
    /// than a new reading arriving - a settled week nobody is recording into
    /// would otherwise sit on screen long after it was deleted.
    /// </summary>
    [Fact]
    public void RemoveEverything_SaysSo()
    {
        using var store = new SqliteUsageHistoryStore(_file);

        store.Write([new UsageMinute(MinuteOf(Noon), 200, 20, 1, 1)]);

        int told = 0;
        store.Erased += (_, _) => told++;

        store.RemoveEverything();

        Assert.Equal(1, told);
    }

    /// <summary>
    /// And is told after the deletion rather than during it, so a listener that
    /// goes straight back to the store is answered rather than deadlocked.
    /// </summary>
    [Fact]
    public void WhenItSaysSo_TheHistoryIsAlreadyGone()
    {
        using var store = new SqliteUsageHistoryStore(_file);

        store.Write([new UsageMinute(MinuteOf(Noon), 200, 20, 1, 1)]);

        bool anythingLeft = true;
        store.Erased += (_, _) => anythingLeft = store.Extent().HasData;

        store.RemoveEverything();

        Assert.False(anythingLeft);
    }

    [Fact]
    public void RemoveEverything_OnAnEmptyStoreIsHarmless()
    {
        using var store = new SqliteUsageHistoryStore(_file);

        Assert.Equal(0, store.RemoveEverything());
        Assert.False(store.Extent().HasData);
    }

    /// <summary>
    /// The space goes back to the disk. A history somebody asked to be rid of
    /// is not gone while its rows are still sitting in the file's free pages.
    /// </summary>
    [Fact]
    public void RemoveEverything_HandsTheSpaceBack()
    {
        using (var store = new SqliteUsageHistoryStore(_file))
        {
            List<UsageMinute> plenty = [];

            for (int each = 0; each < 20_000; each++)
            {
                plenty.Add(new UsageMinute(MinuteOf(Noon.AddMinutes(each)), 1000, 100, 9, 9));
            }

            store.Write(plenty);
        }

        long full = new FileInfo(_file).Length;

        using (var store = new SqliteUsageHistoryStore(_file))
        {
            store.RemoveEverything();
        }

        long after = new FileInfo(_file).Length;

        Assert.True(after < full / 2, $"was {full} bytes, still {after} after");
    }

    [Fact]
    public void Delete_RemovesOnlyTheRangeAskedFor()
    {
        using var store = new SqliteUsageHistoryStore(_file);

        store.Write(
        [
            new UsageMinute(MinuteOf(Noon.AddMinutes(-1)), 100, 10, 1, 1),
            new UsageMinute(MinuteOf(Noon), 200, 20, 1, 1),
            new UsageMinute(MinuteOf(Noon.AddMinutes(1)), 400, 40, 1, 1),
        ]);

        int removed = store.Delete(Noon, Noon.AddMinutes(1));

        Assert.Equal(1, removed);

        // The half-open range takes the minute it names and nothing either side.
        Assert.Equal(110, store.Summarise(Noon.AddMinutes(-1), Noon).BytesTotal);
        Assert.Equal(0, store.Summarise(Noon, Noon.AddMinutes(1)).BytesTotal);
        Assert.Equal(440, store.Summarise(Noon.AddMinutes(1), Noon.AddMinutes(2)).BytesTotal);
    }

    [Fact]
    public void Delete_TakesAWholeHourWhenAskedForOne()
    {
        using var store = new SqliteUsageHistoryStore(_file);

        store.Write(
        [
            new UsageMinute(MinuteOf(Noon), 100, 10, 1, 1),
            new UsageMinute(MinuteOf(Noon.AddMinutes(30)), 200, 20, 1, 1),
            new UsageMinute(MinuteOf(Noon.AddHours(1)), 400, 40, 1, 1),
        ]);
        Assert.Equal(2, store.Delete(Noon, Noon.AddHours(1)));
        Assert.Equal(440, store.Summarise(Noon, Noon.AddHours(2)).BytesTotal);
    }

    [Fact]
    public void Delete_LeavesCoverageAlone()
    {
        using var store = new SqliteUsageHistoryStore(_file);
        long start = MinuteOf(Noon);

        store.Write([new UsageMinute(start, 100, 10, 1, 1)]);
        store.WriteCoverage(start, start + 10);
        store.Delete(Noon, Noon.AddHours(1));
        Assert.Single(store.ReadCoverage(Noon, Noon.AddHours(1)));
    }

    [Fact]
    public void Delete_OnAnEmptyRangeRemovesNothing()
    {
        using var store = new SqliteUsageHistoryStore(_file);

        Assert.Equal(0, store.Delete(Noon, Noon.AddDays(1)));
    }

    [Fact]
    public void HistorySurvivesReopening()
    {
        using (var first = new SqliteUsageHistoryStore(_file))
        {
            first.Write([new UsageMinute(MinuteOf(Noon), 4096, 512, 100, 10)]);
        }

        using var second = new SqliteUsageHistoryStore(_file);

        Assert.Equal(4608, second.Summarise(Noon, Noon.AddHours(1)).BytesTotal);
    }

    [Fact]
    public void AFileThatIsNotADatabase_IsSetAsideRatherThanFatal()
    {
        Directory.CreateDirectory(_directory);
        File.WriteAllText(_file, "this is not a database");

        using var store = new SqliteUsageHistoryStore(_file);
        Assert.True(store.RecoveredFromCorruption);
        Assert.False(store.Extent().HasData);

        store.Write([new UsageMinute(MinuteOf(Noon), 10, 10, 10, 10)]);

        Assert.Equal(20, store.Summarise(Noon, Noon.AddHours(1)).BytesTotal);
        Assert.NotEmpty(Directory.GetFiles(_directory, "*.corrupt"));
    }

    [Fact]
    public void ADatabaseThatCannotBeOpened_IsNotMistakenForACorruptOne()
    {
        Directory.CreateDirectory(_file);

        Assert.Throws<Microsoft.Data.Sqlite.SqliteException>(
            () => new SqliteUsageHistoryStore(_file));

        Assert.Empty(Directory.GetFiles(_directory, "*.corrupt*"));
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
            // A temp directory left behind is not worth failing a test over.
        }
    }
}
