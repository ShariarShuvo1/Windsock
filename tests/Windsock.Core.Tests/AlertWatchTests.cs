using Windsock.Core.Alerts;
using Windsock.Core.Connectivity;
using Windsock.Core.Hardware;
using Windsock.Core.Networking;
using Windsock.Core.Settings;
using Xunit;

namespace Windsock.Core.Tests;

public sealed class PingWatchTests
{
    private static readonly DateTimeOffset Start = new(2026, 6, 10, 12, 0, 0, TimeSpan.Zero);
    private static readonly TimeSpan Gap = TimeSpan.FromSeconds(30);

    private static PingAlertSettings Rule => new() { Enabled = true, Milliseconds = 150, LossPercent = 10, Minutes = 3 };

    private static LatencyPoint Internet(int second, double? ms, bool offline = false) =>
        new(Start.AddSeconds(second), 2, ms, RouterTried: true, InternetTried: true, offline);

    private static LatencyPoint Router(int second, double? ms) =>
        new(Start.AddSeconds(second), ms, null, RouterTried: true, InternetTried: false, Offline: false);

    /// <summary>Feeds checks every five seconds and returns every alarm raised.</summary>
    private static List<PingAlarm> Feed(PingWatch watch, int from, int until, Func<int, LatencyPoint> point, PingAlertSettings? rule = null)
    {
        List<PingAlarm> alarms = [];

        for (int second = from; second <= until; second += 5)
        {
            if (watch.Observe(point(second), rule ?? Rule, Gap) is { } alarm)
            {
                alarms.Add(alarm);
            }
        }

        return alarms;
    }

    [Fact]
    public void NothingFires_BeforeAWholeStretchWasWatched()
    {
        PingWatch watch = new();

        // Slow from the very first check: nothing for the first three minutes, the first minute included.
        Assert.Empty(Feed(watch, 0, 175, second => Internet(second, 400)));
        Assert.Single(Feed(watch, 180, 185, second => Internet(second, 400)));
    }

    [Fact]
    public void ASlowStretch_FiresOnce_WithTheTypicalTime()
    {
        PingWatch watch = new();

        List<PingAlarm> alarms = Feed(watch, 0, 600, second => Internet(second, 240));

        PingAlarm alarm = Assert.Single(alarms);
        Assert.Equal(PingTarget.Internet, alarm.Target);
        Assert.Equal(240, alarm.TypicalMs);
        Assert.True(alarm.Slow);
        Assert.False(alarm.Lossy);
    }

    [Fact]
    public void Spikes_DoNotFireIt()
    {
        PingWatch watch = new();

        Assert.Empty(Feed(watch, 0, 900, second => Internet(second, second % 30 == 0 ? 900 : 20)));
    }

    [Fact]
    public void Loss_FiresIt()
    {
        PingWatch watch = new();

        // One check in five unanswered: 20%.
        PingAlarm alarm = Assert.Single(Feed(watch, 0, 300, second => Internet(second, second % 25 == 0 ? null : 20)));

        Assert.True(alarm.Lossy);
        Assert.False(alarm.Slow);
        Assert.InRange(alarm.Loss, 0.15, 0.25);
    }

    [Fact]
    public void AnOutage_IsNotLoss_AndStartsTheStretchAgain()
    {
        PingWatch watch = new();

        Assert.Empty(Feed(watch, 0, 170, second => Internet(second, 400)));
        Assert.Empty(Feed(watch, 175, 200, second => Internet(second, null, offline: true)));

        // After the outage the stretch must be watched whole again.
        Assert.Empty(Feed(watch, 205, 380, second => Internet(second, 400)));
        Assert.Single(Feed(watch, 385, 400, second => Internet(second, 400)));
    }

    [Fact]
    public void ASilenceBetweenChecks_StartsTheStretchAgain()
    {
        PingWatch watch = new();

        Assert.Empty(Feed(watch, 0, 150, second => Internet(second, 400)));

        // Asleep for ten minutes, then slow again: three more minutes before it fires.
        Assert.Empty(Feed(watch, 750, 925, second => Internet(second, 400)));
        Assert.Single(Feed(watch, 930, 935, second => Internet(second, 400)));
    }

