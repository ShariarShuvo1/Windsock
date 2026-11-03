using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using Windsock.Core.Apps;
using Windsock.Core.Connectivity;
using Windsock.Core.History;
using Windsock.Core.Networking;
using Windsock.Core.Notifications;
using Windsock.Core.Reports;
using Windsock.Core.Settings;
using Xunit;

namespace Windsock.Core.Tests;

/// <summary>Reports, comparisons, the calendar, the week heatmap, lifetime, one app over time, and measuring.</summary>
public sealed class UsageReportTests : IDisposable
{
    private const long MB = 1024L * 1024;

    private static readonly TimeZoneInfo Utc = TimeZoneInfo.Utc;
    private static readonly TimeZoneInfo Berlin = TimeZoneInfo.FindSystemTimeZoneById("W. Europe Standard Time");
    private static readonly NetworkTag Home = new("home-id", "Home", NetworkKind.Ethernet, false);
    private static readonly NetworkTag Phone = new("phone-id", "Phone", NetworkKind.WiFi, true);

    private readonly string _directory = Path.Combine(Path.GetTempPath(), "Windsock-tests", Guid.NewGuid().ToString("N"));

    private static DateTimeOffset On(int year, int month, int day, int hour = 0, int minute = 0) =>
        new(year, month, day, hour, minute, 0, TimeSpan.Zero);

    private static long At(DateTimeOffset moment) => EpochMinute.From(moment);

    // ---- Periods and the clock ---------------------------------------------

    [Fact]
    public void Periods_AreDaysWeeksMonthsAndYearsOnTheLocalClock()
    {
        ReportPeriod month = ReportPeriod.Month(2026, 10, Utc);
        Assert.Equal(On(2026, 10, 1), month.From);
        Assert.Equal(On(2026, 11, 1), month.Until);
        Assert.Equal(On(2026, 9, 1), month.Previous(Utc).From);
        Assert.Equal(On(2025, 10, 1), month.SameLastYear(Utc).From);
        Assert.Equal(CalendarStep.Day, month.Step);

        ReportPeriod week = ReportPeriod.Week(new DateOnly(2026, 10, 7), DayOfWeek.Monday, Utc);
        Assert.Equal(DayOfWeek.Monday, week.First.DayOfWeek);
        Assert.Equal(TimeSpan.FromDays(7), week.Length);
        Assert.Equal(week.First.AddDays(-7), week.Previous(Utc).First);

        Assert.Equal(CalendarStep.Hour, ReportPeriod.Day(new DateOnly(2026, 10, 7), Utc).Step);
        Assert.Equal(CalendarStep.Month, ReportPeriod.Year(2026, Utc).Step);
        Assert.Equal(TimeSpan.FromHours(23), ReportPeriod.Day(new DateOnly(2026, 3, 29), Berlin).Length);
        Assert.Equal(TimeSpan.FromDays(366), ReportPeriod.Year(2028, Utc).Length);
        Assert.Throws<ArgumentException>(() => ReportPeriod.Between(On(2026, 1, 2), On(2026, 1, 1), Utc));
    }

    [Fact]
    public void ATimeTheClockSkips_IsTakenAfterTheGap_AndATimeItRepeats_AtItsFirst()
    {
        Assert.Equal(new DateTimeOffset(2026, 3, 29, 1, 30, 0, TimeSpan.Zero), LocalCalendar.At(new DateTime(2026, 3, 29, 2, 30, 0), Berlin));
        Assert.Equal(new DateTimeOffset(2026, 10, 25, 0, 30, 0, TimeSpan.Zero), LocalCalendar.At(new DateTime(2026, 10, 25, 2, 30, 0), Berlin));
    }

    [Fact]
    public void AQuarterHourOffset_StillBucketsByTheLocalDay()
    {
        TimeZoneInfo nepal = TimeZoneInfo.FindSystemTimeZoneById("Nepal Standard Time");

        // 18:10 UTC is 23:55 in Kathmandu; 18:15 UTC is the next day's 00:00.
        Assert.Equal(new DateTime(2026, 10, 5), LocalCalendar.BucketStart(At(On(2026, 10, 5, 18, 10)), CalendarStep.Day, nepal).DateTime);
        Assert.Equal(new DateTime(2026, 10, 6), LocalCalendar.BucketStart(At(On(2026, 10, 5, 18, 15)), CalendarStep.Day, nepal).DateTime);
        Assert.Equal(TimeSpan.FromHours(1), LocalCalendar.NextBucket(LocalCalendar.At(new DateTime(2026, 3, 29, 1, 0, 0), Berlin), CalendarStep.Hour, Berlin) - LocalCalendar.At(new DateTime(2026, 3, 29, 1, 0, 0), Berlin));
    }

    // ---- Reports -----------------------------------------------------------

