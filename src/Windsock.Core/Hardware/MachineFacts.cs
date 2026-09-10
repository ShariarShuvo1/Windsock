namespace Windsock.Core.Hardware;

/// <summary>
/// What the machine is, as opposed to what it is doing.
/// </summary>
public sealed record MachineFacts(
    string ComputerName,
    string Windows,
    string Board,
    string Bios,
    DateTimeOffset Booted,
    ProcessorFacts Processor,
    IReadOnlyList<MemoryModule> Modules,
    IReadOnlyList<GraphicsFacts> Graphics)
{
    /// <summary>A machine nothing could be read from.</summary>
    public static MachineFacts Unknown { get; } = new(
        Environment.MachineName,
        "Windows",
        string.Empty,
        string.Empty,
        DateTimeOffset.Now,
        ProcessorFacts.Unknown,
        [],
        []);

    /// <summary>How long the machine has been up.</summary>
    public TimeSpan Uptime => DateTimeOffset.Now - Booted;
}

/// <summary>
/// The processor's description.
/// </summary>
public sealed record ProcessorFacts(
    string Name,
    int Cores,
    int Threads,
    int BaseMegahertz,
    string Socket,
    long LevelTwoCache,
    long LevelThreeCache,
    int PerformanceCores,
    int EfficiencyCores)
{
    /// <summary>A processor nothing could be read from.</summary>
    public static ProcessorFacts Unknown { get; } = new(
        "Processor",
        Environment.ProcessorCount,
        Environment.ProcessorCount,
        0,
        string.Empty,
        0,
        0,
        0,
        0);

    /// <summary>Whether the chip has two kinds of core.</summary>
    public bool IsHybrid => PerformanceCores > 0 && EfficiencyCores > 0;
}

/// <summary>
/// One memory module, as the firmware describes the slot it is in.
/// </summary>
public sealed record MemoryModule(
    string Slot,
    long Bytes,
    int Megatransfers,
    string Kind,
    string Maker,
    string Part);

/// <summary>
/// One display adapter.
/// </summary>
public sealed record GraphicsFacts(
    string Name,
    string Driver,
    long Memory,
    GraphicsVendor Vendor);

/// <summary>Who made a display adapter.</summary>
public enum GraphicsVendor
{
    Unknown,

    Nvidia,

    Amd,

    Intel,
}
