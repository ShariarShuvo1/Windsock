namespace Windsock.Core.Networking;

/// <summary>How much is known about the way to one address.</summary>
public enum RouteState
{
    Unknown,

    Walking,

    Found,

    Unreachable,
}

/// <summary>What is known about the way to one address.</summary>
public sealed record NetworkRoute(string Address, IReadOnlyList<PathHop> Hops, RouteState State);

/// <summary>
/// Keeps the routes to the addresses anything is currently interested in, and
/// decides when they are worth walking again.
/// </summary>
public sealed class NetworkRouteCache : IDisposable
{
    /// <summary>How many routes are walked at once by default.</summary>
    public const int DefaultBudget = 3;

    private static readonly TimeSpan DefaultFreshness = TimeSpan.FromMinutes(20);

    private static readonly TimeSpan DefaultPatience = TimeSpan.FromMinutes(5);

    private static readonly TimeSpan LongestPatience = TimeSpan.FromHours(2);

    private const int Remembered = 512;
    private readonly INetworkPathProbe _probe;
    private readonly TimeProvider _clock;
    private readonly Lock _gate = new();
    private readonly Dictionary<string, Entry> _entries = new(StringComparer.Ordinal);
    private readonly Dictionary<object, string[]> _asked = [];
    private readonly HashSet<string> _wanted = new(StringComparer.Ordinal);
    private readonly List<string> _order = [];
    private int _walking;
    private long _turn;
    private volatile bool _enabled = true;
    private bool _disposed;

    public NetworkRouteCache(INetworkPathProbe probe)
        : this(probe, TimeProvider.System)
    {
    }

    public NetworkRouteCache(INetworkPathProbe probe, TimeProvider clock)
    {
        ArgumentNullException.ThrowIfNull(probe);
        ArgumentNullException.ThrowIfNull(clock);

        _probe = probe;
        _clock = clock;
    }

    /// <summary>Raised when a route is learned, extended or replaced.</summary>
    public event EventHandler<NetworkRoute>? RouteChanged;

    /// <summary>How many routes may be walked at once.</summary>
    public int Budget { get; init; } = DefaultBudget;

    /// <summary>How long a route is believed before it is walked again.</summary>
    public TimeSpan Freshness { get; init; } = DefaultFreshness;

    /// <summary>How long an address that answered nothing is left alone.</summary>
    public TimeSpan Patience { get; init; } = DefaultPatience;

    /// <summary>
    /// Whether routes are walked at all.
    /// </summary>
    public bool IsEnabled
    {
        get => _enabled;
        set
        {
            if (_enabled == value)
            {
                return;
            }

            _enabled = value;

            if (value)
            {
                Pump();
            }
            else
            {
                Idle(everything: true);
            }
        }
    }

    /// <summary>How many routes are being walked at this moment.</summary>
    public int Walking
    {
        get
        {
            lock (_gate)
            {
                return _walking;
            }
        }
    }

    /// <summary>
    /// Says which addresses one asker currently cares about.
    /// </summary>
    public void Want(object asker, IReadOnlyList<string> addresses)
    {
        ArgumentNullException.ThrowIfNull(asker);
        ArgumentNullException.ThrowIfNull(addresses);

        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            List<string> kept = [];
            HashSet<string> once = new(StringComparer.Ordinal);

            foreach (string address in addresses)
            {
                if (PathTrace.Walkable(address) && once.Add(address))
                {
                    kept.Add(address);
                }
            }

            _asked[asker] = [.. kept];
            Rank();
        }

