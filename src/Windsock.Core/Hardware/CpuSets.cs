using System.Buffers.Binary;
using System.Runtime.InteropServices;

namespace Windsock.Core.Hardware;

/// <summary>What kind of core a logical processor sits on.</summary>
public enum CoreKind
{
    /// <summary>The chip has one kind of core.</summary>
    Uniform,

    /// <summary>A performance core: the fastest class on a hybrid chip.</summary>
    Performance,

    /// <summary>An efficiency core: any slower class.</summary>
    Efficiency,
}

/// <summary>
/// One logical processor: where the counters file it, and which core it is on.
/// </summary>
/// <param name="Group">The processor group, the first number of a counter instance such as <c>0,5</c>.</param>
/// <param name="Index">Its number within the group, the second.</param>
/// <param name="Core">The core it shares with its sibling thread, if any.</param>
/// <param name="EfficiencyClass">Windows' rank for the core: higher is faster.</param>
/// <param name="Kind">Performance or efficiency on a hybrid chip.</param>
public readonly record struct LogicalProcessor(int Group, int Index, int Core, int EfficiencyClass, CoreKind Kind);

/// <summary>
/// Reads which logical processor sits on which kind of core, from
/// <c>GetSystemCpuSetInformation</c>.
/// </summary>
/// <remarks>
/// Each entry is a <c>SYSTEM_CPU_SET_INFORMATION</c>, walked by its own size:
/// Size at 0, Type at 4 (0 is a CPU set), Id at 8, Group (16 bits) at 12,
/// LogicalProcessorIndex at 14, CoreIndex at 15, LastLevelCacheIndex at 16,
/// NumaNodeIndex at 17, EfficiencyClass at 18, flags at 19. Measured on this
/// i5-12600K: 32 bytes an entry, class 1 on the twelve threads of the six
/// performance cores, 0 on the four efficiency cores.
/// </remarks>
public static partial class CpuSets
{
    private const int CpuSetType = 0;
    private const int Smallest = 20;

    /// <summary>Reads this machine's logical processors, sorted as the counters are.</summary>
    public static IReadOnlyList<LogicalProcessor> Read()
    {
        try
        {
            // The first call fails on purpose: it is how the size is asked for.
            _ = GetSystemCpuSetInformation(null, 0, out uint size, 0, 0);

            if (size == 0)
            {
                return [];
            }

            byte[] buffer = new byte[size];

            return GetSystemCpuSetInformation(buffer, size, out uint got, 0, 0)
                ? Parse(buffer.AsSpan(0, (int)Math.Min(got, size)))
                : [];
        }
        catch (EntryPointNotFoundException)
        {
            return [];
        }
    }

    /// <summary>
    /// Reads a buffer of <c>SYSTEM_CPU_SET_INFORMATION</c> entries, and works
    /// out which are performance and which efficiency cores.
    /// </summary>
    public static IReadOnlyList<LogicalProcessor> Parse(ReadOnlySpan<byte> buffer)
    {
        List<LogicalProcessor> found = [];
        int at = 0;

        while (at + 8 <= buffer.Length)
        {
            int size = (int)BinaryPrimitives.ReadUInt32LittleEndian(buffer[at..]);

            if (size < 8 || at + size > buffer.Length)
            {
                break;
            }

            int type = (int)BinaryPrimitives.ReadUInt32LittleEndian(buffer[(at + 4)..]);

            if (type == CpuSetType && size >= Smallest)
            {
                found.Add(new LogicalProcessor(
                    BinaryPrimitives.ReadUInt16LittleEndian(buffer[(at + 12)..]),
                    buffer[at + 14],
                    buffer[at + 15],
                    buffer[at + 18],
                    CoreKind.Uniform));
            }

            at += size;
        }

        return Classify(found);
    }

    /// <summary>
    /// Names the fastest class performance and every slower one efficiency,
    /// so a chip with three classes still splits two ways; a chip with one
    /// class is uniform.
    /// </summary>
    public static IReadOnlyList<LogicalProcessor> Classify(IReadOnlyList<LogicalProcessor> processors)
    {
        ArgumentNullException.ThrowIfNull(processors);

        int fastest = int.MinValue;
        int slowest = int.MaxValue;

        foreach (LogicalProcessor processor in processors)
        {
            fastest = Math.Max(fastest, processor.EfficiencyClass);
            slowest = Math.Min(slowest, processor.EfficiencyClass);
        }

        bool hybrid = processors.Count > 0 && fastest != slowest;
        List<LogicalProcessor> classified = new(processors.Count);

        foreach (LogicalProcessor processor in processors)
        {
            CoreKind kind = !hybrid ? CoreKind.Uniform
                : processor.EfficiencyClass == fastest ? CoreKind.Performance
                : CoreKind.Efficiency;

            classified.Add(processor with { Kind = kind });
        }

        classified.Sort(static (left, right) =>
            left.Group != right.Group
                ? left.Group.CompareTo(right.Group)
                : left.Index.CompareTo(right.Index));

        return classified;
    }

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GetSystemCpuSetInformation(
        [Out] byte[]? information,
        uint length,
        out uint returned,
        nint process,
        uint flags);
}

/// <summary>
/// Splits per-thread loads into the performance and the efficiency cores'.
/// </summary>
public static class CoreSplit
{
    /// <summary>
    /// The average load of each kind of core, or nothing for a kind the chip
    /// does not have or no reading covered.
    /// </summary>
    /// <param name="loads">Each logical processor's load, by its counter instance.</param>
    /// <param name="kinds">Each logical processor's kind, by group and index.</param>
    public static (double? Performance, double? Efficiency) Average(
        IEnumerable<(int Group, int Index, double Value)> loads,
        IReadOnlyDictionary<(int Group, int Index), CoreKind> kinds)
    {
        ArgumentNullException.ThrowIfNull(loads);
        ArgumentNullException.ThrowIfNull(kinds);

        double fast = 0;
        int fastCount = 0;
        double slow = 0;
        int slowCount = 0;

        foreach ((int group, int index, double value) in loads)
        {
            switch (kinds.GetValueOrDefault((group, index)))
            {
                case CoreKind.Performance:
                    fast += value;
                    fastCount++;
                    break;

                case CoreKind.Efficiency:
                    slow += value;
                    slowCount++;
                    break;

                default:
                    break;
            }
        }

        return (
            fastCount > 0 ? fast / fastCount : null,
            slowCount > 0 ? slow / slowCount : null);
    }
}
