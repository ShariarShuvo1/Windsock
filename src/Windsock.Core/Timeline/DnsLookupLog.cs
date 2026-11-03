using System.Globalization;
using System.Net;

namespace Windsock.Core.Timeline;

/// <summary>What one DNS answer held, as the DNS client writes it in its events.</summary>
/// <param name="Addresses">The addresses the name led to.</param>
/// <param name="Aliases">The names it led through on the way (CNAMEs).</param>
public sealed record DnsAnswer(IReadOnlyList<IPAddress> Addresses, IReadOnlyList<string> Aliases)
{
    /// <summary>An answer with nothing in it.</summary>
    public static DnsAnswer Empty { get; } = new([], []);

    /// <summary>
    /// Reads the DNS client's <c>QueryResults</c> field: entries separated by
    /// semicolons, an address as itself (IPv4 written IPv4-mapped,
    /// <c>::ffff:93.184.216.34</c>), another record as <c>type: 5 target</c>.
    /// </summary>
    /// <remarks>
    /// The format is the provider's, not documented; read from its manifest and
    /// published captures, and read leniently: what is not understood is left out.
    /// </remarks>
    public static DnsAnswer Parse(string? results)
    {
        if (string.IsNullOrWhiteSpace(results))
        {
            return Empty;
        }

        List<IPAddress> addresses = [];
        List<string> aliases = [];

        foreach (string raw in results.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (raw.StartsWith("type:", StringComparison.OrdinalIgnoreCase))
            {
                string[] parts = raw[5..].Split(' ', StringSplitOptions.RemoveEmptyEntries);

                if (parts.Length >= 2 && parts[0] == "5")
                {
                    aliases.Add(parts[1].TrimEnd('.'));
                }

                continue;
            }

            if (IPAddress.TryParse(raw, out IPAddress? address))
            {
                addresses.Add(address.IsIPv4MappedToIPv6 ? address.MapToIPv4() : address);
            }
        }

        return new DnsAnswer(addresses, aliases);
    }
}

/// <summary>One name a process looked up, and how it went.</summary>
/// <param name="Name">The name asked for.</param>
/// <param name="QueryType">The record type: 1 A, 28 AAAA, 5 CNAME, 12 PTR, 65 HTTPS...</param>
/// <param name="Count">How many times it was asked.</param>
/// <param name="First">When it was first asked.</param>
/// <param name="Last">When it was last asked.</param>
/// <param name="Status">The last answer's status: 0 found, 9003 no such name, 9501 no records; null while unanswered.</param>
/// <param name="Addresses">The addresses the last answer gave.</param>
public sealed record DnsLookup(
    string Name,
    int QueryType,
    int Count,
    DateTimeOffset First,
    DateTimeOffset Last,
    int? Status,
    IReadOnlyList<IPAddress> Addresses)
{
    /// <summary>The record type in letters.</summary>
    public string TypeText => QueryType switch
    {
        1 => "A",
        28 => "AAAA",
        5 => "CNAME",
        12 => "PTR",
        15 => "MX",
        16 => "TXT",
        33 => "SRV",
        65 => "HTTPS",
        _ => QueryType.ToString(CultureInfo.InvariantCulture),
    };

    /// <summary>The status in words.</summary>
    public string StatusText => Status switch
    {
        null => "No answer seen",
        0 => "Found",
        9003 => "No such name",
        9501 => "No records of that type",
        1460 => "Timed out",
        { } other => string.Concat("Status ", other.ToString(CultureInfo.InvariantCulture)),
    };
}

