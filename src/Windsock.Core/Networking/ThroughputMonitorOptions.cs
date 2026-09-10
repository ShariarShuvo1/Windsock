namespace Windsock.Core.Networking;

/// <summary>Tuning for <see cref="ThroughputMonitor"/>.</summary>
public sealed class ThroughputMonitorOptions
{
    /// <summary>How often the interface counters are read. Defaults to 500 ms.</summary>
    public TimeSpan Interval { get; set; } = TimeSpan.FromMilliseconds(500);

    /// <summary>
    /// How many samples the in-memory history retains. Defaults to 3600, which
    /// is thirty minutes at the default interval.
    /// </summary>
    public int HistoryCapacity { get; set; } = 3600;

    /// <summary>
    /// How many samples the chart shows by default, and how many zero samples
    /// are seeded at startup so it is immediately usable. Defaults to 120,
    /// which is one minute at the default interval.
    /// </summary>
    public int InitialVisibleSamples { get; set; } = 120;
}