    [Fact]
    public void AMonthsReport_HasItsTotalsPeaksBusiestTimesNetworksAndChange()
    {
        using SqliteUsageHistoryStore store = Store();
        store.Write(
        [
            Minute(On(2026, 9, 3, 10), 100 * MB, Home),
            Minute(On(2026, 10, 2, 9), 10 * MB, Home),
            Minute(On(2026, 10, 2, 9, 1), 20 * MB, Phone, peak: 9000),
            Minute(On(2026, 10, 17, 21), 300 * MB, Home, peak: 5000),
            Minute(On(2026, 10, 17, 22), 5 * MB, null),
        ]);

        UsageReports reports = Reports(store, On(2026, 11, 5));
        PeriodReport report = reports.Report(ReportPeriod.Month(2026, 10, Utc));

        Assert.Equal(335 * MB, report.Totals.All);
        Assert.Equal(4, report.Totals.RecordedMinutes);
        Assert.False(report.IsUnderWay);
        Assert.Equal(31, report.Series.Count);
        Assert.Equal(new DateTimeOffset(2026, 10, 17, 0, 0, 0, TimeSpan.Zero), report.BusiestDay!.Value.Start);
        Assert.Equal(new DateTimeOffset(2026, 10, 17, 21, 0, 0, TimeSpan.Zero), report.BusiestHour!.Value.Start);
        Assert.Equal(9000, report.PeakDown!.Value.BytesPerSecond);
        Assert.Equal(On(2026, 10, 2, 9, 1), report.PeakDown.Value.At);
        Assert.Equal(300 * MB, report.HourOfDay[21]);
        Assert.Equal(305 * MB, report.DayOfWeek[(int)new DateTime(2026, 10, 17).DayOfWeek]);

        Assert.Equal(3, report.Networks.Count);
        Assert.Equal(Home.Key, report.Networks[0].Key);
        Assert.Equal(310 * MB, report.Networks[0].Total);
        Assert.Contains(report.Networks, network => !network.IsKnown && network.Total == 5 * MB);

        Assert.NotNull(report.Change);
        Assert.Equal(235 * MB, report.Change.Difference);
        Assert.Equal(2.35, report.Change.Percent!.Value, 6);
        Assert.False(report.Change.SameElapsed);
    }

    [Fact]
    public void AMonthUnderWay_IsReportedUpToNow_AndSetAgainstTheSameDaysBefore()
    {
        using SqliteUsageHistoryStore store = Store();
        store.Write([Minute(On(2026, 9, 3), 100 * MB, Home), Minute(On(2026, 9, 20), 900 * MB, Home), Minute(On(2026, 10, 2), 50 * MB, Home)]);

        PeriodReport report = Reports(store, On(2026, 10, 5, 12)).Report(ReportPeriod.Month(2026, 10, Utc));

        Assert.True(report.IsUnderWay);
        Assert.Equal(TimeSpan.FromDays(4.5), report.Elapsed);
        Assert.True(report.Change!.SameElapsed);
        Assert.Equal(100 * MB, report.Change.Before.All);
        Assert.Equal(-50 * MB, report.Change.Difference);
        Assert.Equal(50 * MB / 4.5, report.DailyAverage, 0);
    }

    [Fact]
    public void AReport_OnInternetBytesAlone_LeavesOutWhatStayedLocal()
    {
        using SqliteUsageHistoryStore store = Store();
        store.Write([Minute(On(2026, 10, 2, 9), 100 * MB, Home), Minute(On(2026, 10, 3, 9), 60 * MB, Home)]);
        store.WriteLocalTraffic([new LocalMinute(At(On(2026, 10, 2, 9)), 90 * MB, 0)]);

        UsageReports reports = Reports(store, On(2026, 11, 5));
        PeriodReport all = reports.Report(ReportPeriod.Month(2026, 10, Utc));
        PeriodReport internet = reports.Report(ReportPeriod.Month(2026, 10, Utc), internetOnly: true);

        Assert.Equal(160 * MB, all.Totals.All);
        Assert.Equal(70 * MB, all.Totals.Internet);
        Assert.Equal(new DateTimeOffset(2026, 10, 2, 0, 0, 0, TimeSpan.Zero), all.BusiestDay!.Value.Start);
        Assert.Equal(new DateTimeOffset(2026, 10, 3, 0, 0, 0, TimeSpan.Zero), internet.BusiestDay!.Value.Start);
        Assert.True(internet.InternetOnly);
    }

    [Fact]
    public void TwoPeriods_AreComparedStepByStep_WithTheDifferenceStated()
    {
        using SqliteUsageHistoryStore store = Store();
        store.Write([Minute(On(2026, 9, 1), 100 * MB, Home), Minute(On(2026, 10, 1), 150 * MB, Home), Minute(On(2026, 10, 31), 50 * MB, Home)]);

        PeriodComparison comparison = Reports(store, On(2026, 11, 5)).Compare(ReportPeriod.Month(2026, 9, Utc), ReportPeriod.Month(2026, 10, Utc));

        Assert.Equal(100 * MB, comparison.Difference);
        Assert.Equal(1.0, comparison.Percent);
        Assert.Equal(31, comparison.Aligned.Count);
        Assert.Null(comparison.Aligned[30].Baseline);
        Assert.Equal(150 * MB, comparison.Aligned[0].Compared!.Value.Totals.All);
        Assert.Equal(100 * MB, comparison.Aligned[0].Baseline!.Value.Totals.All);
    }

