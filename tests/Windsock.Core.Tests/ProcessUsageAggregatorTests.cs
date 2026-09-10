using Windsock.Core.Processes;
using Xunit;

namespace Windsock.Core.Tests;

public sealed class ProcessUsageAggregatorTests
{
    private static readonly DateTimeOffset Start = new(2026, 1, 1, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void FirstReading_IsABaselineRatherThanARate()
    {
        var traffic = new FakeTraffic { [10] = new ProcessTraffic(5_000_000, 9_000_000, 40) };
        ProcessUsageAggregator aggregator = Build(traffic, new FakeConnections());

        ProcessUsage row = Assert.Single(aggregator.Update(1, Start));
        Assert.Equal(0, row.SendBytesPerSecond);
        Assert.Equal(0, row.ReceiveBytesPerSecond);
    }

    [Fact]
    public void SecondReading_DividesTheDeltaByElapsedTime()
    {
        var traffic = new FakeTraffic { [10] = new ProcessTraffic(1_000, 2_000, 10) };
        ProcessUsageAggregator aggregator = Build(traffic, new FakeConnections());
        aggregator.Update(1, Start);

        traffic[10] = new ProcessTraffic(3_000, 6_000, 30);
        ProcessUsage row = Assert.Single(aggregator.Update(2, Start.AddSeconds(2)));

        Assert.Equal(1_000, row.SendBytesPerSecond);
        Assert.Equal(2_000, row.ReceiveBytesPerSecond);
        Assert.Equal(3_000, row.TotalBytesPerSecond);
    }

    [Fact]
    public void OpenSockets_PutAProcessInTheTableWithoutAnyTraffic()
    {
        var connections = new FakeConnections { [42] = new SocketCounts(2, 1) };
        ProcessUsageAggregator aggregator = Build(new FakeTraffic(), connections);

        ProcessUsage row = Assert.Single(aggregator.Update(1, Start));

        Assert.Equal(42, row.ProcessId);
        Assert.Equal(3, row.Connections);
        Assert.Equal(2, row.Sockets.Tcp);
        Assert.Equal(1, row.Sockets.Udp);
        Assert.Equal(0, row.TotalBytesPerSecond);
    }

    [Fact]
    public void AQuietProcess_LeavesTheTableAfterTheRetentionWindow()
    {
        var connections = new FakeConnections { [42] = new SocketCounts(1, 0) };
        ProcessUsageAggregator aggregator = Build(new FakeTraffic(), connections);
        Assert.Single(aggregator.Update(1, Start));

        connections.Clear();

        Assert.Single(aggregator.Update(1, Start.AddSeconds(5)));
        Assert.Empty(aggregator.Update(1, Start.AddSeconds(31)));
    }

    [Fact]
    public void AProcessThatGoesQuietAndReturns_DoesNotReportItsWholeHistoryAsOneBurst()
    {
        var traffic = new FakeTraffic { [10] = new ProcessTraffic(1_000, 0, 5) };
        ProcessUsageAggregator aggregator = Build(traffic, new FakeConnections());
        aggregator.Update(1, Start);

        // Silent long enough to drop out of the table, but still running.
        Assert.Empty(aggregator.Update(1, Start.AddSeconds(31)));

        traffic[10] = new ProcessTraffic(1_500, 0, 8);
        ProcessUsage row = Assert.Single(aggregator.Update(1, Start.AddSeconds(32)));
        Assert.Equal(500, row.SendBytesPerSecond);
    }

    [Fact]
    public void AnExitedProcess_StopsReportingTheRateItLeftBehind()
    {
        var traffic = new FakeTraffic { [10] = new ProcessTraffic(1_000, 0, 5) };
        var connections = new FakeConnections { [10] = new SocketCounts(1, 0) };
        ProcessUsageAggregator aggregator = Build(traffic, connections);
        aggregator.Update(1, Start);

        traffic[10] = new ProcessTraffic(3_000, 0, 12);
        Assert.Equal(2_000, Assert.Single(aggregator.Update(1, Start.AddSeconds(1))).SendBytesPerSecond);

        // Both sources drop the process: it has exited.
        traffic.Clear();
        connections.Clear();

        ProcessUsage row = Assert.Single(aggregator.Update(1, Start.AddSeconds(2)));
        Assert.Equal(0, row.SendBytesPerSecond);
    }

    [Fact]
    public void NegativeDeltas_AreReportedAsZeroRatherThanAsNegativeRates()
    {
        var traffic = new FakeTraffic { [10] = new ProcessTraffic(5_000, 5_000, 20) };
        ProcessUsageAggregator aggregator = Build(traffic, new FakeConnections());
        aggregator.Update(1, Start);

        // Can happen when a process id is reused and the counters restart.
        traffic[10] = new ProcessTraffic(100, 100, 1);
        ProcessUsage row = Assert.Single(aggregator.Update(1, Start.AddSeconds(1)));

        Assert.Equal(0, row.SendBytesPerSecond);
        Assert.Equal(0, row.ReceiveBytesPerSecond);
    }

    [Fact]
    public void RunningTotals_CountFromWhenWindsockFirstSawTheProcess()
    {
        var traffic = new FakeTraffic { [10] = new ProcessTraffic(5_000_000, 9_000_000, 40) };
        ProcessUsageAggregator aggregator = Build(traffic, new FakeConnections());
        aggregator.Update(1, Start);

        traffic[10] = new ProcessTraffic(5_000_400, 9_001_000, 55);
        ProcessUsage row = Assert.Single(aggregator.Update(1, Start.AddSeconds(1)));

        Assert.Equal(400, row.BytesSent);
        Assert.Equal(1_000, row.BytesReceived);
        Assert.Equal(1_400, row.BytesTransferred);
        Assert.Equal(15, row.PacketsPerSecond);
    }

    [Fact]
    public void ExitedProcesses_HaveTheirCachedNameDiscarded()
    {
        var connections = new FakeConnections { [42] = new SocketCounts(1, 0) };
        var names = new FakeNames();
        ProcessUsageAggregator aggregator = Build(new FakeTraffic(), connections, names);
        aggregator.Update(1, Start);

        connections.Clear();
        aggregator.Update(1, Start.AddSeconds(31));
        Assert.Contains(42, names.Forgotten);
    }

    [Fact]
    public void AfterAPauseInTheAsking_TheFirstReadingIsNotABurst()
    {
        var traffic = new FakeTraffic { [10] = new ProcessTraffic(1_000, 2_000, 10) };
        ProcessUsageAggregator aggregator = Build(traffic, new FakeConnections());
        aggregator.Update(1, Start);

        traffic[10] = new ProcessTraffic(3_600_000_000, 7_200_000_000, 90_000_000);
        aggregator.Rebase();

        ProcessUsage caught = Assert.Single(aggregator.Update(1, Start.AddHours(1)));
        Assert.Equal(0, caught.SendBytesPerSecond);
        Assert.Equal(0, caught.ReceiveBytesPerSecond);
        Assert.Equal(0, caught.PacketsPerSecond);
    }

    [Fact]
    public void AfterAPauseInTheAsking_TheNextReadingRatesNormally()
    {
        var traffic = new FakeTraffic { [10] = new ProcessTraffic(1_000, 2_000, 10) };
        ProcessUsageAggregator aggregator = Build(traffic, new FakeConnections());
        aggregator.Update(1, Start);

        traffic[10] = new ProcessTraffic(500_000, 900_000, 4_000);
        aggregator.Rebase();
        aggregator.Update(1, Start.AddHours(1));

        traffic[10] = new ProcessTraffic(502_000, 903_000, 4_010);
        ProcessUsage row = Assert.Single(aggregator.Update(2, Start.AddHours(1).AddSeconds(2)));

        // One interval on from the swallowed reading, and rates are rates again.
        Assert.Equal(1_000, row.SendBytesPerSecond);
        Assert.Equal(1_500, row.ReceiveBytesPerSecond);
    }

    [Fact]
    public void APauseInTheAsking_DoesNotRewindTheRunningTotals()
    {
        var traffic = new FakeTraffic { [10] = new ProcessTraffic(1_000, 2_000, 10) };
        ProcessUsageAggregator aggregator = Build(traffic, new FakeConnections());
        aggregator.Update(1, Start);
        traffic[10] = new ProcessTraffic(51_000, 302_000, 900);
        aggregator.Rebase();

        ProcessUsage row = Assert.Single(aggregator.Update(1, Start.AddHours(1)));

        Assert.Equal(50_000, row.BytesSent);
        Assert.Equal(300_000, row.BytesReceived);
    }

    private static ProcessUsageAggregator Build(
        FakeTraffic traffic,
        FakeConnections connections,
        IProcessIdentityResolver? names = null) =>
        new(traffic, connections, names ?? new FakeNames())
        {
            Retention = TimeSpan.FromSeconds(20),
        };

    private sealed class FakeTraffic : Dictionary<int, ProcessTraffic>, IProcessTrafficSource
    {
        public TrafficSourceState State => TrafficSourceState.Running;
        public string? Detail => null;
        public string? Explanation => null;

        public void Start()
        {
        }

        public void CopyTo(Dictionary<int, ProcessTraffic> destination)
        {
            destination.Clear();
            foreach ((int processId, ProcessTraffic totals) in this)
            {
                destination[processId] = totals;
            }
        }
    }

    private sealed class FakeConnections : Dictionary<int, SocketCounts>, IProcessConnectionSource
    {
        public void Add(int processId, int tcp) => this[processId] = new SocketCounts(tcp, 0);

        public void CopyTo(Dictionary<int, SocketCounts> destination)
        {
            destination.Clear();
            foreach ((int processId, SocketCounts counts) in this)
            {
                destination[processId] = counts;
            }
        }
    }

    private sealed class FakeNames : IProcessIdentityResolver
    {
        public List<int> Forgotten { get; } = [];

        public ProcessIdentity Resolve(int processId) =>
            new($"process-{processId}", null, null, null);

        public void Forget(int processId) => Forgotten.Add(processId);
    }
}
