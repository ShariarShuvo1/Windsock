using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Windsock.Core.Apps;
using Windsock.Core.Connectivity;
using Windsock.Core.History;
using Windsock.Core.Networking;
using Windsock.Core.Plans;
using Windsock.Core.Settings;
using Xunit;

namespace Windsock.Core.Tests;

/// <summary>Data plans: cycles, rollover, free hours, the forecast, and the monitor.</summary>
public sealed class DataPlanTests : IDisposable
{
    private const long MB = 1024L * 1024;
    private const long GB = 1024L * MB;

    private static readonly TimeZoneInfo Utc = TimeZoneInfo.Utc;
    private static readonly TimeZoneInfo Berlin = TimeZoneInfo.FindSystemTimeZoneById("W. Europe Standard Time");
    private static readonly TimeZoneInfo Beirut = TimeZoneInfo.FindSystemTimeZoneById("Middle East Standard Time");
    private static readonly TimeZoneInfo Santiago = TimeZoneInfo.FindSystemTimeZoneById("Pacific SA Standard Time");

    private static readonly NetworkTag Home = new("home-id", "Home", NetworkKind.Ethernet, false);
    private static readonly NetworkTag Phone = new("phone-id", "Phone", NetworkKind.WiFi, true);

    private readonly string _directory = Path.Combine(Path.GetTempPath(), "Windsock-tests", Guid.NewGuid().ToString("N"));

    private static DateTimeOffset On(int year, int month, int day, int hour = 0, int minute = 0) =>
        new(year, month, day, hour, minute, 0, TimeSpan.Zero);

    private static DateTimeOffset Local(TimeZoneInfo zone, int year, int month, int day, int hour = 0, int minute = 0) =>
        LocalCalendar.At(new DateTime(year, month, day, hour, minute, 0), zone);

    // ---- Cycles ------------------------------------------------------------

    [Fact]
    public void AMonthlyCycle_RunsFromItsResetDayToTheNext()
    {
        DataPlan plan = new() { ResetDay = 15 };

        BillingPeriod period = PlanCycles.Containing(plan, On(2026, 3, 14, 23, 59), Utc)!.Value;

        Assert.Equal(On(2026, 2, 15), period.Start);
        Assert.Equal(On(2026, 3, 15), period.End);
        Assert.Equal(On(2026, 3, 15), PlanCycles.Containing(plan, On(2026, 3, 15), Utc)!.Value.Start);
    }

    [Theory]
    [InlineData(2026, 2, 15, "2026-01-31", "2026-02-28")]
    [InlineData(2026, 2, 28, "2026-02-28", "2026-03-31")]
    [InlineData(2026, 3, 30, "2026-02-28", "2026-03-31")]
    [InlineData(2026, 4, 30, "2026-04-30", "2026-05-31")]
    [InlineData(2028, 2, 28, "2028-01-31", "2028-02-29")]
    [InlineData(2028, 2, 29, "2028-02-29", "2028-03-31")]
    [InlineData(2026, 12, 31, "2026-12-31", "2027-01-31")]
    public void AResetOnThe31st_FallsOnAShortMonthsLastDay_AndComesBack(int year, int month, int day, string start, string end)
    {
        DataPlan plan = new() { ResetDay = 31 };

        BillingPeriod period = PlanCycles.Containing(plan, On(year, month, day, 12), Utc)!.Value;

        Assert.Equal(DateOnly.Parse(start, System.Globalization.CultureInfo.InvariantCulture), DateOnly.FromDateTime(period.Start.UtcDateTime));
        Assert.Equal(DateOnly.Parse(end, System.Globalization.CultureInfo.InvariantCulture), DateOnly.FromDateTime(period.End.UtcDateTime));
    }

    [Fact]
    public void AWeeklyCycle_StartsOnItsDay()
    {
        DataPlan plan = new() { Cycle = PlanCycle.Weekly, ResetWeekday = DayOfWeek.Saturday };
        DateTimeOffset moment = On(2026, 10, 5, 9);

        BillingPeriod period = PlanCycles.Containing(plan, moment, Utc)!.Value;

        Assert.Equal(DayOfWeek.Saturday, period.Start.DayOfWeek);
        Assert.True(period.Contains(moment));
        Assert.Equal(TimeSpan.FromDays(7), period.Length);
    }

    [Fact]
    public void ADailyCycle_OnTheNightTheClocksGoForward_IsShort_AndTheDayAfterLong()
    {
        DataPlan plan = new() { Cycle = PlanCycle.Daily };

        Assert.Equal(TimeSpan.FromHours(23), PlanCycles.Containing(plan, Local(Berlin, 2026, 3, 29, 12), Berlin)!.Value.Length);
        Assert.Equal(TimeSpan.FromHours(25), PlanCycles.Containing(plan, Local(Berlin, 2026, 10, 25, 12), Berlin)!.Value.Length);
        Assert.Equal(TimeSpan.FromHours(24), PlanCycles.Containing(plan, Local(Berlin, 2026, 10, 26, 12), Berlin)!.Value.Length);
    }

