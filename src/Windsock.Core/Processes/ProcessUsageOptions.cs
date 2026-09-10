namespace Windsock.Core.Processes;

/// <summary>Settings for <see cref="ProcessUsageMonitor"/>.</summary>
public sealed class ProcessUsageOptions
{
    /// <summary>
    /// How often the process table is recalculated.
    /// </summary>
    public TimeSpan Interval { get; set; } = TimeSpan.FromSeconds(1);

    /// <summary>How long a silent process stays in the table.</summary>
    public TimeSpan Retention { get; set; } = TimeSpan.FromSeconds(20);

    /// <summary>
    /// How many past readings to remember for the chart's hover readout.
    /// </summary>
    public int AttributionCapacity { get; set; } = 1800;

    /// <summary>How many processes each remembered reading keeps.</summary>
    public int AttributionWidth { get; set; } = 3;
}
