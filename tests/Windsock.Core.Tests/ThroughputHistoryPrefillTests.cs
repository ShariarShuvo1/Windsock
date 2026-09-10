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

    [Fact]
    public void Prefill_RejectsNegativeCounts()
    {
        var history = new ThroughputHistory(10);

        Assert.Throws<ArgumentOutOfRangeException>(() => history.Prefill(-1, Now, Interval));
    }
}
