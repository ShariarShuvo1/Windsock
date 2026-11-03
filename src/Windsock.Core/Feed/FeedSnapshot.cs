using Windsock.Core.History;

namespace Windsock.Core.Feed;

/// <summary>The network rates now.</summary>
/// <param name="At">When they were measured, or nothing before the first reading.</param>
/// <param name="DownBytesPerSecond">Received, or nothing when not measured.</param>
/// <param name="UpBytesPerSecond">Sent, or nothing when not measured.</param>
public sealed record FeedRates(DateTimeOffset? At, double? DownBytesPerSecond, double? UpBytesPerSecond);

/// <summary>The connection now.</summary>
/// <param name="Status">"online", "offline" or "checking".</param>
/// <param name="Since">When that began, when known.</param>
/// <param name="Cause">Why it is offline, in a word, when it is.</param>
/// <param name="Network">The network's name, when known.</param>
/// <param name="RouterMs">How long the router took to answer the last check, in milliseconds, or nothing.</param>
/// <param name="InternetMs">How long the internet address took, or nothing (none chosen, or no answer).</param>
/// <param name="Vpn">Whether a VPN is up, when known.</param>
public sealed record FeedConnection(
    string Status,
    DateTimeOffset? Since,
    string? Cause,
    string? Network,
    double? RouterMs,
    double? InternetMs,
    bool? Vpn);

/// <summary>Bytes recorded over a stretch.</summary>
/// <param name="BytesDown">Received.</param>
/// <param name="BytesUp">Sent.</param>
public sealed record FeedTotals(long BytesDown, long BytesUp)
{
    /// <summary>Both directions.</summary>
    public long BytesTotal => BytesDown + BytesUp;
}

/// <summary>Today's and this month's recorded use.</summary>
/// <param name="Today">Since local midnight.</param>
/// <param name="Month">Since the first of the month.</param>
/// <param name="AsOf">When the totals were read: history is written a minute at a time, so this lags by up to a minute.</param>
public sealed record FeedUsage(FeedTotals Today, FeedTotals Month, DateTimeOffset AsOf);

/// <summary>The data plan in force, as the plans page shows it.</summary>
/// <param name="Name">The plan's name.</param>
/// <param name="LimitBytes">Its allowance this cycle, or nothing for a plan that only keeps count.</param>
/// <param name="UsedBytes">What counts against it so far.</param>
/// <param name="LeftBytes">What is left, or nothing without an allowance.</param>
/// <param name="UsedFraction">The share used, which may pass 1, or nothing without an allowance.</param>
/// <param name="CycleStart">When the cycle began, when known.</param>
/// <param name="CycleEnd">When it ends, when known.</param>
public sealed record FeedPlan(
    string Name,
    long? LimitBytes,
    long UsedBytes,
    long? LeftBytes,
    double? UsedFraction,
    DateTimeOffset? CycleStart,
    DateTimeOffset? CycleEnd);

/// <summary>How busy the machine is.</summary>
/// <param name="At">When it was read, or nothing before the first reading.</param>
/// <param name="ProcessorPercent">Processor busy.</param>
/// <param name="MemoryPercent">Memory in use.</param>
/// <param name="GraphicsPercent">The busiest graphics adapter.</param>
/// <param name="DiskPercent">The busiest drive.</param>
/// <param name="ProcessorCelsius">The processor's temperature, where it can be read.</param>
/// <param name="GraphicsCelsius">The hottest graphics adapter, where it can be read.</param>
public sealed record FeedSystem(
    DateTimeOffset? At,
    double? ProcessorPercent,
    double? MemoryPercent,
    double? GraphicsPercent,
    double? DiskPercent,
    double? ProcessorCelsius,
    double? GraphicsCelsius);

/// <summary>The whole household's traffic, from the router's own counters.</summary>
/// <param name="At">When it was measured.</param>
/// <param name="DownBytesPerSecond">Received by every device together, or nothing.</param>
/// <param name="UpBytesPerSecond">Sent, or nothing.</param>
/// <param name="Router">The router's maker and model, when it said.</param>
public sealed record FeedHousehold(DateTimeOffset? At, double? DownBytesPerSecond, double? UpBytesPerSecond, string? Router);

/// <summary>Everything the feed tells, at one moment. Nothing is a value not known, never a zero standing in for one.</summary>
/// <param name="At">When it was put together.</param>
/// <param name="Rates">The network rates.</param>
/// <param name="Connection">The connection.</param>
/// <param name="Usage">Today's and this month's use, or nothing when history cannot be read.</param>
/// <param name="Plan">The data plan in force, or nothing when there is none.</param>
/// <param name="System">How busy the machine is.</param>
/// <param name="Household">The household's traffic, or nothing when it is not being read.</param>
public sealed record FeedSnapshot(
    DateTimeOffset At,
    FeedRates Rates,
    FeedConnection Connection,
    FeedUsage? Usage,
    FeedPlan? Plan,
    FeedSystem System,
    FeedHousehold? Household);

/// <summary>One stretch of recorded use.</summary>
/// <param name="Start">When it begins, local time.</param>
/// <param name="BytesDown">Received.</param>
/// <param name="BytesUp">Sent.</param>
/// <param name="Minutes">How many of its minutes were recorded.</param>
public sealed record FeedBucket(DateTimeOffset Start, long BytesDown, long BytesUp, int Minutes);

/// <summary>Recorded use over a period, broken into minutes, hours or days.</summary>
/// <param name="From">The period's start.</param>
/// <param name="Until">Its end.</param>
/// <param name="Granularity">How it is broken up.</param>
/// <param name="Total">Everything in it.</param>
/// <param name="Buckets">Each minute, hour or day that has anything recorded.</param>
public sealed record FeedUsageRange(
    DateTimeOffset From,
    DateTimeOffset Until,
    UsageGranularity Granularity,
    FeedTotals Total,
    IReadOnlyList<FeedBucket> Buckets);

/// <summary>Where the feed's figures come from.</summary>
public interface IFeedSource
{
    /// <summary>Everything, now. Never throws.</summary>
    FeedSnapshot Read();

    /// <summary>Recorded use over a period, or nothing when history cannot be read. Never throws.</summary>
    FeedUsageRange? ReadUsage(DateTimeOffset from, DateTimeOffset until, UsageGranularity granularity);

    /// <summary>
    /// Asks the monitors behind the figures to keep them fresh until the token is
    /// let go: the feed holds one while it has clients.
    /// </summary>
    IDisposable Watch();
}