    [Fact]
    public void AMonthUnderWay_IsComparedWithTheSameStretchOfTheOneBefore()
    {
        using SqliteUsageHistoryStore store = Store();
        store.Write([Minute(On(2026, 9, 2), 100 * MB, Home), Minute(On(2026, 9, 20), 900 * MB, Home), Minute(On(2026, 10, 2), 150 * MB, Home)]);

        PeriodComparison comparison = Reports(store, On(2026, 10, 5)).Compare(ReportPeriod.Month(2026, 9, Utc), ReportPeriod.Month(2026, 10, Utc));

        Assert.True(comparison.SameElapsed);
        Assert.Equal(50 * MB, comparison.Difference);
        Assert.Equal(0.5, comparison.Percent);
        Assert.Equal(1000 * MB, comparison.Baseline.Totals.All);
        Assert.Equal(900 * MB, comparison.Aligned[19].Baseline!.Value.Totals.All);
    }

    [Fact]
    public void AYearsCalendar_HasEveryDay_ShadedByQuartiles()
    {
        using SqliteUsageHistoryStore store = Store();
        store.Write([Minute(On(2028, 1, 1), 1 * MB, Home), Minute(On(2028, 2, 29), 2 * MB, Home), Minute(On(2028, 6, 1), 3 * MB, Home), Minute(On(2028, 12, 31), 40 * MB, Home)]);

        YearCalendar calendar = Reports(store, On(2029, 1, 5)).Calendar(2028);

        Assert.Equal(366, calendar.Days.Count);
        Assert.Equal(new DateOnly(2028, 12, 31), calendar.Busiest!.Value.Date);
        Assert.Equal(46 * MB, calendar.Totals.All);
        Assert.Equal(1, calendar.Days[0].Shade);
        Assert.Equal(4, calendar.Days[^1].Shade);
        Assert.Equal(0, calendar.Days[1].Shade);
        Assert.Equal(2, calendar.Days.Single(day => day.Date == new DateOnly(2028, 2, 29)).Shade);
    }

    [Fact]
    public void TheWeekHeatmap_AveragesEachHourOfTheWeek()
    {
        using SqliteUsageHistoryStore store = Store();
        DateTimeOffset monday = On(2026, 10, 5, 10);
        Assert.Equal(DayOfWeek.Monday, monday.DayOfWeek);

        store.Write([Minute(monday, 30 * MB, Home), Minute(monday.AddMinutes(30), 30 * MB, Home), Minute(monday.AddDays(7), 60 * MB, Home), Minute(monday.AddDays(8).AddHours(5), 1 * MB, Home)]);

        WeekHeat heat = Reports(store, On(2026, 11, 1)).WeekHeat(On(2026, 10, 1), On(2026, 11, 1));

        Assert.Equal(2, heat.Hours[(int)DayOfWeek.Monday][10]);
        Assert.Equal(120 * MB, heat.Totals[(int)DayOfWeek.Monday][10]);
        Assert.Equal(60 * MB, heat.Average(DayOfWeek.Monday, 10));
        Assert.Equal(60 * MB, heat.Highest);
        Assert.Equal(0, heat.Average(DayOfWeek.Sunday, 3));
    }

    [Fact]
    public void Lifetime_GivesEverythingAndEachYear()
    {
        using SqliteUsageHistoryStore store = Store();
        store.Write([Minute(On(2025, 12, 31, 23, 59), 7 * MB, Home), Minute(On(2026, 1, 1), 3 * MB, Home), Minute(On(2026, 6, 1), 5 * MB, Phone, peak: 77)]);

        LifetimeReport lifetime = Reports(store, On(2026, 10, 5)).Lifetime();

        Assert.Equal(15 * MB, lifetime.Totals.All);
        Assert.Equal([2025, 2026], lifetime.Years.Select(year => year.Year).ToArray());
        Assert.Equal(8 * MB, lifetime.Years[1].Totals.All);
        Assert.Equal(new DateTimeOffset(2025, 12, 31, 0, 0, 0, TimeSpan.Zero), lifetime.BusiestDay!.Value.Start);
        Assert.Equal(77, lifetime.PeakDown!.Value.BytesPerSecond);
        Assert.Equal(On(2025, 12, 31, 23, 59), lifetime.First!.Value.ToUniversalTime());
    }

