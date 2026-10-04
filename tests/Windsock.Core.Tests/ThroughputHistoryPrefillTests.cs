using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Windsock.Core.Networking;
using Xunit;

namespace Windsock.Core.Tests;

public sealed class ThroughputHistoryPrefillTests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 4, 12, 0, 0, TimeSpan.Zero);
    private static readonly TimeSpan Interval = TimeSpan.FromMilliseconds(500);

    [Fact]
    public void Prefill_FillsTheWindowWithZeroSamples()
    {
        var history = new ThroughputHistory(3600);

        history.Prefill(120, Now, Interval);

        Assert.Equal(120, history.Count);

        Span<ThroughputSample> destination = new ThroughputSample[120];
        int written = history.CopyTo(destination);

        Assert.Equal(120, written);
        foreach (ThroughputSample sample in destination)
        {
            Assert.Equal(0, sample.DownloadBytesPerSecond);
            Assert.Equal(0, sample.UploadBytesPerSecond);
        }
    }

    [Fact]
    public void Prefill_BackdatesSamplesEndingJustBeforeTheGivenTime()
    {
        var history = new ThroughputHistory(10);

        history.Prefill(4, Now, Interval);

        Span<ThroughputSample> destination = new ThroughputSample[4];
        history.CopyTo(destination);

        // Oldest first, one interval apart, the newest one interval before now.
        Assert.Equal(Now - (Interval * 4), destination[0].Timestamp);
        Assert.Equal(Now - (Interval * 3), destination[1].Timestamp);
        Assert.Equal(Now - Interval, destination[3].Timestamp);
    }

    [Fact]
    public void Prefill_NeverExceedsCapacity()
    {
        var history = new ThroughputHistory(8);

        history.Prefill(500, Now, Interval);

        Assert.Equal(8, history.Count);
    }

    [Fact]
    public void Prefill_LeavesRoomForRealSamplesToFollow()
    {
        var history = new ThroughputHistory(10);
        history.Prefill(4, Now, Interval);

        history.Add(new ThroughputSample(Now, 1024, 512));

        Span<ThroughputSample> destination = new ThroughputSample[10];
        int written = history.CopyTo(destination);

        Assert.Equal(5, written);
        Assert.Equal(1024, destination[4].DownloadBytesPerSecond);
    }

    /// <summary>
    /// The filler that gives the chart its axes at launch is dated before the
    /// monitor says it began measuring, so a peak or an average taken over the
    /// history can leave it out instead of counting it as a minute of nothing.
    /// </summary>
    [Fact]
    public void Monitor_DatesItsFillerBeforeMeasuringBegan()
    {
        var monitor = new ThroughputMonitor(
            new StillTotals(),
            Options.Create(new ThroughputMonitorOptions()),
            NullLogger<ThroughputMonitor>.Instance);

        ThroughputSample[] samples = new ThroughputSample[monitor.History.Capacity];
        int count = monitor.History.CopyTo(samples);

        Assert.True(count > 0);
        Assert.All(samples[..count], sample => Assert.True(sample.Timestamp < monitor.MeasuringSince));

        DateTimeOffset before = monitor.MeasuringSince;
        monitor.SetInterval(TimeSpan.FromSeconds(2));

        Assert.True(monitor.MeasuringSince >= before);
        count = monitor.History.CopyTo(samples);
        Assert.All(samples[..count], sample => Assert.True(sample.Timestamp < monitor.MeasuringSince));
    }

    private sealed class StillTotals : INetworkTotalsSource
    {
        public NetworkTotals ReadTotals() => default;
    }

    [Fact]
    public void Prefill_RejectsNegativeCounts()
    {
        var history = new ThroughputHistory(10);

        Assert.Throws<ArgumentOutOfRangeException>(() => history.Prefill(-1, Now, Interval));
    }
}
