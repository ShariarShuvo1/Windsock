using System.Net;
using Microsoft.Extensions.Logging;

namespace Windsock.Core.Networking;

/// <summary>
/// The hop-by-hop table: WinMTR's per-hop loss and response time, built on the
/// route walks the map already makes. Full edition, and only while route
/// tracing is switched on and the table is open.
/// </summary>
/// <remarks>
/// <para>
/// The route comes from <see cref="NetworkRouteCache"/>, which this asks to
/// walk the target like any map place, so the map and the table agree on the
/// path. Each round then repeats the walk's own step - one ICMP echo per hop
/// limit, from <see cref="IHopProbe"/>, the same code the walk uses - for every
/// hop up to the far end, staggered 30 ms apart as WinMTR does, once a second.
/// Before the route is known, a round goes out as far as a walk would.
/// </para>
/// <para>
/// It sends to every router on the way and to the target, so it obeys the same
/// switch as route tracing, which the Store edition never turns on.
/// </para>
/// </remarks>
public sealed partial class HopWatch : IDisposable
{
    private static readonly TimeSpan Every = TimeSpan.FromSeconds(1);
    private static readonly TimeSpan Stagger = TimeSpan.FromMilliseconds(30);

    private readonly IHopProbe _probe;
    private readonly NetworkRouteCache _routes;
    private readonly TimeProvider _time;
    private readonly ILogger<HopWatch> _logger;
    private readonly Lock _gate = new();

    private CancellationTokenSource? _running;
    private HopTally _tally = new();
    private string? _target;
    private DateTimeOffset _since;

    public HopWatch(IHopProbe probe, NetworkRouteCache routes, TimeProvider time, ILogger<HopWatch> logger)
    {
        ArgumentNullException.ThrowIfNull(probe);
        ArgumentNullException.ThrowIfNull(routes);

        _probe = probe;
        _routes = routes;
        _time = time;
        _logger = logger;
    }

    /// <summary>Raised on a worker thread after each round.</summary>
    public event EventHandler<HopTableSnapshot>? Updated;

    /// <summary>
    /// Raised on a worker thread when the table stops by itself: route tracing
    /// was switched off somewhere else, or a round failed. Not raised by <see cref="Stop"/>.
    /// </summary>
    public event EventHandler? Ended;

    /// <summary>Whether the table can run: route tracing is on (never in the Store edition).</summary>
    public bool IsOffered => _routes.IsEnabled;

    /// <summary>Whether it is running.</summary>
    public bool IsRunning
    {
        get
        {
            lock (_gate)
            {
                return _running is not null;
            }
        }
    }

    /// <summary>The table as it stands, or nothing before it has started.</summary>
    public HopTableSnapshot? Snapshot
    {
        get
        {
            lock (_gate)
            {
                return _target is null ? null : new HopTableSnapshot(_target, _since, _tally.Rounds, _tally.Rows());
            }
        }
    }

    /// <summary>Starts a table towards an address, replacing any running one. False when tracing is off or the address is not one.</summary>
    public bool Start(string target)
    {
        if (!IsOffered || !PathTrace.Walkable(target) || !IPAddress.TryParse(target, out IPAddress? address))
        {
            return false;
        }

        Stop();

        CancellationTokenSource running = new();

        lock (_gate)
        {
            _running = running;
            _tally = new HopTally();
            _target = target;
            _since = _time.GetLocalNow();
        }

        _routes.Want(this, [target]);
        _ = RunAsync(address, running);
        return true;
    }

