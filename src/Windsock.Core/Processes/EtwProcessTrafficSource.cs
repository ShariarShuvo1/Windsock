using System.Net;
using System.Security.Principal;
using Microsoft.Diagnostics.Tracing.Parsers;
using Microsoft.Diagnostics.Tracing.Session;

namespace Windsock.Core.Processes;

/// <summary>
/// Attributes packets to processes by listening to the Windows kernel's network
/// trace events.
/// </summary>
public sealed class EtwProcessTrafficSource : IProcessTrafficSource, IProcessEndpointSource, IProcessTrafficLedger, IDisposable
{
    private const string SessionName = "Windsock-network";
    private const int EndpointCap = 2048;

    /// <summary>
    /// How many ended processes wait to be taken. Nobody takes them while
    /// history is off; past this, the oldest go, as they would have anyway.
    /// </summary>
    private const int DepartedCap = 4096;

    private static readonly TimeSpan EndpointMemory = TimeSpan.FromSeconds(45);

    private readonly Lock _gate = new();
    private readonly Dictionary<int, Counters> _totals = [];
    private readonly Dictionary<int, Dictionary<EndpointKey, Counters>> _endpoints = [];
    private readonly Dictionary<int, int> _watched = [];
    private readonly Dictionary<int, string> _images = [];
    private readonly Queue<DepartedTraffic> _departed = new();

    /// <summary>
    /// Process ids that have ended and not been given to a new process yet.
    /// A socket can go on closing after its process has gone, and a packet for
    /// it used to bring the process's counters back: a total nobody would ever
    /// remove, which the next process given the id then carried on from.
    /// </summary>
    private readonly HashSet<int> _ended = [];

    /// <summary>How many ended ids are remembered before the oldest are let go.</summary>
    private const int EndedCap = 16_384;
    private readonly List<EndpointKey> _stale = [];
    private volatile int _watching;
    private TraceEventSession? _session;
    private Thread? _worker;
    private volatile bool _stopping;
    private volatile bool _disposed;

    private sealed record Condition(TrafficSourceState State, string? Detail, string? Explanation);

    private volatile Condition _condition = new(TrafficSourceState.Stopped, null, null);

    /// <inheritdoc />
    public TrafficSourceState State => _condition.State;

    /// <inheritdoc />
    public string? Detail => _condition.Detail;

    /// <inheritdoc />
    public string? Explanation => _condition.Explanation;

    /// <summary>
    /// Events the kernel dropped because the session's buffers were full, since
    /// it started: bytes nothing counted. Zero while the trace keeps up.
    /// </summary>
    public int EventsLost
    {
        get
        {
            try
            {
                return _session?.EventsLost ?? 0;
            }
            catch (ObjectDisposedException)
            {
                return 0;
            }
        }
    }

