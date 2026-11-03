using System.Buffers.Binary;
using System.Diagnostics;
using System.Text;
using Windsock.Core.Hardware;
using Xunit;

namespace Windsock.Core.Tests;

/// <summary>
/// The process table, held to the 64-bit SYSTEM_PROCESS_INFORMATION layout,
/// and the busiest processes made from two readings of it.
/// </summary>
public sealed class BusiestProcessesTests
{
    private const ulong Base = 0x0000_7FF0_1000_0000;

    private sealed record Fake(
        int Id,
        string Name,
        long Created,
        long User,
        long Kernel,
        long Private,
        long WorkingSet,
        long Read,
        long Written,
        int Threads = 1);

    /// <summary>
    /// Lays processes out as Windows does: each entry's fixed 256 bytes, its
    /// threads (80 bytes each), and the names after every entry.
    /// </summary>
    private static byte[] Table(params Fake[] processes)
    {
        int threadSize = 80;
        int entries = processes.Sum(process => 256 + (process.Threads * threadSize));
        int names = processes.Sum(process => (process.Name.Length * 2) + 2);
        byte[] buffer = new byte[entries + names];

        int at = 0;
        int nameAt = entries;

        for (int index = 0; index < processes.Length; index++)
        {
            Fake process = processes[index];
            Span<byte> entry = buffer.AsSpan(at);
            int size = 256 + (process.Threads * threadSize);

            BinaryPrimitives.WriteUInt32LittleEndian(entry, index == processes.Length - 1 ? 0u : (uint)size);
            BinaryPrimitives.WriteUInt32LittleEndian(entry[4..], (uint)process.Threads);
            BinaryPrimitives.WriteInt64LittleEndian(entry[8..], process.Private);
            BinaryPrimitives.WriteInt64LittleEndian(entry[24..], 123_456_789);
            BinaryPrimitives.WriteInt64LittleEndian(entry[32..], process.Created);
            BinaryPrimitives.WriteInt64LittleEndian(entry[40..], process.User);
            BinaryPrimitives.WriteInt64LittleEndian(entry[48..], process.Kernel);

            byte[] name = Encoding.Unicode.GetBytes(process.Name);
            BinaryPrimitives.WriteUInt16LittleEndian(entry[56..], (ushort)name.Length);
            BinaryPrimitives.WriteUInt16LittleEndian(entry[58..], (ushort)(name.Length + 2));
            BinaryPrimitives.WriteUInt64LittleEndian(entry[64..], name.Length == 0 ? 0 : Base + (ulong)nameAt);
            name.CopyTo(buffer, nameAt);
            nameAt += name.Length + 2;

            BinaryPrimitives.WriteInt32LittleEndian(entry[72..], 8);
            BinaryPrimitives.WriteInt64LittleEndian(entry[80..], process.Id);
            BinaryPrimitives.WriteInt64LittleEndian(entry[88..], 4);
            BinaryPrimitives.WriteUInt32LittleEndian(entry[96..], 321);
            BinaryPrimitives.WriteInt64LittleEndian(entry[144..], process.WorkingSet);
            BinaryPrimitives.WriteInt64LittleEndian(entry[184..], 999);
            BinaryPrimitives.WriteInt64LittleEndian(entry[200..], 888);
            BinaryPrimitives.WriteInt64LittleEndian(entry[232..], process.Read);
            BinaryPrimitives.WriteInt64LittleEndian(entry[240..], process.Written);
            BinaryPrimitives.WriteInt64LittleEndian(entry[248..], 777_777);

            at += size;
        }

        return buffer;
    }

    private sealed record Parsed(int Id, string Name, long Created, long Processor, long Private, long WorkingSet, long Read, long Written);

    private static List<Parsed> Parse(byte[] buffer, ulong baseAddress = Base)
    {
        List<Parsed> parsed = [];

        foreach (ProcessEntry entry in new ProcessEntries(buffer, baseAddress))
        {
            parsed.Add(new Parsed(entry.ProcessId, entry.Name.ToString(), entry.CreateTime, entry.ProcessorTime, entry.PrivateWorkingSet, entry.WorkingSet, entry.ReadBytes, entry.WriteBytes));
        }

        return parsed;
    }