    [Theory]
    [InlineData("Middle East Standard Time", 2026, 3, 29)]
    [InlineData("Pacific SA Standard Time", 2026, 9, 6)]
    public void ADayWhoseMidnightIsSkipped_StartsWhenTheClockFirstShowsIt(string zoneId, int year, int month, int day)
    {
        TimeZoneInfo zone = TimeZoneInfo.FindSystemTimeZoneById(zoneId);
        DataPlan plan = new() { Cycle = PlanCycle.Daily };

        BillingPeriod period = PlanCycles.Containing(plan, Local(zone, year, month, day, 12), zone)!.Value;
        DateTime wallStart = TimeZoneInfo.ConvertTime(period.Start, zone).DateTime;

        Assert.Equal(new DateTime(year, month, day, 1, 0, 0), wallStart);
        Assert.Equal(TimeSpan.FromHours(23), period.Length);

        // The day before ends exactly where this one starts.
        Assert.Equal(period.Start, PlanCycles.Before(plan, period, zone)!.Value.End);
    }

    [Fact]
    public void AMonthlyCycleResettingOnASkippedMidnight_StillJoinsUp()
    {
        DataPlan plan = new() { ResetDay = 6 };

        BillingPeriod september = PlanCycles.Containing(plan, Local(Santiago, 2026, 9, 20), Santiago)!.Value;
        BillingPeriod august = PlanCycles.Before(plan, september, Santiago)!.Value;

        Assert.Equal(august.End, september.Start);
        Assert.Equal(1, TimeZoneInfo.ConvertTime(september.Start, Santiago).Hour);
    }

    [Fact]
    public void AOneOffPlan_HasItsOwnStretch_AndNoOther()
    {
        DataPlan plan = new() { Cycle = PlanCycle.OneOff, OneOffStart = On(2026, 10, 5, 14), OneOffEnd = On(2026, 11, 4, 14) };

        BillingPeriod period = PlanCycles.Containing(plan, On(2026, 12, 1), Utc)!.Value;

        Assert.Equal(On(2026, 10, 5, 14), period.Start);
        Assert.Null(PlanCycles.Before(plan, period, Utc));
        Assert.Null(PlanCycles.Containing(new DataPlan { Cycle = PlanCycle.OneOff }, On(2026, 1, 1), Utc));
    }

    [Fact]
    public void TheCyclesSinceAPlanBegan_AreListedOldestFirst()
    {
        DataPlan plan = new() { ResetDay = 1, Since = On(2026, 1, 10) };

        IReadOnlyList<BillingPeriod> periods = PlanCycles.Through(plan, On(2026, 3, 14), Utc);

        Assert.Equal(new[] { On(2026, 1, 1), On(2026, 2, 1), On(2026, 3, 1) }, periods.Select(period => period.Start).ToArray());
        Assert.Single(PlanCycles.Through(new DataPlan(), On(2026, 3, 14), Utc));
    }

    // ---- Rollover ----------------------------------------------------------

    [Fact]
    public void RolloverForTheNextCycleOnly_IsUsedFirst_AndNeverRollsTwice()
    {
        DataPlan plan = new() { LimitBytes = 10, Rollover = RolloverRule.NextCycleOnly };

        // 4 of 10 used: 6 carried. Then 12 used: the 6 carried first, 6 of the
        // own 10, so 4 carried. Then 3 used of 4 carried: the own 10 untouched.
        Assert.Equal(new long[] { 0, 6, 4, 10 }, PlanRollover.Carried(plan, [4, 12, 3, 0]).ToArray());
    }

    [Fact]
    public void AccumulatingRollover_AddsUp_UpToItsCap()
    {
        DataPlan plan = new() { LimitBytes = 10, Rollover = RolloverRule.Accumulate };
        Assert.Equal(new long[] { 0, 6, 14, 24 }, PlanRollover.Carried(plan, [4, 2, 0, 0]).ToArray());

        plan.RolloverCapBytes = 15;
        Assert.Equal(new long[] { 0, 6, 14, 15 }, PlanRollover.Carried(plan, [4, 2, 0, 0]).ToArray());
    }

    [Fact]
    public void WhatWasCarriedIn_GoesIntoTheFirstCycleOnly_WithoutRollover()
    {
        DataPlan plan = new() { LimitBytes = 10, CarriedInBytes = 3 };
        Assert.Equal(new long[] { 3, 0, 0 }, PlanRollover.Carried(plan, [1, 1, 1]).ToArray());
        Assert.Equal(new long[] { 0, 0 }, PlanRollover.Carried(new DataPlan { Rollover = RolloverRule.Accumulate }, [1, 1]).ToArray());
    }

    // ---- Free hours --------------------------------------------------------

    [Theory]
    [InlineData(60, 420, 59, false)]
    [InlineData(60, 420, 60, true)]
    [InlineData(60, 420, 419, true)]
    [InlineData(60, 420, 420, false)]
    [InlineData(1380, 420, 1380, true)]
    [InlineData(1380, 420, 0, true)]
    [InlineData(1380, 420, 419, true)]
    [InlineData(1380, 420, 720, false)]
    public void FreeHours_RunOverMidnightWhenTheyEndBeforeTheyStart(int start, int end, int minute, bool inside)
    {
        OffPeakWindow window = new() { Enabled = true, StartMinute = start, EndMinute = end };
        Assert.Equal(inside, window.Contains(minute));
    }

    [Fact]
    public void FreeHours_AreHeldToQuarterHours_AndHaveALength()
    {
        OffPeakWindow window = new() { Enabled = true, StartMinute = 23 * 60 + 7, EndMinute = 7 * 60 };
        Assert.Equal(23 * 60, window.Start);
        Assert.Equal(TimeSpan.FromHours(8), window.Length);
        Assert.Equal(TimeSpan.Zero, new OffPeakWindow().Length);
    }

    // ---- Counting a cycle --------------------------------------------------

