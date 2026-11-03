using System.Diagnostics;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Windsock.Core.Power;

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

    /// <summary>
    /// Where the battery's figures come from. Nothing, the default, is the
    /// machine's own battery driver; a harness on a desktop can hand in a
    /// made-up battery to see the pages that only a laptop shows.
    /// </summary>
    public Func<IBatterySource>? BatterySource { get; set; }

    /// <summary>
    /// Opens the counter query; nothing, the default, is the real one. Tests
    /// hand in one that fails, to see the monitor retry it and nothing else.
    /// </summary>
    internal Func<PerformanceQuery?>? QueryOpener { get; set; }
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
    private readonly BusiestTally _busiest = new();
    private readonly Lock _gate = new();

    /// <summary>
    /// Held for the whole of a reading, and by <see cref="Dispose"/> before it
    /// frees anything a reading uses: the host's stop has one budget for every
    /// service, so the sampling thread can still be inside a reading - walking
    /// the native process table, collecting counters - when the host disposes.
    /// </summary>
    private readonly Lock _readGate = new();

    private PerformanceQuery? _query;
#if !STORE
    private CpuThermalProbe? _cpuThermal;
#endif
    private BatteryProbe? _battery;
    private PowerModeWatch? _powerMode;
    private IReadOnlyList<NpuFacts> _npus = [];
    private SystemProcessTable? _processes;
    private int _watchers;
    private int _quietWatchers;
    private int _busiestWatchers;
    private bool _busiestFresh;
    private bool _begun;
    private bool _disposed;
    private bool _saidNoCounters;
    private long _tick;
    private long _beats;

    /// <summary>
    /// How many of the timer's beats pass between readings while only history
    /// is asking: a reading every five seconds is plenty for a minute's average
    /// and peak, at a fifth of the cost of what a page on screen needs.
    /// </summary>
    private const int QuietEvery = 5;

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
#if STORE
#pragma warning disable CA1822 // An instance member in the full edition; the Store edition has nothing to read.
    public (PawnIoState State, string? Detail, string? Explanation) CpuTemperature =>
        (PawnIoState.Stopped, null, null);

    /// <summary>Whether the processor package's power is being read. Never, in the Store edition.</summary>
    public bool ReadsProcessorPower => false;
#pragma warning restore CA1822
#else
    public (PawnIoState State, string? Detail, string? Explanation) CpuTemperature =>
        _cpuThermal is { } probe
            ? (probe.State, probe.Detail, probe.Explanation)
            : (PawnIoState.Stopped, null, null);

    /// <summary>
    /// Whether the processor package's power is being read: PawnIO is running
    /// and the chip's energy counter answered. When it is not,
    /// <see cref="CpuTemperature"/> says why.
    /// </summary>
    public bool ReadsProcessorPower => _cpuThermal?.ReadsPower ?? false;