        Idle(everything: false);
        Pump();
    }

    /// <summary>Forgets what one asker wanted, when it has gone away.</summary>
    public void Release(object asker)
    {
        ArgumentNullException.ThrowIfNull(asker);

        lock (_gate)
        {
            if (_disposed || !_asked.Remove(asker))
            {
                return;
            }

            Rank();
        }

        Idle(everything: false);
        Pump();
    }

    /// <summary>
    /// What is known about the way to an address right now.
    /// </summary>
    public NetworkRoute? Route(string address)
    {
        if (string.IsNullOrWhiteSpace(address))
        {
            return null;
        }

        lock (_gate)
        {
            if (_disposed || !_entries.TryGetValue(address, out Entry? entry))
            {
                return null;
            }

            entry.Touched = ++_turn;
            return entry.Latest;
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            _asked.Clear();
            _wanted.Clear();
            _order.Clear();

            foreach (Entry entry in _entries.Values)
            {
                entry.Walk?.Cancel();
            }
        }
    }

    private void Rank()
    {
        Dictionary<string, int> best = new(StringComparer.Ordinal);

        foreach (string[] list in _asked.Values)
        {
            for (int place = 0; place < list.Length; place++)
            {
                if (!best.TryGetValue(list[place], out int had) || place < had)
                {
                    best[list[place]] = place;
                }
            }
        }

        _wanted.Clear();
        _order.Clear();

        foreach ((string address, _) in best.OrderBy(pair => pair.Value).ThenBy(pair => pair.Key, StringComparer.Ordinal))
        {
            _wanted.Add(address);
            _order.Add(address);

            if (_entries.TryGetValue(address, out Entry? entry))
            {
                entry.Touched = ++_turn;
            }
        }

        Forget();
    }

    private void Forget()
    {
        if (_entries.Count <= Remembered)
        {
            return;
        }

        List<KeyValuePair<string, Entry>> spare = [.. _entries.Where(pair =>
            !pair.Value.Walking && !_wanted.Contains(pair.Key))];

        spare.Sort((left, right) => left.Value.Touched.CompareTo(right.Value.Touched));

        int over = _entries.Count - Remembered;

        for (int index = 0; index < spare.Count && index < over; index++)
        {
            _entries.Remove(spare[index].Key);
        }
    }

    private void Idle(bool everything)
    {
        List<CancellationTokenSource> stopping = [];

        lock (_gate)
        {
            foreach ((string address, Entry entry) in _entries)
            {
                if (entry.Walk is { } walk && (everything || !_wanted.Contains(address)))
                {
                    stopping.Add(walk);
                }
            }
        }

        foreach (CancellationTokenSource walk in stopping)
        {
            walk.Cancel();
        }
    }

    private void Pump()
    {
        List<(string Address, CancellationTokenSource Walk)> starting = [];

        lock (_gate)
        {
            if (_disposed || !_enabled)
            {
                return;
            }

            foreach (string address in _order)
            {
                if (_walking >= Budget)
                {
                    break;
                }

                if (!_entries.TryGetValue(address, out Entry? entry))
                {
                    entry = new Entry { Latest = new NetworkRoute(address, [], RouteState.Unknown) };
                    _entries[address] = entry;
                }

                if (entry.Walking || !Stale(entry))
                {
                    continue;
                }

                CancellationTokenSource walk = new();

                entry.Walk = walk;
                entry.Walking = true;
                entry.Touched = ++_turn;
                _walking++;

                starting.Add((address, walk));
            }
        }

        foreach ((string address, CancellationTokenSource walk) in starting)
        {
            _ = WalkAsync(address, walk);
        }
    }

    private bool Stale(Entry entry) => entry.State switch
    {
        RouteState.Found => _clock.GetUtcNow() - entry.Settled >= Freshness,
        RouteState.Unreachable => _clock.GetUtcNow() - entry.Settled >= Backoff(entry.Misses),

        // Unknown, or left in Walking by a walk that was abandoned.
        _ => true,
    };

    private TimeSpan Backoff(int misses)
    {
        TimeSpan wait = Patience;

        for (int doubled = 1; doubled < misses && wait < LongestPatience; doubled++)
        {
            wait += wait;
        }

        return wait > LongestPatience ? LongestPatience : wait;
    }

    private async Task WalkAsync(string address, CancellationTokenSource walk)
    {
        bool first;

        lock (_gate)
        {
            first = !_entries.TryGetValue(address, out Entry? entry) || entry.Hops.Count == 0;
        }

        List<PathHop> found = [];

        try
        {
            await foreach (PathHop hop in _probe.TraceAsync(address, walk.Token).ConfigureAwait(false))
            {
                found.Add(hop);

                if (first)
                {
                    Announce(Progress(address, [.. found]));
                }
            }

            Announce(Settle(address, PathTrace.Trim(found)));
        }
        catch (OperationCanceledException)
        {
        }
        finally
        {
            Done(address, walk);
            walk.Dispose();
            Pump();
        }
    }

    private NetworkRoute? Progress(string address, IReadOnlyList<PathHop> hops)
    {
        lock (_gate)
        {
            if (_disposed || !_entries.TryGetValue(address, out Entry? entry))
            {
                return null;
            }

            entry.Hops = hops;
            entry.State = RouteState.Walking;
            entry.Latest = new NetworkRoute(address, hops, RouteState.Walking);
            return entry.Latest;
        }
    }

    private NetworkRoute? Settle(string address, IReadOnlyList<PathHop> hops)
    {
        lock (_gate)
        {
            if (_disposed || !_entries.TryGetValue(address, out Entry? entry))
            {
                return null;
            }

            bool learned = hops.Count > 0;

            entry.Hops = hops;
            entry.State = learned ? RouteState.Found : RouteState.Unreachable;
            entry.Misses = learned ? 0 : entry.Misses + 1;
            entry.Settled = _clock.GetUtcNow();
            entry.Latest = new NetworkRoute(address, hops, entry.State);
            return entry.Latest;
        }
    }

    private void Done(string address, CancellationTokenSource walk)
    {
        lock (_gate)
        {
            _walking = Math.Max(0, _walking - 1);

            if (!_entries.TryGetValue(address, out Entry? entry) || !ReferenceEquals(entry.Walk, walk))
            {
                return;
            }

            entry.Walk = null;
            entry.Walking = false;
            if (entry.State == RouteState.Walking)
            {
                entry.State = RouteState.Unknown;
                entry.Latest = new NetworkRoute(address, entry.Hops, RouteState.Unknown);
            }
        }
    }

    private void Announce(NetworkRoute? route)
    {
        if (route is null || _disposed)
        {
            return;
        }

        RouteChanged?.Invoke(this, route);
    }

    private sealed class Entry
    {
        public IReadOnlyList<PathHop> Hops { get; set; } = [];

        public RouteState State { get; set; } = RouteState.Unknown;

        /// <summary>What was last handed out, kept so it need not be remade.</summary>
        public NetworkRoute Latest { get; set; } = new(string.Empty, [], RouteState.Unknown);

        /// <summary>When the last finished walk finished.</summary>
        public DateTimeOffset Settled { get; set; }

        /// <summary>How many walks in a row have found nothing.</summary>
        public int Misses { get; set; }

        public bool Walking { get; set; }

        public CancellationTokenSource? Walk { get; set; }

        /// <summary>When anything last asked about this, for deciding what to drop.</summary>
        public long Touched { get; set; }
    }
}