    [Fact]
    public void OneApp_OverTime_TakesWhatWindsockMeasuredAndWhatWindowsCountedElsewhere()
    {
        using SqliteUsageHistoryStore store = new(Path.Combine(_directory, "apps.db"), new FixedClock(On(2026, 10, 10)));
        long dayOne = At(On(2026, 10, 2, 9));
        long dayThree = At(On(2026, 10, 4, 9));

        store.WriteMeasuredMinute(dayOne, [new AppBytes("chrome", 4000, 400), new AppBytes("steam", 9, 9)]);
        store.WriteAppUsage(
            [new UsageWindow(dayThree, 60)],
            [new AppUsageSlice(dayThree, 60, "chrome", 7000, 700)],
            new AppUsagePosition(dayThree + 60, dayThree + 60));

        IReadOnlyList<AppPoint> points = Reports(store, On(2026, 10, 10)).AppOverTime("chrome", On(2026, 10, 1), On(2026, 10, 8), CalendarStep.Day);

        Assert.Equal(7, points.Count);
        Assert.Equal(4400, points[1].Total);
        Assert.Equal(4000, points[1].MeasuredDown);
        Assert.Equal(0, points[2].Total);
        Assert.Equal(7000, points[3].WindowsDown);
        Assert.Equal(0, points[3].MeasuredDown);
    }

    [Fact]
    public void AStretchDraggedAcrossAChart_IsTotalled_WhicheverWayItWasDragged()
    {
        DateTimeOffset start = On(2026, 10, 5, 9);
        UsageBucket[] buckets =
        [
            new(start, 100, 10, 50, 5, 60),
            new(start.AddHours(1), 200, 20, 70, 7, 60) { LocalDown = 150 },
            new(start.AddHours(2), 300, 30, 90, 9, 60, EstimatedDown: 1000, EstimatedMinutes: 60),
            new(start.AddHours(3), 400, 40, 10, 1, 60),
        ];

        MeasuredSpan span = UsageMeasure.Of(buckets, start.AddHours(2).AddMinutes(30), start.AddHours(1).AddMinutes(10), TimeSpan.FromHours(1));

        Assert.Equal(start.AddHours(1), span.From);
        Assert.Equal(start.AddHours(3), span.Until);
        Assert.Equal(500, span.Down);
        Assert.Equal(1000, span.EstimatedDown);
        Assert.Equal(90, span.PeakDown);
        Assert.Equal(2, span.Buckets);
        Assert.Equal(150, span.LocalDown);
        Assert.Equal(550 + 1000 - 150, span.Internet);
        Assert.Equal(550 / 7200.0, span.AverageRate, 6);

        Assert.Equal(1, UsageMeasure.Of(buckets, start.AddMinutes(5), start.AddMinutes(5)).Buckets);
    }

    [Fact]
    public void TheStopwatch_TimesADownload()
    {
        DownloadStopwatch watch = new();
        DateTimeOffset start = On(2026, 10, 5, 9);

        watch.Observe(new ThroughputDelta(start, 999, 0, 0.5));
        watch.Start(start);
        watch.Observe(new ThroughputDelta(start.AddSeconds(1), 1000, 10, 0.5));
        watch.Observe(new ThroughputDelta(start.AddSeconds(2), 3000, 10, 1));
        watch.Stop(start.AddSeconds(10));
        watch.Observe(new ThroughputDelta(start.AddSeconds(11), 5000, 0, 1));

        StopwatchReading reading = watch.Read(start.AddSeconds(30));
        Assert.Equal(TimeSpan.FromSeconds(10), reading.Elapsed);
        Assert.Equal(4000, reading.Down);
        Assert.Equal(3000, reading.PeakDown);
        Assert.Equal(400, reading.AverageDown);
        Assert.False(reading.Running);
    }

    // ---- JSON export -------------------------------------------------------

    [Fact]
    public void TheJsonExport_HoldsNetworkUseWithItsNetworks_AppsNotesAndTheMachine_ButNeverWindowsFill()
    {
        DateTimeOffset start = new(new DateTime(2026, 3, 14, 10, 0, 0, DateTimeKind.Local));

        using SqliteUsageHistoryStore store = new(Path.Combine(_directory, "json.db"), new FixedClock(start.AddDays(2)));
        store.Write([Minute(start, 1000, Home), Minute(start.AddMinutes(1), 2000, Phone), Minute(start.AddMinutes(2), 3000, null)]);
        store.WriteMeasuredMinute(At(start), [new AppBytes("chrome", 500, 50)]);
        store.WriteSystem([new SystemMinute(At(start), 2, 500, 400, 0, 0, null, 0, 0, null, 2, 1, 4, 10, 20)]);
        store.AddNote(start, start.AddHours(1), "game update");

        // Windows counted something an hour later, when Windsock was closed.
        long closed = At(start.AddHours(2));
        store.WriteAppUsage([new UsageWindow(closed, 60)], [new AppUsageSlice(closed, 60, "steam", 99_999, 0)], new AppUsagePosition(closed + 60, closed + 60));

        HistoryJson json = new(store, store, store, store, new AppHistory(store, store, store), id => id.ToUpperInvariant());
        using MemoryStream stream = new();
        HistoryExportCounts counts = json.Write(stream, start, start.AddHours(4), UsageGranularity.Hour, start.AddDays(1));

        using JsonDocument document = JsonDocument.Parse(stream.ToArray());
        JsonElement root = document.RootElement;

        Assert.Equal("windsock-history", root.GetProperty("format").GetString());
        Assert.Equal(1, root.GetProperty("version").GetInt32());
        Assert.Equal("hour", root.GetProperty("granularity").GetString());
        Assert.Equal(2, root.GetProperty("networks").GetArrayLength());

        JsonElement usage = Assert.Single(root.GetProperty("usage").EnumerateArray());
        Assert.Equal(6000, usage.GetProperty("bytesDown").GetInt64());
        Assert.Equal(3, usage.GetProperty("networks").GetArrayLength());
        Assert.Contains(usage.GetProperty("networks").EnumerateArray(), each => each.GetProperty("network").ValueKind == JsonValueKind.Null && each.GetProperty("bytesDown").GetInt64() == 3000);

        Assert.Contains(root.GetProperty("apps").EnumerateArray(), app => app.GetProperty("app").GetString() == "chrome" && app.GetProperty("measuredDown").GetInt64() == 500 && app.GetProperty("name").GetString() == "CHROME");
        Assert.Contains(root.GetProperty("apps").EnumerateArray(), app => app.GetProperty("app").GetString() == "steam" && app.GetProperty("windowsDown").GetInt64() == 99_999);
        Assert.Equal("game update", Assert.Single(root.GetProperty("notes").EnumerateArray()).GetProperty("text").GetString());
        Assert.Equal(1, root.GetProperty("machine").GetArrayLength());
        Assert.Equal(25.0, root.GetProperty("machine")[0].GetProperty("processorPercent").GetDouble());
        Assert.Equal(JsonValueKind.Null, root.GetProperty("machine")[0].GetProperty("processorCelsius").ValueKind);
        Assert.Equal(1, counts.Usage);
        Assert.Equal(2, counts.Networks);
    }