#endif

    /// <summary>
    /// Asks for readings, and keeps them coming until the token is let go.
    /// </summary>
    public IDisposable Watch()
    {
        lock (_gate)
        {
            _watchers++;
        }

        return new Watcher(this, Kind.Full);
    }

    /// <summary>
    /// Asks for readings now and then, for history, until the token is let go.
    /// While anything on screen watches too, readings come at the full rate
    /// and a quiet watcher sees every one.
    /// </summary>
    public IDisposable WatchQuietly()
    {
        lock (_gate)
        {
            _quietWatchers++;
        }

        return new Watcher(this, Kind.Quiet);
    }

    /// <summary>
    /// Asks for readings at the full rate with the busiest processes in them
    /// (<see cref="SystemSnapshot.Busiest"/>), until the token is let go.
    /// </summary>
    /// <remarks>
    /// The process table is a single system call covering every process, but
    /// it is a large one, so it is read only while a page that shows it is
    /// on screen. The first reading after asking has memory only: processor
    /// and file figures are differences, and need a second reading.
    /// </remarks>
    public IDisposable WatchBusiest()
    {
        lock (_gate)
        {
            _watchers++;

            if (_busiestWatchers++ == 0)
            {
                _busiestFresh = true;
            }
        }

        return new Watcher(this, Kind.Busiest);
    }

    private void Release(Kind kind)
    {
        lock (_gate)
        {
            switch (kind)
            {
                case Kind.Quiet:
                    _quietWatchers = Math.Max(0, _quietWatchers - 1);
                    break;

                case Kind.Busiest:
                    _busiestWatchers = Math.Max(0, _busiestWatchers - 1);
                    _watchers = Math.Max(0, _watchers - 1);
                    break;

                default:
                    _watchers = Math.Max(0, _watchers - 1);
                    break;
            }
        }
    }

    private bool IsQuietlyWatched
    {
        get
        {
            lock (_gate)
            {
                return _quietWatchers > 0;
            }
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
        finally
        {
            // The process table is native memory; let it go on the thread that
            // walks it, now that no reading follows.
            lock (_readGate)
            {
                _processes?.Dispose();
                _processes = null;
            }
        }
    }

    private async Task Sample(PeriodicTimer timer, CancellationToken stoppingToken)
    {
        while (await timer.WaitForNextTickAsync(stoppingToken).ConfigureAwait(false))
        {
            bool quietTurn = _beats++ % QuietEvery == 0;
            bool watched = IsWatched;

            if (!watched && !(quietTurn && IsQuietlyWatched))
            {
                continue;
            }

            try
            {
                lock (_readGate)
                {
                    if (!_disposed)
                    {
                        Read(watched);
                    }
                }
            }
#pragma warning disable CA1031 // Any failure here is one tile going quiet, not a reason to stop reading.
            catch (Exception error)
#pragma warning restore CA1031
            {
                LogReadFailed(error);
            }
        }
    }

    /// <summary>Takes a reading. Called with the read gate held.</summary>
    /// <param name="watched">
    /// Whether something on screen asked for it, rather than only history: the
    /// battery is read for the first and not the second, since history keeps
    /// nothing of it, and asking a battery's controller every five seconds
    /// around the clock for nobody is a laptop's worth of wake-ups.
    /// </param>
    private void Read(bool watched)
    {
        // Identification and the probes happen once, whatever becomes of them;
        // only the counter query is tried again, a reading at a time, until it
        // opens. Until 2026-10-05 the whole of Begin ran again on every reading
        // while the query would not open, and each run left a battery probe, a
        // power-mode registration and a PawnIO handle behind it.
        if (!_begun)
        {
            _begun = true;
            Begin();
        }

        _query ??= OpenQuery();

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

#if STORE
        ProcessorReading processor = _processor.Read(query, counts);
#else
        ProcessorReading processor = _processor.Read(query, counts, _cpuThermal?.Read(), _cpuThermal?.ReadWatts());
#endif
        IReadOnlyList<GraphicsReading> graphics = _graphics.Read(query);
        BatteryReading? battery = watched ? _battery?.Read(TimeSpan.FromMilliseconds(Environment.TickCount64)) : null;

        SystemSnapshot snapshot = new(
            DateTimeOffset.Now,
            processor,
            graphics,
            Memory(counts),
            _storage.Read(query, rescan),
            _thermal.Read(query))
        {
            Battery = battery,
            Power = _battery is { } power ? PowerReading.From(power.System, _powerMode?.Current) : PowerReading.Unknown,
            Npus = NpuProbe.Read(_npus, _graphics.Tally),
            Busiest = Busiest(),
        };

        Latest = snapshot;
        Sampled?.Invoke(this, snapshot);
    }

    /// <summary>The busiest processes, while asked for; otherwise the table is let go.</summary>
    private BusiestProcesses? Busiest()
    {
        bool wanted;
        bool fresh;

        lock (_gate)
        {
            wanted = _busiestWatchers > 0;
            fresh = _busiestFresh;
            _busiestFresh = false;
        }

        if (!wanted || !SystemProcessTable.IsSupported)
        {
            if (_processes is not null)
            {
                _processes.Dispose();
                _processes = null;
                _busiest.Reset();
            }

            return null;
        }

        if (fresh)
        {
            _busiest.Reset();
        }

        _processes ??= new SystemProcessTable();
        // The machine's logical processors, as Task Manager divides by: the
        // runtime's count shrinks under an affinity mask or a job object.
        int processors = Machine.Processor.Threads > 0 ? Machine.Processor.Threads : Environment.ProcessorCount;
        _busiest.Begin(Stopwatch.GetTimestamp(), processors);

        foreach (ProcessEntry entry in _processes.Read())
        {
            _busiest.Add(entry);
        }

        return _busiest.End();
    }

    private void Begin()
    {
        IReadOnlyList<GraphicsFacts> graphics = _graphics.ReadFacts();

        _battery = new BatteryProbe(_options.BatterySource?.Invoke() ?? new WindowsBatterySource());
        _npus = NpuProbe.Find();
        _powerMode = PowerModeWatch.Start();

        Machine = MachineProbe.Read(graphics) with
        {
            HasBattery = _battery.Read(TimeSpan.FromMilliseconds(Environment.TickCount64)) is not null,
            Npus = _npus,
        };

        IsIdentified = true;
        Identified?.Invoke(this, EventArgs.Empty);
#if !STORE
        _cpuThermal = new CpuThermalProbe();
#endif

        LogStarted(Machine.Processor.Name, graphics.Count);
    }

    /// <summary>Opens the counter query and adds every probe's counters, or answers nothing.</summary>
    private PerformanceQuery? OpenQuery()
    {
        PerformanceQuery? query = _options.QueryOpener is { } opener ? opener() : PerformanceQuery.Open();

        if (query is null)
        {
            if (!_saidNoCounters)
            {
                _saidNoCounters = true;
                LogNoCounters();
            }

            return null;
        }

        _processor.Register(query, Machine.Processor);
        _thermal.Register(query);
        GraphicsProbe.Register(query);
        StorageProbe.Register(query);

        if (_npus.Count > 0)
        {
            GraphicsProbe.RegisterShared(query);
        }

        return query;
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

        // Wait for a reading under way to finish before freeing what it uses.
        // One stuck in a driver call past the wait keeps its resources: they
        // go with the process, which is exiting, rather than from under it.
        bool held = _readGate.TryEnter(TimeSpan.FromSeconds(2));

        try
        {
            _disposed = true;

            if (held)
            {
                Release();
            }
        }
        finally
        {
            if (held)
            {
                _readGate.Exit();
            }
        }
    }

    private void Release()
    {
        _query?.Dispose();
        _query = null;
#if !STORE
        _cpuThermal?.Dispose();
        _cpuThermal = null;
#endif
        _graphics.Dispose();
        _battery?.Dispose();
        _battery = null;
        _powerMode?.Dispose();
        _powerMode = null;
        _processes?.Dispose();
        _processes = null;
    }

    private enum Kind
    {
        Full,

        Quiet,

        Busiest,
    }

    private sealed class Watcher(SystemMonitor monitor, Kind kind) : IDisposable
    {
        private SystemMonitor? _monitor = monitor;

        public void Dispose()
        {
            _monitor?.Release(kind);
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