    /// <summary>
    /// Starts a table towards an address or a host name - the connection
    /// checks' internet target may be either - looking the name up first
    /// through the DNS server this PC already uses.
    /// </summary>
    /// <param name="target">The address or name.</param>
    /// <param name="cancellationToken">
    /// Cancelled when whoever asked has gone: a table is never started after
    /// that, however long the name took to look up.
    /// </param>
    public async Task<bool> StartAsync(string target, CancellationToken cancellationToken = default)
    {
        if (cancellationToken.IsCancellationRequested)
        {
            return false;
        }

        if (!IsOffered || string.IsNullOrWhiteSpace(target))
        {
            return false;
        }

        string trimmed = target.Trim();

        if (IPAddress.TryParse(trimmed, out _))
        {
            return Start(trimmed);
        }

        if (!Settings.ConnectionSettings.IsTarget(trimmed))
        {
            return false;
        }

        IPAddress[] found;

        try
        {
            found = await Dns.GetHostAddressesAsync(trimmed, cancellationToken).ConfigureAwait(false);
        }
        catch (System.Net.Sockets.SocketException)
        {
            return false;
        }
        catch (OperationCanceledException)
        {
            return false;
        }

        // The page may have gone while the name was looked up.
        return !cancellationToken.IsCancellationRequested && found.Length > 0 && Start(found[0].ToString());
    }

    /// <summary>Stops the table; its figures stay readable until the next start.</summary>
    public void Stop()
    {
        CancellationTokenSource? running;

        lock (_gate)
        {
            running = _running;
            _running = null;
        }

        if (running is not null)
        {
            running.Cancel();
            running.Dispose();
            _routes.Release(this);
        }
    }

    private async Task RunAsync(IPAddress target, CancellationTokenSource running)
    {
        CancellationToken cancellationToken = running.Token;
        bool ended = false;

        try
        {
            while (!cancellationToken.IsCancellationRequested && _routes.IsEnabled)
            {
                long started = _time.GetTimestamp();
                await RoundAsync(target, cancellationToken).ConfigureAwait(false);

                TimeSpan rest = Every - _time.GetElapsedTime(started);

                if (rest > TimeSpan.Zero)
                {
                    await Task.Delay(rest, _time, cancellationToken).ConfigureAwait(false);
                }
            }
        }
        catch (OperationCanceledException)
        {
        }
#pragma warning disable CA1031 // A failed round ends the table; the page shows what it had.
        catch (Exception ex)
#pragma warning restore CA1031
        {
            LogFailed(ex);
        }
        finally
        {
            // Ended without Stop - tracing switched off elsewhere, or a failure:
            // let go of the run and of the route, as Stop would have.
            lock (_gate)
            {
                if (ReferenceEquals(_running, running))
                {
                    _running = null;
                    ended = true;
                }
            }

            if (ended)
            {
                running.Dispose();
                _routes.Release(this);
            }
        }

        if (ended)
        {
            Ended?.Invoke(this, EventArgs.Empty);
        }
    }

    private async Task RoundAsync(IPAddress target, CancellationToken cancellationToken)
    {
        int furthest;

        lock (_gate)
        {
            furthest = _tally.Furthest(PathTrace.MaximumHops);
        }

        // A route the cache already walked tells how far out the far end is.
        if (_routes.Route(target.ToString()) is { State: RouteState.Found, Hops.Count: > 0 } known)
        {
            furthest = Math.Max(furthest, Math.Min(known.Hops[^1].Distance, PathTrace.MaximumHops));
        }

        List<Task<HopReply?>> probes = new(furthest);

        for (int distance = 1; distance <= furthest; distance++)
        {
            probes.Add(_probe.ProbeAsync(target, distance, cancellationToken));
            await Task.Delay(Stagger, _time, cancellationToken).ConfigureAwait(false);
        }

        HopReply?[] replies = await Task.WhenAll(probes).ConfigureAwait(false);
        HopTableSnapshot snapshot;

        lock (_gate)
        {
            _tally.Add(_time.GetLocalNow(), [.. replies.Where(each => each is not null).Select(each => each!.Value)]);
            snapshot = new HopTableSnapshot(_target ?? target.ToString(), _since, _tally.Rounds, _tally.Rows());
        }

        Updated?.Invoke(this, snapshot);
    }

    public void Dispose() => Stop();

    [LoggerMessage(Level = LogLevel.Warning, Message = "The hop-by-hop table stopped")]
    private partial void LogFailed(Exception error);
}