/// <summary>
/// The names each process looked up, kept in memory only, from the DNS
/// client's events.
/// </summary>
/// <remarks>
/// <para>
/// Names looked up are browsing history, so nothing here is written to disk
/// or sent anywhere, and it is gone when Windsock closes. Each name is kept
/// for two hours after it was last asked or answered, whatever else the
/// process asks; at most 512 names a process (the oldest make room for the
/// newest) and 512 processes.
/// </para>
/// <para>
/// Each address an answer gave is remembered against the process that asked,
/// which is a better name for that process's connection than the DNS cache's,
/// shared by every program.
/// </para>
/// </remarks>
public sealed class DnsLookupLog
{
    private const int NamesPerProcess = 512;
    private const int AddressesPerProcess = NamesPerProcess * 4;
    private const int Processes = 512;
    private static readonly TimeSpan Kept = TimeSpan.FromHours(2);
    private static readonly TimeSpan PruneEvery = TimeSpan.FromMinutes(1);

    private readonly TimeProvider _clock;
    private readonly Lock _gate = new();
    private readonly Dictionary<int, ProcessNames> _byProcess = [];
    private DateTimeOffset _prunedAt = DateTimeOffset.MinValue;

    public DnsLookupLog()
        : this(TimeProvider.System)
    {
    }

    internal DnsLookupLog(TimeProvider clock)
    {
        ArgumentNullException.ThrowIfNull(clock);
        _clock = clock;
    }

    /// <summary>Raised when a lookup was noted, on whichever thread noted it.</summary>
    public event EventHandler? Changed;

    /// <summary>Notes that a process asked for a name.</summary>
    public void Asked(int processId, string name, int queryType)
    {
        Note(processId, name, queryType, status: null, DnsAnswer.Empty, asked: true);
    }

    /// <summary>Notes the answer a process got.</summary>
    public void Answered(int processId, string name, int queryType, int status, DnsAnswer answer)
    {
        ArgumentNullException.ThrowIfNull(answer);
        Note(processId, name, queryType, status, answer, asked: false);
    }

    /// <summary>The names a process looked up, the latest first.</summary>
    public IReadOnlyList<DnsLookup> For(int processId)
    {
        DateTimeOffset now = _clock.GetUtcNow();

        lock (_gate)
        {
            return _byProcess.TryGetValue(processId, out ProcessNames? names)
                ? [.. names.Lookups.Values.Where(each => IsKept(each.Last, now)).OrderByDescending(static each => each.Last)]
                : [];
        }
    }

    /// <summary>The names every process in a set looked up, together, the latest first: an app's.</summary>
    public IReadOnlyList<(int ProcessId, DnsLookup Lookup)> For(IEnumerable<int> processIds)
    {
        ArgumentNullException.ThrowIfNull(processIds);

        List<(int, DnsLookup)> all = [];
        DateTimeOffset now = _clock.GetUtcNow();

        lock (_gate)
        {
            foreach (int processId in processIds.Distinct())
            {
                if (_byProcess.TryGetValue(processId, out ProcessNames? names))
                {
                    all.AddRange(names.Lookups.Values.Where(each => IsKept(each.Last, now)).Select(each => (processId, each)));
                }
            }
        }

        return [.. all.OrderByDescending(static each => each.Item2.Last)];
    }

    /// <summary>Every process's names together, the latest first, at most <paramref name="limit"/>.</summary>
    public IReadOnlyList<(int ProcessId, DnsLookup Lookup)> All(int limit = 2000)
    {
        List<(int, DnsLookup)> all = [];
        DateTimeOffset now = _clock.GetUtcNow();

        lock (_gate)
        {
            foreach ((int processId, ProcessNames names) in _byProcess)
            {
                all.AddRange(names.Lookups.Values.Where(each => IsKept(each.Last, now)).Select(each => (processId, each)));
            }
        }

        return [.. all.OrderByDescending(static each => each.Item2.Last).Take(Math.Max(0, limit))];
    }

    /// <summary>The name a process reached an address by, from its own lookups, or null.</summary>
    public string? NameFor(int processId, string? address)
    {
        if (address is null || !IPAddress.TryParse(address, out IPAddress? parsed))
        {
            return null;
        }

        if (parsed.IsIPv4MappedToIPv6)
        {
            parsed = parsed.MapToIPv4();
        }

        DateTimeOffset now = _clock.GetUtcNow();

        lock (_gate)
        {
            return _byProcess.TryGetValue(processId, out ProcessNames? names)
                && names.ByAddress.TryGetValue(parsed, out (string Name, DateTimeOffset Seen) known)
                && IsKept(known.Seen, now)
                    ? known.Name
                    : null;
        }
    }

