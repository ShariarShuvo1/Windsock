using System.Diagnostics;

namespace Windsock.Core.Hardware;

/// <summary>
/// The processes doing the most, three ways.
/// </summary>
/// <param name="Processor">Busiest on the processor first; only those that used any.</param>
/// <param name="Memory">Largest private working set first.</param>
/// <param name="FileIo">
/// Most file I/O first; only those that did any. This is every read and write
/// a process asked the file system for - including what the cache answered and
/// files on network shares - not what reached a disk. Windows keeps per-process
/// disk figures only for an administrator's trace.
/// </param>
/// <param name="HasRates">
/// Whether the processor and file figures are measured yet: they are
/// differences between two readings, so the first reading after anyone asked
/// has only memory.
/// </param>
/// <param name="ProcessCount">How many processes were running.</param>
public sealed record BusiestProcesses(
    IReadOnlyList<ProcessLoad> Processor,
    IReadOnlyList<ProcessLoad> Memory,
    IReadOnlyList<ProcessLoad> FileIo,
    bool HasRates,
    int ProcessCount)
{
    /// <summary>
    /// The same three ways, with every process of one program added together,
    /// as Task Manager's Processes tab groups them: a browser's dozen processes
    /// are one row. Each row's <see cref="ProcessLoad.Count"/> says how many.
    /// </summary>
    public IReadOnlyList<ProcessLoad> ProcessorByProgram { get; init; } = [];

    /// <summary>Programs by private working set, all their processes together.</summary>
    public IReadOnlyList<ProcessLoad> MemoryByProgram { get; init; } = [];

    /// <summary>Programs by file I/O, all their processes together.</summary>
    public IReadOnlyList<ProcessLoad> FileIoByProgram { get; init; } = [];
}

/// <summary>
/// One process, and what it was doing.
/// </summary>
/// <param name="ProcessId">Its id. With <paramref name="CreateTime"/>, which process exactly.</param>
/// <param name="Name">Its image name, such as <c>chrome.exe</c>.</param>
/// <param name="CreateTime">When it started, as a FILETIME: ids are reused, this is not.</param>
/// <param name="ProcessorPercent">
/// Its share of the whole processor since the last reading: processor time
/// over elapsed time times the logical processors, Task Manager's formula
/// since 2025.
/// </param>
/// <param name="PrivateBytes">Its private working set, Task Manager's Memory column.</param>
/// <param name="FileBytesPerSecond">File reads and writes, bytes a second.</param>
public sealed record ProcessLoad(
    int ProcessId,
    string Name,
    long CreateTime,
    double ProcessorPercent,
    long PrivateBytes,
    double FileBytesPerSecond)
{
    /// <summary>When the process started, or nothing for the ones Windows started before time began.</summary>
    public DateTimeOffset? Started => CreateTime > 0 ? DateTimeOffset.FromFileTime(CreateTime) : null;

    /// <summary>
    /// How many processes the row stands for: one for a process, more for a
    /// program whose processes were added together. A program's id and start
    /// are its busiest process's.
    /// </summary>
    public int Count { get; init; } = 1;
}

/// <summary>
/// Turns successive process tables into the busiest processes, by the
/// difference between one reading and the last.
/// </summary>
/// <remarks>
/// A process is followed by its id and start time together, so an id handed
/// to a new process starts from nothing rather than inheriting its
/// predecessor's counters. A name is read once, the first time a process is
/// seen; image names do not change. A reading is <see cref="Begin"/>, an
/// <see cref="Add"/> for each process in the table, then <see cref="End"/>.
/// </remarks>
public sealed class BusiestTally
{
    private const double HundredNanoseconds = 10_000_000;

    private readonly Dictionary<(int Id, long Created), Track> _tracks = [];
    private readonly List<ProcessLoad> _loads = [];
    private readonly List<(int Id, long Created)> _gone = [];
    private long _generation;
    private long _lastAt;
    private bool _hasLast;
    private double _seconds;
    private int _processors = 1;

    /// <summary>How many processes each list keeps.</summary>
    public int Top { get; init; } = 8;

    /// <summary>
    /// Forgets the last reading, so the next one stands alone: a reading after
    /// a pause would otherwise be an average over the pause presented as now.
    /// </summary>
    public void Reset()
    {
        _tracks.Clear();
        _loads.Clear();
        _hasLast = false;
    }

    /// <summary>Starts a reading taken at a <see cref="Stopwatch"/> timestamp.</summary>
    /// <param name="timestamp">When the table is read.</param>
    /// <param name="logicalProcessors">How many logical processors the machine has.</param>
    public void Begin(long timestamp, int logicalProcessors)
    {
        _generation++;
        _loads.Clear();
        _processors = Math.Max(1, logicalProcessors);
        _seconds = _hasLast && timestamp > _lastAt ? (double)(timestamp - _lastAt) / Stopwatch.Frequency : 0;
        _lastAt = timestamp;
    }