    [Fact]
    public void AJsonExportByTheMinute_NamesEachMinutesNetwork()
    {
        DateTimeOffset start = new(new DateTime(2026, 3, 14, 10, 0, 0, DateTimeKind.Local));

        using SqliteUsageHistoryStore store = Store();
        store.Write([Minute(start, 1000, Home), Minute(start.AddMinutes(1), 2000, Phone)]);

        HistoryJson json = new(store, store, store, store, new AppHistory(store, store, store), nameOf: null);
        using MemoryStream stream = new();
        json.Write(stream, start, start.AddMinutes(2), UsageGranularity.Minute, start);

        using JsonDocument document = JsonDocument.Parse(stream.ToArray());
        JsonElement[] usage = [.. document.RootElement.GetProperty("usage").EnumerateArray()];
        Assert.Equal(Home.Key, Assert.Single(usage[0].GetProperty("networks").EnumerateArray()).GetProperty("network").GetString());
        Assert.Equal(Phone.Key, Assert.Single(usage[1].GetProperty("networks").EnumerateArray()).GetProperty("network").GetString());
        Assert.Equal("hour", document.RootElement.GetProperty("appGranularity").GetString());
    }

    [Fact]
    public void CsvWithNetworksAndLocalShares_ReadsBackWhole_AndAFormulaLikeNameIsDisarmed()
    {
        NetworkTag odd = new("name:wifi:=cmd|' /c calc'!A1", "=cmd|' /c calc'!A1, \"quoted\"", NetworkKind.WiFi);
        long minute = At(On(2026, 3, 14, 12));
        UsageMinute[] rows =
        [
            new UsageMinute(minute, 100, 10, 5, 1) { Network = odd, LocalDown = 40, LocalUp = 4 },
            new UsageMinute(minute + 1, 200, 20, 6, 2),
        ];

        StringWriter writer = new();
        UsageCsv.WriteMinutes(writer, rows);
        string csv = writer.ToString();

        Assert.DoesNotContain(",=cmd", csv, StringComparison.Ordinal);
        UsageImport read = UsageCsv.Read(new StringReader(csv));

        Assert.Equal(0, read.Skipped);
        Assert.Equal(odd, read.Minutes[0].Network);
        Assert.Equal(40, read.Minutes[0].LocalDown);
        Assert.Null(read.Minutes[1].Network);
        Assert.Null(read.Minutes[1].LocalDown);
    }

    [Fact]
    public void ACsvFromBeforeNetworks_StillReads()
    {
        const string old =
            """
            utc,local,granularity,bytes_down,bytes_up,bytes_total,peak_down_bytes_per_second,peak_up_bytes_per_second,minutes
            2026-03-14T06:00:00Z,2026-03-14T12:00:00+06:00,minute,100,10,110,5,1,1
            """;

        UsageMinute minute = Assert.Single(UsageCsv.Read(new StringReader(old)).Minutes);
        Assert.Null(minute.Network);
        Assert.Equal(100, minute.BytesDown);
    }

    // ---- Scheduled export --------------------------------------------------

