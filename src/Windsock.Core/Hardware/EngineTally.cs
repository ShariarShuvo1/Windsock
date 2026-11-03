namespace Windsock.Core.Hardware;

/// <summary>
/// Adds up the <c>GPU Engine</c> and <c>GPU Adapter Memory</c> counters by
/// adapter, for display adapters and neural processors alike.
/// </summary>
/// <remarks>
/// Windows files every adapter that schedules work through the graphics
/// kernel under the same counter set, keyed by its LUID: a neural processor's
/// engines appear beside a graphics card's, typed <c>Neural</c> or
/// <c>Compute</c>. An engine's load is the sum over processes; an adapter's is
/// its busiest engine, which is how Task Manager draws both.
/// </remarks>
public sealed class EngineTally
{
    /// <summary>Below this an engine or a process is not worth a line.</summary>
    public const double Noticeable = 0.5;

    private readonly Dictionary<string, Dictionary<string, double>> _engines = new(StringComparer.Ordinal);
    private readonly Dictionary<string, Dictionary<int, double>> _processes = new(StringComparer.Ordinal);
    private readonly Dictionary<string, long> _dedicated = new(StringComparer.Ordinal);
    private readonly Dictionary<string, long> _shared = new(StringComparer.Ordinal);

    /// <summary>Forgets the last collection.</summary>
    public void Clear()
    {
        foreach (Dictionary<string, double> engines in _engines.Values)
        {
            engines.Clear();
        }

        foreach (Dictionary<int, double> processes in _processes.Values)
        {
            processes.Clear();
        }

        _dedicated.Clear();
        _shared.Clear();
    }

    /// <summary>Takes one <c>GPU Engine(*)\Utilization Percentage</c> instance.</summary>
    public void AddEngine(string instance, double value)
    {
        if (value <= 0 || GpuEngineName.Parse(instance) is not { } engine)
        {
            return;
        }

        Dictionary<string, double> engines = For(_engines, engine.Luid);
        engines[engine.Engine] = engines.GetValueOrDefault(engine.Engine) + value;

        Dictionary<int, double> processes = For(_processes, engine.Luid);
        processes[engine.ProcessId] = Math.Max(processes.GetValueOrDefault(engine.ProcessId), value);
    }

    /// <summary>Takes one <c>GPU Adapter Memory(*)\Dedicated Usage</c> instance.</summary>
    public void AddDedicated(string instance, double bytes) => Add(_dedicated, instance, bytes);

    /// <summary>Takes one <c>GPU Adapter Memory(*)\Shared Usage</c> instance.</summary>
    public void AddShared(string instance, double bytes) => Add(_shared, instance, bytes);

    /// <summary>An adapter's dedicated memory in use, or nothing where no counter named it.</summary>
    public long? Dedicated(string luid) => _dedicated.TryGetValue(luid, out long bytes) ? bytes : null;

    /// <summary>An adapter's shared memory in use, or nothing where no counter named it.</summary>
    public long? Shared(string luid) => _shared.TryGetValue(luid, out long bytes) ? bytes : null;

    /// <summary>An adapter's busiest engine, and each engine worth a line, busiest first.</summary>
    public (double Busy, List<EngineLoad> Engines) Load(string luid)
    {
        List<EngineLoad> loads = [];
        double busiest = 0;

        if (!_engines.TryGetValue(luid, out Dictionary<string, double>? engines))
        {
            return (0, loads);
        }

        foreach ((string kind, double value) in engines)
        {
            double percent = Math.Clamp(value, 0, 100);

            if (percent < Noticeable)
            {
                continue;
            }

            busiest = Math.Max(busiest, percent);
            loads.Add(new EngineLoad(GpuEngineName.Describe(kind), percent));
        }

        loads.Sort(static (left, right) => right.Percent.CompareTo(left.Percent));

        return (busiest, loads);
    }

    /// <summary>The processes using an adapter, busiest first.</summary>
    public List<GraphicsProcess> Processes(string luid, int top)
    {
        List<GraphicsProcess> busiest = [];

        if (!_processes.TryGetValue(luid, out Dictionary<int, double>? processes))
        {
            return busiest;
        }

        foreach ((int process, double value) in processes)
        {
            if (value >= Noticeable)
            {
                busiest.Add(new GraphicsProcess(process, Math.Clamp(value, 0, 100)));
            }
        }

        busiest.Sort(static (left, right) => right.Percent.CompareTo(left.Percent));

        if (busiest.Count > top)
        {
            busiest.RemoveRange(top, busiest.Count - top);
        }

        return busiest;
    }

    private static void Add(Dictionary<string, long> map, string instance, double bytes)
    {
        string adapter = GpuEngineName.Adapter(instance);

        if (adapter.Length > 0)
        {
            map[adapter] = map.GetValueOrDefault(adapter) + (long)bytes;
        }
    }

    private static Dictionary<string, TValue> For<TValue>(
        Dictionary<string, Dictionary<string, TValue>> map,
        string key)
    {
        if (!map.TryGetValue(key, out Dictionary<string, TValue>? found))
        {
            found = new Dictionary<string, TValue>(StringComparer.Ordinal);
            map[key] = found;
        }

        return found;
    }

    private static Dictionary<int, TValue> For<TValue>(
        Dictionary<string, Dictionary<int, TValue>> map,
        string key)
    {
        if (!map.TryGetValue(key, out Dictionary<int, TValue>? found))
        {
            found = [];
            map[key] = found;
        }

        return found;
    }
}
