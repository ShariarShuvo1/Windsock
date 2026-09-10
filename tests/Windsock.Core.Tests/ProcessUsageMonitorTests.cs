using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Windsock.Core.Processes;
using Xunit;

namespace Windsock.Core.Tests;

/// <summary>
/// The monitor's own behaviour: who is asking for the table, and whether it is
/// being recalculated for them. The arithmetic belongs to the aggregator and is
/// tested there.
/// </summary>
public sealed class ProcessUsageMonitorTests
{
    [Fact]
    public void WithNobodyWatching_TheTableIsNotBeingRecalculated()
    {
        using ProcessUsageMonitor monitor = Build();

        Assert.False(monitor.IsWatched);
    }

    [Fact]
    public void OneWatcher_IsEnoughToStartAndEnoughToStop()
    {
        using ProcessUsageMonitor monitor = Build();

        IDisposable watch = monitor.Watch();
        Assert.True(monitor.IsWatched);

        watch.Dispose();
        Assert.False(monitor.IsWatched);
    }

    [Fact]
    public void TheFirstOfTwoWatchersToLeave_DoesNotTakeTheTableFromTheOther()
    {
        using ProcessUsageMonitor monitor = Build();

        IDisposable first = monitor.Watch();
        IDisposable second = monitor.Watch();

        first.Dispose();
        Assert.True(monitor.IsWatched);

        second.Dispose();
        Assert.False(monitor.IsWatched);
    }

    [Fact]
    public void AWatchLetGoTwice_DoesNotCountAsTwo()
    {
        using ProcessUsageMonitor monitor = Build();

        IDisposable held = monitor.Watch();
        IDisposable spare = monitor.Watch();

        held.Dispose();
        held.Dispose();

        Assert.True(monitor.IsWatched);

        spare.Dispose();
        Assert.False(monitor.IsWatched);
    }

    private static ProcessUsageMonitor Build() =>
        new(
            new IdleTraffic(),
            new NoConnections(),
            new NoNames(),
            new ProcessAttributionHistory(8, 3, TimeSpan.FromSeconds(1)),
            Options.Create(new ProcessUsageOptions()),
            NullLogger<ProcessUsageMonitor>.Instance);

    private sealed class IdleTraffic : IProcessTrafficSource
    {
        public TrafficSourceState State => TrafficSourceState.Stopped;
        public string? Detail => null;
        public string? Explanation => null;

        public void Start()
        {
        }

        public void CopyTo(Dictionary<int, ProcessTraffic> destination) => destination.Clear();
    }

    private sealed class NoConnections : IProcessConnectionSource
    {
        public void CopyTo(Dictionary<int, SocketCounts> destination) => destination.Clear();
    }

    private sealed class NoNames : IProcessIdentityResolver
    {
        public ProcessIdentity Resolve(int processId) =>
            new(processId.ToString(System.Globalization.CultureInfo.InvariantCulture), null, null, null);

        public void Forget(int processId)
        {
        }
    }
}