    [Fact]
    public void ScheduledExport_StartsWithTheLastFinishedDay_ThenCatchesUpOnDaysMissed()
    {
        string folder = Path.Combine(_directory, "exports");
        Directory.CreateDirectory(folder);
        using SqliteUsageHistoryStore store = Store();
        store.Write([Minute(On(2026, 10, 3, 10), 1, Home), Minute(On(2026, 10, 4, 10), 2, Home), Minute(On(2026, 10, 6, 10), 3, Home)]);

        WindsockSettings settings = new();
        settings.Usage.AutoExport = new AutoExportSettings { Enabled = true, Folder = folder };
        FixedClock clock = new(On(2026, 10, 5, 9));
        ScheduledExport export = Export(settings, store, clock);

        ExportRun first = export.RunDue(clock.GetUtcNow())!;
        Assert.Equal([Path.Combine(folder, "Windsock-2026-10-04.csv")], first.Written.ToArray());
        Assert.Contains("utc,local", File.ReadAllText(first.Written[0]), StringComparison.Ordinal);

        // Off for three days.
        clock.Now = On(2026, 10, 8, 12);
        File.WriteAllText(Path.Combine(folder, "Windsock-2026-10-06.csv"), "somebody else's");
        ExportRun second = export.RunDue(clock.GetUtcNow())!;

        Assert.Empty(second.Written);
        Assert.Equal(2, second.Empty);
        Assert.Single(second.LeftAlone);
        Assert.Equal("somebody else's", File.ReadAllText(Path.Combine(folder, "Windsock-2026-10-06.csv")));

        // Not twice.
        Assert.Empty(export.RunDue(clock.GetUtcNow())!.Written);
        Assert.Null(export.LastRun!.Problem);
        Assert.Empty(Directory.GetFiles(folder, "*.partial"));
    }

    [Fact]
    public void ScheduledExport_WaitsForAFolderThatIsNotThere_WithoutLosingItsPlace()
    {
        string folder = Path.Combine(_directory, "unplugged");
        using SqliteUsageHistoryStore store = Store();
        store.Write([Minute(On(2026, 10, 4, 10), 2, Home)]);

        WindsockSettings settings = new();
        settings.Usage.AutoExport = new AutoExportSettings { Enabled = true, Folder = folder, Format = ExportFormat.Json };
        FixedClock clock = new(On(2026, 10, 5, 9));
        ScheduledExport export = Export(settings, store, clock);

        Assert.NotNull(export.RunDue(clock.GetUtcNow())!.Problem);

        Directory.CreateDirectory(folder);
        ExportRun run = export.RunDue(clock.GetUtcNow())!;
        Assert.Null(run.Problem);
        string file = Assert.Single(run.Written);
        Assert.EndsWith("Windsock-2026-10-04.json", file, StringComparison.Ordinal);
        using JsonDocument document = JsonDocument.Parse(File.ReadAllBytes(file));
        Assert.Equal("windsock-history", document.RootElement.GetProperty("format").GetString());

        settings.Usage.AutoExport.Enabled = false;
        Assert.Null(export.RunDue(clock.GetUtcNow()));
    }

    [Fact]
    public void TheExportSchedule_WaitsAnHourAfterAStretchEnds()
    {
        IReadOnlyList<ExportStretch> due = ExportSchedule.Due(ExportEvery.Day, new DateOnly(2026, 10, 3), On(2026, 10, 5, 0, 59), Utc);
        Assert.Equal([new DateOnly(2026, 10, 3)], due.Select(each => each.First).ToArray());

        Assert.Equal(2, ExportSchedule.Due(ExportEvery.Day, new DateOnly(2026, 10, 3), On(2026, 10, 5, 1), Utc).Count);
        Assert.Equal([new DateOnly(2026, 8, 1), new DateOnly(2026, 9, 1)], ExportSchedule.Due(ExportEvery.Month, new DateOnly(2026, 8, 17), On(2026, 10, 5), Utc).Select(each => each.First).ToArray());
        Assert.Equal("Windsock-2026-09.json", ExportSchedule.FileName(ExportEvery.Month, new DateOnly(2026, 9, 1), ExportFormat.Json));
    }

    // ---- Backup ------------------------------------------------------------

    [Fact]
    public void BackupCopies_AreNamedByDate_AndTheOldestGoFirst()
    {
        Assert.Equal("Windsock-2026-10-05.db", BackupRotation.NameFor(new DateOnly(2026, 10, 5)));
        Assert.Equal(new DateOnly(2026, 10, 5), BackupRotation.DateOf(@"C:\x\Windsock-2026-10-05.db"));
        Assert.Null(BackupRotation.DateOf("Windsock.db"));
        Assert.Null(BackupRotation.DateOf("Windsock-2026-13-05.db"));

        string[] files = ["Windsock-2026-09-07.db", "Windsock-2026-10-05.db", "notes.txt", "Windsock-2026-09-28.db", "Windsock-2026-09-21.db"];
        Assert.Equal(["Windsock-2026-09-21.db", "Windsock-2026-09-07.db"], BackupRotation.ToRemove(files, 2).ToArray());

        Assert.True(BackupRotation.IsDue(null, new DateOnly(2026, 10, 5)));
        Assert.False(BackupRotation.IsDue(new DateOnly(2026, 9, 29), new DateOnly(2026, 10, 5)));
        Assert.True(BackupRotation.IsDue(new DateOnly(2026, 9, 28), new DateOnly(2026, 10, 5)));
    }

