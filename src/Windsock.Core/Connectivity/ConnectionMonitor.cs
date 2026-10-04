using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Windsock.Core.History;
using Windsock.Core.Networking;
using Windsock.Core.Settings;

namespace Windsock.Core.Connectivity;

/// <summary>
/// Checks the connection every few seconds while anyone wants to know, and
/// settles those checks into outages, network changes and VPN changes.
/// </summary>
/// <remarks>
/// <para>
/// A check asks Windows what it thinks, pings the router, and pings an internet
/// target when one is chosen. None of it happens while nobody is watching: the
/// Connection section watches while it is on screen, and the alerts watch for as
/// long as watching in the background is switched on.
/// </para>
/// <para>
/// Events are raised on the checking thread. Anything that touches the screen
/// must marshal.
/// </para>
/// </remarks>
public sealed partial class ConnectionMonitor : BackgroundService
{
    /// <summary>How long after Windows reports a change before looking, so a burst of reports is one check.</summary>
    private static readonly TimeSpan SettleDelay = TimeSpan.FromSeconds(1.5);

    /// <summary>How often an outage in progress is noted in the log, in case Windsock never sees it end.</summary>
    private static readonly TimeSpan TouchEvery = TimeSpan.FromSeconds(30);

    private readonly IConnectionFactsSource _facts;
    private readonly INetworkPathProbe _probe;
    private readonly IOutageStore _store;
    private readonly WindsockSettings _settings;
    private readonly ILogger<ConnectionMonitor> _logger;
    private readonly TimeProvider _time;
    private readonly OutageTracker _tracker = new();
    private readonly SemaphoreSlim _nudge = new(0, 1);
    private readonly Lock _gate = new();

    private int _watchers;
    private bool _paused = true;
    private long _openId;
    private DateTimeOffset _touched;
    private bool _storeFailed;

    public ConnectionMonitor(
        IConnectionFactsSource facts,
        INetworkPathProbe probe,
        IOutageStore store,
        WindsockSettings settings,
        ILogger<ConnectionMonitor> logger,
        TimeProvider time)
    {
        ArgumentNullException.ThrowIfNull(facts);

        _facts = facts;
        _probe = probe;
        _store = store;
        _settings = settings;
        _logger = logger;
        _time = time;

        _facts.Changed += OnFactsChanged;
    }

    /// <summary>Raised after every check.</summary>
    public event EventHandler<ConnectionSample>? Sampled;

    /// <summary>Raised when the settled state changes.</summary>
    public event EventHandler<ConnectionState>? StateChanged;

    /// <summary>Raised for each outage, network change and VPN change as it is settled.</summary>
    public event EventHandler<ConnectionEvent>? Happened;

    /// <summary>The settled state.</summary>
    public ConnectionState State { get; private set; } = ConnectionState.Checking;

    /// <summary>The most recent check, if there has been one since watching began.</summary>
    public ConnectionSample? Latest { get; private set; }

    /// <summary>The outage in progress, with its row in the log when it has one.</summary>
    public Outage? Current
    {
        get
        {
            lock (_gate)
            {
                return _tracker.Current is { } open ? open with { Id = _openId } : null;
            }
        }
    }

    /// <summary>The checks of roughly the last hour at the fastest interval.</summary>
    public LatencyHistory History { get; } = new(1800);

    /// <summary>Whether anyone is watching.</summary>
    public bool IsWatched
    {
        get
        {
            lock (_gate)
            {
                return _watchers > 0;
            }
        }
    }

    /// <summary>Whether the router on the current network has ever answered a ping.</summary>
    public bool RouterAnswers
    {
        get
        {
            lock (_gate)
            {
                return _tracker.RouterAnswers(Latest?.Facts.Gateway);
            }
        }
    }

    /// <summary>Asks for checks, and keeps them coming until the token is let go.</summary>
    public IDisposable Watch()
    {
        bool first;

        lock (_gate)
        {
            first = _watchers++ == 0;
        }

        if (first)
        {
            Nudge();
        }

        return new Watcher(this);
    }

