using System.Globalization;
using System.Runtime.InteropServices;
using Microsoft.Win32;

namespace Windsock.Core.Hardware;

/// <summary>
/// Reads what the processor is and what it is doing.
/// </summary>
public sealed partial class ProcessorProbe
{
    private const string TotalCounter = "cpu.total";
    private const string CoreCounter = "cpu.cores";
    private const string ClockCounter = "cpu.clock";
    private const string KernelCounter = "cpu.kernel";
    private const string CpuKey = @"HARDWARE\DESCRIPTION\System\CentralProcessor\0";

    private readonly List<(int Group, int Index, double Value)> _ordered = [];

    private int _baseMegahertz;

    /// <summary>Adds the counters this probe reads to a shared query.</summary>
    public void Register(PerformanceQuery query, ProcessorFacts facts)
    {
        ArgumentNullException.ThrowIfNull(query);
        ArgumentNullException.ThrowIfNull(facts);

        _baseMegahertz = facts.BaseMegahertz;
        if (!query.Add(TotalCounter, @"\Processor Information(_Total)\% Processor Utility"))
        {
            _ = query.Add(TotalCounter, @"\Processor Information(_Total)\% Processor Time");
        }

        if (!query.Add(CoreCounter, @"\Processor Information(*)\% Processor Utility", wildcard: true))
        {
            _ = query.Add(CoreCounter, @"\Processor Information(*)\% Processor Time", wildcard: true);
        }

        _ = query.Add(KernelCounter, @"\Processor Information(_Total)\% Privileged Time");
        _ = query.Add(ClockCounter, @"\Processor Information(_Total)\% Processor Performance");
    }

    /// <summary>Takes a reading from the last collection.</summary>
    public ProcessorReading Read(PerformanceQuery query, SystemCounts counts, double? celsius = null)
    {
        ArgumentNullException.ThrowIfNull(query);

        return new ProcessorReading(
            Percent(query.Value(TotalCounter)),
            Percent(query.Value(KernelCounter)),
            Cores(query),
            Clock(query),
            counts.Processes,
            counts.Threads,
            counts.Handles,
            celsius);
    }

    private List<double> Cores(PerformanceQuery query)
    {
        _ordered.Clear();

        foreach (PerformanceQuery.Reading reading in query.Values(CoreCounter))
        {
            if (CoreName.Parse(reading.Instance) is not { } core)
            {
                continue;
            }

            _ordered.Add((core.Group, core.Index, Percent(reading.Value)));
        }

        _ordered.Sort(static (left, right) =>
            left.Group != right.Group
                ? left.Group.CompareTo(right.Group)
                : left.Index.CompareTo(right.Index));

        List<double> cores = new(_ordered.Count);

        foreach ((_, _, double value) in _ordered)
        {
            cores.Add(value);
        }

        return cores;
    }

    private double Clock(PerformanceQuery query)
    {
        double ratio = query.Value(ClockCounter);

        return _baseMegahertz > 0 && ratio > 0
            ? _baseMegahertz * ratio / 100
            : 0;
    }

    private static double Percent(double value) => Math.Clamp(value, 0, 100);

    /// <summary>Reads the processor's description. Costly, and never changes.</summary>
    public static ProcessorFacts ReadFacts(SmbiosTables firmware)
    {
        ArgumentNullException.ThrowIfNull(firmware);

        string name = Registry.GetValue($@"HKEY_LOCAL_MACHINE\{CpuKey}", "ProcessorNameString", null) as string
            ?? ProcessorFacts.Unknown.Name;

        int megahertz = Registry.GetValue($@"HKEY_LOCAL_MACHINE\{CpuKey}", "~MHz", 0) as int? ?? 0;

        Topology topology = ReadTopology();

        return new ProcessorFacts(
            name.Trim(),
            topology.Cores > 0 ? topology.Cores : firmware.Cores,
            topology.Threads > 0 ? topology.Threads : Environment.ProcessorCount,
            megahertz > 0 ? megahertz : firmware.MaxMegahertz,
            firmware.Socket,
            topology.LevelTwo,
            topology.LevelThree,
            topology.Performance,
            topology.Efficiency);
    }

