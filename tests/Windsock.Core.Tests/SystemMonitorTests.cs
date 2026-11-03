using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Windsock.Core.Hardware;
using Windsock.Core.Power;
using Xunit;

namespace Windsock.Core.Tests;

/// <summary>
/// The monitor's own life: what it does once, what it tries again, and that
/// it stops cleanly.
/// </summary>
public sealed class SystemMonitorTests
{
    /// <summary>
    /// With the counters refusing to open, the query is asked for again at
    /// every reading, and nothing else is: one battery source, one
    /// identification. Until 2026-10-05 every reading built the probes again
    /// and left the old ones open.
    /// </summary>
    [Fact]
    public async Task FailingCounters_AreRetried_AndNothingElseIs()
    {
        int opened = 0;
        int batteries = 0;
        int identified = 0;

        SystemMonitorOptions options = new()
        {
            Interval = TimeSpan.FromMilliseconds(20),
            BatterySource = () =>
            {
                Interlocked.Increment(ref batteries);
                return new NoBattery();
            },
            QueryOpener = () =>
            {
                Interlocked.Increment(ref opened);
                return null;
            },
        };

        using SystemMonitor monitor = new(Options.Create(options), NullLogger<SystemMonitor>.Instance);
        monitor.Identified += (_, _) => Interlocked.Increment(ref identified);

        await monitor.StartAsync(TestContext.Current.CancellationToken);

        using (monitor.Watch())
        {
            for (int wait = 0; wait < 500 && Volatile.Read(ref opened) < 4; wait++)
            {
                await Task.Delay(20, TestContext.Current.CancellationToken);
            }
        }

        await monitor.StopAsync(TestContext.Current.CancellationToken);

        Assert.True(Volatile.Read(ref opened) >= 4, $"The query was asked for {opened} times.");
        Assert.Equal(1, batteries);
        Assert.Equal(1, identified);
        Assert.True(monitor.IsIdentified);
        Assert.False(monitor.Latest.HasValue);
    }

    /// <summary>Disposing after a stop, twice even, frees what is held and throws nothing.</summary>
    [Fact]
    public async Task Dispose_AfterReadings_IsQuiet()
    {
        SystemMonitorOptions options = new()
        {
            Interval = TimeSpan.FromMilliseconds(20),
            BatterySource = () => new NoBattery(),
        };

        SystemMonitor monitor = new(Options.Create(options), NullLogger<SystemMonitor>.Instance);
        await monitor.StartAsync(TestContext.Current.CancellationToken);

        using (monitor.WatchBusiest())
        {
            for (int wait = 0; wait < 500 && monitor.Latest.Busiest is null; wait++)
            {
                await Task.Delay(20, TestContext.Current.CancellationToken);
            }
        }

        Assert.NotNull(monitor.Latest.Busiest);

        monitor.Dispose();
        monitor.Dispose();
    }

    private sealed class NoBattery : IBatterySource
    {
        public SystemPowerStatus ReadSystemStatus() => new(1, 128, 255, 0, BatteryLayout.Unknown, BatteryLayout.Unknown);

        public BatteryScan ReadBatteries(bool refresh) => BatteryScan.Empty;

        public void Dispose()
        {
        }
    }
}