    [Fact]
    public void CopiesOfErasedHistory_ThatCouldNotBeDeleted_GoAtTheNextLook_HalfMadeOnesToo()
    {
        string folder = Path.Combine(_directory, "Backups");
        using SqliteUsageHistoryStore store = Store();
        store.Write([Minute(On(2026, 10, 1), 5, Home), Minute(On(2026, 10, 2), 5, Home)]);

        WindsockSettings settings = new();
        using HistoryBackup backup = new(store, store, settings, folder, TimeProvider.System, NullLogger<HistoryBackup>.Instance);
        Assert.NotNull(backup.RunDue(On(2026, 9, 1)));

        string copy = backup.Backups()[0].Path;
        string halfMade = Path.Combine(folder, "Windsock-2026-08-01.db.tmp");
        string notOurs = Path.Combine(folder, "notes.txt");
        File.WriteAllText(halfMade, "half");
        File.WriteAllText(notOurs, "mine");

        using (FileStream held = new(copy, FileMode.Open, FileAccess.Read, FileShare.None))
        {
            store.Delete(On(2026, 10, 1), On(2026, 10, 1, 0, 1));
            Assert.True(File.Exists(copy));
        }

        Assert.False(File.Exists(halfMade));

        backup.RunDue(On(2026, 9, 2));
        Assert.False(File.Exists(copy));
        Assert.True(File.Exists(notOurs));

        // What is left is copied again at once, now without the erased minute.
        Assert.Equal([new DateOnly(2026, 9, 2)], backup.Backups().Select(each => each.Date).ToArray());
    }

    [Fact]
    public void TheWeeklyBackup_IsMadeWhenDue_KeepsAsManyAsAsked_AndGoesWithAnErase()
    {
        string folder = Path.Combine(_directory, "Backups");
        using SqliteUsageHistoryStore store = Store();
        store.Write([Minute(On(2026, 10, 1), 5, Home)]);

        WindsockSettings settings = new();
        settings.Usage.Backup.Keep = 2;
        using HistoryBackup backup = new(store, store, settings, folder, TimeProvider.System, NullLogger<HistoryBackup>.Instance);

        Assert.NotNull(backup.RunDue(On(2026, 9, 1)));
        Assert.Null(backup.RunDue(On(2026, 9, 3)));
        Assert.NotNull(backup.RunDue(On(2026, 9, 8)));
        Assert.NotNull(backup.RunDue(On(2026, 9, 15)));

        IReadOnlyList<BackupInfo> copies = backup.Backups();
        Assert.Equal([new DateOnly(2026, 9, 15), new DateOnly(2026, 9, 8)], copies.Select(copy => copy.Date).ToArray());
        Assert.Empty(Directory.GetFiles(folder, "*.tmp*"));

        store.Delete(On(2026, 10, 1), On(2026, 10, 1, 0, 1));
        Assert.Empty(backup.Backups());

        // History is empty now, so there is nothing to copy.
        Assert.Null(backup.RunDue(On(2026, 9, 16)));
        store.Write([Minute(On(2026, 10, 2), 5, Home)]);
        store.Delete(On(2026, 10, 3), On(2026, 10, 4));
        Assert.NotNull(backup.RunDue(On(2026, 9, 16)));

        settings.Usage.Backup.Enabled = false;
        Assert.Null(backup.RunDue(On(2026, 12, 1)));
    }

    // ---- Weekly digest -----------------------------------------------------

    [Fact]
    public void TheDigest_IsDueOnTheFirstDayOfTheWeekFromNine_OnceAndNotLate()
    {
        DateTimeOffset monday = On(2026, 10, 5, 8);
        Assert.Equal(DayOfWeek.Monday, monday.DayOfWeek);

        Assert.Null(WeeklyDigest.Due(null, monday, Utc, DayOfWeek.Monday));
        ReportPeriod week = WeeklyDigest.Due(null, monday.AddHours(1), Utc, DayOfWeek.Monday)!;
        Assert.Equal(new DateOnly(2026, 9, 28), week.First);

        Assert.Null(WeeklyDigest.Due(new DateOnly(2026, 9, 28), monday.AddHours(5), Utc, DayOfWeek.Monday));
        Assert.Equal(new DateOnly(2026, 9, 28), WeeklyDigest.Due(new DateOnly(2026, 9, 21), monday.AddDays(3), Utc, DayOfWeek.Monday)!.First);

        // A week whose turn passed while the PC was off is not sent the week after.
        Assert.Equal(new DateOnly(2026, 10, 5), WeeklyDigest.Due(new DateOnly(2026, 9, 21), monday.AddDays(7).AddHours(2), Utc, DayOfWeek.Monday)!.First);
    }

