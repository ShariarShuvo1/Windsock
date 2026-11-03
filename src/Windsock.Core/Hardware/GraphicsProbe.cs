namespace Windsock.Core.Hardware;

/// <summary>
/// Reads what each display adapter is and what it is doing.
/// </summary>
public sealed class GraphicsProbe : IDisposable
{
    private const string EngineCounter = "gpu.engines";
    private const string MemoryCounter = "gpu.memory";
    private const string SharedCounter = "gpu.shared";
    private const int TopProcesses = 6;
    private readonly EngineTally _tally = new();
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

    /// <summary>
    /// Adds the shared-memory counter, which only a neural processor needs:
    /// it has no memory of its own and works in the system's.
    /// </summary>
    public static void RegisterShared(PerformanceQuery query)
    {
        ArgumentNullException.ThrowIfNull(query);

        _ = query.Add(SharedCounter, @"\GPU Adapter Memory(*)\Shared Usage", wildcard: true);
    }

    /// <summary>
    /// The engine and memory counters from the last <see cref="Read"/>, for
    /// every adapter they name, display or not.
    /// </summary>
    public EngineTally Tally => _tally;

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
        _tally.Clear();

        foreach (PerformanceQuery.Reading reading in query.Values(EngineCounter))
        {
            _tally.AddEngine(reading.Instance, reading.Value);
        }

        foreach (PerformanceQuery.Reading reading in query.Values(MemoryCounter))
        {
            _tally.AddDedicated(reading.Instance, reading.Value);
        }

        foreach (PerformanceQuery.Reading reading in query.Values(SharedCounter))
        {
            _tally.AddShared(reading.Instance, reading.Value);
        }
    }

    private GraphicsReading Reading(DisplayAdapter adapter, int nvidia, int amd, int intel)
    {
        NvidiaReading? sensors = nvidia >= 0 ? _nvidia?.Read(nvidia) : null;
        double? celsius = sensors?.Celsius
            ?? (amd >= 0 ? _amd?.Read(amd) : null)
            ?? (intel >= 0 ? _intel?.Read(intel) : null);

        (double busy, List<EngineLoad> engines) = _tally.Load(adapter.Luid);
        double load = sensors?.BusyPercent ?? busy;

        long used = _tally.Dedicated(adapter.Luid) ?? 0;

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
            _tally.Processes(adapter.Luid, TopProcesses));
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