    /// <summary>Whether the current process can open a kernel trace session.</summary>
    public static bool IsElevated()
    {
        using WindowsIdentity identity = WindowsIdentity.GetCurrent();
        return new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator);
    }

    /// <inheritdoc />
    public void Start()
    {
        if (State == TrafficSourceState.Running)
        {
            return;
        }

        if (!IsElevated())
        {
            _condition = new Condition(
                TrafficSourceState.RequiresElevation,
                "Per-process rates need administrator rights.",
                "Windows only opens the kernel trace session that attributes packets to processes for an administrator. Everything else in this table works without it.");
            return;
        }

        try
        {
            StopStaleSession();

            var session = new TraceEventSession(SessionName) { StopOnDispose = true };
            session.EnableKernelProvider(
                KernelTraceEventParser.Keywords.NetworkTCPIP | KernelTraceEventParser.Keywords.Process);

            // The kernel names the far end saddr on a packet coming in and daddr
            // on one going out; the local end is the other of the two.
            KernelTraceEventParser kernel = session.Source.Kernel;
            kernel.TcpIpSend += data => Count(data.ProcessID, data.size, sent: true, data.saddr, data.daddr, data.dport);
            kernel.TcpIpRecv += data => Count(data.ProcessID, data.size, sent: false, data.daddr, data.saddr, data.sport);
            kernel.TcpIpSendIPV6 += data => Count(data.ProcessID, data.size, sent: true, data.saddr, data.daddr, data.dport);
            kernel.TcpIpRecvIPV6 += data => Count(data.ProcessID, data.size, sent: false, data.daddr, data.saddr, data.sport);
            kernel.UdpIpSend += data => Count(data.ProcessID, data.size, sent: true, data.saddr, data.daddr, data.dport);
            kernel.UdpIpRecv += data => Count(data.ProcessID, data.size, sent: false, data.daddr, data.saddr, data.sport);
            kernel.UdpIpSendIPV6 += data => Count(data.ProcessID, data.size, sent: true, data.saddr, data.daddr, data.dport);
            kernel.UdpIpRecvIPV6 += data => Count(data.ProcessID, data.size, sent: false, data.daddr, data.saddr, data.sport);

            // The processes already running are listed as the session starts
            // (DCStart), so every process's file is known, not only new ones.
            kernel.ProcessStart += data => Name(data.ProcessID, data.KernelImageFileName);
            kernel.ProcessDCStart += data => Name(data.ProcessID, data.KernelImageFileName);
            kernel.ProcessStop += data => Forget(data.ProcessID, data.KernelImageFileName);

            _session = session;
            _worker = new Thread(Pump)
            {
                IsBackground = true,
                Name = "Windsock-network-trace",
            };

            _worker.Start();

            _condition = new Condition(TrafficSourceState.Running, null, null);
        }
        catch (Exception ex)
        {
            _condition = new Condition(TrafficSourceState.Unavailable, ex.Message, null);
            _session?.Dispose();
            _session = null;
        }
    }

    /// <inheritdoc />
    public void CopyTo(Dictionary<int, ProcessTraffic> destination)
    {
        destination.Clear();

        lock (_gate)
        {
            foreach ((int processId, Counters counters) in _totals)
            {
                destination[processId] = new ProcessTraffic(counters.Sent, counters.Received, counters.Packets);
            }
        }
    }

    /// <inheritdoc />
    public void Take(Dictionary<int, ProcessTraffic> running, List<DepartedTraffic> departed)
    {
        ArgumentNullException.ThrowIfNull(running);
        ArgumentNullException.ThrowIfNull(departed);

        running.Clear();
        departed.Clear();

        lock (_gate)
        {
            foreach ((int processId, Counters counters) in _totals)
            {
                running[processId] = new ProcessTraffic(counters.Sent, counters.Received, counters.Packets);
            }

            departed.AddRange(_departed);
            _departed.Clear();
        }
    }

    /// <inheritdoc />
    public string? ImageOf(int processId)
    {
        lock (_gate)
        {
            return _images.GetValueOrDefault(processId);
        }
    }

    /// <inheritdoc />
    public bool CanAttribute => State == TrafficSourceState.Running;

    /// <summary>
    /// Whether a process's traffic is still being broken down by endpoint.
    /// </summary>
    public bool IsWatching(int processId)
    {
        lock (_gate)
        {
            return _watched.ContainsKey(processId);
        }
    }

    /// <inheritdoc />
    public void Watch(int processId)
    {
        if (processId <= 0)
        {
            return;
        }

        lock (_gate)
        {
            if (!_watched.TryGetValue(processId, out int askers))
            {
                _endpoints[processId] = [];
            }

            _watched[processId] = askers + 1;
            _watching = _watched.Count;
        }
    }

    /// <inheritdoc />
    public void Unwatch(int processId)
    {
        lock (_gate)
        {
            if (!_watched.TryGetValue(processId, out int askers))
            {
                return;
            }
            if (askers > 1)
            {
                _watched[processId] = askers - 1;
                return;
            }

            _watched.Remove(processId);
            _endpoints.Remove(processId);
            _watching = _watched.Count;
        }
    }

    /// <inheritdoc />
    public void CopyEndpoints(int processId, Dictionary<EndpointKey, EndpointTraffic> destination)
    {
        ArgumentNullException.ThrowIfNull(destination);
        destination.Clear();

        long cutoff = DateTime.UtcNow.Ticks - EndpointMemory.Ticks;

        lock (_gate)
        {
            if (!_endpoints.TryGetValue(processId, out Dictionary<EndpointKey, Counters>? mine))
            {
                return;
            }

            _stale.Clear();

            foreach ((EndpointKey key, Counters counters) in mine)
            {
                if (counters.LastSeen < cutoff)
                {
                    _stale.Add(key);
                    continue;
                }

                destination[key] = new EndpointTraffic(
                    counters.Sent,
                    counters.Received,
                    counters.Packets,
                    new DateTimeOffset(counters.LastSeen, TimeSpan.Zero).ToLocalTime());
            }

            foreach (EndpointKey key in _stale)
            {
                mine.Remove(key);
            }

            _stale.Clear();
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _stopping = true;

        _session?.Dispose();
        _session = null;
        _worker?.Join(TimeSpan.FromSeconds(2));
        _worker = null;

        _condition = new Condition(TrafficSourceState.Stopped, null, null);
    }

    private void Pump()
    {
        try
        {
            _session?.Source.Process();
        }
        catch (Exception ex) when (!_stopping)
        {
            _condition = new Condition(TrafficSourceState.Unavailable, ex.Message, null);
        }
    }

    private static void StopStaleSession()
    {
        try
        {
            TraceEventSession.GetActiveSession(SessionName)?.Stop(noThrow: true);
        }
        catch (Exception)
        {
        }
    }

    /// <summary>
    /// Files one packet: against the process, unless it never left the
    /// machine, and against its far end while someone is watching the process.
    /// </summary>
    /// <remarks>
    /// The kernel raises these events for loopback too. A program talking to a
    /// local proxy, a database or a development server moves nothing over any
    /// network, and counting it made a proxy's traffic appear twice: once for
    /// the program that handed it over and again for the proxy that sent it on.
    /// The far end is still noted, so a watched process's own window can say
    /// what it talks to on this machine; only the process's totals, and so its
    /// rates, leave it out.
    /// </remarks>
    private void Count(int processId, int bytes, bool sent, IPAddress? local, IPAddress? remote, int port)
    {
        if (!LoopbackEnds.StaysOnMachine(local, remote))
        {
            Add(processId, bytes, sent);
        }

        if (_watching != 0)
        {
            Note(processId, bytes, sent, remote, port);
        }
    }

    private void Add(int processId, int bytes, bool sent)
    {
        if (processId <= 0 || bytes <= 0)
        {
            return;
        }

        lock (_gate)
        {
            if (_ended.Contains(processId))
            {
                return;
            }

            if (!_totals.TryGetValue(processId, out Counters? counters))
            {
                counters = new Counters();
                _totals[processId] = counters;
            }

            counters.Packets++;

            if (sent)
            {
                counters.Sent += bytes;
            }
            else
            {
                counters.Received += bytes;
            }
        }
    }

    private void Note(int processId, int bytes, bool sent, IPAddress? address, int port)
    {
        if (processId <= 0 || bytes <= 0 || address is null)
        {
            return;
        }

        EndpointKey key = EndpointKey.From(address, port);

        lock (_gate)
        {
            if (!_endpoints.TryGetValue(processId, out Dictionary<EndpointKey, Counters>? mine))
            {
                return;
            }

            if (!mine.TryGetValue(key, out Counters? counters))
            {
                if (mine.Count >= EndpointCap)
                {
                    return;
                }

                counters = new Counters();
                mine[key] = counters;
            }

            counters.Packets++;
            counters.LastSeen = DateTime.UtcNow.Ticks;

            if (sent)
            {
                counters.Sent += bytes;
            }
            else
            {
                counters.Received += bytes;
            }
        }
    }

    private void Name(int processId, string? image)
    {
        if (processId <= 0 || string.IsNullOrEmpty(image))
        {
            return;
        }

        lock (_gate)
        {
            _images[processId] = image;

            // A new process has the id: whatever a late packet left under it
            // belonged to the one before, which has already been accounted for.
            if (_ended.Remove(processId))
            {
                _totals.Remove(processId);
            }
        }
    }

    private void Forget(int processId, string? image)
    {
        lock (_gate)
        {
            string? file = string.IsNullOrEmpty(image) ? _images.GetValueOrDefault(processId) : image;
            _images.Remove(processId);

            if (_ended.Count >= EndedCap)
            {
                _ended.Clear();
            }

            _ended.Add(processId);

            if (_totals.Remove(processId, out Counters? counters))
            {
                if (_departed.Count >= DepartedCap)
                {
                    _departed.Dequeue();
                }

                _departed.Enqueue(new DepartedTraffic(
                    processId,
                    new ProcessTraffic(counters.Sent, counters.Received, counters.Packets),
                    file));
            }

            _endpoints.Remove(processId);

            if (_watched.Remove(processId))
            {
                _watching = _watched.Count;
            }
        }
    }

    private sealed class Counters
    {
        public long Sent;
        public long Received;

        /// <summary>
        /// One per event. The kernel raises an event per packet, so counting
        /// them costs nothing and tells the two shapes of traffic apart: a few
        /// large transfers and a flood of small ones can read the same in bytes
        /// per second and behave nothing alike.
        /// </summary>
        public long Packets;

        /// <summary>
        /// UTC ticks of the last packet, for the endpoint tables only.
        /// </summary>
        public long LastSeen;
    }
}