    /// <summary>Every field is read from its own offset, and the walk follows NextEntryOffset.</summary>
    [Fact]
    public void Entries_ReadEachFieldFromItsOffset()
    {
        byte[] buffer = Table(
            new Fake(0, string.Empty, 0, 0, 900_000, 8_192, 8_192, 0, 0, Threads: 16),
            new Fake(4, "System", 1, 10, 20_000, 40_960, 4_096_000, 1_000, 2_000, Threads: 200),
            new Fake(4321, "chrome.exe", 133_700_000_000_000_000, 5_000_000, 1_000_000, 300_000_000, 400_000_000, 12_345, 67_890, Threads: 30));

        List<Parsed> parsed = Parse(buffer);

        Assert.Equal(3, parsed.Count);
        Assert.Equal(new Parsed(0, string.Empty, 0, 900_000, 8_192, 8_192, 0, 0), parsed[0]);
        Assert.Equal(new Parsed(4, "System", 1, 20_010, 40_960, 4_096_000, 1_000, 2_000), parsed[1]);
        Assert.Equal(new Parsed(4321, "chrome.exe", 133_700_000_000_000_000, 6_000_000, 300_000_000, 400_000_000, 12_345, 67_890), parsed[2]);
    }

    /// <summary>A name pointing outside the table is no name, never a read past the end.</summary>
    [Fact]
    public void Entries_RefuseANameOutsideTheTable()
    {
        byte[] buffer = Table(new Fake(8, "lost.exe", 1, 0, 0, 0, 0, 0, 0));

        Assert.Equal(string.Empty, Parse(buffer, baseAddress: Base + 0x10_0000)[0].Name);
        Assert.Equal(string.Empty, Parse(buffer, baseAddress: Base - 0x10_0000)[0].Name);
    }

    /// <summary>A next offset running past the table ends the walk after that entry.</summary>
    [Fact]
    public void Entries_StopAtAnOffsetPastTheEnd()
    {
        byte[] buffer = Table(new Fake(8, "a.exe", 1, 0, 0, 0, 0, 0, 0), new Fake(9, "b.exe", 1, 0, 0, 0, 0, 0, 0));
        BinaryPrimitives.WriteUInt32LittleEndian(buffer, 1_000_000);

        Assert.Single(Parse(buffer));
    }

    /// <summary>A table shorter than one entry has none.</summary>
    [Fact]
    public void Entries_EmptyForAShortTable()
    {
        Assert.Empty(Parse(new byte[100]));
    }

    private static BusiestProcesses Reading(BusiestTally tally, long at, params Fake[] processes)
    {
        byte[] buffer = Table(processes);
        tally.Begin(at, logicalProcessors: 4);

        foreach (ProcessEntry entry in new ProcessEntries(buffer, Base))
        {
            tally.Add(entry);
        }

        return tally.End();
    }

    /// <summary>
    /// Processor share is time over elapsed time times the processors: a
    /// process that used one full second of one of four processors in a
    /// second is 25 per cent, as Task Manager counts it.
    /// </summary>
    [Fact]
    public void Tally_MeasuresTheProcessorLikeTaskManager()
    {
        BusiestTally tally = new();
        long second = Stopwatch.Frequency;

        BusiestProcesses first = Reading(tally, 10 * second, new Fake(100, "busy.exe", 5, 0, 0, 1_000, 0, 0, 0), new Fake(200, "idle.exe", 6, 0, 0, 5_000, 0, 0, 0));
        BusiestProcesses second2 = Reading(tally, 11 * second, new Fake(100, "busy.exe", 5, 7_500_000, 2_500_000, 1_000, 0, 0, 0), new Fake(200, "idle.exe", 6, 0, 0, 5_000, 0, 0, 0));

        Assert.False(first.HasRates);
        Assert.Empty(first.Processor);
        Assert.Equal(["idle.exe", "busy.exe"], first.Memory.Select(load => load.Name));

        Assert.True(second2.HasRates);
        ProcessLoad busy = Assert.Single(second2.Processor);
        Assert.Equal("busy.exe", busy.Name);
        Assert.Equal(25, busy.ProcessorPercent, 6);
        Assert.Equal(2, second2.ProcessCount);
    }

    /// <summary>File I/O is read plus written bytes, a second.</summary>
    [Fact]
    public void Tally_MeasuresFileBytesASecond()
    {
        BusiestTally tally = new();
        long second = Stopwatch.Frequency;

        _ = Reading(tally, 0, new Fake(300, "copy.exe", 9, 0, 0, 0, 0, 1_000, 2_000));
        BusiestProcesses after = Reading(tally, 2 * second, new Fake(300, "copy.exe", 9, 0, 0, 0, 0, 3_001_000, 1_002_000));

        Assert.Equal(2_000_000, Assert.Single(after.FileIo).FileBytesPerSecond, 6);
    }

