namespace Windsock.Core.Apps;

/// <summary>A stretch of time, in whole minutes since the Unix epoch.</summary>
public readonly record struct UsageWindow(long StartMinute, int Minutes)
{
    /// <summary>The first minute after the window. Exclusive.</summary>
    public long EndMinute => StartMinute + Minutes;
}

/// <summary>What one app moved within one window, as Windows counted it.</summary>
public readonly record struct AppUsageRecord(string AttributionId, long BytesReceived, long BytesSent)
{
    /// <summary>Both directions.</summary>
    public long BytesTotal => BytesReceived + BytesSent;
}

/// <summary>Every app Windows counted within one window.</summary>
public sealed record AppUsageReading(UsageWindow Window, IReadOnlyList<AppUsageRecord> Records);

/// <summary>One app's use of one stored window.</summary>
public readonly record struct AppUsageSlice(
    long StartMinute,
    int Minutes,
    string AttributionId,
    long BytesReceived,
    long BytesSent);

/// <summary>One app's use across a period.</summary>
public readonly record struct AppUsageTotal(string AttributionId, long BytesReceived, long BytesSent)
{
    /// <summary>Both directions.</summary>
    public long BytesTotal => BytesReceived + BytesSent;
}

/// <summary>
/// Everything Windows counted in one stored window, every app together: the
/// machine's use, as Windows saw it.
/// </summary>
public readonly record struct UsageSpan(long StartMinute, int Minutes, long BytesReceived, long BytesSent)
{
    /// <summary>The first minute after the span. Exclusive.</summary>
    public long EndMinute => StartMinute + Minutes;
}

/// <summary>
/// Asks Windows how much each app used the network.
/// </summary>
public interface IAppUsageReader
{
    /// <summary>
    /// Reads each window, every network Windows has a profile for together.
    /// </summary>
    /// <exception cref="AppUsageUnavailableException">
    /// Windows would not answer for any network, and nothing about any window is known.
    /// </exception>
    /// <remarks>
    /// A network that refuses while others answer is left out of the readings
    /// and listed in <see cref="LeftOut"/>, so it does not stop every read.
    /// </remarks>
    Task<IReadOnlyList<AppUsageReading>> ReadAsync(IReadOnlyList<UsageWindow> windows, CancellationToken token);

    /// <summary>
    /// Networks being left out of reads because Windows refused them, as of the
    /// last read. Empty when every network answered.
    /// </summary>
    IReadOnlyList<LeftOutNetwork> LeftOut => [];
}

/// <summary>Windows' data usage could not be read at all.</summary>
public sealed class AppUsageUnavailableException : Exception
{
    public AppUsageUnavailableException()
    {
    }

    public AppUsageUnavailableException(string message)
        : base(message)
    {
    }

    public AppUsageUnavailableException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