    [Fact]
    public void ACycle_CountsItsOwnNetworks_AndSaysWhatWasOnOthers()
    {
        DataPlan plan = new() { LimitBytes = GB, Networks = [new PlanNetwork { Key = Home.Key, Name = Home.Name }] };
        BillingPeriod period = new(On(2026, 10, 1), On(2026, 11, 1));
        long start = EpochMinute.From(On(2026, 10, 2, 10));

        PlanTally tally = PlanTally.Count(
            plan,
            period,
            On(2026, 10, 3),
            [
                new UsageQuarter(start, 1, 100, 10, 0, 0, 15),
                new UsageQuarter(start + 15, 2, 500, 50, 0, 0, 15),
                new UsageQuarter(start + 30, 0, 7000, 0, 0, 0, 15),
            ],
            new HashSet<long> { 1 },
            [new UsageSpan(start + 60, 60, 99_999, 0)],
            Utc);

        Assert.Equal(110, tally.Counted);
        Assert.Equal(550, tally.OtherNetworks);
        Assert.Equal(7000, tally.UnknownNetwork);
        Assert.Equal(0, tally.Estimated);
        Assert.Equal(45, tally.RecordedMinutes);
        Assert.Equal(31, tally.Days.Count);
        Assert.True(tally.Days[^1].IsFuture);
        Assert.Equal(110, tally.Days[1].Counted);
    }

    [Fact]
    public void FreeHours_AreLeftOutOfTheCount_EvenAcrossMidnight()
    {
        DataPlan plan = new() { LimitBytes = GB, OffPeak = new OffPeakWindow { Enabled = true, StartMinute = 23 * 60, EndMinute = 7 * 60 } };
        BillingPeriod period = new(On(2026, 10, 1), On(2026, 11, 1));

        PlanTally tally = PlanTally.Count(
            plan,
            period,
            On(2026, 10, 10),
            [
                new UsageQuarter(EpochMinute.From(On(2026, 10, 2, 22, 45)), 0, 1, 0, 0, 0, 15),
                new UsageQuarter(EpochMinute.From(On(2026, 10, 2, 23, 0)), 0, 10, 0, 0, 0, 15),
                new UsageQuarter(EpochMinute.From(On(2026, 10, 3, 6, 45)), 0, 100, 0, 0, 0, 15),
                new UsageQuarter(EpochMinute.From(On(2026, 10, 3, 7, 0)), 0, 1000, 0, 0, 0, 15),
            ],
            new HashSet<long>(),
            [],
            Utc);

        Assert.Equal(1001, tally.Counted);
        Assert.Equal(110, tally.OffPeak);
    }

    [Fact]
    public void FreeHours_FollowTheWallClock_OnTheNightItSkipsAnHour()
    {
        DataPlan plan = new() { LimitBytes = GB, OffPeak = new OffPeakWindow { Enabled = true, StartMinute = 23 * 60, EndMinute = 7 * 60 } };
        BillingPeriod period = new(Local(Berlin, 2026, 3, 1), Local(Berlin, 2026, 4, 1));
        List<UsageQuarter> quarters = [];

        // A byte every quarter hour from 20:00 on the 28th to 10:00 on the 29th.
        for (long minute = EpochMinute.From(Local(Berlin, 2026, 3, 28, 20)); minute < EpochMinute.From(Local(Berlin, 2026, 3, 29, 10)); minute += 15)
        {
            quarters.Add(new UsageQuarter(minute, 0, 1, 0, 0, 0, 15));
        }

        PlanTally tally = PlanTally.Count(plan, period, Local(Berlin, 2026, 3, 30), quarters, new HashSet<long>(), [], Berlin);

        // 23:00 to 07:00 that night is seven hours: the hour from 02:00 never happens.
        Assert.Equal(7 * 4, tally.OffPeak);
        Assert.Equal(quarters.Count - (7 * 4), tally.Counted);
    }

    [Fact]
    public void FreeHours_FollowTheWallClock_OnTheNightItRepeatsAnHour()
    {
        DataPlan plan = new() { LimitBytes = GB, OffPeak = new OffPeakWindow { Enabled = true, StartMinute = 23 * 60, EndMinute = 7 * 60 } };
        BillingPeriod period = new(Local(Berlin, 2026, 10, 1), Local(Berlin, 2026, 11, 1));
        List<UsageQuarter> quarters = [];

        for (long minute = EpochMinute.From(Local(Berlin, 2026, 10, 24, 20)); minute < EpochMinute.From(Local(Berlin, 2026, 10, 25, 10)); minute += 15)
        {
            quarters.Add(new UsageQuarter(minute, 0, 1, 0, 0, 0, 15));
        }

        PlanTally tally = PlanTally.Count(plan, period, Local(Berlin, 2026, 10, 30), quarters, new HashSet<long>(), [], Berlin);

        // 23:00 to 07:00 that night is nine hours: the hour from 02:00 comes twice.
        Assert.Equal(9 * 4, tally.OffPeak);
        Assert.Equal(15 * 4, quarters.Count);
    }