    /// <summary>Checks again soon rather than at the next turn, after a setting changed.</summary>
    public void Nudge()
    {
        try
        {
            _nudge.Release();
        }
        catch (SemaphoreFullException)
        {
            // Already asked.
        }
    }

    private void Release()
    {
        bool last;

        lock (_gate)
        {
            last = _watchers > 0 && --_watchers == 0;
        }

        if (last)
        {
            Nudge();
        }
    }

    private void OnFactsChanged(object? sender, EventArgs e)
    {
        if (IsWatched)
        {
            Nudge();
        }
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await Task.Yield();
        CloseUnfinished();

        try
        {
            while (!stoppingToken.IsCancellationRequested)
            {
                if (!IsWatched)
                {
                    Pause();
                    await _nudge.WaitAsync(stoppingToken).ConfigureAwait(false);
                    continue;
                }

                long started = _time.GetTimestamp();

                await SampleOnceAsync(stoppingToken).ConfigureAwait(false);

                TimeSpan rest = _settings.Connection.CheckEvery - _time.GetElapsedTime(started);

                if (rest > TimeSpan.Zero && await _nudge.WaitAsync(rest, stoppingToken).ConfigureAwait(false))
                {
                    // Something changed; give it a moment to finish changing.
                    await Task.Delay(SettleDelay, _time, stoppingToken).ConfigureAwait(false);
                }
            }
        }
        catch (OperationCanceledException)
        {
        }
        finally
        {
            Pause();
        }
    }

    /// <summary>Takes one check and settles it. Exposed so tests can drive the monitor a check at a time.</summary>
    internal async Task SampleOnceAsync(CancellationToken cancellationToken)
    {
        ConnectionSettings choices = _settings.Connection;
        ConnectionFacts facts = ReadFacts();
        string? target = facts.HasNetwork || facts.Reach != WindowsReach.None ? choices.InternetTarget : null;

        Task<TimeSpan?> router = facts.Gateway is { } gateway ? Ping(gateway, cancellationToken) : Task.FromResult<TimeSpan?>(null);
        Task<TimeSpan?> internet = target is not null ? Ping(target, cancellationToken) : Task.FromResult<TimeSpan?>(null);

        await Task.WhenAll(router, internet).ConfigureAwait(false);

        ConnectionSample sample = new(
            _time.GetLocalNow(),
            facts,
            await router.ConfigureAwait(false),
            await internet.ConfigureAwait(false),
            InternetChecked: choices.InternetTarget is not null);

        Observe(sample, TrackerRules.For(choices.CheckEvery, choices.OutageAfter));
    }

    private void Observe(in ConnectionSample sample, TrackerRules rules)
    {
        IReadOnlyList<ConnectionEvent> events;
        ConnectionState before;
        ConnectionState after;

        lock (_gate)
        {
            before = State;
            events = _tracker.Observe(sample, rules);
            after = _tracker.State;
            State = after;
            Latest = sample;
            _paused = false;
        }

        History.Add(new LatencyPoint(
            sample.At,
            sample.Router?.TotalMilliseconds,
            sample.Internet?.TotalMilliseconds,
            sample.RouterChecked,
            sample.InternetChecked,
            after.IsOffline));

        Settle(events, sample.At);

        Sampled?.Invoke(this, sample);

        if (after != before)
        {
            StateChanged?.Invoke(this, after);
        }
    }

    private void Pause()
    {
        IReadOnlyList<ConnectionEvent> events;
        bool changed;

        lock (_gate)
        {
            if (_paused)
            {
                return;
            }

            _paused = true;
            events = _tracker.Pause();
            changed = State != ConnectionState.Checking;
            State = ConnectionState.Checking;
            Latest = null;
        }

        Settle(events, _time.GetLocalNow());

        if (changed)
        {
            StateChanged?.Invoke(this, ConnectionState.Checking);
        }
    }

