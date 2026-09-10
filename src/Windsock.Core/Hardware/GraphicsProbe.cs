namespace Windsock.Core.Hardware;

/// <summary>
/// Reads what each display adapter is and what it is doing.
/// </summary>
public sealed class GraphicsProbe : IDisposable
{
    private const string EngineCounter = "gpu.engines";
    private const string MemoryCounter = "gpu.memory";
    private const int TopProcesses = 6;
    private const double Noticeable = 0.5;
    private readonly Dictionary<string, Dictionary<string, double>> _engines = [];
    private readonly Dictionary<string, Dictionary<int, double>> _processes = [];
    private readonly Dictionary<string, long> _memory = [];
    private IReadOnlyList<DisplayAdapter> _adapters = [];
    private Nvml? _nvidia;
    private int[] _nvidiaFor = [];
    private Adl? _amd;
    private int[] _amdFor = [];
    private Igcl? _intel;
    private int[] _intelFor = [];

    /// <summary>Reads what is fitted, and opens whatever will talk about it.</summary>
    public IReadOnlyList<GraphicsFacts> ReadFacts()
    {
        _adapters = DisplayAdapters.Read();
        _nvidia = Nvml.Open();
        _nvidiaFor = Pair(_adapters, GraphicsVendor.Nvidia, _nvidia?.Count ?? 0);
        _amd = Adl.Open();
        _amdFor = Pair(_adapters, GraphicsVendor.Amd, _amd?.Count ?? 0);
        _intel = Igcl.Open();
        _intelFor = Pair(_adapters, GraphicsVendor.Intel, _intel?.Count ?? 0);

        List<GraphicsFacts> facts = [];

        for (int index = 0; index < _adapters.Count; index++)
        {
            DisplayAdapter adapter = _adapters[index];

            facts.Add(new GraphicsFacts(
                adapter.Name,
                adapter.Vendor == GraphicsVendor.Nvidia && _nvidia is not null ? _nvidia.Driver : string.Empty,
                adapter.Memory,
                adapter.Vendor));
        }

        return facts;
    }

    private static int[] Pair(IReadOnlyList<DisplayAdapter> adapters, GraphicsVendor vendor, int devices)
    {
        int[] paired = new int[adapters.Count];
        Array.Fill(paired, -1);

        int next = 0;

        for (int index = 0; index < adapters.Count && next < devices; index++)
        {
            if (adapters[index].Vendor == vendor)
            {
                paired[index] = next++;
            }
        }

        return paired;
    }

    /// <summary>Adds the counters this probe reads to a shared query.</summary>
    public static void Register(PerformanceQuery query)
    {
        ArgumentNullException.ThrowIfNull(query);

        _ = query.Add(EngineCounter, @"\GPU Engine(*)\Utilization Percentage", wildcard: true);
        _ = query.Add(MemoryCounter, @"\GPU Adapter Memory(*)\Dedicated Usage", wildcard: true);
    }

    /// <summary>Takes a reading from the last collection.</summary>
    public IReadOnlyList<GraphicsReading> Read(PerformanceQuery query)
    {
        ArgumentNullException.ThrowIfNull(query);

        Gather(query);

        List<GraphicsReading> readings = new(_adapters.Count);

        for (int index = 0; index < _adapters.Count; index++)
        {
            readings.Add(Reading(
                _adapters[index],
                _nvidiaFor[index],
                _amdFor[index],
                _intelFor[index]));
        }

        return readings;
    }

    private void Gather(PerformanceQuery query)
    {
        foreach (Dictionary<string, double> engines in _engines.Values)
        {
            engines.Clear();
        }

        foreach (Dictionary<int, double> processes in _processes.Values)
        {
            processes.Clear();
        }

        _memory.Clear();

        foreach (PerformanceQuery.Reading reading in query.Values(EngineCounter))
        {
            if (reading.Value <= 0 || GpuEngineName.Parse(reading.Instance) is not { } engine)
            {
                continue;
            }

            Dictionary<string, double> engines = For(_engines, engine.Luid);
            engines[engine.Engine] = engines.GetValueOrDefault(engine.Engine) + reading.Value;
            Dictionary<int, double> processes = For(_processes, engine.Luid);
            processes[engine.ProcessId] = Math.Max(
                processes.GetValueOrDefault(engine.ProcessId),
                reading.Value);
        }

        foreach (PerformanceQuery.Reading reading in query.Values(MemoryCounter))
        {
            string adapter = GpuEngineName.Adapter(reading.Instance);

            if (adapter.Length > 0)
            {
                _memory[adapter] = _memory.GetValueOrDefault(adapter) + (long)reading.Value;
            }
        }
    }

    private static Dictionary<string, TValue> For<TValue>(
        Dictionary<string, Dictionary<string, TValue>> map,
        string key)
    {
        if (!map.TryGetValue(key, out Dictionary<string, TValue>? found))
        {
            found = [];
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

    private GraphicsReading Reading(DisplayAdapter adapter, int nvidia, int amd, int intel)
    {
        NvidiaReading? sensors = nvidia >= 0 ? _nvidia?.Read(nvidia) : null;
        double? celsius = sensors?.Celsius
            ?? (amd >= 0 ? _amd?.Read(amd) : null)
            ?? (intel >= 0 ? _intel?.Read(intel) : null);

        (double busy, List<EngineLoad> engines) = Engines(adapter.Luid);
        double load = sensors?.BusyPercent ?? busy;

        long used = _memory.GetValueOrDefault(adapter.Luid);

        if (sensors is { MemoryUsed: > 0 })
        {
            used = sensors.MemoryUsed;
        }

        long fitted = sensors is { MemoryTotal: > 0 } ? sensors.MemoryTotal : adapter.Memory;

        return new GraphicsReading(
            adapter.Name,
            Math.Clamp(load, 0, 100),
            celsius,
            sensors?.Watts,
            sensors?.FanPercent,
            sensors?.Megahertz,
            sensors?.MemoryMegahertz,
            used,
            fitted,
            engines,
            Processes(adapter.Luid));
    }

    private (double Busy, List<EngineLoad> Engines) Engines(string luid)
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

    private List<GraphicsProcess> Processes(string luid)
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

        if (busiest.Count > TopProcesses)
        {
            busiest.RemoveRange(TopProcesses, busiest.Count - TopProcesses);
        }

        return busiest;
    }

    public void Dispose()
    {
        _nvidia?.Dispose();
        _nvidia = null;
        _amd?.Dispose();
        _amd = null;
        _intel?.Dispose();
        _intel = null;
    }
}