    [Fact]
    public void WindowsFiguresForTimeClosed_AreSharedOutOverTheirMinutes()
    {
        DataPlan plan = new() { LimitBytes = GB, OffPeak = new OffPeakWindow { Enabled = true, StartMinute = 60, EndMinute = 7 * 60 } };
        BillingPeriod period = new(On(2026, 10, 1), On(2026, 11, 1));
        long start = EpochMinute.From(On(2026, 10, 2, 6, 30));

        PlanTally tally = PlanTally.Count(plan, period, On(2026, 10, 5), [], new HashSet<long>(), [new UsageSpan(start, 60, 6000, 0)], Utc);

        Assert.Equal(3000, tally.OffPeak);
        Assert.Equal(3000, tally.Counted);
        Assert.Equal(3000, tally.Estimated);
        Assert.Equal(0, tally.Measured);

        DataPlan chosen = new() { LimitBytes = GB, Networks = [new PlanNetwork { Key = "x" }] };
        Assert.Equal(0, PlanTally.Count(chosen, period, On(2026, 10, 5), [], new HashSet<long>(), [new UsageSpan(start, 60, 6000, 0)], Utc).Counted);
    }

    [Fact]
    public void ADownloadOnlyPlan_CountsOnlyDownload()
    {
        DataPlan plan = new() { LimitBytes = GB, Counts = PlanCounts.DownloadOnly };
        BillingPeriod period = new(On(2026, 10, 1), On(2026, 11, 1));

        PlanTally tally = PlanTally.Count(plan, period, On(2026, 10, 5), [new UsageQuarter(EpochMinute.From(On(2026, 10, 2)), 0, 100, 900, 0, 0, 15)], new HashSet<long>(), [], Utc);

        Assert.Equal(100, tally.Counted);
    }

    [Fact]
    public void WhatStayedOnTheLocalNetwork_IsNotCounted_WhereItIsKnown()
    {
        DataPlan plan = new() { LimitBytes = GB };
        BillingPeriod period = new(On(2026, 10, 1), On(2026, 11, 1));
        long start = EpochMinute.From(On(2026, 10, 2));

        PlanTally tally = PlanTally.Count(
            plan,
            period,
            On(2026, 10, 5),
            [
                new UsageQuarter(start, 0, 1000, 100, 0, 0, 15, LocalDown: 800, LocalUp: 50, LocalMinutes: 15),
                new UsageQuarter(start + 15, 0, 1000, 100, 0, 0, 15),
            ],
            new HashSet<long>(),
            [],
            Utc);

        Assert.Equal(250 + 1100, tally.Counted);
        Assert.Equal(850, tally.Local);
    }

    // ---- Forecast ----------------------------------------------------------

    [Fact]
    public void TheForecast_ProjectsThePaceSoFar_AndGivesADailyBudget()
    {
        BillingPeriod period = new(On(2026, 10, 1), On(2026, 10, 31));

        PlanOutlook outlook = PlanForecast.Look(period, On(2026, 10, 13, 12), 100 * GB, 300 * GB, Utc);

        Assert.False(outlook.TooEarly);
        Assert.InRange(outlook.Projected!.Value, (240 * GB) - 2, (240 * GB) + 2);
        Assert.Equal(18, outlook.DaysLeft);
        Assert.Equal(200 * GB / 18, outlook.DailyBudget);
        Assert.Null(outlook.RunsOutAt);
        Assert.Equal(new DateOnly(2026, 10, 30), outlook.LastDay);
    }

    [Fact]
    public void DaysLeft_CountToday_ToACycleEndingAsThe30thBegins()
    {
        BillingPeriod period = new(On(2026, 9, 30), On(2026, 10, 30));
        Assert.Equal(17, PlanForecast.Look(period, On(2026, 10, 13, 15), 0, 0, Utc).DaysLeft);
    }

    [Fact]
    public void TheForecast_SaysWhenTheAllowanceRunsOut()
    {
        BillingPeriod period = new(On(2026, 10, 1), On(2026, 10, 31));

        PlanOutlook outlook = PlanForecast.Look(period, On(2026, 10, 11), 200 * GB, 300 * GB, Utc);

        Assert.InRange(outlook.RunsOutAt!.Value, On(2026, 10, 16).AddSeconds(-1), On(2026, 10, 16).AddSeconds(1));
        Assert.InRange(outlook.Projected!.Value, (600 * GB) - 2, (600 * GB) + 2);
    }

    [Fact]
    public void TheForecast_WaitsUntilThereIsAPace()
    {
        BillingPeriod period = new(On(2026, 10, 1), On(2026, 10, 31));

        PlanOutlook early = PlanForecast.Look(period, On(2026, 10, 1, 6), 5 * GB, 300 * GB, Utc);
        Assert.True(early.TooEarly);
        Assert.Null(early.Projected);
        Assert.Null(early.RunsOutAt);
        Assert.NotNull(early.DailyBudget);

        // A day's plan is far enough on after a few hours.
        BillingPeriod day = new(On(2026, 10, 1), On(2026, 10, 2));
        Assert.False(PlanForecast.Look(day, On(2026, 10, 1, 3), GB, 5 * GB, Utc).TooEarly);
    }

    [Fact]
    public void ACycleAlreadyOver_HasNoBudget_AndAnEndedCycleNoOutlook()
    {
        BillingPeriod period = new(On(2026, 10, 1), On(2026, 10, 31));

        PlanOutlook over = PlanForecast.Look(period, On(2026, 10, 20), 350 * GB, 300 * GB, Utc);
        Assert.Equal(0, over.DailyBudget);
        Assert.Null(over.RunsOutAt);

        PlanOutlook ended = PlanForecast.Look(period, On(2026, 11, 2), 350 * GB, 300 * GB, Utc);
        Assert.Equal(0, ended.DaysLeft);
        Assert.Null(ended.Projected);
    }

