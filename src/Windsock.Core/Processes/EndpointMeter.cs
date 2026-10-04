namespace Windsock.Core.Processes;

/// <summary>What one process moved to one far end: its running total, and its rate since the last reading.</summary>
/// <param name="ProcessId">The process that moved it.</param>
/// <param name="Key">The far end.</param>
/// <param name="Total">Everything moved to and from it since tracing began.</param>
/// <param name="SendBytesPerSecond">Outbound since the last reading, or zero when there is no rate yet.</param>
/// <param name="ReceiveBytesPerSecond">Inbound since the last reading, or zero when there is no rate yet.</param>
public readonly record struct EndpointReading(
    int ProcessId,
    EndpointKey Key,
    EndpointTraffic Total,
    double SendBytesPerSecond,
    double ReceiveBytesPerSecond);

/// <summary>
/// Reads the far ends a set of processes are talking to, and turns each
/// process's running totals into rates.
/// </summary>
/// <remarks>
/// <para>
/// Kept per process, never summed first: an app's processes come and go, and a
/// process joining the set would otherwise bring every byte it ever moved into
/// one reading as a rate. A process seen for the first time gives its totals
/// and no rate until the reading after.
/// </para>
/// <para>
/// A reading that comes too soon after the last - two refreshes can land a few
/// milliseconds apart - repeats the last rates rather than dividing by almost
/// nothing or giving zero. Measured on 2026-09-15 with the trace running: the
/// map's places all dropped to no rate for one frame in fifteen, before this.
/// </para>
/// </remarks>
public sealed class EndpointMeter
{
    private readonly IProcessEndpointSource _source;
    private readonly TimeProvider _clock;
    private readonly Dictionary<int, Readings> _processes = [];
    private DateTimeOffset _measuredAt;

    public EndpointMeter(IProcessEndpointSource source)
        : this(source, TimeProvider.System)
    {
    }

    public EndpointMeter(IProcessEndpointSource source, TimeProvider clock)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(clock);

        _source = source;
        _clock = clock;
    }

    /// <summary>
    /// Reads every far end of the given processes. Processes no longer in the
    /// set are forgotten, and are not unwatched: watching is the caller's.
    /// </summary>
    public IReadOnlyList<EndpointReading> Measure(IReadOnlyCollection<int> processIds)
    {
        ArgumentNullException.ThrowIfNull(processIds);

        if (!_source.CanAttribute)
        {
            return [];
        }

        DateTimeOffset now = _clock.GetUtcNow();
        double seconds = (now - _measuredAt).TotalSeconds;

        // A gap too short is noise, so the last rates stand and the baseline
        // stays where it was; one too long - the page was paused, or hidden -
        // is an average over time nobody asked about, so it starts again.
        bool tooSoon = _measuredAt != default && seconds <= 0.2;
        bool usable = seconds is > 0.2 and < 30;

        if (!tooSoon)
        {
            _measuredAt = now;
        }

        foreach (int gone in _processes.Keys.Where(id => !processIds.Contains(id)).ToList())
        {
            _processes.Remove(gone);
        }

        List<EndpointReading> readings = [];

        foreach (int processId in processIds)
        {
            if (!_processes.TryGetValue(processId, out Readings? kept))
            {
                kept = new Readings();
                _processes[processId] = kept;
            }

            _source.CopyEndpoints(processId, kept.Now);

            if (tooSoon)
            {
                foreach ((EndpointKey key, EndpointTraffic traffic) in kept.Now)
                {
                    (double send, double receive) = kept.Rates.GetValueOrDefault(key);
                    readings.Add(new EndpointReading(processId, key, traffic, send, receive));
                }

                continue;
            }

            kept.Rates.Clear();

            foreach ((EndpointKey key, EndpointTraffic traffic) in kept.Now)
            {
                double send = 0;
                double receive = 0;

                if (usable && kept.HasEarlier && kept.Earlier.TryGetValue(key, out EndpointTraffic before))
                {
                    send = Math.Max(0, traffic.BytesSent - before.BytesSent) / seconds;
                    receive = Math.Max(0, traffic.BytesReceived - before.BytesReceived) / seconds;
                }

                kept.Rates[key] = (send, receive);
                readings.Add(new EndpointReading(processId, key, traffic, send, receive));
            }

            (kept.Earlier, kept.Now) = (kept.Now, kept.Earlier);
            kept.HasEarlier = true;
        }

        return readings;
    }

    private sealed class Readings
    {
        public Dictionary<EndpointKey, EndpointTraffic> Now { get; set; } = [];

        public Dictionary<EndpointKey, EndpointTraffic> Earlier { get; set; } = [];

        public bool HasEarlier { get; set; }

        public Dictionary<EndpointKey, (double Send, double Receive)> Rates { get; } = [];
    }
}
