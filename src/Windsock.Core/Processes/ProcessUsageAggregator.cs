namespace Windsock.Core.Processes;

/// <summary>
/// Turns cumulative per-process counters into rates and running totals, and
/// keeps track of which processes are still worth listing.
/// </summary>
public sealed class ProcessUsageAggregator(
    IProcessTrafficSource traffic,
    IProcessConnectionSource connections,
    IProcessIdentityResolver identities)
{
    private readonly Dictionary<int, Entry> _entries = [];
    private readonly Dictionary<int, ProcessTraffic> _traffic = [];
    private readonly Dictionary<int, SocketCounts> _connections = [];
    private readonly List<int> _expired = [];
    private bool _rebase;

    /// <summary>
    /// How long a process with no traffic and no sockets stays in the table.
    /// </summary>
    public TimeSpan Retention { get; init; } = TimeSpan.FromSeconds(20);

    /// <summary>
    /// Takes the next reading as a fresh starting point rather than as a rate.
    /// </summary>
    public void Rebase() => _rebase = true;

    /// <summary>Reads both sources and returns the current table contents.</summary>
    public IReadOnlyList<ProcessUsage> Update(double elapsedSeconds, DateTimeOffset now)
    {
        traffic.CopyTo(_traffic);
        connections.CopyTo(_connections);

        foreach (Entry entry in _entries.Values)
        {
            entry.Seen = false;
            entry.Sockets = default;
        }

        bool rebasing = _rebase;
        _rebase = false;

        foreach ((int processId, ProcessTraffic totals) in _traffic)
        {
            Entry entry = Track(processId, now);
            Accumulate(entry, totals, elapsedSeconds, now, rebasing);
        }

        foreach ((int processId, SocketCounts sockets) in _connections)
        {
            Entry entry = Track(processId, now);
            entry.Sockets = sockets;

            if (sockets.Total > 0)
            {
                entry.LastActive = now;
            }
        }

        var rows = new List<ProcessUsage>(_entries.Count);

        foreach ((int processId, Entry entry) in _entries)
        {
            bool quiet = now - entry.LastActive > Retention;

            if (!entry.Seen)
            {
                entry.SendRate = 0;
                entry.ReceiveRate = 0;
                entry.PacketRate = 0;

                if (quiet)
                {
                    _expired.Add(processId);
                }
            }
            if (!quiet)
            {
                rows.Add(new ProcessUsage(
                    processId,
                    entry.Identity,
                    entry.SendRate,
                    entry.ReceiveRate,
                    entry.PacketRate,
                    entry.Sent - entry.BaselineSent,
                    entry.Received - entry.BaselineReceived,
                    entry.Sockets));
            }
        }

        foreach (int processId in _expired)
        {
            _entries.Remove(processId);
            identities.Forget(processId);
        }

        _expired.Clear();

        return rows;
    }

    private Entry Track(int processId, DateTimeOffset now)
    {
        if (!_entries.TryGetValue(processId, out Entry? entry))
        {
            entry = new Entry
            {
                Identity = identities.Resolve(processId),
                LastActive = now,
            };

            _entries[processId] = entry;
        }

        entry.Seen = true;
        return entry;
    }

    private static void Accumulate(
        Entry entry,
        ProcessTraffic totals,
        double elapsedSeconds,
        DateTimeOffset now,
        bool rebasing)
    {
        if (rebasing && entry.HasBaseline)
        {
            bool moved = totals.BytesSent > entry.Sent || totals.BytesReceived > entry.Received;

            entry.Sent = totals.BytesSent;
            entry.Received = totals.BytesReceived;
            entry.Packets = totals.Packets;
            entry.SendRate = 0;
            entry.ReceiveRate = 0;
            entry.PacketRate = 0;
            if (moved)
            {
                entry.LastActive = now;
            }

            return;
        }

        if (!entry.HasBaseline)
        {
            entry.HasBaseline = true;
            entry.Sent = totals.BytesSent;
            entry.Received = totals.BytesReceived;
            entry.Packets = totals.Packets;
            entry.BaselineSent = totals.BytesSent;
            entry.BaselineReceived = totals.BytesReceived;
            return;
        }

        long sent = totals.BytesSent - entry.Sent;
        long received = totals.BytesReceived - entry.Received;
        long packets = totals.Packets - entry.Packets;

        entry.Sent = totals.BytesSent;
        entry.Received = totals.BytesReceived;
        entry.Packets = totals.Packets;

        entry.SendRate = Rate(sent, elapsedSeconds);
        entry.ReceiveRate = Rate(received, elapsedSeconds);
        entry.PacketRate = Rate(packets, elapsedSeconds);

        if (sent > 0 || received > 0)
        {
            entry.LastActive = now;
        }
    }

    private static double Rate(long delta, double seconds) =>
        delta <= 0 || seconds <= 0 ? 0 : delta / seconds;

    private sealed class Entry
    {
        public ProcessIdentity Identity = new(string.Empty, null, null, null);
        public long Sent;
        public long Received;
        public long Packets;
        public long BaselineSent;
        public long BaselineReceived;
        public double SendRate;
        public double ReceiveRate;
        public double PacketRate;
        public SocketCounts Sockets;
        public bool HasBaseline;
        public bool Seen;
        public DateTimeOffset LastActive;
    }
}
