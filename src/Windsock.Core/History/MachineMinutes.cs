namespace Windsock.Core.History;

/// <summary>
/// One minute of the machine: its readings summed and their peaks, so any
/// stretch's average is the sums over the counts, whatever the stretch.
/// </summary>
/// <remarks>
/// Percentages are kept in tenths of a percent and temperatures in tenths of a
/// degree, as whole numbers, which SQLite stores in a byte or two.
/// </remarks>
/// <param name="Minute">The epoch minute.</param>
/// <param name="Samples">How many readings went into it.</param>
/// <param name="ProcessorSum">Processor load, tenths of a percent, summed over the readings.</param>
/// <param name="ProcessorPeak">The busiest reading, tenths of a percent.</param>
/// <param name="ProcessorHeatSum">Processor temperature, tenths of a degree, summed over the readings that had one.</param>
/// <param name="ProcessorHeatSamples">How many readings had a processor temperature.</param>
/// <param name="ProcessorHeatPeak">The hottest reading, tenths of a degree, or null when none had one.</param>
/// <param name="GraphicsSum">The busiest adapter's load, tenths of a percent, summed.</param>
/// <param name="GraphicsPeak">The busiest reading of it.</param>
/// <param name="GraphicsHeatPeak">The hottest adapter reading, tenths of a degree, or null.</param>
/// <param name="MemorySum">Memory in use, bytes, summed.</param>
/// <param name="MemoryPeak">The most memory in use at a reading.</param>
/// <param name="MemoryTotal">Physical memory.</param>
/// <param name="DiskRead">Bytes read from every drive over the minute.</param>
/// <param name="DiskWrite">Bytes written to every drive over the minute.</param>
public readonly record struct SystemMinute(
    long Minute,
    int Samples,
    long ProcessorSum,
    int ProcessorPeak,
    long ProcessorHeatSum,
    int ProcessorHeatSamples,
    int? ProcessorHeatPeak,
    long GraphicsSum,
    int GraphicsPeak,
    int? GraphicsHeatPeak,
    long MemorySum,
    long MemoryPeak,
    long MemoryTotal,
    long DiskRead,
    long DiskWrite);

/// <summary>One minute of the connection checks.</summary>
/// <param name="Minute">The epoch minute.</param>
/// <param name="Checks">How many checks ran.</param>
/// <param name="Offline">How many found the internet unreachable.</param>
/// <param name="RouterTried">How many pinged the router.</param>
/// <param name="RouterAnswered">How many of those it answered.</param>
/// <param name="RouterSum">The router's answers, tenths of a millisecond, summed.</param>
/// <param name="RouterPeak">The slowest answer, tenths of a millisecond.</param>
/// <param name="InternetTried">How many pinged the chosen internet address.</param>
/// <param name="InternetAnswered">How many of those it answered.</param>
/// <param name="InternetSum">Its answers, tenths of a millisecond, summed.</param>
/// <param name="InternetPeak">The slowest answer, tenths of a millisecond.</param>
public readonly record struct ConnectionMinute(
    long Minute,
    int Checks,
    int Offline,
    int RouterTried,
    int RouterAnswered,
    long RouterSum,
    int RouterPeak,
    int InternetTried,
    int InternetAnswered,
    long InternetSum,
    int InternetPeak);

/// <summary>The machine over one bucket of History.</summary>
public readonly record struct SystemBucket(
    DateTimeOffset Start,
    int Minutes,
    double ProcessorAverage,
    double ProcessorPeak,
    double? ProcessorHeatAverage,
    double? ProcessorHeatPeak,
    double GraphicsAverage,
    double GraphicsPeak,
    double? GraphicsHeatPeak,
    long MemoryAverage,
    long MemoryPeak,
    long MemoryTotal,
    long DiskRead,
    long DiskWrite);

/// <summary>The connection over one bucket of History.</summary>
public readonly record struct ConnectionBucket(
    DateTimeOffset Start,
    int Minutes,
    int Checks,
    int Offline,
    double? RouterAverageMs,
    double? RouterPeakMs,
    double? RouterLoss,
    double? InternetAverageMs,
    double? InternetPeakMs,
    double? InternetLoss);

/// <summary>History of the machine and of the connection, a minute at a time.</summary>
/// <remarks>
/// Kept a minute at a time for good, like network use: a year of both is some
/// tens of megabytes. Erasing a period of history takes these with it.
/// </remarks>
public interface IMachineHistoryStore
{
    /// <summary>Keeps minutes of the machine; a minute already kept is added to.</summary>
    void WriteSystem(IReadOnlyList<SystemMinute> minutes);

    /// <summary>Keeps minutes of the connection; a minute already kept is added to.</summary>
    void WriteConnection(IReadOnlyList<ConnectionMinute> minutes);

    /// <summary>The machine over a period, by minute, hour or day in local time.</summary>
    IReadOnlyList<SystemBucket> ReadSystem(DateTimeOffset from, DateTimeOffset until, UsageGranularity granularity);

    /// <summary>The connection over a period, by minute, hour or day in local time.</summary>
    IReadOnlyList<ConnectionBucket> ReadConnection(DateTimeOffset from, DateTimeOffset until, UsageGranularity granularity);
}
