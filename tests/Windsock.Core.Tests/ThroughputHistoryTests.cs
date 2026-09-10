using Windsock.Core.Networking;
using Xunit;

namespace Windsock.Core.Tests;

public sealed class ThroughputHistoryTests
{
    private static ThroughputSample SampleAt(int index) =>
        new(DateTimeOffset.UnixEpoch.AddSeconds(index), index, index * 10);

    [Fact]
    public void CopyTo_ReturnsSamplesOldestFirst()
    {
        var history = new ThroughputHistory(4);
        for (int i = 0; i < 3; i++)
        {
            history.Add(SampleAt(i));
        }

        Span<ThroughputSample> destination = new ThroughputSample[4];
        int written = history.CopyTo(destination);

        Assert.Equal(3, written);
        Assert.Equal(0, destination[0].DownloadBytesPerSecond);
        Assert.Equal(1, destination[1].DownloadBytesPerSecond);
        Assert.Equal(2, destination[2].DownloadBytesPerSecond);
    }

    [Fact]
    public void Add_EvictsOldestOnceAtCapacity()
    {
        var history = new ThroughputHistory(3);
        for (int i = 0; i < 5; i++)
        {
            history.Add(SampleAt(i));
        }

        Assert.Equal(3, history.Count);

        Span<ThroughputSample> destination = new ThroughputSample[3];
        int written = history.CopyTo(destination);

        Assert.Equal(3, written);
        Assert.Equal(2, destination[0].DownloadBytesPerSecond);
        Assert.Equal(3, destination[1].DownloadBytesPerSecond);
        Assert.Equal(4, destination[2].DownloadBytesPerSecond);
    }

    [Fact]
    public void CopyTo_KeepsNewestWhenDestinationIsTooSmall()
    {
        var history = new ThroughputHistory(5);
        for (int i = 0; i < 5; i++)
        {
            history.Add(SampleAt(i));
        }

        Span<ThroughputSample> destination = new ThroughputSample[2];
        int written = history.CopyTo(destination);

        Assert.Equal(2, written);
        Assert.Equal(3, destination[0].DownloadBytesPerSecond);
        Assert.Equal(4, destination[1].DownloadBytesPerSecond);
    }

    [Fact]
    public void Clear_EmptiesTheRing()
    {
        var history = new ThroughputHistory(3);
        history.Add(SampleAt(1));
        history.Clear();

        Assert.Equal(0, history.Count);
        Assert.Equal(0, history.CopyTo(new ThroughputSample[3]));
    }

    [Fact]
    public void Constructor_RejectsNonPositiveCapacity()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new ThroughputHistory(0));
    }
}
