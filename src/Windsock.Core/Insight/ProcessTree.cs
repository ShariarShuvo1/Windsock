using System.Buffers.Binary;
using System.Runtime.InteropServices;
using Windows.Wdk;
using Windows.Wdk.System.SystemInformation;

namespace Windsock.Core.Insight;

/// <summary>One process as the kernel lists it, with no handle opened.</summary>
/// <param name="ProcessId">Its id.</param>
/// <param name="ParentId">
/// The id its parent had when it started. Never updated: the parent may have
/// ended since and the id gone to something else (see <see cref="ProcessTree.ParentOf"/>).
/// </param>
/// <param name="CreateTime">When it started, in FILETIME ticks (UTC); zero for the idle process.</param>
/// <param name="ImageName">The executable's file name, without a path; empty for the idle process.</param>
/// <param name="SessionId">The Windows session it runs in; 0 for services.</param>
public readonly record struct KernelProcess(int ProcessId, int ParentId, long CreateTime, string ImageName, int SessionId)
{
    /// <summary>When it started, or null when the kernel did not say.</summary>
    public DateTimeOffset? StartedAt =>
        CreateTime > 0 ? DateTimeOffset.FromFileTime(CreateTime) : null;
}

/// <summary>A process's parent, as far as it can still be known.</summary>
/// <param name="ProcessId">The id the parent had.</param>
/// <param name="Entry">The parent itself when it is still running, otherwise null.</param>
public sealed record ParentProcess(int ProcessId, KernelProcess? Entry)
{
    /// <summary>Whether the process that started it is still running.</summary>
    public bool IsRunning => Entry is not null;
}

/// <summary>
/// Every process and the one that started it, from one reading of the
/// kernel's process list.
/// </summary>
/// <remarks>
/// <para>
/// <c>NtQuerySystemInformation(SystemProcessInformation)</c> lists every
/// process, protected ones included, without opening any - the way Task
/// Manager does. A standard user cannot open 177 of 408 processes on the
/// development machine, so a handle-based answer would leave most services
/// without a parent.
/// </para>
/// <para>
/// A parent id is a note taken at creation. When the parent ends its id can be
/// given to an unrelated process, so a "parent" that started after its child
/// is not the parent: <see cref="ParentOf"/> says the parent has ended rather
/// than name the stranger. The parent can also be set on purpose by whoever
/// created the process, so it is not always the creator.
/// </para>
/// </remarks>
public sealed class ProcessTree
{
    private const int InfoLengthMismatch = unchecked((int)0xC0000004);

    // SYSTEM_PROCESS_INFORMATION on x64, from phnt's ntexapi.h. The offsets of
    // the fields used here were checked against live data on 2026-10-05.
    internal const int NextEntryOffsetAt = 0;
    internal const int CreateTimeAt = 32;
    internal const int ImageNameLengthAt = 56;
    internal const int ImageNameBufferAt = 64;
    internal const int ProcessIdAt = 80;
    internal const int ParentIdAt = 88;
    internal const int SessionIdAt = 100;
    internal const int EntryMinimum = 256;

    private readonly Dictionary<int, KernelProcess> _entries;

    /// <summary>A tree made of entries already read.</summary>
    public ProcessTree(IEnumerable<KernelProcess> entries)
    {
        ArgumentNullException.ThrowIfNull(entries);

        _entries = [];

        foreach (KernelProcess entry in entries)
        {
            _entries[entry.ProcessId] = entry;
        }
    }

    /// <summary>An empty tree, for when the list could not be read.</summary>
    public static ProcessTree Empty { get; } = new([]);

    /// <summary>How many processes are listed.</summary>
    public int Count => _entries.Count;

    /// <summary>Every process listed.</summary>
    public IEnumerable<KernelProcess> Entries => _entries.Values;

    /// <summary>A process by id, if it is running.</summary>
    public KernelProcess? Find(int processId) =>
        _entries.TryGetValue(processId, out KernelProcess entry) ? entry : null;

    /// <summary>
    /// The parent of a process: running, or known only by the id it had. Null
    /// for a process that is not listed or has no parent (the idle process and
    /// System).
    /// </summary>
    public ParentProcess? ParentOf(int processId)
    {
        if (!_entries.TryGetValue(processId, out KernelProcess child) || child.ParentId <= 0 || processId <= 4)
        {
            return null;
        }

        // A process cannot have started before its parent; one that did holds
        // a reused id, and the real parent has gone.
        return _entries.TryGetValue(child.ParentId, out KernelProcess parent)
            && parent.ProcessId != processId
            && (parent.CreateTime <= child.CreateTime || child.CreateTime == 0)
                ? new ParentProcess(child.ParentId, parent)
                : new ParentProcess(child.ParentId, null);
    }

