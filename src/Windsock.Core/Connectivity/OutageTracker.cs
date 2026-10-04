namespace Windsock.Core.Connectivity;

/// <summary>Whether the internet is reachable, as last settled.</summary>
public enum ConnectionStatus
{
    /// <summary>Not settled yet: just started, just woke, or not being watched.</summary>
    Checking,

    /// <summary>Reachable.</summary>
    Online,

    /// <summary>Gone for long enough to count.</summary>
    Offline,
}

/// <summary>The settled state of the connection.</summary>
/// <param name="Status">Online, offline, or not yet known.</param>
/// <param name="Cause">Why it is offline; nothing otherwise.</param>
/// <param name="Since">When the current status began, when known.</param>
public readonly record struct ConnectionState(ConnectionStatus Status, OutageCause? Cause, DateTimeOffset? Since)
{
    /// <summary>The state before anything has been checked.</summary>
    public static ConnectionState Checking { get; } = new(ConnectionStatus.Checking, null, null);

    /// <summary>Whether this is a settled outage.</summary>
    public bool IsOffline => Status == ConnectionStatus.Offline;
}

/// <summary>One spell without the internet.</summary>
/// <param name="Start">When the internet was first found missing.</param>
/// <param name="End">When it was first found back; nothing while it is still out.</param>
/// <param name="Cause">The likeliest cause when it began.</param>
/// <param name="Network">The network it happened on, as a person would name it.</param>
public sealed record Outage(DateTimeOffset Start, DateTimeOffset? End, OutageCause Cause, string Network)
{
    /// <summary>The row the outage log keeps it under, once it has one.</summary>
    public long Id { get; init; }

    /// <summary>
    /// Whether Windsock stopped watching before the internet came back - the PC
    /// slept, or Windsock closed - so the end is only when watching stopped.
    /// </summary>
    public bool Interrupted { get; init; }

    /// <summary>How long it lasted, once it is over.</summary>
    public TimeSpan? Duration => End - Start;
}

/// <summary>Something that happened to the connection.</summary>
public abstract record ConnectionEvent;

/// <summary>An outage was called.</summary>
public sealed record OutageBegan(Outage Outage) : ConnectionEvent;

/// <summary>An outage is over.</summary>
public sealed record OutageEnded(Outage Outage) : ConnectionEvent;

/// <summary>The PC moved to a different network.</summary>
public sealed record NetworkChanged(NetworkIdentity? From, NetworkIdentity To) : ConnectionEvent;

/// <summary>A VPN went on or off.</summary>
public sealed record VpnChanged(bool Active) : ConnectionEvent;

/// <summary>The timings the tracker works to, which may change between checks.</summary>
/// <param name="OutageAfter">How long the internet must be missing before an outage is called.</param>
/// <param name="GapLimit">A longer silence between checks than this means Windsock was not watching.</param>
/// <param name="Grace">How long after starting, or after such a silence, before outages are called.</param>
public readonly record struct TrackerRules(TimeSpan OutageAfter, TimeSpan GapLimit, TimeSpan Grace)
{
    /// <summary>The rules for a given check interval and outage delay.</summary>
    public static TrackerRules For(TimeSpan checkEvery, TimeSpan outageAfter) => new(
        outageAfter,
        TimeSpan.FromTicks(Math.Max(checkEvery.Ticks * 3, TimeSpan.FromSeconds(30).Ticks)),
        TimeSpan.FromSeconds(45));
}

/// <summary>
/// Turns a stream of checks into settled outages, network changes and VPN changes.
/// </summary>
/// <remarks>
/// <para>
/// An outage is called only after the internet has been missing for
/// <see cref="TrackerRules.OutageAfter"/>, and it starts from the first check that
/// found it missing, not from when it was called. It ends at the first check that
/// found it back, but only once a second check agrees, so a single lucky answer
/// in the middle of a flapping connection does not split one outage into two.
/// </para>
/// <para>
/// A long silence between checks means the PC slept or nobody was watching. An
/// outage open across one is closed where watching stopped and marked
/// interrupted, because what happened in between is not known. Nothing is called
/// for a short grace afterwards, which is the few seconds Wi-Fi takes to come back
/// after waking and is not an outage anybody wants to hear about.
/// </para>
/// </remarks>
public sealed class OutageTracker
{
    private readonly HashSet<string> _answering = new(StringComparer.Ordinal);