    /// <summary>Forgets everything noted.</summary>
    public void Clear()
    {
        lock (_gate)
        {
            _byProcess.Clear();
        }

        Changed?.Invoke(this, EventArgs.Empty);
    }

    private void Note(int processId, string name, int queryType, int? status, DnsAnswer answer, bool asked)
    {
        if (processId <= 0 || string.IsNullOrWhiteSpace(name))
        {
            return;
        }

        string trimmed = name.Trim().TrimEnd('.');
        DateTimeOffset now = _clock.GetUtcNow();

        lock (_gate)
        {
            if (now - _prunedAt >= PruneEvery || now < _prunedAt)
            {
                _prunedAt = now;
                Prune(now);
            }

            if (!_byProcess.TryGetValue(processId, out ProcessNames? names))
            {
                if (_byProcess.Count >= Processes)
                {
                    int oldest = _byProcess.MinBy(static each => each.Value.Last).Key;
                    _byProcess.Remove(oldest);
                }

                names = new ProcessNames();
                _byProcess[processId] = names;
            }

            names.Last = now;
            string key = string.Concat(trimmed.ToLowerInvariant(), "|", queryType.ToString(CultureInfo.InvariantCulture));

            if (names.Lookups.TryGetValue(key, out DnsLookup? known))
            {
                names.Lookups[key] = known with
                {
                    Count = asked ? known.Count + 1 : known.Count,
                    Last = now,
                    Status = status ?? known.Status,
                    Addresses = answer.Addresses.Count > 0 ? answer.Addresses : known.Addresses,
                };
            }
            else
            {
                // Full, the oldest name makes room: the newest are the ones
                // somebody looking is likeliest to want.
                if (names.Lookups.Count >= NamesPerProcess)
                {
                    names.Lookups.Remove(names.Lookups.MinBy(static each => each.Value.Last).Key);
                }

                names.Lookups[key] = new DnsLookup(trimmed, queryType, asked ? 1 : 0, now, now, status, answer.Addresses);
            }

            foreach (IPAddress address in answer.Addresses)
            {
                if (names.ByAddress.Count >= AddressesPerProcess && !names.ByAddress.ContainsKey(address))
                {
                    names.ByAddress.Remove(names.ByAddress.MinBy(static each => each.Value.Seen).Key);
                }

                names.ByAddress[address] = (trimmed, now);
            }
        }

        Changed?.Invoke(this, EventArgs.Empty);
    }

    private static bool IsKept(DateTimeOffset seen, DateTimeOffset now) => now - seen <= Kept;

    /// <summary>Forgets each name, each address and each process past its own two hours. Call with the gate held.</summary>
    private void Prune(DateTimeOffset now)
    {
        foreach (int stale in _byProcess.Where(each => !IsKept(each.Value.Last, now)).Select(static each => each.Key).ToList())
        {
            _byProcess.Remove(stale);
        }

        foreach (ProcessNames names in _byProcess.Values)
        {
            foreach (string old in names.Lookups.Where(each => !IsKept(each.Value.Last, now)).Select(static each => each.Key).ToList())
            {
                names.Lookups.Remove(old);
            }

            foreach (IPAddress old in names.ByAddress.Where(each => !IsKept(each.Value.Seen, now)).Select(static each => each.Key).ToList())
            {
                names.ByAddress.Remove(old);
            }
        }
    }

    private sealed class ProcessNames
    {
        public Dictionary<string, DnsLookup> Lookups { get; } = new(StringComparer.Ordinal);

        public Dictionary<IPAddress, (string Name, DateTimeOffset Seen)> ByAddress { get; } = [];

        public DateTimeOffset Last { get; set; }
    }
}
