namespace Windsock.Core.Hardware;

/// <summary>
/// Reads what each neural processor is doing, from the same engine counters
/// a graphics card's load comes from.
/// </summary>
/// <remarks>
/// Not seen on real hardware: the development machine has no NPU. What is
/// known is in <c>.reference/system/hardware.md</c>: Task Manager shows an NPU
/// with engines named "NPU - Compute" or "NPU - Neural"; a third-party tool
/// tested on an Intel Core Ultra reads it from <c>GPU Engine</c> instances
/// typed <c>Neural</c> under the NPU's LUID. So the NPU is decided by DXCore,
/// by adapter, and every engine under its LUID counts, whatever its type.
/// Machines without one get an empty list and nothing is drawn.
/// </remarks>
public static class NpuProbe
{
    private const int TopProcesses = 6;

    /// <summary>Finds the neural processors. Costly once, and the answer does not change.</summary>
    public static IReadOnlyList<NpuFacts> Find() => DxCoreAdapters.Npus();

    /// <summary>Each neural processor's load, from the last collection's counters.</summary>
    public static IReadOnlyList<NpuReading> Read(IReadOnlyList<NpuFacts> npus, EngineTally tally)
    {
        ArgumentNullException.ThrowIfNull(npus);
        ArgumentNullException.ThrowIfNull(tally);

        if (npus.Count == 0)
        {
            return [];
        }

        List<NpuReading> readings = new(npus.Count);

        foreach (NpuFacts npu in npus)
        {
            (double busy, List<EngineLoad> engines) = tally.Load(npu.Luid);

            readings.Add(new NpuReading(
                npu.Name,
                Math.Clamp(busy, 0, 100),
                engines,
                tally.Dedicated(npu.Luid) is > 0 and var dedicated ? dedicated : null,
                tally.Shared(npu.Luid),
                tally.Processes(npu.Luid, TopProcesses)));
        }

        return readings;
    }
}