    [Fact]
    public void Hovering_AtTheLevel_DoesNotFireAgain_UntilAWholeStretchIsClearlyGood()
    {
        PingWatch watch = new();

        Assert.Single(Feed(watch, 0, 300, second => Internet(second, 200)));

        // Just under the level is not clearly good: no re-arm, and back over is no new alert.
        Assert.Empty(Feed(watch, 305, 700, second => Internet(second, 140)));
        Assert.Empty(Feed(watch, 705, 1100, second => Internet(second, 200)));
        Assert.True(watch.HasFired);

        // Clearly good for a whole stretch re-arms it.
        Assert.Empty(Feed(watch, 1105, 1400, second => Internet(second, 30)));
        Assert.False(watch.HasFired);
        Assert.Single(Feed(watch, 1405, 1800, second => Internet(second, 200)));
    }

    [Fact]
    public void ARouterThatNeverAnswers_IsNotLosingPings()
    {
        PingWatch watch = new();

        // The reviewer's case: no internet address, and a router that drops every ping.
        Assert.Empty(Feed(watch, 0, 1200, second => Router(second, null)));
    }

    [Fact]
    public void ARouterThatAnswersSometimes_IsLosingPings()
    {
        PingWatch watch = new();

        PingAlarm alarm = Assert.Single(Feed(watch, 0, 300, second => Router(second, second % 20 == 0 ? null : 3)));

        Assert.Equal(PingTarget.Router, alarm.Target);
        Assert.True(alarm.Lossy);
    }

    [Fact]
    public void WithNoInternetAddress_TheRoutersAnswersAreJudged()
    {
        PingWatch watch = new();

        PingAlarm alarm = Assert.Single(Feed(watch, 0, 300, second => Router(second, 300)));

        Assert.Equal(PingTarget.Router, alarm.Target);
    }

    [Fact]
    public void SwitchedOff_NothingFires() =>
        Assert.Empty(Feed(new PingWatch(), 0, 900, second => Internet(second, 900), new PingAlertSettings { Enabled = false }));
}

public sealed class SpeedDropWatchTests
{
    private static readonly DateTimeOffset Start = new(2026, 6, 10, 12, 0, 0, TimeSpan.Zero);

    private static SpeedDropSettings Rule => new() { Enabled = true, BelowBytesPerSecond = 1_000_000, Minutes = 5 };

    private static List<SpeedDropAlarm> Feed(SpeedDropWatch watch, int from, int until, Func<int, double> down)
    {
        List<SpeedDropAlarm> alarms = [];

        for (int second = from; second <= until; second++)
        {
            if (watch.Observe(new ThroughputSample(Start.AddSeconds(second), down(second), 0), Rule) is { } alarm)
            {
                alarms.Add(alarm);
            }
        }

        return alarms;
    }

    [Fact]
    public void AnIdleLine_NeverAlerts()
    {
        SpeedDropWatch watch = new();

        // A few kilobytes a second, with a burst every half minute.
        Assert.Empty(Feed(watch, 0, 3600, second => second % 30 == 0 ? 2_000_000 : 3_000));
    }

    [Fact]
    public void AVideoStreamFetchingInBursts_DoesNotAlert()
    {
        SpeedDropWatch watch = new();

        // Four seconds of segment at 600 KB/s, six seconds of nothing.
        Assert.Empty(Feed(watch, 0, 1800, second => second % 10 < 4 ? 600_000 : 0));
    }

    /// <summary>A download at 4 MB/s for a minute, then at 300 KB/s: a drop.</summary>
    private static double Dropping(int second, int startedAt = 0) => second - startedAt < 60 ? 4_000_000 : 300_000;

    [Fact]
    public void ADownloadThatSlowsDown_AlertsOnce_AfterTheWholeStretch()
    {
        SpeedDropWatch watch = new();

        // Not before a whole stretch was watched, and not while the fast part is a tenth of it.
        Assert.Empty(Feed(watch, 0, 320, second => Dropping(second)));
        SpeedDropAlarm alarm = Assert.Single(Feed(watch, 321, 1200, second => Dropping(second)));

        Assert.Equal(300_000, alarm.TypicalBytesPerSecond);
        Assert.Equal(4_000_000, alarm.PeakBytesPerSecond);
        Assert.InRange(alarm.Received, 80_000_000, 250_000_000);
    }

    [Fact]
    public void ASteadyCallOrStream_BelowTheLevel_NeverAlerts()
    {
        SpeedDropWatch watch = new();

        // A video call: 300 KB/s, steady, for an hour, never faster.
        Assert.Empty(Feed(watch, 0, 3600, second => 250_000 + (second % 7 * 20_000)));
    }

