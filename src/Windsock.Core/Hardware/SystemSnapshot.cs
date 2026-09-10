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
}

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