    [Fact]
    public void TheWords_SayWhatIsLeftAndForHowLong()
    {
        DataPlan plan = new() { LimitBytes = 100 * GB };
        BillingPeriod period = new(On(2026, 9, 30), On(2026, 10, 30));
        DateTimeOffset now = On(2026, 10, 13, 15);
        PlanTally tally = PlanTally.Empty with { Counted = 59 * GB };
        PlanStatus status = new(plan, period, PlanState.Running, now, 0, tally, PlanForecast.Look(period, now, 59 * GB, 100 * GB, Utc));

        Assert.Equal("41.0 GB left · 17 days", PlanWords.Left(status));
        Assert.StartsWith("At this pace, ", PlanWords.Forecast(status), StringComparison.Ordinal);
        Assert.Contains("by the 29th", PlanWords.Forecast(status), StringComparison.Ordinal);
        Assert.Contains("a day and stay under", PlanWords.Forecast(status), StringComparison.Ordinal);

        PlanStatus over = status with { Tally = tally with { Counted = 102 * GB } };
        Assert.Equal("Over by 2.00 GB · 17 days", PlanWords.Left(over));

        PlanOutlook lastHours = PlanForecast.Look(period, On(2026, 10, 29, 15), 0, 0, Utc);
        Assert.Equal("9 hours", PlanWords.TimeLeft(lastHours));
    }

    [Theory]
    [InlineData(1, "1st")]
    [InlineData(2, "2nd")]
    [InlineData(3, "3rd")]
    [InlineData(11, "11th")]
    [InlineData(12, "12th")]
    [InlineData(13, "13th")]
    [InlineData(21, "21st")]
    [InlineData(22, "22nd")]
    [InlineData(30, "30th")]
    public void Days_AreWrittenAsOrdinals(int day, string expected) => Assert.Equal(expected, PlanWords.Ordinal(day));

    // ---- Plans and networks ------------------------------------------------

    [Fact]
    public void ThePlanForANetwork_IsTheOneNamingIt_ElseTheOneForEveryNetwork()
    {
        DataPlan any = new() { Name = "Any" };
        DataPlan phone = new() { Name = "Phone", Networks = [new PlanNetwork { Key = Phone.Key }] };

        Assert.Same(phone, DataPlan.For([any, phone], Phone.Key));
        Assert.Same(any, DataPlan.For([any, phone], Home.Key));
        Assert.Same(any, DataPlan.For([any, phone], null));
        Assert.Null(DataPlan.For([phone], Home.Key));
    }

    [Fact]
    public void APlanFromWindows_TakesItsLimitAndBillingDay()
    {
        DateTimeOffset billing = new(new DateTime(2026, 10, 5, 12, 0, 0, DateTimeKind.Local));
        NetworkSighting sighting = new(Phone, NetworkCost.Fixed, WindowsPlan: new WindowsDataPlan(20 * GB, billing, null, null));

        DataPlan plan = DataPlan.FromWindowsLimit(sighting)!;

        Assert.Equal(20 * GB, plan.LimitBytes);
        Assert.Equal(5, plan.ResetDay);
        Assert.True(plan.FromWindows);
        Assert.True(plan.Covers(Phone.Key));
        Assert.Null(DataPlan.FromWindowsLimit(new NetworkSighting(Home, NetworkCost.Unrestricted)));
    }

    [Fact]
    public void APlansProblems_AreSaidInWords()
    {
        Assert.Empty(new DataPlan().Problems());
        Assert.NotEmpty(new DataPlan { Cycle = PlanCycle.OneOff }.Problems());
        Assert.NotEmpty(new DataPlan { ResetDay = 32 }.Problems());
        Assert.NotEmpty(new DataPlan { OffPeak = new OffPeakWindow { Enabled = true, StartMinute = 60, EndMinute = 60 } }.Problems());
        Assert.Equal([80, 100], new DataPlan { AlertLevels = [100, 80, 80, -5, 2000] }.Levels.ToArray());
    }

    [Theory]
    [InlineData(NetworkKind.WiFi, NetworkCost.Fixed, null, true, HotspotReason.MeteredWiFi)]
    [InlineData(NetworkKind.WiFi, NetworkCost.Unrestricted, null, false, HotspotReason.None)]
    [InlineData(NetworkKind.Ethernet, NetworkCost.Fixed, null, false, HotspotReason.None)]
    [InlineData(NetworkKind.Cellular, NetworkCost.Unknown, null, true, HotspotReason.Mobile)]
    [InlineData(NetworkKind.WiFi, NetworkCost.Unrestricted, true, true, HotspotReason.MarkedByYou)]
    [InlineData(NetworkKind.WiFi, NetworkCost.Fixed, false, false, HotspotReason.UnmarkedByYou)]
    public void AHotspot_IsMeteredWiFiOrMobile_UnlessThePersonSaysOtherwise(NetworkKind kind, NetworkCost cost, bool? marking, bool hotspot, HotspotReason reason)
    {
        NetworkSighting sighting = new(new NetworkTag("k", "n", kind), cost);
        Assert.Equal(new HotspotVerdict(hotspot, reason), HotspotJudge.Judge(sighting, marking));
        Assert.Equal(HotspotVerdict.No, HotspotJudge.Judge(null, true));
    }

