using Windsock.Core.Power;

namespace Windsock.Core.Hardware;

/// <summary>
/// What the machine is doing, at one instant.
/// </summary>
public sealed record SystemSnapshot(
    DateTimeOffset Taken,
    ProcessorReading Processor,
    IReadOnlyList<GraphicsReading> Graphics,
    MemoryReading Memory,
    IReadOnlyList<DriveReading> Drives,
    double? BoardCelsius = null)
{
    /// <summary>A reading before anything has been read.</summary>
    public static SystemSnapshot Empty { get; } = new(
        DateTimeOffset.MinValue,
        ProcessorReading.Empty,
        [],
        MemoryReading.Empty,
        []);

    /// <summary>Whether this is a real reading rather than the empty one.</summary>
    public bool HasValue => Taken != DateTimeOffset.MinValue;

    /// <summary>
    /// The batteries taken together, or nothing on a machine without one.
    /// Read every five seconds, however often the rest is.
    /// </summary>
    public BatteryReading? Battery { get; init; }

    /// <summary>Mains or battery, the power mode and battery saver.</summary>
    public PowerReading Power { get; init; } = PowerReading.Unknown;

    /// <summary>Each neural processor's load; empty on a machine without one.</summary>
    public IReadOnlyList<NpuReading> Npus { get; init; } = [];

    /// <summary>
    /// The busiest processes for processor, memory and file I/O, or nothing
    /// while nobody has asked for them.
    /// </summary>
    public BusiestProcesses? Busiest { get; init; }
}

/// <summary>
/// What the processor was doing.
/// </summary>
public sealed record ProcessorReading(
    double BusyPercent,
    double KernelPercent,
    IReadOnlyList<double> Cores,
    double Megahertz,
    int Processes,
    int Threads,
    int Handles,
    double? Celsius = null)
{
    /// <summary>A reading before anything has been read.</summary>
    public static ProcessorReading Empty { get; } = new(0, 0, [], 0, 0, 0, 0);

    /// <summary>
    /// Windows' <c>% Processor Utility</c>: the time busy scaled by the clock
    /// against the base clock, so it passes 100 under turbo and falls below
    /// the time busy when the chip slows down. What Task Manager showed before
    /// it moved to time busy in 2025, and what its Details tab still offers as
    /// "CPU Utility". Nothing where the counter is absent.
    /// </summary>
    /// <remarks>
    /// <see cref="BusyPercent"/> is the share of time not idle, which is what
    /// Task Manager shows since KB5064081 (August 2025) on Windows 11 24H2,
    /// and on 25H2.
    /// </remarks>
    public double? UtilityPercent { get; init; }

    /// <summary>The performance cores' average load, on a hybrid chip.</summary>
    public double? PerformancePercent { get; init; }

    /// <summary>The efficiency cores' average load, on a hybrid chip.</summary>
    public double? EfficiencyPercent { get; init; }

    /// <summary>
    /// The processor package's power, from its energy counter, in the full
    /// edition run as administrator with PawnIO installed; otherwise nothing.
    /// </summary>
    public double? Watts { get; init; }
}

/// <summary>
/// What one neural processor was doing.
/// </summary>
/// <param name="Name">The driver's description of it.</param>
/// <param name="BusyPercent">Its busiest engine, 0 to 100, as Task Manager shows an NPU.</param>
/// <param name="Engines">Each kind of engine and how busy it was.</param>
/// <param name="DedicatedBytes">Its own memory in use, or nothing where it has none or will not say.</param>
/// <param name="SharedBytes">System memory it is using.</param>
/// <param name="Processes">The processes using it, busiest first.</param>
public sealed record NpuReading(
    string Name,
    double BusyPercent,
    IReadOnlyList<EngineLoad> Engines,
    long? DedicatedBytes,
    long? SharedBytes,
    IReadOnlyList<GraphicsProcess> Processes);

/// <summary>
/// What one display adapter was doing.
/// </summary>
public sealed record GraphicsReading(
    string Name,
    double BusyPercent,
    double? Celsius,
    double? Watts,
    double? FanPercent,
    double? Megahertz,
    double? MemoryMegahertz,
    long MemoryUsed,
    long MemoryTotal,
    IReadOnlyList<EngineLoad> Engines,
    IReadOnlyList<GraphicsProcess> Processes);

/// <summary>
/// One kind of work a graphics adapter does, and how busy it was.
/// </summary>
public sealed record EngineLoad(string Kind, double Percent);

/// <summary>
/// A process using the graphics adapter.
/// </summary>
public sealed record GraphicsProcess(int ProcessId, double Percent);

/// <summary>
/// What memory was being used.
/// </summary>
public sealed record MemoryReading(
    long Total,
    long Available,
    long Cached,
    long Committed,
    long CommitLimit,
    long PagedPool,
    long NonPagedPool)
{
    /// <summary>A reading before anything has been read.</summary>
    public static MemoryReading Empty { get; } = new(0, 0, 0, 0, 0, 0, 0);

    /// <summary>Physical memory in use, in bytes.</summary>
    public long Used => Math.Max(0, Total - Available);

    /// <summary>How much of the memory fitted is in use.</summary>
    public double UsedPercent => Total > 0 ? Used * 100.0 / Total : 0;
}

/// <summary>
/// One drive: what it is, and what it was doing.
/// </summary>
public sealed record DriveReading(
    int Index,
    string Model,
    string Bus,
    bool IsSpinning,
    long Size,
    double? Celsius,
    double? LifeUsedPercent,
    double ReadBytesPerSecond,
    double WriteBytesPerSecond,
    double BusyPercent,
    IReadOnlyList<VolumeReading> Volumes)
{
    /// <summary>Bytes free across every volume on this drive.</summary>
    public long Free
    {
        get
        {
            long free = 0;

            foreach (VolumeReading volume in Volumes)
            {
                free += volume.Free;
            }

            return free;
        }
    }

    /// <summary>Bytes in use across every volume on this drive.</summary>
    public long Used
    {
        get
        {
            long used = 0;

            foreach (VolumeReading volume in Volumes)
            {
                used += volume.Size - volume.Free;
            }

            return used;
        }
    }

    /// <summary>
    /// How full the drive is, over the volumes that could be measured.
    /// </summary>
    public double UsedPercent
    {
        get
        {
            long total = Used + Free;
            return total > 0 ? Used * 100.0 / total : 0;
        }
    }
}

/// <summary>
/// One mounted volume.
/// </summary>
public sealed record VolumeReading(
    string Letter,
    string Label,
    string Format,
    long Size,
    long Free)
{
    /// <summary>How full the volume is.</summary>
    public double UsedPercent => Size > 0 ? (Size - Free) * 100.0 / Size : 0;
}
