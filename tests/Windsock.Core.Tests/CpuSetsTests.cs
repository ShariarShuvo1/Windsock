using System.Buffers.Binary;
using Windsock.Core.Hardware;
using Xunit;

namespace Windsock.Core.Tests;

/// <summary>
/// Which logical processor is a performance core and which an efficiency
/// core, from SYSTEM_CPU_SET_INFORMATION.
/// </summary>
public sealed class CpuSetsTests
{
    /// <summary>
    /// One 32-byte entry: Size @0, Type @4, Id @8, Group @12 (16 bits),
    /// LogicalProcessorIndex @14, CoreIndex @15, LastLevelCacheIndex @16,
    /// NumaNodeIndex @17, EfficiencyClass @18, flags @19.
    /// </summary>
    private static byte[] Entry(int group, int index, int core, int efficiencyClass, int type = 0, int size = 32)
    {
        byte[] entry = new byte[size];
        BinaryPrimitives.WriteUInt32LittleEndian(entry, (uint)size);
        BinaryPrimitives.WriteUInt32LittleEndian(entry.AsSpan(4), (uint)type);
        BinaryPrimitives.WriteUInt32LittleEndian(entry.AsSpan(8), (uint)(256 + index));
        BinaryPrimitives.WriteUInt16LittleEndian(entry.AsSpan(12), (ushort)group);
        entry[14] = (byte)index;
        entry[15] = (byte)core;
        entry[16] = 0xAA;
        entry[17] = 0xBB;
        entry[18] = (byte)efficiencyClass;
        entry[19] = 0x01;
        return entry;
    }

    /// <summary>
    /// The development machine's layout, as measured: an i5-12600K, twelve
    /// threads of class 1 on six cores, four threads of class 0.
    /// </summary>
    private static byte[] AlderLake()
    {
        List<byte> buffer = [];

        for (int thread = 0; thread < 12; thread++)
        {
            buffer.AddRange(Entry(0, thread, thread / 2 * 2, 1));
        }

        for (int thread = 12; thread < 16; thread++)
        {
            buffer.AddRange(Entry(0, thread, thread, 0));
        }

        return [.. buffer];
    }

    /// <summary>The measured layout reads as six performance cores' threads and four efficiency cores.</summary>
    [Fact]
    public void Parse_ReadsTheDevelopmentMachine()
    {
        IReadOnlyList<LogicalProcessor> processors = CpuSets.Parse(AlderLake());

        Assert.Equal(16, processors.Count);
        Assert.Equal(12, processors.Count(processor => processor.Kind == CoreKind.Performance));
        Assert.Equal(4, processors.Count(processor => processor.Kind == CoreKind.Efficiency));
        Assert.Equal(new LogicalProcessor(0, 5, 4, 1, CoreKind.Performance), processors[5]);
        Assert.Equal(new LogicalProcessor(0, 13, 13, 0, CoreKind.Efficiency), processors[13]);
    }

    /// <summary>One class of core is not hybrid, whatever its number.</summary>
    [Fact]
    public void Parse_OneClassIsUniform()
    {
        byte[] buffer = [.. Entry(0, 0, 0, 3), .. Entry(0, 1, 0, 3)];

        Assert.All(CpuSets.Parse(buffer), processor => Assert.Equal(CoreKind.Uniform, processor.Kind));
    }

    /// <summary>Three classes still split two ways: the fastest, and the rest.</summary>
    [Fact]
    public void Parse_ThreeClassesSplitTwoWays()
    {
        byte[] buffer = [.. Entry(0, 0, 0, 2), .. Entry(0, 1, 1, 1), .. Entry(0, 2, 2, 0)];

        IReadOnlyList<LogicalProcessor> processors = CpuSets.Parse(buffer);

        Assert.Equal([CoreKind.Performance, CoreKind.Efficiency, CoreKind.Efficiency], processors.Select(processor => processor.Kind));
    }

    /// <summary>Entries are walked by their own size, and other types are skipped.</summary>
    [Fact]
    public void Parse_WalksBySizeAndSkipsOtherTypes()
    {
        byte[] buffer = [.. Entry(0, 0, 0, 1, size: 40), .. Entry(0, 1, 1, 0, type: 7), .. Entry(0, 2, 2, 0)];

        IReadOnlyList<LogicalProcessor> processors = CpuSets.Parse(buffer);

        Assert.Equal([0, 2], processors.Select(processor => processor.Index));
    }

    /// <summary>A size that runs past the buffer stops the walk rather than reading beyond it.</summary>
    [Fact]
    public void Parse_StopsAtATruncatedEntry()
    {
        byte[] buffer = [.. Entry(0, 0, 0, 1), .. Entry(0, 1, 1, 1).AsSpan(0, 20).ToArray()];

        Assert.Single(CpuSets.Parse(buffer));
    }

    /// <summary>Processors come back in the counters' order, group then index.</summary>
    [Fact]
    public void Parse_SortsByGroupThenIndex()
    {
        byte[] buffer = [.. Entry(1, 0, 0, 0), .. Entry(0, 1, 1, 0), .. Entry(0, 0, 0, 0)];

        Assert.Equal([(0, 0), (0, 1), (1, 0)], CpuSets.Parse(buffer).Select(processor => (processor.Group, processor.Index)));
    }

    /// <summary>The loads split by kind, each kind averaged over its own threads.</summary>
    [Fact]
    public void Split_AveragesEachKind()
    {
        Dictionary<(int Group, int Index), CoreKind> kinds = CpuSets.Parse(AlderLake())
            .ToDictionary(processor => (processor.Group, processor.Index), processor => processor.Kind);

        List<(int Group, int Index, double Value)> loads = [];

        for (int thread = 0; thread < 16; thread++)
        {
            loads.Add((0, thread, thread < 12 ? 50 : 10));
        }

        (double? performance, double? efficiency) = CoreSplit.Average(loads, kinds);

        Assert.Equal(50, performance);
        Assert.Equal(10, efficiency);
    }

    /// <summary>A chip with one kind of core has no split.</summary>
    [Fact]
    public void Split_UniformHasNone()
    {
        (double? performance, double? efficiency) = CoreSplit.Average([(0, 0, 40.0)], new Dictionary<(int, int), CoreKind>());

        Assert.Null(performance);
        Assert.Null(efficiency);
    }

    /// <summary>This machine's own answer reads, whatever it is.</summary>
    [Fact]
    public void Read_AnswersForThisMachine()
    {
        IReadOnlyList<LogicalProcessor> processors = CpuSets.Read();

        Assert.NotEmpty(processors);
        Assert.Equal(processors.Count, processors.Distinct().Count());
    }
}
