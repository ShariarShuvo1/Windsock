namespace Windsock.Core.Apps;

/// <summary>A network Windows would not give data usage for, and since when.</summary>
/// <param name="Name">The network's profile name, as Settings shows it.</param>
/// <param name="SinceMinute">The earliest minute a read went without it.</param>
public readonly record struct LeftOutNetwork(string Name, long SinceMinute);

/// <summary>
/// Remembers which networks' data usage Windows refused, so one that keeps
/// refusing is left out of a read rather than stopping every read, and is asked
/// again less and less often until it answers.
/// </summary>
/// <remarks>
/// <para>
/// Windows keeps data usage per connection profile - every network the machine
/// has joined. Failing a whole read because one profile refused kept figures
/// exact, but a profile that refused for good (a damaged entry for an old VPN or
/// hotspot) would have stopped per-app usage for good, in both editions. Leaving
/// it out loses nothing as long as somebody goes back for what it missed: the
/// collector keeps the earliest minute read without it and, once it answers,
/// reads from there again (see <see cref="AppUsageCollector"/>).
/// </para>
/// <para>
/// A refusing profile is asked again after a minute, then two, four and so on up
/// to fifteen, because a profile that does not answer can take the full patience
/// of a call each time it is asked.
/// </para>
/// </remarks>
public sealed class NetworkProfileRefusals
{
    private static readonly TimeSpan FirstWait = TimeSpan.FromMinutes(1);
    private static readonly TimeSpan LongestWait = TimeSpan.FromMinutes(15);

    private readonly TimeProvider _clock;
    private readonly Lock _gate = new();
    private readonly Dictionary<string, Entry> _entries = new(StringComparer.Ordinal);

    public NetworkProfileRefusals(TimeProvider clock)
    {
        ArgumentNullException.ThrowIfNull(clock);
        _clock = clock;
    }

    /// <summary>Every network being left out, whether or not it was asked this time.</summary>
    public IReadOnlyList<LeftOutNetwork> LeftOut
    {
        get
        {
            lock (_gate)
            {
                return [.. _entries.Select(pair => new LeftOutNetwork(pair.Key, pair.Value.SinceMinute)).OrderBy(network => network.Name, StringComparer.Ordinal)];
            }
        }
    }

    /// <summary>Whether a network should be asked now: always, unless it refused and its wait is not over.</summary>
    public bool ShouldAsk(string name)
    {
        lock (_gate)
        {
            return !_entries.TryGetValue(name, out Entry? entry) || _clock.GetUtcNow() >= entry.NextAsk;
        }
    }

    /// <summary>Notes a refusal of a read whose earliest window began at <paramref name="fromMinute"/>.</summary>
    public void Refused(string name, long fromMinute)
    {
        lock (_gate)
        {
            DateTimeOffset now = _clock.GetUtcNow();

            if (_entries.TryGetValue(name, out Entry? entry))
            {
                TimeSpan wait = entry.Wait * 2 > LongestWait ? LongestWait : entry.Wait * 2;
                _entries[name] = new Entry(Math.Min(entry.SinceMinute, fromMinute), now + wait, wait);
            }
            else
            {
                _entries[name] = new Entry(fromMinute, now + FirstWait, FirstWait);
            }
        }
    }

    /// <summary>Notes that a network answered everything it was asked.</summary>
    public void Answered(string name)
    {
        lock (_gate)
        {
            _entries.Remove(name);
        }
    }

    /// <summary>
    /// Forgets networks Windows no longer lists: their data usage went with
    /// them, so there is nothing left to go back for.
    /// </summary>
    public void KeepOnly(IEnumerable<string> listed)
    {
        ArgumentNullException.ThrowIfNull(listed);
        HashSet<string> present = new(listed, StringComparer.Ordinal);

        lock (_gate)
        {
            foreach (string name in _entries.Keys.Where(name => !present.Contains(name)).ToList())
            {
                _entries.Remove(name);
            }
        }
    }

    private sealed record Entry(long SinceMinute, DateTimeOffset NextAsk, TimeSpan Wait);
}