    [Fact]
    public void TheCurrentNetwork_IsReadAndToldOfOnlyWhenItChanges()
    {
        WindsockSettings settings = new();
        settings.Usage.Networks = [new NetworkChoice { Key = Phone.Key, Label = "My phone", Hotspot = false }];
        FakeSightings source = new() { Next = new NetworkSighting(Phone, NetworkCost.Fixed) };
        using CurrentNetwork network = new(source, settings, TimeProvider.System, NullLogger<CurrentNetwork>.Instance);
        int told = 0;
        network.Changed += (_, _) => told++;

        network.ReadNow();
        network.ReadNow();

        Assert.Equal(1, told);
        Assert.Equal(Phone, network.Current);
        Assert.Equal("My phone", network.DisplayName);
        Assert.Equal(new HotspotVerdict(false, HotspotReason.UnmarkedByYou), network.Hotspot);

        source.Fail = true;
        network.ReadNow();
        Assert.Null(network.Current);
        Assert.Equal(2, told);
    }

    [Fact]
    public void EachRecordedMinute_IsPutDownToTheNetworkThatCarriedMostOfIt()
    {
        string file = Path.Combine(_directory, "recorder.db");
        using SqliteUsageHistoryStore store = new(file);
        FakeTags tags = new();
        UsageRecorder recorder = new(Monitor(), store, new WindsockSettings { RecordHistory = true }, tags, NullLogger<UsageRecorder>.Instance);
        DateTimeOffset minute = On(2026, 3, 14, 12);

        tags.Current = Home;
        recorder.Observe(new Networking.ThroughputDelta(minute.AddSeconds(1), 100, 0, 0.5));
        tags.Current = Phone;
        recorder.Observe(new Networking.ThroughputDelta(minute.AddSeconds(30), 5000, 0, 0.5));
        tags.Current = Home;
        recorder.Observe(new Networking.ThroughputDelta(minute.AddSeconds(59), 200, 0, 0.5));

        // A quiet minute is the network it ended on; an unknown one stays unknown.
        tags.Current = Phone;
        recorder.Observe(new Networking.ThroughputDelta(minute.AddMinutes(1).AddSeconds(1), 0, 0, 0.5));
        tags.Current = Home;
        recorder.Observe(new Networking.ThroughputDelta(minute.AddMinutes(1).AddSeconds(2), 0, 0, 0.5));
        tags.Current = null;
        recorder.Observe(new Networking.ThroughputDelta(minute.AddMinutes(2).AddSeconds(1), 300, 0, 0.5));
        recorder.FlushOnShutdown();

        IReadOnlyList<UsageMinute> rows = store.ReadMinuteRows(EpochMinute.From(minute), EpochMinute.From(minute) + 3);
        Assert.Equal(Phone.Key, rows[0].Network?.Key);
        Assert.Equal(Home.Key, rows[1].Network?.Key);
        Assert.Null(rows[2].Network);
    }

    // ---- Working plans out from history ------------------------------------

    [Fact]
    public void APlansStatus_IsWorkedOutFromHistory()
    {
        using SqliteUsageHistoryStore store = Store();
        long day = EpochMinute.From(On(2026, 10, 2, 10));
        store.Write([Tagged(day, 100 * MB, Home), Tagged(day + 1, 50 * MB, Phone), Tagged(day + 2, 10 * MB, null)]);

        PlanCalculator calculator = Calculator(store);
        DataPlan any = new() { LimitBytes = GB, ResetDay = 1 };
        DataPlan home = new() { LimitBytes = GB, ResetDay = 1, Networks = [new PlanNetwork { Key = Home.Key }] };

        PlanStatus anyStatus = calculator.Status(any, On(2026, 10, 10))!;
        Assert.Equal(160 * MB, anyStatus.Used);
        Assert.Equal(GB - (160 * MB), anyStatus.Left);
        Assert.Equal(PlanState.Running, anyStatus.State);

        PlanStatus homeStatus = calculator.Status(home, On(2026, 10, 10))!;
        Assert.Equal(100 * MB, homeStatus.Used);
        Assert.Equal(50 * MB, homeStatus.Tally.OtherNetworks);
        Assert.Equal(10 * MB, homeStatus.Tally.UnknownNetwork);
    }

    [Fact]
    public void Rollover_IsWorkedOutThroughTheCyclesSinceThePlanBegan_AndFollowsAnErase()
    {
        using SqliteUsageHistoryStore store = Store();
        store.Write([Tagged(EpochMinute.From(On(2026, 9, 10)), 30 * MB, Home), Tagged(EpochMinute.From(On(2026, 9, 11)), 20 * MB, Home)]);

        PlanCalculator calculator = Calculator(store);
        DataPlan plan = new() { LimitBytes = 100 * MB, ResetDay = 1, Since = On(2026, 9, 1), Rollover = RolloverRule.NextCycleOnly };

        PlanStatus october = calculator.Status(plan, On(2026, 10, 5))!;
        Assert.Equal(50 * MB, october.CarriedIn);
        Assert.Equal(150 * MB, october.Allowance);

        store.Delete(On(2026, 9, 11), On(2026, 9, 12));
        Assert.Equal(70 * MB, calculator.Status(plan, On(2026, 10, 5))!.CarriedIn);

        PlanStatus september = Assert.Single(calculator.Earlier(plan, On(2026, 10, 5), 5));
        Assert.Equal(30 * MB, september.Used);
        Assert.Equal(PlanState.Ended, september.State);
    }