    /// <summary>Takes one process from the table.</summary>
    public void Add(in ProcessEntry entry)
    {
        if (entry.ProcessId == 0)
        {
            // The idle process is the processor not being used.
            return;
        }

        (int, long) key = (entry.ProcessId, entry.CreateTime);
        long io = entry.ReadBytes + entry.WriteBytes;

        double processor = 0;
        double file = 0;

        if (_tracks.TryGetValue(key, out Track? track))
        {
            if (_seconds > 0)
            {
                long spent = Math.Max(0, entry.ProcessorTime - track.ProcessorTime);
                processor = Math.Clamp(spent / (_seconds * HundredNanoseconds * _processors) * 100, 0, 100);
                file = Math.Max(0, io - track.Io) / _seconds;
            }
        }
        else
        {
            track = new Track(entry.Name.Length > 0 ? entry.Name.ToString() : Unnamed(entry.ProcessId));
            _tracks[key] = track;
        }

        track.ProcessorTime = entry.ProcessorTime;
        track.Io = io;
        track.Generation = _generation;

        _loads.Add(new ProcessLoad(entry.ProcessId, track.Name, entry.CreateTime, processor, entry.PrivateWorkingSet, file));
    }

    /// <summary>Finishes the reading and ranks it.</summary>
    public BusiestProcesses End()
    {
        bool rated = _seconds > 0;

        if (_tracks.Count > _loads.Count)
        {
            _gone.Clear();

            foreach (KeyValuePair<(int Id, long Created), Track> pair in _tracks)
            {
                if (pair.Value.Generation != _generation)
                {
                    _gone.Add(pair.Key);
                }
            }

            foreach ((int, long) gone in _gone)
            {
                _ = _tracks.Remove(gone);
            }
        }

        _hasLast = true;

        List<ProcessLoad> programs = Programs(_loads);

        return new BusiestProcesses(
            rated ? Rank(_loads, static load => load.ProcessorPercent, onlyActive: true) : [],
            Rank(_loads, static load => load.PrivateBytes, onlyActive: false),
            rated ? Rank(_loads, static load => load.FileBytesPerSecond, onlyActive: true) : [],
            rated,
            _loads.Count)
        {
            ProcessorByProgram = rated ? Rank(programs, static load => load.ProcessorPercent, onlyActive: true) : [],
            MemoryByProgram = Rank(programs, static load => load.PrivateBytes, onlyActive: false),
            FileIoByProgram = rated ? Rank(programs, static load => load.FileBytesPerSecond, onlyActive: true) : [],
        };
    }

    /// <summary>
    /// Adds together every process of one image name. The row keeps the id
    /// and start of the process doing the most, so it can still be pointed at.
    /// </summary>
    private static List<ProcessLoad> Programs(List<ProcessLoad> loads)
    {
        Dictionary<string, ProcessLoad> programs = new(StringComparer.OrdinalIgnoreCase);

        foreach (ProcessLoad load in loads)
        {
            if (!programs.TryGetValue(load.Name, out ProcessLoad? program))
            {
                programs[load.Name] = load;
                continue;
            }

            bool busier = load.ProcessorPercent + (load.PrivateBytes / 1e12) > program.ProcessorPercent + (program.PrivateBytes / 1e12);
            ProcessLoad lead = busier ? load : program;

            programs[load.Name] = lead with
            {
                ProcessorPercent = Math.Min(100, program.ProcessorPercent + load.ProcessorPercent),
                PrivateBytes = program.PrivateBytes + load.PrivateBytes,
                FileBytesPerSecond = program.FileBytesPerSecond + load.FileBytesPerSecond,
                Count = program.Count + 1,
            };
        }

        return [.. programs.Values];
    }

    private List<ProcessLoad> Rank(List<ProcessLoad> loads, Func<ProcessLoad, double> measure, bool onlyActive)
    {
        List<ProcessLoad> ranked = onlyActive ? loads.FindAll(load => measure(load) > 0) : [.. loads];

        ranked.Sort((left, right) =>
        {
            int order = measure(right).CompareTo(measure(left));

            if (order == 0)
            {
                order = string.Compare(left.Name, right.Name, StringComparison.OrdinalIgnoreCase);
            }

            return order != 0 ? order : left.ProcessId.CompareTo(right.ProcessId);
        });

        if (ranked.Count > Top)
        {
            ranked.RemoveRange(Top, ranked.Count - Top);
        }

        return ranked;
    }

    private static string Unnamed(int processId) => processId == 4 ? "System" : "Process " + processId.ToString(System.Globalization.CultureInfo.InvariantCulture);

    private sealed class Track(string name)
    {
        public string Name { get; } = name;

        public long ProcessorTime { get; set; }

        public long Io { get; set; }

        public long Generation { get; set; }
    }
}