    /// <summary>
    /// An id handed to a new process starts from nothing: the new process is
    /// not charged its predecessor's counters, nor credited with them.
    /// </summary>
    [Fact]
    public void Tally_ReusedIdStartsAfresh()
    {
        BusiestTally tally = new();
        long second = Stopwatch.Frequency;

        _ = Reading(tally, 0, new Fake(500, "old.exe", 1, 900_000_000, 0, 0, 0, 0, 0));
        BusiestProcesses after = Reading(tally, second, new Fake(500, "new.exe", 2, 1_000, 0, 0, 0, 0, 0));

        Assert.Empty(after.Processor);
        Assert.Equal("new.exe", Assert.Single(after.Memory).Name);
    }

    /// <summary>After a reset the next reading stands alone, rather than averaging over the pause.</summary>
    [Fact]
    public void Tally_ResetForgetsTheLastReading()
    {
        BusiestTally tally = new();
        long second = Stopwatch.Frequency;

        _ = Reading(tally, 0, new Fake(100, "busy.exe", 5, 0, 0, 0, 0, 0, 0));
        tally.Reset();
        BusiestProcesses after = Reading(tally, 600 * second, new Fake(100, "busy.exe", 5, 10_000_000, 0, 0, 0, 0, 0));

        Assert.False(after.HasRates);
        Assert.Empty(after.Processor);
    }

    /// <summary>The idle process is the processor not being used, and is never ranked.</summary>
    [Fact]
    public void Tally_LeavesOutTheIdleProcess()
    {
        BusiestTally tally = new();
        long second = Stopwatch.Frequency;

        _ = Reading(tally, 0, new Fake(0, string.Empty, 0, 0, 0, 0, 0, 0, 0));
        BusiestProcesses after = Reading(tally, second, new Fake(0, string.Empty, 0, 0, 30_000_000, 0, 0, 0, 0));

        Assert.Empty(after.Processor);
        Assert.Empty(after.Memory);
    }

    /// <summary>Each list keeps the busiest few, busiest first.</summary>
    [Fact]
    public void Tally_KeepsTheTopFew()
    {
        BusiestTally tally = new() { Top = 3 };
        Fake[] processes = [.. Enumerable.Range(1, 10).Select(id => new Fake(id * 4, $"p{id}.exe", id, 0, 0, id * 1_000L, 0, 0, 0))];

        BusiestProcesses reading = Reading(tally, 0, processes);

        Assert.Equal(["p10.exe", "p9.exe", "p8.exe"], reading.Memory.Select(load => load.Name));
    }

    /// <summary>
    /// A program's processes add up to one row, as Task Manager groups them;
    /// the row keeps its busiest process's id, and says how many it stands for.
    /// </summary>
    [Fact]
    public void Tally_AddsUpEachProgram()
    {
        BusiestTally tally = new();
        long second = Stopwatch.Frequency;

        _ = Reading(
            tally,
            0,
            new Fake(10, "chrome.exe", 1, 0, 0, 100, 0, 0, 0),
            new Fake(11, "chrome.exe", 2, 0, 0, 200, 0, 0, 0),
            new Fake(12, "Code.exe", 3, 0, 0, 250, 0, 0, 0));

        BusiestProcesses after = Reading(
            tally,
            second,
            new Fake(10, "chrome.exe", 1, 4_000_000, 0, 100, 0, 1_000, 0),
            new Fake(11, "CHROME.EXE", 2, 2_000_000, 0, 200, 0, 0, 1_000),
            new Fake(12, "Code.exe", 3, 1_000_000, 0, 250, 0, 0, 0));

        ProcessLoad chrome = after.MemoryByProgram[0];
        Assert.Equal("chrome.exe", chrome.Name);
        Assert.Equal(2, chrome.Count);
        Assert.Equal(300, chrome.PrivateBytes);
        Assert.Equal(10, chrome.ProcessId);
        Assert.Equal(15, chrome.ProcessorPercent, 6);
        Assert.Equal(2_000, chrome.FileBytesPerSecond, 6);

        Assert.Equal(["chrome.exe", "Code.exe"], after.ProcessorByProgram.Select(load => load.Name));
        Assert.Equal(1, after.ProcessorByProgram[1].Count);
        Assert.Equal(3, after.Memory.Count);
    }

    /// <summary>The live table reads on this machine with no handle to any process.</summary>
    [Fact]
    public void Table_ReadsThisMachine()
    {
        using SystemProcessTable table = new();
        int count = 0;
        bool sawSystem = false;
        bool sawSelf = false;

        foreach (ProcessEntry entry in table.Read())
        {
            count++;
            sawSystem |= entry.ProcessId == 4;
            sawSelf |= entry.ProcessId == Environment.ProcessId && entry.Name.Length > 0;
        }

        Assert.True(count > 10);
        Assert.True(sawSystem);
        Assert.True(sawSelf);
    }
}