    [Fact]
    public void TheDigest_SaysWhatTheWeekUsed_ItsBusiestAppAndDay_AndItsOutages()
    {
        using SqliteUsageHistoryStore store = new(Path.Combine(_directory, "digest.db"), new FixedClock(On(2026, 10, 10)));
        store.Write([Minute(On(2026, 9, 22, 10), 100 * MB, Home), Minute(On(2026, 9, 30, 10), 150 * MB, Home)]);
        store.WriteAppUsage(
            [new UsageWindow(At(On(2026, 9, 30, 10)), 60)],
            [new AppUsageSlice(At(On(2026, 9, 30, 10)), 60, "steam", 90 * MB, 0)],
            new AppUsagePosition(At(On(2026, 9, 30, 11)), At(On(2026, 9, 30, 11))));

        FakeOutages outages = new([new Outage(On(2026, 10, 1, 8), On(2026, 10, 1, 8, 14), OutageCause.Unknown, "Home")]);
        UsageReports reports = new(store, store, store, new AppHistory(store, store, store), outages, new FixedClock(On(2026, 10, 5, 10)), () => Utc);
        FakeSink sink = new();
        WindsockSettings settings = new();
        settings.Usage.Digest = new DigestSettings { Enabled = true, WeekStarts = DayOfWeek.Monday };

        DigestService digest = new(settings, reports, store, sink, id => id == "steam" ? "Steam" : id, new FixedClock(On(2026, 10, 5, 10)), NullLogger<DigestService>.Instance);

        Assert.True(digest.SendIfDue(On(2026, 10, 5, 10)));
        Assert.False(digest.SendIfDue(On(2026, 10, 5, 11)));

        Notice notice = Assert.Single(sink.Sent);
        Assert.Equal(WeeklyDigest.Topic, notice.Topic);
        Assert.Equal("Last week: 150 MB", notice.Title);
        Assert.Contains("Up 50% on the week before.", notice.Message, StringComparison.Ordinal);
        Assert.Contains("Busiest app: Steam, 90.0 MB.", notice.Message, StringComparison.Ordinal);
        Assert.Contains("Busiest day: Wednesday.", notice.Message, StringComparison.Ordinal);
        Assert.Contains("1 outage, ", notice.Message, StringComparison.Ordinal);

        settings.Usage.Digest.Enabled = false;
        Assert.False(digest.SendIfDue(On(2026, 10, 12, 10)));
    }

    [Fact]
    public void AWeekWithNothingKnown_HasNoDigest()
    {
        using SqliteUsageHistoryStore store = Store();
        PeriodReport empty = Reports(store, On(2026, 10, 5)).Report(ReportPeriod.Week(new DateOnly(2026, 9, 28), DayOfWeek.Monday, Utc));
        Assert.Null(WeeklyDigest.Compose(empty, id => id));
    }

    [Theory]
    [InlineData(0, 0)]
    [InlineData(1, 1)]
    [InlineData(2, 1)]
    [InlineData(3, 2)]
    [InlineData(5, 3)]
    [InlineData(6, 3)]
    [InlineData(7, 4)]
    [InlineData(100, 4)]
    public void CalendarShades_FollowTheQuartiles(long value, int shade) =>
        Assert.Equal(shade, UsageReports.Shade(value, [1, 2, 3, 4, 5, 6, 7, 8]));

    private SqliteUsageHistoryStore Store() => new(Path.Combine(_directory, Guid.NewGuid().ToString("N") + ".db"));

    private static UsageReports Reports(SqliteUsageHistoryStore store, DateTimeOffset now) =>
        new(store, store, store, new AppHistory(store, store, store), null, new FixedClock(now), () => Utc);

    private static ScheduledExport Export(WindsockSettings settings, SqliteUsageHistoryStore store, TimeProvider clock) =>
        new(settings, store, store, store, new HistoryJson(store, store, store, store, new AppHistory(store, store, store), nameOf: null), clock, () => Utc, NullLogger<ScheduledExport>.Instance);

    private static UsageMinute Minute(DateTimeOffset at, long down, NetworkTag? network, long peak = 0) =>
        new(EpochMinute.From(at), down, 0, peak, 0) { Network = network };

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

    private sealed class FixedClock(DateTimeOffset now) : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = now;

        public override DateTimeOffset GetUtcNow() => Now.ToUniversalTime();
    }

    private sealed class FakeSink : INoticeSink
    {
        public List<Notice> Sent { get; } = [];

        public bool Send(Notice notice)
        {
            Sent.Add(notice);
            return true;
        }
    }

    private sealed class FakeOutages(IReadOnlyList<Outage> outages) : IOutageStore
    {
        public event EventHandler? Changed;

        public long Begin(Outage outage) => 0;

        public void Touch(long id, DateTimeOffset stillOut)
        {
        }

        public void Finish(long id, DateTimeOffset finishedAt, bool interrupted)
        {
        }

        public int CloseUnfinished() => 0;

        public IReadOnlyList<Outage> Read(DateTimeOffset from, DateTimeOffset until) =>
            [.. outages.Where(outage => outage.Start < until && (outage.End ?? DateTimeOffset.MaxValue) > from)];

        public int Clear()
        {
            Changed?.Invoke(this, EventArgs.Empty);
            return 0;
        }
    }
}