    private void Settle(IReadOnlyList<ConnectionEvent> events, DateTimeOffset now)
    {
        foreach (ConnectionEvent happened in events)
        {
            ConnectionEvent told = happened;

            switch (happened)
            {
                case OutageBegan began:
                    long opened = Log(() => _store.Begin(began.Outage));

                    lock (_gate)
                    {
                        _openId = opened;
                    }

                    _touched = now;
                    told = new OutageBegan(began.Outage with { Id = opened });
                    LogOutageBegan(began.Outage.Cause);
                    break;

                case OutageEnded ended:
                    long id;

                    lock (_gate)
                    {
                        id = _openId;
                        _openId = 0;
                    }

                    if (id != 0)
                    {
                        Log(() =>
                        {
                            _store.Finish(id, ended.Outage.End ?? now, ended.Outage.Interrupted);
                            return 0L;
                        });
                    }

                    told = new OutageEnded(ended.Outage with { Id = id });
                    LogOutageEnded(ended.Outage.Duration ?? TimeSpan.Zero, ended.Outage.Interrupted);
                    break;
            }

            Happened?.Invoke(this, told);
        }

        if (_openId != 0 && now - _touched >= TouchEvery)
        {
            long id = _openId;
            _touched = now;
            Log(() =>
            {
                _store.Touch(id, now);
                return 0L;
            });
        }
    }

    private void CloseUnfinished()
    {
        int closed = (int)Log(() => _store.CloseUnfinished());

        if (closed > 0)
        {
            LogClosedUnfinished(closed);
        }
    }

    private long Log(Func<long> write)
    {
        try
        {
            long result = write();
            _storeFailed = false;
            return result;
        }
        catch (Exception ex) when (HistoryFailure.IsStorage(ex))
        {
            if (!_storeFailed)
            {
                _storeFailed = true;
                LogStoreFailed(ex);
            }

            return 0;
        }
    }

    private ConnectionFacts ReadFacts()
    {
        try
        {
            return _facts.Read();
        }
#pragma warning disable CA1031 // A failed read is an unknown reading, not a reason to stop checking.
        catch (Exception ex)
#pragma warning restore CA1031
        {
            LogFactsFailed(ex);
            return ConnectionFacts.Unknown;
        }
    }

    private async Task<TimeSpan?> Ping(string address, CancellationToken cancellationToken)
    {
        try
        {
            return await _probe.PingAsync(address, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
#pragma warning disable CA1031 // An unanswerable ping is a missed answer.
        catch (Exception ex)
#pragma warning restore CA1031
        {
            LogPingFailed(ex);
            return null;
        }
    }

    public override void Dispose()
    {
        _facts.Changed -= OnFactsChanged;
        base.Dispose();
        _nudge.Dispose();
    }

    private sealed class Watcher(ConnectionMonitor monitor) : IDisposable
    {
        private ConnectionMonitor? _monitor = monitor;

        public void Dispose()
        {
            Interlocked.Exchange(ref _monitor, null)?.Release();
        }
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Internet connection lost ({Cause})")]
    private partial void LogOutageBegan(OutageCause cause);

    [LoggerMessage(Level = LogLevel.Information, Message = "Internet connection back after {Duration} (interrupted: {Interrupted})")]
    private partial void LogOutageEnded(TimeSpan duration, bool interrupted);

    [LoggerMessage(Level = LogLevel.Information, Message = "Closed {Count} outage(s) a previous run never saw end")]
    private partial void LogClosedUnfinished(int count);

    [LoggerMessage(Level = LogLevel.Warning, Message = "The outage log could not be written; outages will be shown but not kept")]
    private partial void LogStoreFailed(Exception error);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Reading the connection facts failed")]
    private partial void LogFactsFailed(Exception error);

    [LoggerMessage(Level = LogLevel.Debug, Message = "A connection ping failed")]
    private partial void LogPingFailed(Exception error);
}
