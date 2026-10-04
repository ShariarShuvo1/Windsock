using Microsoft.Data.Sqlite;
using Windsock.Core.Connectivity;
using Windsock.Core.Hardware;
using Windsock.Core.History;
using Xunit;

namespace Windsock.Core.Tests;

public sealed class MachineHistoryTests : IDisposable
{
    private static readonly DateTimeOffset Ten = new(2026, 9, 16, 10, 0, 0, TimeSpan.Zero);

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

    [Fact]
    public void Readings_BecomeAMinuteOfSumsAndPeaks()
    {
        SystemMinuteTally tally = new();

        // Every five seconds: load 20% then 40%, a drive reading 1 MB/s.
        for (int second = 0; second < 60; second += 5)
        {
            double load = second < 30 ? 20 : 40;
            Assert.Null(tally.Add(Snapshot(Ten.AddSeconds(second), load, celsius: second == 55 ? 71.5 : 60, readPerSecond: 1_000_000)));
        }

        SystemMinute? minute = tally.Add(Snapshot(Ten.AddSeconds(60), 90, 60, 0));

        Assert.NotNull(minute);
        Assert.Equal(EpochMinute.From(Ten), minute.Value.Minute);
        Assert.Equal(12, minute.Value.Samples);
        Assert.Equal((6 * 200) + (6 * 400), minute.Value.ProcessorSum);
        Assert.Equal(400, minute.Value.ProcessorPeak);
        Assert.Equal(715, minute.Value.ProcessorHeatPeak);
        Assert.Equal(16_000_000_000L, minute.Value.MemoryPeak);

        // The first reading stands for a second, each after it for the five since.
        Assert.Equal(1_000_000 + (11 * 5_000_000), minute.Value.DiskRead);
    }

    [Fact]
    public void Checks_BecomeAMinute_WithLossAndTimes()
    {
        ConnectionMinuteTally tally = new();
        ConnectionFacts facts = Facts();

        for (int second = 0; second < 60; second += 5)
        {
            TimeSpan? router = second == 30 ? null : TimeSpan.FromMilliseconds(second == 45 ? 12.5 : 2);
            ConnectionSample sample = new(Ten.AddSeconds(second), facts, router, null, InternetChecked: false);
            Assert.Null(tally.Add(sample, offline: second >= 50));
        }

        ConnectionMinute? minute = tally.Add(new ConnectionSample(Ten.AddSeconds(60), facts, null, null, false), false);

        Assert.NotNull(minute);
        Assert.Equal(12, minute.Value.Checks);
        Assert.Equal(2, minute.Value.Offline);
        Assert.Equal(12, minute.Value.RouterTried);
        Assert.Equal(11, minute.Value.RouterAnswered);
        Assert.Equal(125, minute.Value.RouterPeak);
        Assert.Equal(0, minute.Value.InternetTried);
    }