    [Fact]
    public void TheMonitor_RaisesEachLevelOncePerCycle_EvenAcrossRuns()
    {
        using SqliteUsageHistoryStore store = Store();
        store.Write([Tagged(EpochMinute.From(On(2026, 10, 2)), 85 * MB, Home)]);

        WindsockSettings settings = new();
        DataPlan plan = new() { LimitBytes = 100 * MB, ResetDay = 1 };
        settings.Usage.Plans = [plan];
        ManualClock clock = new(On(2026, 10, 5));

        List<PlanLevelCrossing> crossings = [];
        PlanMonitor first = Monitor(settings, store, clock);
        first.LevelCrossed += (_, crossing) => crossings.Add(crossing);
        first.Evaluate();
        first.Evaluate();

        PlanLevelCrossing crossed = Assert.Single(crossings);
        Assert.Equal(80, crossed.Level);
        Assert.True(crossed.FirstLook);
        Assert.Same(plan, first.Active?.Plan);

        PlanMonitor second = Monitor(settings, store, clock);
        second.LevelCrossed += (_, crossing) => crossings.Add(crossing);
        second.Evaluate();
        Assert.Single(crossings);

        store.Write([Tagged(EpochMinute.From(On(2026, 10, 4)), 20 * MB, Home)]);
        second.Evaluate();
        Assert.Equal(2, crossings.Count);
        Assert.Equal(100, crossings[1].Level);
        Assert.False(crossings[1].FirstLook);

        // The next cycle starts again.
        clock.Now = On(2026, 11, 3);
        store.Write([Tagged(EpochMinute.From(On(2026, 11, 2)), 90 * MB, Home)]);
        second.Evaluate();
        Assert.Equal(3, crossings.Count);
    }

    /// <summary>
    /// A plan the store cannot remember - an id left blank in a file edited by
    /// hand - is passed over; the plan beside it is still told, and the
    /// monitor runs on rather than taking the host down at every start.
    /// </summary>
    [Fact]
    public void ThePlanItCannotWorkOut_IsPassedOver_AndTheOthersAreTold()
    {
        using SqliteUsageHistoryStore store = Store();
        store.Write([Tagged(EpochMinute.From(On(2026, 10, 2)), 85 * MB, Home)]);

        DataPlan blank = new() { Name = "Blank", LimitBytes = 100 * MB, ResetDay = 1, Id = " " };
        DataPlan good = new() { Name = "Good", LimitBytes = 100 * MB, ResetDay = 1 };
        WindsockSettings settings = new();
        settings.Usage.Plans = [blank, good];

        List<PlanLevelCrossing> crossings = [];
        PlanMonitor monitor = Monitor(settings, store, new ManualClock(On(2026, 10, 5)));
        monitor.LevelCrossed += (_, crossing) => crossings.Add(crossing);

        monitor.Evaluate();
        monitor.Evaluate();

        Assert.Same(good, Assert.Single(crossings).Plan);
    }

    [Fact]
    public void ADailyAndAMonthlyPlan_DoNotShareWhatTheirEarlierCyclesCounted()
    {
        // Found by review: a daily cycle and a monthly one starting at the same
        // minute were kept under one key, so whichever was worked out first
        // decided what the other's cycle had counted.
        using SqliteUsageHistoryStore store = Store();
        List<UsageMinute> rows = [];

        for (DateTimeOffset day = On(2026, 6, 15, 12); day < On(2026, 10, 5); day = day.AddDays(1))
        {
            rows.Add(Tagged(EpochMinute.From(day), GB, Home));
        }

        store.Write(rows);

        DataPlan monthly = new() { LimitBytes = 100 * GB, ResetDay = 1, Rollover = RolloverRule.Accumulate, Since = On(2026, 6, 15, 9), CountWindowsEstimates = false };
        DataPlan daily = new() { LimitBytes = 5 * GB, Cycle = PlanCycle.Daily, Rollover = RolloverRule.Accumulate, Since = On(2026, 6, 15, 9), CountWindowsEstimates = false };
        DateTimeOffset now = On(2026, 10, 5, 18);

        long alone = Calculator(store).Status(monthly, now)!.CarriedIn;

        PlanCalculator shared = Calculator(store);
        shared.Status(daily, now);

        Assert.Equal(alone, shared.Status(monthly, now)!.CarriedIn);
    }

    [Fact]
    public void ACycleJustEnded_TakesItsLastMinuteAndWindowsBackfill_IntoWhatItCarries()
    {
        // Found by review: a finished cycle's total was kept as soon as it was
        // first worked out, before its last minute and Windows' figures for the
        // evening the PC was closed arrived, and stood for the rest of the run.
        using SqliteUsageHistoryStore store = Store();
        List<UsageMinute> rows = [];

        for (int day = 1; day <= 29; day++)
        {
            rows.Add(Tagged(EpochMinute.From(On(2026, 9, day, 12)), GB, Home));
        }

        store.Write(rows);
        store.WriteCoverage(EpochMinute.From(On(2026, 9, 1)), EpochMinute.From(On(2026, 9, 29, 13)));

        DataPlan monthly = new() { LimitBytes = 100 * GB, ResetDay = 1, Rollover = RolloverRule.Accumulate, Since = On(2026, 9, 1, 9) };
        PlanCalculator calculator = Calculator(store);
        DateTimeOffset now = On(2026, 10, 2, 9);
        long before = calculator.Status(monthly, now)!.CarriedIn;

        store.Write([Tagged(EpochMinute.From(On(2026, 9, 30, 23, 59)), 5 * GB, Home)]);
        long window = EpochMinute.From(On(2026, 9, 30, 18));
        store.WriteAppUsage([new UsageWindow(window, 60)], [new AppUsageSlice(window, 60, "x", 20 * GB, 0)], new AppUsagePosition(window + 60, window + 60));

        // Windows' figures are kept five minutes; anything longer is the bug.
        DateTimeOffset later = now.AddMinutes(6);
        long after = calculator.Status(monthly, later)!.CarriedIn;

        Assert.Equal(71 * GB, before);
        Assert.Equal(Calculator(store).Status(monthly, later)!.CarriedIn, after);
        Assert.Equal(46 * GB, after);
    }