    [Fact]
    public void AFastDownloadBefore_ACallAfterAPause_IsNotADrop()
    {
        SpeedDropWatch watch = new();

        // A download finishes, the line is idle for a minute, then a call starts.
        Assert.Empty(Feed(watch, 0, 120, _ => 4_000_000));
        Assert.Empty(Feed(watch, 121, 180, _ => 1_000));
        Assert.Empty(Feed(watch, 181, 1500, _ => 300_000));
    }

    [Fact]
    public void AFastDownload_DoesNotAlert()
    {
        SpeedDropWatch watch = new();

        Assert.Empty(Feed(watch, 0, 1200, _ => 4_000_000));
    }

    [Fact]
    public void ADownloadThatSometimesReachesTheLevel_IsNotSlow()
    {
        SpeedDropWatch watch = new();

        // Under the level most of the time, but over it one second in five.
        Assert.Empty(Feed(watch, 0, 1200, second => second % 5 == 0 ? 2_000_000 : 400_000));
    }

    [Fact]
    public void AfterTheDownloadEnds_ASlowOneAlertsAgain()
    {
        SpeedDropWatch watch = new();

        Assert.Single(Feed(watch, 0, 600, second => Dropping(second)));
        Assert.Empty(Feed(watch, 601, 700, _ => 1_000));
        Assert.False(watch.HasFired);
        Assert.Single(Feed(watch, 701, 1200, second => Dropping(second, startedAt: 701)));
    }

    [Fact]
    public void ASleep_StartsTheStretchAgain()
    {
        SpeedDropWatch watch = new();

        Assert.Empty(Feed(watch, 0, 200, second => Dropping(second)));

        // After the sleep the run starts again: it must be fast, then slow, for a whole stretch -
        // slow before the speed, as a call that a download follows, does not count.
        Assert.Empty(Feed(watch, 1000, 1290, _ => 300_000));
        Assert.Empty(Feed(watch, 1291, 1600, second => Dropping(second, startedAt: 1291)));
        Assert.Single(Feed(watch, 1601, 1700, second => Dropping(second, startedAt: 1291)));
    }

    [Fact]
    public void TheIdleFloor_NeverPassesHalfTheLevel()
    {
        Assert.Equal(SpeedDropWatch.ActivityFloor, SpeedDropWatch.FloorFor(10_000_000));
        Assert.Equal(25_000, SpeedDropWatch.FloorFor(50_000));
    }
}

public sealed class UploadWatchTests
{
    private static readonly DateTimeOffset Start = new(2026, 6, 10, 12, 0, 0, TimeSpan.Zero);

    private static UploadAlertSettings Rule => new() { Enabled = true, AboveBytesPerSecond = 1_000_000, Minutes = 10 };

    private static List<UploadAlarm> Feed(UploadWatch watch, int from, int until, Func<int, double> up)
    {
        List<UploadAlarm> alarms = [];

        for (int second = from; second <= until; second++)
        {
            if (watch.Observe(new ThroughputSample(Start.AddSeconds(second), 0, up(second)), Rule) is { } alarm)
            {
                alarms.Add(alarm);
            }
        }

        return alarms;
    }

    [Fact]
    public void ASustainedUpload_AlertsOnce_WithHowMuchWent()
    {
        UploadWatch watch = new();

        Assert.Empty(Feed(watch, 0, 598, _ => 2_000_000));
        UploadAlarm alarm = Assert.Single(Feed(watch, 599, 3000, _ => 2_000_000));

        Assert.Equal(2_000_000, alarm.TypicalBytesPerSecond);
        Assert.InRange(alarm.Sent, 1_150_000_000, 1_250_000_000);
    }

    [Fact]
    public void ABurst_HoweverLarge_IsNotSustained()
    {
        UploadWatch watch = new();

        // Four minutes at 20 MB/s inside each ten: under half the stretch.
        Assert.Empty(Feed(watch, 0, 3600, second => second % 600 < 240 ? 20_000_000 : 10_000));
    }

