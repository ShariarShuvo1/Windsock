using Windsock.Core.Processes;
using Windsock.Core.Security;

namespace Windsock.App.Services;

/// <summary>
/// Which processes are talking to an address on a blocklist the person
/// switched on, read from the connection tables every few seconds while a
/// page asks and a list is held.
/// </summary>
/// <remarks>
/// Nothing is read at all while no list is on: the tables are read only to
/// check them against a list, and with no list there is nothing to check.
/// </remarks>
public sealed class ListedConnections
{
    private static readonly TimeSpan Freshness = TimeSpan.FromSeconds(5);

    private readonly Blocklists _lists;
    private readonly IProcessConnectionDetailSource _connections;
    private readonly Lock _gate = new();
    private Dictionary<int, (BlocklistHit Hit, string Address)> _current = [];
    private long _readAt = long.MinValue;
    private int _reading;

    public ListedConnections(Blocklists lists, IProcessConnectionDetailSource connections)
    {
        ArgumentNullException.ThrowIfNull(lists);
        ArgumentNullException.ThrowIfNull(connections);

        _lists = lists;
        _connections = connections;
        _lists.Changed += (_, _) => Interlocked.Exchange(ref _readAt, long.MinValue);
    }

    /// <summary>Raised on a worker thread after a reading changed what is listed.</summary>
    public event EventHandler? Changed;

    /// <summary>Whether any list is held, so there is anything to mark.</summary>
    public bool IsChecking => _lists.Status().Any(each => each.Networks > 0);

    /// <summary>
    /// The process's first connection to a listed address, from the last
    /// reading; a fresh reading is started in the background when that is old.
    /// </summary>
    public (BlocklistHit Hit, string Address)? For(int processId)
    {
        Refresh();

        lock (_gate)
        {
            return _current.TryGetValue(processId, out var found) ? found : null;
        }
    }

    /// <summary>Reads the tables again in the background, when a list is held and the last reading is stale.</summary>
    public void Refresh()
    {
        long now = Environment.TickCount64;
        long last = Interlocked.Read(ref _readAt);

        // Compared only once there is a reading: now minus long.MinValue
        // overflows to a negative number, which would never be stale.
        if ((last != long.MinValue && now - last < Freshness.TotalMilliseconds)
            || Interlocked.CompareExchange(ref _reading, 1, 0) != 0)
        {
            return;
        }

        Interlocked.Exchange(ref _readAt, now);

        _ = Task.Run(() =>
        {
            try
            {
                Dictionary<int, (BlocklistHit, string)> found = [];

                if (IsChecking)
                {
                    foreach (ProcessConnection connection in _connections.ReadAll())
                    {
                        if (connection.RemoteAddress is { } address
                            && !found.ContainsKey(connection.ProcessId)
                            && _lists.Check(address) is { } hit)
                        {
                            found[connection.ProcessId] = (hit, address);
                        }
                    }
                }

                bool changed;

                lock (_gate)
                {
                    changed = found.Count != _current.Count || found.Keys.Any(id => !_current.ContainsKey(id));
                    _current = found;
                }

                if (changed)
                {
                    Changed?.Invoke(this, EventArgs.Empty);
                }
            }
            finally
            {
                Interlocked.Exchange(ref _reading, 0);
            }
        });
    }
}