    /// <summary>
    /// The chain from a process up to the first ancestor that has ended or has
    /// no parent, nearest first, at most <paramref name="depth"/> long.
    /// </summary>
    public IReadOnlyList<ParentProcess> AncestorsOf(int processId, int depth = 16)
    {
        List<ParentProcess> chain = [];
        HashSet<int> seen = [processId];
        int current = processId;

        while (chain.Count < depth && ParentOf(current) is { } parent)
        {
            chain.Add(parent);

            if (!parent.IsRunning || !seen.Add(parent.ProcessId))
            {
                break;
            }

            current = parent.ProcessId;
        }

        return chain;
    }

    /// <summary>
    /// Reads the kernel's process list. Never throws; an empty tree when the
    /// call is refused or unavailable.
    /// </summary>
    public static unsafe ProcessTree Read()
    {
        int size = 1024 * 1024;

        for (int attempt = 0; attempt < 4; attempt++)
        {
            byte[] buffer = new byte[size];
            uint returned = 0;
            int status;

            fixed (byte* start = buffer)
            {
                try
                {
                    status = PInvoke.NtQuerySystemInformation(
                        SYSTEM_INFORMATION_CLASS.SystemProcessInformation,
                        start,
                        (uint)buffer.Length,
                        &returned).Value;
                }
                catch (Exception ex) when (ex is EntryPointNotFoundException or DllNotFoundException)
                {
                    return Empty;
                }

                if (status >= 0)
                {
                    return new ProcessTree(Decode(new ReadOnlySpan<byte>(start, buffer.Length), (nint)start));
                }
            }

            if (status != InfoLengthMismatch)
            {
                return Empty;
            }

            // Processes start between the two calls; leave room for them.
            size = (int)Math.Min(int.MaxValue / 2, Math.Max((long)returned + (256 * 1024), (long)size * 2));
        }

        return Empty;
    }

    /// <summary>
    /// Walks a <c>SYSTEM_PROCESS_INFORMATION</c> list in a buffer that sits at
    /// <paramref name="baseAddress"/>, where the kernel wrote it: the image
    /// names are pointers into the same buffer.
    /// </summary>
    internal static List<KernelProcess> Decode(ReadOnlySpan<byte> buffer, nint baseAddress)
    {
        List<KernelProcess> entries = [];
        int at = 0;

        while (at >= 0 && at + EntryMinimum <= buffer.Length && entries.Count < 65_536)
        {
            ReadOnlySpan<byte> entry = buffer[at..];

            int processId = (int)BinaryPrimitives.ReadInt64LittleEndian(entry[ProcessIdAt..]);
            int parentId = (int)BinaryPrimitives.ReadInt64LittleEndian(entry[ParentIdAt..]);
            long created = BinaryPrimitives.ReadInt64LittleEndian(entry[CreateTimeAt..]);
            int session = BinaryPrimitives.ReadInt32LittleEndian(entry[SessionIdAt..]);
            ushort nameBytes = BinaryPrimitives.ReadUInt16LittleEndian(entry[ImageNameLengthAt..]);
            long namePointer = BinaryPrimitives.ReadInt64LittleEndian(entry[ImageNameBufferAt..]);

            entries.Add(new KernelProcess(processId, parentId, created, Name(buffer, baseAddress, namePointer, nameBytes), session));

            uint next = BinaryPrimitives.ReadUInt32LittleEndian(entry[NextEntryOffsetAt..]);

            if (next == 0)
            {
                break;
            }

            at += (int)next;
        }

        return entries;
    }

    private static string Name(ReadOnlySpan<byte> buffer, nint baseAddress, long pointer, ushort bytes)
    {
        if (pointer == 0 || bytes == 0 || (bytes & 1) != 0)
        {
            return string.Empty;
        }

        long offset = pointer - baseAddress;

        return offset < 0 || offset + bytes > buffer.Length
            ? string.Empty
            : new string(MemoryMarshal.Cast<byte, char>(buffer.Slice((int)offset, bytes)));
    }
}
