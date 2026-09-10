using System.Runtime.InteropServices;

namespace Windsock.Core.Hardware;

/// <summary>
/// A set of Windows performance counters, read together.
/// </summary>
public sealed partial class PerformanceQuery : IDisposable
{
    private const uint AsDouble = 0x00000200 | 0x00008000;
    private const uint Ok = 0;
    private const uint MoreData = 0x800007D2;

    private sealed class Counter
    {
        public nint Handle;
        public bool Wildcard;
        public byte[] Buffer = [];
        public List<Reading> Readings = [];
        public double Single;
    }

    /// <summary>One instance of a counter, and what it read.</summary>
    public readonly record struct Reading(string Instance, double Value);

    private readonly Dictionary<string, Counter> _counters = new(StringComparer.Ordinal);
    private nint _query;
    private bool _primed;
    private bool _closed;

    /// <summary>Opens an empty query.</summary>
    public static PerformanceQuery? Open()
    {
        PerformanceQuery query = new();

        if (PdhOpenQueryW(null, 0, out query._query) != Ok)
        {
            return null;
        }

        return query;
    }

    /// <summary>Whether a first collection has been made.</summary>
    public bool IsReady => _primed;

    /// <summary>
    /// Adds a counter under a name of the caller's choosing.
    /// </summary>
    public bool Add(string name, string path, bool wildcard = false)
    {
        ArgumentNullException.ThrowIfNull(name);

        if (_closed || _query == 0)
        {
            return false;
        }

        if (PdhAddEnglishCounterW(_query, path, 0, out nint handle) != Ok)
        {
            return false;
        }

        _counters[name] = new Counter
        {
            Handle = handle,
            Wildcard = wildcard,
            Buffer = wildcard ? new byte[8192] : [],
        };

        return true;
    }

    /// <summary>Whether a counter of that name resolved when it was added.</summary>
    public bool Has(string name) => _counters.ContainsKey(name);

    /// <summary>Reads every counter, replacing what the last collection held.</summary>
    public void Collect()
    {
        if (_closed || _query == 0 || PdhCollectQueryData(_query) != Ok)
        {
            return;
        }

        foreach (Counter counter in _counters.Values)
        {
            if (counter.Wildcard)
            {
                ReadArray(counter);
            }
            else
            {
                ReadOne(counter);
            }
        }

        _primed = true;
    }

    /// <summary>What a single-instance counter last read.</summary>
    public double Value(string name) =>
        _counters.TryGetValue(name, out Counter? counter) ? counter.Single : 0;

    /// <summary>What each instance of a counter last read.</summary>
    public IReadOnlyList<Reading> Values(string name) =>
        _counters.TryGetValue(name, out Counter? counter) ? counter.Readings : [];

    private static void ReadOne(Counter counter)
    {
        uint status = PdhGetFormattedCounterValue(counter.Handle, AsDouble, out uint _, out CounterValue value);
        counter.Single = status == Ok && value.Status == Ok ? value.Double : 0;
    }

    private static void ReadArray(Counter counter)
    {
        counter.Readings.Clear();

        uint size = (uint)counter.Buffer.Length;
        uint count = 0;
        uint status;
        for (int attempt = 0; attempt < 2; attempt++)
        {
            status = Fill(counter, ref size, ref count);

            if (status == Ok)
            {
                Take(counter, count);
                return;
            }

            if (status != MoreData)
            {
                return;
            }

            counter.Buffer = new byte[Math.Max(size, (uint)counter.Buffer.Length * 2)];
            size = (uint)counter.Buffer.Length;
        }
    }

    private static unsafe uint Fill(Counter counter, ref uint size, ref uint count)
    {
        fixed (byte* buffer = counter.Buffer)
        {
            return PdhGetFormattedCounterArrayW(counter.Handle, AsDouble, ref size, ref count, buffer);
        }
    }

    private static unsafe void Take(Counter counter, uint count)
    {
        fixed (byte* buffer = counter.Buffer)
        {
            CounterItem* items = (CounterItem*)buffer;

            for (uint index = 0; index < count; index++)
            {
                CounterItem item = items[index];

                if (item.Value.Status != Ok)
                {
                    continue;
                }

                string instance = item.Name == 0
                    ? string.Empty
                    : Marshal.PtrToStringUni(item.Name) ?? string.Empty;

                counter.Readings.Add(new Reading(instance, item.Value.Double));
            }
        }
    }

    public void Dispose()
    {
        if (_closed)
        {
            return;
        }

        _closed = true;

        if (_query != 0)
        {
            _ = PdhCloseQuery(_query);
            _query = 0;
        }

        _counters.Clear();
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct CounterValue
    {
        public uint Status;
        private readonly uint _padding;
        public double Double;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct CounterItem
    {
        public nint Name;
        public CounterValue Value;
    }

    [LibraryImport("pdh.dll", StringMarshalling = StringMarshalling.Utf16)]
    private static partial uint PdhOpenQueryW(string? source, nuint userData, out nint query);

    [LibraryImport("pdh.dll", StringMarshalling = StringMarshalling.Utf16)]
    private static partial uint PdhAddEnglishCounterW(nint query, string path, nuint userData, out nint counter);

    [LibraryImport("pdh.dll")]
    private static partial uint PdhCollectQueryData(nint query);

    [LibraryImport("pdh.dll")]
    private static partial uint PdhGetFormattedCounterValue(
        nint counter,
        uint format,
        out uint type,
        out CounterValue value);

    [LibraryImport("pdh.dll")]
    private static unsafe partial uint PdhGetFormattedCounterArrayW(
        nint counter,
        uint format,
        ref uint size,
        ref uint count,
        byte* buffer);

    [LibraryImport("pdh.dll")]
    private static partial uint PdhCloseQuery(nint query);
}
