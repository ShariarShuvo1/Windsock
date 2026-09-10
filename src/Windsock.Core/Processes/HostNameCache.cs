using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;

namespace Windsock.Core.Processes;

/// <summary>
/// Puts names to remote addresses, in the background and only once each.
/// </summary>
public sealed class HostNameCache
{
    private const int Capacity = 4096;

    private static readonly TimeSpan Patience = TimeSpan.FromSeconds(3);

    private readonly ConcurrentDictionary<string, string?> _names = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, byte> _asking = new(StringComparer.Ordinal);

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

        if (_names.TryGetValue(address, out string? known))
        {
            return known;
        }

        if (_names.Count >= Capacity || !Worth(address))
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
        && !IPAddress.IsLoopback(parsed)
        && !parsed.Equals(IPAddress.Any)
        && !parsed.Equals(IPAddress.IPv6Any);

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

        _names[address] = name;
        _asking.TryRemove(address, out _);

        if (name is not null)
        {
            Resolved?.Invoke(this, EventArgs.Empty);
        }
    }
}