    private readonly record struct Topology(
        int Cores,
        int Threads,
        int Performance,
        int Efficiency,
        long LevelTwo,
        long LevelThree);

    private static Topology ReadTopology()
    {
        byte[]? buffer = Describe();

        if (buffer is null)
        {
            return default;
        }

        int cores = 0;
        int threads = 0;
        long levelTwo = 0;
        long levelThree = 0;
        Dictionary<byte, int> classes = [];

        int at = 0;

        while (at + 8 <= buffer.Length)
        {
            uint relation = BitConverter.ToUInt32(buffer, at);
            int size = BitConverter.ToInt32(buffer, at + 4);

            if (size < 8 || at + size > buffer.Length)
            {
                break;
            }

            switch (relation)
            {
                case RelationProcessorCore:
                    cores++;
                    threads += Threads(buffer, at);

                    byte grade = buffer[at + 9];
                    classes[grade] = classes.GetValueOrDefault(grade) + 1;
                    break;

                case RelationCache:
                    long bytes = BitConverter.ToUInt32(buffer, at + 12);

                    switch (buffer[at + 8])
                    {
                        case 2: levelTwo += bytes; break;
                        case 3: levelThree += bytes; break;
                        default: break;
                    }

                    break;

                default:
                    break;
            }

            at += size;
        }

        (int fast, int slow) = Split(classes);

        return new Topology(cores, threads, fast, slow, levelTwo, levelThree);
    }

    private static int Threads(byte[] buffer, int at)
    {
        int groups = BitConverter.ToUInt16(buffer, at + 30);
        int total = 0;

        for (int group = 0; group < groups; group++)
        {
            int mask = at + 32 + (group * 16);

            if (mask + 8 > buffer.Length)
            {
                break;
            }

            total += System.Numerics.BitOperations.PopCount(BitConverter.ToUInt64(buffer, mask));
        }

        return total == 0 ? 1 : total;
    }

    private static (int Fast, int Slow) Split(Dictionary<byte, int> classes)
    {
        if (classes.Count < 2)
        {
            return (0, 0);
        }

        byte best = byte.MinValue;

        foreach (byte grade in classes.Keys)
        {
            best = Math.Max(best, grade);
        }

        int fast = 0;
        int slow = 0;

        foreach ((byte grade, int count) in classes)
        {
            if (grade == best)
            {
                fast += count;
            }
            else
            {
                slow += count;
            }
        }

        return (fast, slow);
    }

    private static byte[]? Describe()
    {
        uint size = 0;

        // The first call is expected to fail: it is how the size is asked for.
        _ = GetLogicalProcessorInformationEx(RelationAll, null, ref size);

        if (size == 0)
        {
            return null;
        }

        byte[] buffer = new byte[size];

        return GetLogicalProcessorInformationEx(RelationAll, buffer, ref size)
            ? buffer
            : null;
    }

    private const uint RelationProcessorCore = 0;
    private const uint RelationCache = 2;
    private const uint RelationAll = 0xFFFF;

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GetLogicalProcessorInformationEx(
        uint relationship,
        [Out] byte[]? buffer,
        ref uint length);
}

/// <summary>
/// The group and index a processor counter instance names.
/// </summary>
public static class CoreName
{
    /// <summary>Reads an instance name.</summary>
    public static (int Group, int Index)? Parse(string? instance)
    {
        if (string.IsNullOrEmpty(instance))
        {
            return null;
        }

        int comma = instance.IndexOf(',', StringComparison.Ordinal);
        if (comma < 0)
        {
            return int.TryParse(instance, NumberStyles.None, CultureInfo.InvariantCulture, out int only)
                ? (0, only)
                : null;
        }

        ReadOnlySpan<char> group = instance.AsSpan(0, comma);
        ReadOnlySpan<char> index = instance.AsSpan(comma + 1);

        return int.TryParse(group, NumberStyles.None, CultureInfo.InvariantCulture, out int which)
            && int.TryParse(index, NumberStyles.None, CultureInfo.InvariantCulture, out int core)
                ? (which, core)
                : null;
    }
}
