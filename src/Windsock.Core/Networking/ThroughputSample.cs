namespace Windsock.Core.Networking;

/// <summary>
/// One measurement of network transfer rate, covering the interval that ended
/// at <paramref name="Timestamp"/>.
/// </summary>
public readonly record struct ThroughputSample(
    DateTimeOffset Timestamp,
    double DownloadBytesPerSecond,
    double UploadBytesPerSecond)
{
    /// <summary>A sample with both rates at zero.</summary>
    public static ThroughputSample Empty => new(DateTimeOffset.MinValue, 0, 0);
}

/// <summary>Cumulative byte counters read from the network interfaces.</summary>
public readonly record struct NetworkTotals(long BytesReceived, long BytesSent);

/// <summary>
/// The raw bytes counted over one sampling interval, before they are turned
/// into a rate.
/// </summary>
public readonly record struct ThroughputDelta(
    DateTimeOffset Timestamp,
    long BytesReceived,
    long BytesSent,
    double Seconds);