    [Fact]
    public void OnceTheUploadFalls_ItCanAlertAgain()
    {
        UploadWatch watch = new();

        Assert.Single(Feed(watch, 0, 700, _ => 2_000_000));
        Assert.Empty(Feed(watch, 701, 900, _ => 100_000));
        Assert.False(watch.HasFired);
        Assert.Single(Feed(watch, 901, 1600, _ => 2_000_000));
    }
}

public sealed class HardwareWatchTests
{
    private static readonly DateTimeOffset Start = new(2026, 6, 10, 12, 0, 0, TimeSpan.Zero);

    private static SystemSnapshot Snapshot(int second, double? gpu = null, double? drive = null, double? wear = null, long free = 500, double? cpu = null) => new(
        Start.AddSeconds(second),
        new ProcessorReading(10, 1, [10], 3000, 100, 1000, 10000, cpu),
        [new GraphicsReading("RTX", 50, gpu, null, null, null, null, 0, 0, [], [])],
        MemoryReading.Empty,
        [new DriveReading(0, "Fast SSD", "NVMe", false, 1000, drive, wear, 0, 0, 0, [new VolumeReading("C:\\", "System", "NTFS", 1000, free)])]);

    private static List<HardwareAlarm> Feed(HardwareWatch watch, HardwareAlertSettings rule, int from, int until, Func<int, SystemSnapshot> snapshot)
    {
        List<HardwareAlarm> alarms = [];

        for (int second = from; second <= until; second += 5)
        {
            alarms.AddRange(watch.Observe(snapshot(second), rule));
        }

        return alarms;
    }

    [Fact]
    public void AHotGraphicsAdapter_MustStayHotForHalfAMinute()
    {
        HardwareWatch watch = new();
        HardwareAlertSettings rule = new() { GraphicsHot = true, GraphicsCelsius = 85 };

        Assert.Empty(Feed(watch, rule, 0, 25, second => Snapshot(second, gpu: 90)));
        HardwareAlarm alarm = Assert.Single(Feed(watch, rule, 30, 300, second => Snapshot(second, gpu: 90)));

        Assert.Equal(HardwareProblem.GraphicsHot, alarm.Problem);
        Assert.Equal("RTX", alarm.Subject);
        Assert.Equal(90, alarm.Value);
    }

    [Fact]
    public void ATemperatureOnItsLevel_FiresOnce_AndCoolingDownReArmsIt()
    {
        HardwareWatch watch = new();
        HardwareAlertSettings rule = new() { GraphicsHot = true, GraphicsCelsius = 85 };

        Assert.Single(Feed(watch, rule, 0, 60, second => Snapshot(second, gpu: 86)));

        // A degree or two either side of the level is not cooling down.
        Assert.Empty(Feed(watch, rule, 65, 600, second => Snapshot(second, gpu: second % 10 == 0 ? 84 : 86)));

        // Five degrees under for two minutes is.
        Assert.Empty(Feed(watch, rule, 605, 730, second => Snapshot(second, gpu: 75)));
        Assert.Single(Feed(watch, rule, 735, 800, second => Snapshot(second, gpu: 90)));
    }

    [Fact]
    public void AMisreadTemperature_IsIgnored()
    {
        HardwareWatch watch = new();
        HardwareAlertSettings rule = new() { GraphicsHot = true, GraphicsCelsius = 85, DriveHot = true, DriveCelsius = 60 };

        Assert.Empty(Feed(watch, rule, 0, 300, second => Snapshot(second, gpu: 255, drive: 0)));
    }

    [Fact]
    public void ANearlyFullDrive_IsNamedByItsLetter_WithWhatIsFree()
    {
        HardwareWatch watch = new();
        HardwareAlertSettings rule = new() { DriveFull = true, DriveFullPercent = 90 };

        HardwareAlarm alarm = Assert.Single(Feed(watch, rule, 0, 300, second => Snapshot(second, free: 50)));

        Assert.Equal("Drive C:", alarm.Subject);
        Assert.Equal(50, alarm.Free);
        Assert.Equal(95, alarm.Value);
    }

    private static SystemSnapshot Twins(int second, double hot, double cool, double? wearA = null, double? wearB = null) => new(
        Start.AddSeconds(second),
        ProcessorReading.Empty,
        [],
        MemoryReading.Empty,
        [
            new DriveReading(0, "Same SSD", "NVMe", false, 1000, hot, wearA, 0, 0, 0, []),
            new DriveReading(1, "Same SSD", "NVMe", false, 1000, cool, wearB, 0, 0, 0, []),
        ]);

