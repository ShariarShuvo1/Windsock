using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Windsock.Core.Hardware;

/// <summary>Tuning for <see cref="SystemMonitor"/>.</summary>
public sealed class SystemMonitorOptions
{
    /// <summary>How often the machine is read. Defaults to one second.</summary>
    public TimeSpan Interval { get; set; } = TimeSpan.FromSeconds(1);

    /// <summary>
    /// How many readings apart the drives are asked about themselves.
    /// Defaults to every fifth.
    /// </summary>
    public int RescanEvery { get; set; } = 5;
}

/// <summary>
/// Reads what the machine is doing, while anyone is watching.
/// </summary>
public sealed partial class SystemMonitor : BackgroundService
{
    private readonly ILogger<SystemMonitor> _logger;
    private readonly SystemMonitorOptions _options;
    private readonly ProcessorProbe _processor = new();
    private readonly ThermalZone _thermal = new();
    private readonly GraphicsProbe _graphics = new();
    private readonly StorageProbe _storage = new();
    private readonly Lock _gate = new();

    private PerformanceQuery? _query;
    private CpuThermalProbe? _cpuThermal;
    private int _watchers;
    private long _tick;

    public SystemMonitor(IOptions<SystemMonitorOptions> options, ILogger<SystemMonitor> logger)
    {
        ArgumentNullException.ThrowIfNull(options);

        _logger = logger;
        _options = options.Value;
    }

    /// <summary>Raised on the sampling thread each time the machine is read.</summary>
    public event EventHandler<SystemSnapshot>? Sampled;

    /// <summary>Raised on the sampling thread once the machine has been identified.</summary>
    public event EventHandler? Identified;

    /// <summary>What the machine is. Empty until the first watcher arrives.</summary>
    public MachineFacts Machine { get; private set; } = MachineFacts.Unknown;

    /// <summary>Whether the machine has been identified yet.</summary>
    public bool IsIdentified { get; private set; }

    /// <summary>The most recent reading.</summary>
    public SystemSnapshot Latest { get; private set; } = SystemSnapshot.Empty;

    /// <summary>How often readings are taken.</summary>
    public TimeSpan Interval => _options.Interval;

    /// <summary>
    /// Whether the processor's temperature is being read, and if not, why not.
    /// </summary>
    public (PawnIoState State, string? Detail, string? Explanation) CpuTemperature =>
        _cpuThermal is { } probe
            ? (probe.State, probe.Detail, probe.Explanation)
            : (PawnIoState.Stopped, null, null);

    /// <summary>
    /// Asks for readings, and keeps them coming until the token is let go.
    /// </summary>
    public IDisposable Watch()
    {
        lock (_gate)
        {
            _watchers++;
        }

        return new Watcher(this);
    }

    private void Release()
    {
        lock (_gate)
        {
            _watchers = Math.Max(0, _watchers - 1);
        }
    }

    private bool IsWatched
    {
        get
        {
            lock (_gate)
            {
                return _watchers > 0;
            }
        }
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using PeriodicTimer timer = new(_options.Interval);

        try
        {
            await Sample(timer, stoppingToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
        }
    }

    private async Task Sample(PeriodicTimer timer, CancellationToken stoppingToken)
    {
        while (await timer.WaitForNextTickAsync(stoppingToken).ConfigureAwait(false))
        {
            if (!IsWatched)
            {
                continue;
            }

            try
            {
                Read();
            }
#pragma warning disable CA1031 // Any failure here is one tile going quiet, not a reason to stop reading.
            catch (Exception error)
#pragma warning restore CA1031
            {
                LogReadFailed(error);
            }
        }
    }

    private void Read()
    {
        if (_query is null)
        {
            Begin();
        }

        if (_query is not { } query)
        {
            return;
        }

        query.Collect();
        if (_tick++ == 0)
        {
            return;
        }

        SystemCounts counts = SystemInformation.Read();
        bool rescan = _options.RescanEvery <= 1 || _tick % _options.RescanEvery == 1;

        SystemSnapshot snapshot = new(
            DateTimeOffset.Now,
            _processor.Read(query, counts, _cpuThermal?.Read()),
            _graphics.Read(query),
            Memory(counts),
            _storage.Read(query, rescan),
            _thermal.Read(query));

        Latest = snapshot;
        Sampled?.Invoke(this, snapshot);
    }

    private void Begin()
    {
        IReadOnlyList<GraphicsFacts> graphics = _graphics.ReadFacts();

        Machine = MachineProbe.Read(graphics);
        IsIdentified = true;
        Identified?.Invoke(this, EventArgs.Empty);
        _cpuThermal = new CpuThermalProbe();

        PerformanceQuery? query = PerformanceQuery.Open();

        if (query is null)
        {
            LogNoCounters();
            return;
        }

        _processor.Register(query, Machine.Processor);
        _thermal.Register(query);
        GraphicsProbe.Register(query);
        StorageProbe.Register(query);

        _query = query;

        LogStarted(Machine.Processor.Name, graphics.Count);
    }

    private static MemoryReading Memory(SystemCounts counts) => new(
        counts.PhysicalTotal,
        counts.PhysicalAvailable,
        counts.SystemCache,
        counts.CommitTotal,
        counts.CommitLimit,
        counts.KernelPaged,
        counts.KernelNonPaged);

    public override void Dispose()
    {
        base.Dispose();

        _query?.Dispose();
        _query = null;
        _cpuThermal?.Dispose();
        _cpuThermal = null;
        _graphics.Dispose();
    }

    private sealed class Watcher(SystemMonitor monitor) : IDisposable
    {
        private SystemMonitor? _monitor = monitor;

        public void Dispose()
        {
            _monitor?.Release();
            _monitor = null;
        }
    }

    [LoggerMessage(
        Level = LogLevel.Information,
        Message = "System monitor started on {Processor} with {Adapters} display adapter(s)")]
    private partial void LogStarted(string processor, int adapters);

    [LoggerMessage(
        Level = LogLevel.Warning,
        Message = "Performance counters would not open; the system tab will show what it can")]
    private partial void LogNoCounters();

    [LoggerMessage(Level = LogLevel.Warning, Message = "A system reading failed")]
    private partial void LogReadFailed(Exception error);
}