    [Fact]
    public void TheActivePlan_IsARunningOne_EvenWithAnEndedOrUnbegunPlanListedFirst()
    {
        // Found by review: a one-off plan that had ended, listed first, beat a
        // running monthly plan, so the taskbar showed a dash.
        using SqliteUsageHistoryStore store = Store();
        WindsockSettings settings = new();
        DataPlan ended = new() { Name = "Trip", Cycle = PlanCycle.OneOff, OneOffStart = On(2026, 8, 1), OneOffEnd = On(2026, 8, 15), LimitBytes = GB };
        DataPlan unbegun = new() { Name = "Next trip", Cycle = PlanCycle.OneOff, OneOffStart = On(2026, 12, 1), OneOffEnd = On(2026, 12, 15), LimitBytes = GB };
        DataPlan home = new() { Name = "Home", LimitBytes = GB };
        settings.Usage.Plans = [ended, unbegun, home];

        PlanMonitor monitor = Monitor(settings, store, new ManualClock(On(2026, 10, 5)));
        monitor.Evaluate();

        Assert.Same(home, monitor.Active?.Plan);
        Assert.NotNull(monitor.ActiveText);

        // With no plan running, the first that applies is still named.
        settings.Usage.Plans = [ended, unbegun];
        monitor.Evaluate();
        Assert.Same(ended, monitor.Active?.Plan);
        Assert.Null(monitor.ActiveText);
    }

    [Fact]
    public void TheActivePlan_FollowsTheNetworkInUse()
    {
        using SqliteUsageHistoryStore store = Store();
        WindsockSettings settings = new();
        DataPlan any = new() { Name = "Broadband", LimitBytes = GB };
        DataPlan phone = new() { Name = "Phone", LimitBytes = GB, Networks = [new PlanNetwork { Key = Phone.Key }] };
        settings.Usage.Plans = [any, phone];
        FakeSightings source = new() { Next = new NetworkSighting(Phone, NetworkCost.Fixed) };
        using CurrentNetwork network = new(source, settings, TimeProvider.System, NullLogger<CurrentNetwork>.Instance);
        network.ReadNow();

        PlanMonitor monitor = new(settings, Calculator(store), store, new ManualClock(On(2026, 10, 5)), NullLogger<PlanMonitor>.Instance, network);
        monitor.Evaluate();
        Assert.Same(phone, monitor.Active?.Plan);

        source.Next = new NetworkSighting(Home, NetworkCost.Unrestricted);
        network.ReadNow();
        monitor.Evaluate();
        Assert.Same(any, monitor.Active?.Plan);
        Assert.Equal(2, monitor.Statuses.Count);
    }

    [Fact]
    public void TheActivePlan_IsOfferedInAFewWords_ForTheTaskbar()
    {
        using SqliteUsageHistoryStore store = Store();
        WindsockSettings settings = new();
        settings.Usage.Plans = [new DataPlan { Name = "Broadband", LimitBytes = GB, ResetDay = 1 }];
        store.Write([Tagged(EpochMinute.From(On(2026, 10, 2)), 256 * MB, Home)]);

        PlanMonitor monitor = Monitor(settings, store, new ManualClock(On(2026, 10, 5)));
        Assert.Null(monitor.ActiveText);

        monitor.Evaluate();
        Assert.Equal(PlanWords.Left(monitor.Active!), monitor.ActiveText);
        Assert.StartsWith("768 MB left", monitor.ActiveText, StringComparison.Ordinal);
        Assert.Equal(0.25, monitor.ActiveFraction!.Value, 3);

        settings.Usage.Plans = [];
        monitor.Evaluate();
        Assert.Null(monitor.ActiveText);
        Assert.Null(monitor.ActiveFraction);
    }

    private SqliteUsageHistoryStore Store() => new(Path.Combine(_directory, Guid.NewGuid().ToString("N") + ".db"));

    private static PlanCalculator Calculator(SqliteUsageHistoryStore store) => new(store, store, store, () => TimeZoneInfo.Utc);

    private static PlanMonitor Monitor(WindsockSettings settings, SqliteUsageHistoryStore store, TimeProvider clock) =>
        new(settings, Calculator(store), store, clock, NullLogger<PlanMonitor>.Instance);

    private static UsageMinute Tagged(long minute, long down, NetworkTag? network) => new(minute, down, 0, 0, 0) { Network = network };

    private static ThroughputMonitor Monitor() => new(
        new StillTotals(),
        Options.Create(new ThroughputMonitorOptions()),
        NullLogger<ThroughputMonitor>.Instance);

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

    private sealed class StillTotals : INetworkTotalsSource
    {
        public NetworkTotals ReadTotals() => default;
    }

    private sealed class FakeTags : INetworkTagSource
    {
        public NetworkTag? Current { get; set; }
    }

    private sealed class FakeSightings : INetworkSightingSource
    {
        public NetworkSighting? Next { get; set; }

        public bool Fail { get; set; }

        public event EventHandler? Changed;

        public NetworkSighting? Read() => Fail ? throw new InvalidOperationException("Windows said no.") : Next;

        public void Tell() => Changed?.Invoke(this, EventArgs.Empty);
    }

    private sealed class ManualClock(DateTimeOffset now) : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = now;

        public override DateTimeOffset GetUtcNow() => Now.ToUniversalTime();
    }
}