    [Fact]
    public void TheStore_KeepsMinutes_AddsUpARestart_AndBucketsThem()
    {
        using SqliteUsageHistoryStore store = new(File);
        long minute = EpochMinute.From(Ten);

        store.WriteSystem(
        [
            new SystemMinute(minute, 6, 600, 150, 3_600, 6, 650, 60, 20, null, 6 * 8_000_000_000L, 8_000_000_000L, 16_000_000_000L, 100, 200),
            new SystemMinute(minute + 1, 12, 3_600, 900, 0, 0, null, 0, 0, 550, 12 * 9_000_000_000L, 9_500_000_000L, 16_000_000_000L, 10, 20),
        ]);

        // Closed and opened again inside the first minute.
        store.WriteSystem([new SystemMinute(minute, 6, 1_800, 400, 4_200, 6, 710, 0, 0, null, 6 * 8_000_000_000L, 8_100_000_000L, 16_000_000_000L, 50, 0)]);

        IReadOnlyList<SystemBucket> minutes = store.ReadSystem(Ten, Ten.AddMinutes(2), UsageGranularity.Minute);
        Assert.Equal(2, minutes.Count);
        Assert.Equal(20, minutes[0].ProcessorAverage, 3);
        Assert.Equal(40, minutes[0].ProcessorPeak, 3);
        Assert.Equal(65, minutes[0].ProcessorHeatAverage!.Value, 3);
        Assert.Equal(71, minutes[0].ProcessorHeatPeak!.Value, 3);
        Assert.Equal(150, minutes[0].DiskRead);

        SystemBucket hour = Assert.Single(store.ReadSystem(Ten.AddHours(-1), Ten.AddHours(1), UsageGranularity.Hour));
        Assert.Equal(2, hour.Minutes);
        Assert.Equal((600 + 1_800 + 3_600) / 10.0 / 24, hour.ProcessorAverage, 3);
        Assert.Equal(90, hour.ProcessorPeak, 3);
        Assert.Equal(55, hour.GraphicsHeatPeak!.Value, 3);
        Assert.Equal(9_500_000_000L, hour.MemoryPeak);

        store.WriteConnection([new ConnectionMinute(minute, 12, 2, 12, 9, 9 * 20, 80, 12, 12, 12 * 150, 400)]);
        ConnectionBucket connection = Assert.Single(store.ReadConnection(Ten, Ten.AddMinutes(5), UsageGranularity.Minute));
        Assert.Equal(2.0, connection.RouterAverageMs!.Value, 3);
        Assert.Equal(0.25, connection.RouterLoss!.Value, 3);
        Assert.Equal(15.0, connection.InternetAverageMs!.Value, 3);
        Assert.Equal(0.0, connection.InternetLoss!.Value, 3);

        store.Delete(Ten, Ten.AddMinutes(1));
        Assert.Single(store.ReadSystem(Ten, Ten.AddMinutes(2), UsageGranularity.Minute));
        Assert.Empty(store.ReadConnection(Ten, Ten.AddMinutes(5), UsageGranularity.Minute));

        store.RemoveEverything();
        Assert.Empty(store.ReadSystem(Ten, Ten.AddMinutes(2), UsageGranularity.Minute));
    }

    [Fact]
    public void AMinuteGatheredAsHistoryIsErased_IsNotWrittenBack()
    {
        using SqliteUsageHistoryStore store = new(File, new ManualClock(Ten.AddSeconds(30)));
        long minute = EpochMinute.From(Ten);

        store.RemoveEverything();

        store.WriteSystem([new SystemMinute(minute, 6, 600, 150, 0, 0, null, 0, 0, null, 1, 1, 1, 0, 0)]);
        store.WriteConnection([new ConnectionMinute(minute, 12, 0, 12, 12, 100, 10, 0, 0, 0, 0)]);

        Assert.Empty(store.ReadSystem(Ten, Ten.AddMinutes(1), UsageGranularity.Minute));
        Assert.Empty(store.ReadConnection(Ten, Ten.AddMinutes(1), UsageGranularity.Minute));

        // The next minute is new history, and kept.
        store.WriteSystem([new SystemMinute(minute + 1, 6, 600, 150, 0, 0, null, 0, 0, null, 1, 1, 1, 0, 0)]);
        Assert.Single(store.ReadSystem(Ten, Ten.AddMinutes(2), UsageGranularity.Minute));
    }

    private sealed class ManualClock(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }

    private static SystemSnapshot Snapshot(DateTimeOffset at, double load, double celsius, double readPerSecond) => new(
        at,
        new ProcessorReading(load, 1, [load], 3_000, 300, 4_000, 90_000, celsius),
        [],
        new MemoryReading(32_000_000_000L, 16_000_000_000L, 1, 1, 1, 1, 1),
        [new DriveReading(0, "Disk", "NVMe", false, 1_000_000_000_000L, null, null, readPerSecond, 0, 1, [])]);

    private static ConnectionFacts Facts() => new(default, null, "192.168.0.1", false);
}