    [Fact]
    public void TwoDrivesOfOneModel_AreWatchedApart()
    {
        HardwareWatch watch = new();
        HardwareAlertSettings rule = new() { DriveHot = true, DriveCelsius = 65, DriveWear = true, DriveWearPercent = 80 };

        List<HardwareAlarm> alarms = Feed(watch, rule, 0, 300, second => Twins(second, hot: 70, cool: 40, wearA: 85, wearB: 90));

        HardwareAlarm hot = Assert.Single(alarms, alarm => alarm.Problem == HardwareProblem.DriveHot);
        Assert.Equal(70, hot.Value);
        Assert.Equal(2, alarms.Count(alarm => alarm.Problem == HardwareProblem.DriveWear));
        Assert.Equal(2, alarms.Where(alarm => alarm.Problem == HardwareProblem.DriveWear).Select(alarm => alarm.Tag).Distinct().Count());
    }

    [Fact]
    public void Wear_IsSaidOnce()
    {
        HardwareWatch watch = new();
        HardwareAlertSettings rule = new() { DriveWear = true, DriveWearPercent = 80 };

        HardwareAlarm alarm = Assert.Single(Feed(watch, rule, 0, 600, second => Snapshot(second, wear: 82)));

        Assert.Equal(HardwareProblem.DriveWear, alarm.Problem);
        Assert.Contains("80", alarm.Tag, StringComparison.Ordinal);
    }

    [Fact]
    public void ARuleSwitchedOff_IsForgotten_AndStartsFreshWhenOnAgain()
    {
        HardwareWatch watch = new();
        HardwareAlertSettings on = new() { GraphicsHot = true, GraphicsCelsius = 85 };

        Assert.Single(Feed(watch, on, 0, 60, second => Snapshot(second, gpu: 90)));
        Assert.Empty(Feed(watch, new HardwareAlertSettings(), 65, 70, second => Snapshot(second, gpu: 90)));
        Assert.Single(Feed(watch, on, 75, 120, second => Snapshot(second, gpu: 90)));
    }

    [Fact]
    public void TheProcessor_IsWatchedOnlyWhereItsTemperatureCanBeRead()
    {
        HardwareWatch watch = new();
        HardwareAlertSettings rule = new() { ProcessorHot = true, ProcessorCelsius = 90 };

        List<HardwareAlarm> alarms = Feed(watch, rule, 0, 120, second => Snapshot(second, cpu: 97));

#if STORE
        Assert.Empty(alarms);
#else
        Assert.Equal(HardwareProblem.ProcessorHot, Assert.Single(alarms).Problem);
#endif
    }
}

public sealed class LevelWatchTests
{
    private static readonly DateTimeOffset Start = new(2026, 6, 10, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void AMissingReading_StartsTheClockAgain()
    {
        LevelWatch watch = new();
        TimeSpan hold = TimeSpan.FromSeconds(30);

        Assert.False(watch.Observe(Start, 90, 85, 5, hold, hold));
        Assert.False(watch.Observe(Start.AddSeconds(20), null, 85, 5, hold, hold));
        Assert.False(watch.Observe(Start.AddSeconds(40), 90, 85, 5, hold, hold));
        Assert.True(watch.Observe(Start.AddSeconds(70), 90, 85, 5, hold, hold));
    }
}

public sealed class AwayTallyTests
{
    private static readonly DateTimeOffset Start = new(2026, 6, 10, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void OnlyWhatMovesWhileLocked_IsCounted()
    {
        AwayTally tally = new();

        tally.Count(1_000, 1_000);
        tally.Lock(Start);
        tally.Count(5_000, 200);
        tally.Count(-10, 300);

        AwayReport report = tally.Unlock(Start.AddHours(2))!;

        Assert.Equal(5_000, report.Received);
        Assert.Equal(500, report.Sent);
        Assert.Equal(TimeSpan.FromHours(2), report.Duration);
        Assert.False(tally.IsLocked);
        Assert.Null(tally.Unlock(Start.AddHours(3)));
    }

    [Fact]
    public void ALockHeardTwice_KeepsTheFirst()
    {
        AwayTally tally = new();

        tally.Lock(Start);
        tally.Lock(Start.AddMinutes(10));

        Assert.Equal(Start, tally.Unlock(Start.AddHours(1))!.Since);
    }
}
