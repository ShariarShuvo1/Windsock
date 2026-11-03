using System.Net;
using System.Security.Principal;
using Microsoft.Diagnostics.Tracing.Parsers;
using Microsoft.Diagnostics.Tracing.Session;
using Windsock.Core.Timeline;

namespace Windsock.Core.Processes;

/// <summary>
/// Attributes packets to processes by listening to the Windows kernel's network
/// trace events.
/// </summary>
public sealed class EtwProcessTrafficSource : IProcessTrafficSource, IProcessEndpointSource, IProcessTrafficLedger, IFlowLedger, ITrafficSplitSource, IDisposable
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

    /// <summary>
    /// How many process-and-far-end pairs are kept between two takes. A
    /// torrent client reaches a couple of thousand; past this, a process's
    /// further far ends are folded into one entry with no address.
    /// </summary>
    private const int FlowCap = 32_768;

    /// <summary>
    /// How many process-and-far-end pairs are kept between takes before each
    /// process's new far ends are folded into one entry of its own. Settable
    /// for tests.
    /// </summary>
    internal int FlowCapacity { get; init; } = FlowCap;

    private readonly Dictionary<FlowKey, FlowCounters> _flows = [];
    private readonly HashSet<int> _flowProcesses = [];
    private readonly Dictionary<int, string> _flowImages = [];
    private volatile bool _collectFlows;

    // Running totals for the local/internet split, added to per packet
    // without the lock.
    private long _localSent;
    private long _localReceived;
    private long _internetSent;
    private long _internetReceived;
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

    /// <inheritdoc cref="IProcessTrafficLedger.CanAttribute" />
    public bool CanAttribute => State == TrafficSourceState.Running;

    /// <inheritdoc />
    public bool CollectFlows
    {
        get => _collectFlows;
        set
        {
            _collectFlows = value;

            if (!value)
            {
                lock (_gate)
                {
                    _flows.Clear();
                    _flowProcesses.Clear();
                    _flowImages.Clear();
                }
            }
        }
    }

    /// <inheritdoc />
    public void TakeFlows(List<FlowSample> into)
    {
        ArgumentNullException.ThrowIfNull(into);
        into.Clear();

        lock (_gate)
        {
            foreach ((FlowKey key, FlowCounters counters) in _flows)
            {
                string? image = _images.GetValueOrDefault(key.ProcessId) ?? _flowImages.GetValueOrDefault(key.ProcessId);

                into.Add(new FlowSample(
                    key.ProcessId,
                    key.Remote,
                    counters.Sent,
                    counters.Received,
                    counters.Packets,
                    counters.First,
                    counters.Last,
                    image));
            }

            _flows.Clear();
            _flowProcesses.Clear();
            _flowImages.Clear();
        }
    }

    /// <inheritdoc />
    public TrafficSplit ReadSplit() => new(
        Interlocked.Read(ref _localSent),
        Interlocked.Read(ref _localReceived),
        Interlocked.Read(ref _internetSent),
        Interlocked.Read(ref _internetReceived));

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
    internal void Count(int processId, int bytes, bool sent, IPAddress? local, IPAddress? remote, int port)
    {
        if (!LoopbackEnds.StaysOnMachine(local, remote))
        {
            Split(bytes, sent, remote);
            Add(processId, bytes, sent, remote, port);
        }

        if (_watching != 0)
        {
            Note(processId, bytes, sent, remote, port);
        }
    }

    /// <summary>
    /// Adds a packet to the local or the internet total by its far end. Not
    /// per process and not under the lock: four running sums, each read atomically.
    /// </summary>
    private void Split(int bytes, bool sent, IPAddress? remote)
    {
        if (bytes <= 0)
        {
            return;
        }

        bool local = TrafficReach.IsLocal(remote);

        if (sent)
        {
            Interlocked.Add(ref local ? ref _localSent : ref _internetSent, bytes);
        }
        else
        {
            Interlocked.Add(ref local ? ref _localReceived : ref _internetReceived, bytes);
        }
    }

    private void Add(int processId, int bytes, bool sent, IPAddress? remote, int port)
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

            if (_collectFlows)
            {
                Flow(processId, bytes, sent, remote, port);
            }
        }
    }

    /// <summary>Adds a packet to its process-and-far-end pair. Call with the lock held.</summary>
    private void Flow(int processId, int bytes, bool sent, IPAddress? remote, int port)
    {
        FlowKey key = new(processId, EndpointKey.From(remote, port));

        if (!_flows.TryGetValue(key, out FlowCounters? counters))
        {
            // Past the cap a process's new far ends share one entry with no
            // address, so its bytes still count. That entry is made when first
            // needed, one a process at most, so the cap can be passed by no
            // more than the number of processes.
            if (_flows.Count >= FlowCapacity)
            {
                key = new FlowKey(processId, default);
            }

            if (!_flows.TryGetValue(key, out counters))
            {
                counters = new FlowCounters { First = DateTime.UtcNow.Ticks };
                _flows[key] = counters;
                _flowProcesses.Add(processId);
            }
        }

        counters.Packets++;
        counters.Last = DateTime.UtcNow.Ticks;

        if (sent)
        {
            counters.Sent += bytes;
        }
        else
        {
            counters.Received += bytes;
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

            // Its flows wait to be taken, and need its file to be put to an app.
            if (file is not null && _flowProcesses.Contains(processId))
            {
                _flowImages[processId] = file;
            }

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

    private readonly record struct FlowKey(int ProcessId, EndpointKey Remote);

    private sealed class FlowCounters
    {
        public long Sent;
        public long Received;
        public long Packets;

        /// <summary>UTC ticks of the first packet since the last take.</summary>
        public long First;

        /// <summary>UTC ticks of the last packet.</summary>
        public long Last;
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
