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

    /// <summary>
    /// Whether Windows said there was a system battery when the machine was
    /// identified. A laptop's, or a UPS reporting itself as one; the live
    /// figures are <see cref="SystemSnapshot.Battery"/>, which can come and go.
    /// </summary>
    public bool HasBattery { get; init; }

    /// <summary>The neural processors Windows lists, or none: most machines have none.</summary>
    public IReadOnlyList<NpuFacts> Npus { get; init; } = [];
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

    /// <summary>
    /// Every logical processor, sorted by group and index as
    /// <see cref="ProcessorReading.Cores"/> is, with the kind of core it sits
    /// on. Empty where Windows would not say.
    /// </summary>
    public IReadOnlyList<LogicalProcessor> Logical { get; init; } = [];
}

/// <summary>
/// A neural processing unit: an accelerator that does machine learning and
/// nothing else, listed by DXCore rather than by the display stack.
/// </summary>
/// <param name="Name">The driver's description of it.</param>
/// <param name="Luid">Its identifier in the form the performance counters spell it.</param>
public sealed record NpuFacts(string Name, string Luid);

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
