using System.Buffers.Binary;
using System.Runtime.InteropServices;

namespace Windsock.Core.Hardware;

/// <summary>
/// One process as <c>SYSTEM_PROCESS_INFORMATION</c> describes it. Its name
/// points into the table it came from and lives only as long as that.
/// </summary>
public readonly ref struct ProcessEntry
{
    public ProcessEntry(
        int processId,
        long createTime,
        long processorTime,
        long privateWorkingSet,
        long workingSet,
        long readBytes,
        long writeBytes,
        ReadOnlySpan<char> name)
    {
        ProcessId = processId;
        CreateTime = createTime;
        ProcessorTime = processorTime;
        PrivateWorkingSet = privateWorkingSet;
        WorkingSet = workingSet;
        ReadBytes = readBytes;
        WriteBytes = writeBytes;
        Name = name;
    }

    /// <summary><c>UniqueProcessId</c>.</summary>
    public int ProcessId { get; }

    /// <summary><c>CreateTime</c>, a FILETIME.</summary>
    public long CreateTime { get; }

    /// <summary><c>UserTime</c> plus <c>KernelTime</c>, in 100 ns.</summary>
    public long ProcessorTime { get; }

    /// <summary><c>WorkingSetPrivateSize</c>, bytes.</summary>
    public long PrivateWorkingSet { get; }

    /// <summary><c>WorkingSetSize</c>, bytes.</summary>
    public long WorkingSet { get; }

    /// <summary><c>ReadTransferCount</c>: bytes read through the file system.</summary>
    public long ReadBytes { get; }

    /// <summary><c>WriteTransferCount</c>: bytes written through the file system.</summary>
    public long WriteBytes { get; }

    /// <summary><c>ImageName</c>: the file name alone, empty for the idle process.</summary>
    public ReadOnlySpan<char> Name { get; }
}

/// <summary>
/// Walks a <c>NtQuerySystemInformation(SystemProcessInformation)</c> buffer.
/// </summary>
/// <remarks>
/// The 64-bit layout, from phnt's <c>ntexapi.h</c>, measured against live data
/// on this machine: NextEntryOffset at 0, WorkingSetPrivateSize at 8,
/// CreateTime at 32, UserTime at 40, KernelTime at 48, ImageName at 56 (a
/// <c>UNICODE_STRING</c>: byte length at 56, buffer pointer at 64),
/// UniqueProcessId at 80, WorkingSetSize at 144, ReadTransferCount at 232,
/// WriteTransferCount at 240; the first thread follows at 256. The name's
/// pointer is an address inside the same buffer, so it is read as an offset
/// from where the buffer was.
/// </remarks>
public ref struct ProcessEntries
{
    /// <summary>The fixed part of an entry, before its threads.</summary>
    public const int EntrySize = 256;

    private readonly ReadOnlySpan<byte> _buffer;
    private readonly ulong _base;
    private int _at;
    private bool _done;
    private ProcessEntry _current;

    /// <param name="buffer">The table.</param>
    /// <param name="baseAddress">Where the table was in memory when Windows filled it.</param>
    public ProcessEntries(ReadOnlySpan<byte> buffer, ulong baseAddress)
    {
        _buffer = buffer;
        _base = baseAddress;
        _done = buffer.Length < EntrySize;
    }

    public readonly ProcessEntries GetEnumerator() => this;

    public readonly ProcessEntry Current => _current;

    public bool MoveNext()
    {
        if (_done || _at + EntrySize > _buffer.Length)
        {
            return false;
        }

        ReadOnlySpan<byte> entry = _buffer[_at..];
        uint next = BinaryPrimitives.ReadUInt32LittleEndian(entry);

        _current = new ProcessEntry(
            (int)BinaryPrimitives.ReadInt64LittleEndian(entry[80..]),
            BinaryPrimitives.ReadInt64LittleEndian(entry[32..]),
            BinaryPrimitives.ReadInt64LittleEndian(entry[40..]) + BinaryPrimitives.ReadInt64LittleEndian(entry[48..]),
            BinaryPrimitives.ReadInt64LittleEndian(entry[8..]),
            BinaryPrimitives.ReadInt64LittleEndian(entry[144..]),
            BinaryPrimitives.ReadInt64LittleEndian(entry[232..]),
            BinaryPrimitives.ReadInt64LittleEndian(entry[240..]),
            Name(entry));

        if (next == 0 || next > _buffer.Length - _at)
        {
            _done = true;
        }
        else
        {
            _at += (int)next;
        }

        return true;
    }

    private readonly ReadOnlySpan<char> Name(ReadOnlySpan<byte> entry)
    {
        int length = BinaryPrimitives.ReadUInt16LittleEndian(entry[56..]) & ~1;
        ulong pointer = BinaryPrimitives.ReadUInt64LittleEndian(entry[64..]);

        if (length == 0 || pointer < _base)
        {
            return [];
        }

        ulong offset = pointer - _base;

        if (offset + (ulong)length > (ulong)_buffer.Length)
        {
            return [];
        }

        return MemoryMarshal.Cast<byte, char>(_buffer.Slice((int)offset, length));
    }
}

/// <summary>
/// Reads every process in one call, with no handle to any of them, the way
/// Task Manager does: protected and other users' processes included.
/// </summary>
/// <remarks>
/// The buffer is native memory kept between readings, because the table is
/// around 800 KB with four hundred processes and asking the collector for that
/// every second would be a large-object allocation a second.
/// </remarks>
public sealed unsafe partial class SystemProcessTable : IDisposable
{
    private const int SystemProcessInformation = 5;
    private const int LengthMismatch = unchecked((int)0xC0000004);
    private const int Slack = 128 * 1024;

    private byte* _buffer;
    private int _size;
    private int _filled;

    /// <summary>Whether this process can read the table at all: the layout is the 64-bit one.</summary>
    public static bool IsSupported => Environment.Is64BitProcess;

    /// <summary>
    /// Reads the table. Its entries are good until the next reading or
    /// <see cref="Dispose"/>.
    /// </summary>
    public ProcessEntries Read()
    {
        if (!IsSupported || !Fill())
        {
            return new ProcessEntries([], 0);
        }

        return new ProcessEntries(new ReadOnlySpan<byte>(_buffer, _filled), (ulong)_buffer);
    }

    private bool Fill()
    {
        if (_buffer is null)
        {
            Grow(1024 * 1024);
        }

        for (int attempt = 0; attempt < 4; attempt++)
        {
            int status = NtQuerySystemInformation(SystemProcessInformation, _buffer, (uint)_size, out uint needed);

            if (status >= 0)
            {
                _filled = (int)Math.Min(needed == 0 ? (uint)_size : needed, (uint)_size);
                return true;
            }

            if (status != LengthMismatch)
            {
                return false;
            }

            // Processes start between the asking and the answer, so leave room.
            Grow((int)Math.Min(int.MaxValue / 2, (long)needed + Slack));
        }

        return false;
    }

    private void Grow(int size)
    {
        if (_buffer is not null)
        {
            NativeMemory.Free(_buffer);
        }

        _buffer = (byte*)NativeMemory.Alloc((nuint)size);
        _size = size;
        _filled = 0;
    }

    public void Dispose()
    {
        if (_buffer is not null)
        {
            NativeMemory.Free(_buffer);
            _buffer = null;
            _size = 0;
            _filled = 0;
        }
    }

    [LibraryImport("ntdll.dll")]
    private static partial int NtQuerySystemInformation(int informationClass, void* information, uint length, out uint returned);
}