    private DateTimeOffset? _last;
    private DateTimeOffset _graceUntil;
    private DateTimeOffset? _badSince;
    private OutageCause _badCause;
    private DateTimeOffset? _goodSince;
    private int _goodCount;
    private Outage? _open;
    private NetworkIdentity? _network;
    private bool? _vpn;

    /// <summary>The settled state.</summary>
    public ConnectionState State { get; private set; } = ConnectionState.Checking;

    /// <summary>The outage in progress, if there is one.</summary>
    public Outage? Current => _open;

    /// <summary>Whether the router on this check's network has ever answered.</summary>
    public bool RouterAnswers(string? gateway) => gateway is not null && _answering.Contains(gateway);

    /// <summary>
    /// Takes one check and returns whatever it settled. Checks must arrive in
    /// time order.
    /// </summary>
    public IReadOnlyList<ConnectionEvent> Observe(in ConnectionSample sample, TrackerRules rules)
    {
        List<ConnectionEvent> events = [];
        DateTimeOffset at = sample.At;

        if (_last is not { } last)
        {
            _graceUntil = at + rules.Grace;
        }
        else if (at - last > rules.GapLimit)
        {
            Interrupt(last, events);
            _graceUntil = at + rules.Grace;
        }

        ConnectionFacts facts = sample.Facts;

        if (sample.Router is not null && facts.Gateway is { } gateway)
        {
            _answering.Add(gateway);
        }

        Follow(facts, events, first: _last is null);

        OutageCause? cause = ConnectionJudge.Judge(sample, RouterAnswers(facts.Gateway));

        if (cause is null)
        {
            Good(at, events);
        }
        else
        {
            Bad(at, cause.Value, rules, events);
        }

        _last = at;
        return events;
    }

    /// <summary>
    /// Closes an open outage because watching stopped, and forgets anything not
    /// yet settled. Called when nobody is watching any more.
    /// </summary>
    public IReadOnlyList<ConnectionEvent> Pause()
    {
        List<ConnectionEvent> events = [];

        if (_last is { } last)
        {
            Interrupt(last, events);
        }

        _last = null;
        return events;
    }

    private void Interrupt(DateTimeOffset last, List<ConnectionEvent> events)
    {
        if (_open is { } open)
        {
            Outage ended = open with { End = last, Interrupted = true };
            _open = null;
            events.Add(new OutageEnded(ended));
        }

        _badSince = null;
        _goodSince = null;
        _goodCount = 0;
        State = ConnectionState.Checking;
    }

    private void Follow(ConnectionFacts facts, List<ConnectionEvent> events, bool first)
    {
        if (facts.Network is { } network)
        {
            if (!first && _network is { } previous && previous != network)
            {
                events.Add(new NetworkChanged(previous, network));
            }

            _network = network;
        }

        if (facts.Reach == WindowsReach.Unknown && !facts.HasNetwork)
        {
            return;
        }

        if (_vpn is { } wasOn && wasOn != facts.VpnActive && !first)
        {
            events.Add(new VpnChanged(facts.VpnActive));
        }

        _vpn = facts.VpnActive;
    }

    private void Good(DateTimeOffset at, List<ConnectionEvent> events)
    {
        _badSince = null;

        if (_open is { } open)
        {
            _goodSince ??= at;
            _goodCount++;

            if (_goodCount < 2)
            {
                return;
            }

            DateTimeOffset back = _goodSince.Value;
            Outage ended = open with { End = back };

            _open = null;
            _goodSince = null;
            _goodCount = 0;
            events.Add(new OutageEnded(ended));
            State = new ConnectionState(ConnectionStatus.Online, null, back);
            return;
        }

        if (State.Status != ConnectionStatus.Online)
        {
            State = new ConnectionState(ConnectionStatus.Online, null, at);
        }
    }

    private void Bad(DateTimeOffset at, OutageCause cause, TrackerRules rules, List<ConnectionEvent> events)
    {
        _goodSince = null;
        _goodCount = 0;

        if (_open is not null)
        {
            return;
        }

        if (_badSince is null)
        {
            _badSince = at;
            _badCause = cause;
        }

        // The most specific cause seen wins over "unknown".
        if (_badCause == OutageCause.Unknown)
        {
            _badCause = cause;
        }

        if (at < _graceUntil || at - _badSince.Value < rules.OutageAfter)
        {
            return;
        }

        Outage began = new(_badSince.Value, null, _badCause, _network?.Name ?? string.Empty);

        _open = began;
        State = new ConnectionState(ConnectionStatus.Offline, _badCause, began.Start);
        events.Add(new OutageBegan(began));
    }
}
