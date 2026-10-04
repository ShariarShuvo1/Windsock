using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;

namespace Windsock.Core.Processes;

/// <summary>
/// Puts names to remote addresses, in the background and only once each.
/// </summary>
/// <remarks>
/// <para>
/// The DNS client's cache is asked first. It holds the names programs on this
/// machine actually looked up - <c>api.example.com</c> rather than the hosting
/// provider's name for the machine behind it - and reading it sends nothing
/// anywhere.
/// </para>
/// <para>
/// Where the cache has no answer, a reverse lookup goes to the resolver the
/// machine already uses, but only when <c>askResolver</c> allows it. The Store
/// edition does not: its privacy policy promises that the only packets it sends
/// are its connection checks, so there an address the cache cannot name stays
/// an address until some program looks it up.
/// </para>
/// </remarks>
public sealed class HostNameCache
{
    private const int Capacity = 4096;

    private static readonly TimeSpan Patience = TimeSpan.FromSeconds(3);
    private static readonly TimeSpan CacheFreshness = TimeSpan.FromSeconds(10);

    private readonly IDnsCacheSource? _cache;
    private readonly bool _askResolver;
    private readonly ConcurrentDictionary<string, string?> _names = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, byte> _asking = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, byte> _waiting = new(StringComparer.Ordinal);

    private volatile IReadOnlyDictionary<string, string> _cached = new Dictionary<string, string>();
    private long _cachedAt = long.MinValue;
    private int _reading;

    /// <summary>Names addresses from the DNS cache, and by reverse lookup where that is allowed.</summary>
    public HostNameCache(IDnsCacheSource? cache, bool askResolver)
    {
        _cache = cache;
        _askResolver = askResolver;
    }

    /// <summary>Raised when a name arrives, so a view can redraw.</summary>
    public event EventHandler? Resolved;

    /// <summary>
    /// The name for an address if it is known, otherwise null and a lookup
    /// starts in the background.
    /// </summary>
    public string? Find(string? address)
    {
        if (string.IsNullOrEmpty(address))
        {
            return null;
        }

        if (_names.TryGetValue(address, out string? known) && known is not null)
        {
            return known;
        }

        if (!Worth(address))
        {
            return null;
        }

        if (_cache is not null)
        {
            if (_cached.TryGetValue(address, out string? cached))
            {
                // Remembered while there is room; the reading answers again anyway.
                if (_names.Count < Capacity)
                {
                    _names[address] = cached;
                }

                _waiting.TryRemove(address, out _);
                return cached;
            }

            // Listed before the reading starts, so a reading that finishes at
            // once still finds it waiting and names it.
            if (_waiting.Count < Capacity)
            {
                _waiting.TryAdd(address, 0);
            }

            Refresh();
        }

        // A lookup that came back empty stays empty; the cache above can still
        // name it later, when some program looks the address up.
        if (_names.ContainsKey(address) || _names.Count >= Capacity || !_askResolver)
        {
            return null;
        }

        // TryAdd is the whole guard: whoever wins starts the one lookup.
        if (_asking.TryAdd(address, 0))
        {
            _ = Ask(address);
        }

        return null;
    }

    private static bool Worth(string address) =>
        IPAddress.TryParse(address, out IPAddress? parsed)
        && !LoopbackEnds.IsLoopback(parsed)
        && !parsed.Equals(IPAddress.Any)
        && !parsed.Equals(IPAddress.IPv6Any);

    /// <summary>
    /// Reads the DNS cache again in the background when the last reading is
    /// stale. One reading at a time, and not more often than every few seconds.
    /// </summary>
    private void Refresh()
    {
        long now = Environment.TickCount64;
        long last = Interlocked.Read(ref _cachedAt);

        if ((last != long.MinValue && now - last < CacheFreshness.TotalMilliseconds)
            || Interlocked.CompareExchange(ref _reading, 1, 0) != 0)
        {
            return;
        }

        Interlocked.Exchange(ref _cachedAt, now);

        _ = Task.Run(() =>
        {
            try
            {
                _cached = DnsCacheNames.Index(_cache!.Read());
                Announce();
            }
            finally
            {
                Interlocked.Exchange(ref _reading, 0);
            }
        });
    }

    /// <summary>Names whatever was waiting that the fresh reading can name.</summary>
    private void Announce()
    {
        bool named = false;

        foreach (string address in _waiting.Keys)
        {
            if (_cached.TryGetValue(address, out string? name))
            {
                if (_names.Count < Capacity)
                {
                    _names[address] = name;
                }

                _waiting.TryRemove(address, out _);
                named = true;
            }
        }

        foreach ((string address, string? name) in _names)
        {
            if (name is null && _cached.TryGetValue(address, out string? found))
            {
                _names[address] = found;
                named = true;
            }
        }

        if (named)
        {
            Resolved?.Invoke(this, EventArgs.Empty);
        }
    }

    private static void Watch(Task task) =>
        _ = task.ContinueWith(
            static done => _ = done.Exception,
            CancellationToken.None,
            TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);

    private async Task Ask(string address)
    {
        string? name = null;

        try
        {
            Task<IPHostEntry> lookup = Dns.GetHostEntryAsync(address);
            Watch(lookup);

            IPHostEntry entry = await lookup.WaitAsync(Patience).ConfigureAwait(false);

            // A resolver that just echoes the address back has told us nothing.
            if (!string.Equals(entry.HostName, address, StringComparison.OrdinalIgnoreCase))
            {
                name = entry.HostName;
            }
        }
        catch (Exception ex) when (ex is SocketException or TimeoutException or ArgumentException)
        {
        }

        // The cache may have named it while the resolver was being asked.
        if (name is not null || !_names.TryGetValue(address, out string? meanwhile) || meanwhile is null)
        {
            _names[address] = name;
        }

        _asking.TryRemove(address, out _);

        if (name is not null)
        {
            Resolved?.Invoke(this, EventArgs.Empty);
        }
    }
}
