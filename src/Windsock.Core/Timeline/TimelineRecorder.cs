using System.Net;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Windsock.Core.Adapters;
using Windsock.Core.History;
using Windsock.Core.Insight;
using Windsock.Core.Processes;
using Windsock.Core.Settings;

namespace Windsock.Core.Timeline;

/// <summary>What the timeline recorder needs to name a far end, all optional but the country.</summary>
/// <param name="Country">The two-letter country of a public address, from the place table shipped with Windsock.</param>
/// <param name="Organisation">Who runs an address's network, from the offline ASN table once it exists; null until then.</param>
/// <param name="HostOf">
/// The name a process reached an address by: its own DNS lookups where the
/// per-app lookup trace saw them, else the DNS client's cache. Never asks a
/// resolver: names it cannot find now are filled in by a later pass.
/// </param>
public sealed record FarEndNamer(
    Func<IPAddress, string?> Country,
    Func<IPAddress, string?>? Organisation,
    Func<int, string, string?>? HostOf);

/// <summary>
/// Keeps the connection timeline, each day's places, and each service host's
/// days, from the kernel trace. Full edition, as administrator.
/// </summary>
/// <remarks>
/// <para>
/// Every ten seconds, while the trace attributes and history is recorded:
/// each service host's bytes go to its day (always - they name no place, and
/// are how Windows Update's own traffic is measured); and, only while the
/// timeline is switched on, the flows the trace kept are put to their apps,
/// named, and kept. Asking the trace for flows costs a lookup a packet, so it
/// is asked only then.
/// </para>
/// <para>
/// Kept for the days chosen (30 by default), pruned hourly whether or not
/// anything is being kept now, and never past <see cref="SqliteTimelineStore.MostConversations"/>
/// conversations. Erasing all history erases it too; erasing a period of
/// history reaches it through <see cref="IErasedWithHistory"/>.
/// </para>
/// </remarks>
public sealed partial class TimelineRecorder : BackgroundService
{
    private static readonly TimeSpan Step = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan PruneEvery = TimeSpan.FromHours(1);

    /// <summary>The most far ends kept for one app in one pass; the rest are folded into one "other places" row.</summary>
    internal const int PerAppCap = 256;

    private readonly IFlowLedger _ledger;
    private readonly IProcessTrafficSource _traffic;
    private readonly ServiceHosts _services;
    private readonly ITimelineStore _store;
    private readonly FarEndNamer _namer;
    private readonly IProcessIdentityResolver _identities;
    private readonly WindsockSettings _settings;
    private readonly Func<bool> _recording;
    private readonly IUsageHistoryStore? _history;
    private readonly TimeProvider _clock;
    private readonly ILogger<TimelineRecorder> _logger;
    private readonly List<FlowSample> _samples = [];
    private readonly Dictionary<int, ProcessTraffic> _running = [];
    private readonly Dictionary<int, ProcessTraffic> _before = [];
    private readonly Dictionary<int, (string App, string? Name)> _apps = [];
    private DateTimeOffset _prunedAt = DateTimeOffset.MinValue;
    private bool _measuredLast;

    public TimelineRecorder(
        IFlowLedger ledger,
        IProcessTrafficSource traffic,
        ServiceHosts services,
        ITimelineStore store,
        FarEndNamer namer,
        IProcessIdentityResolver identities,
        WindsockSettings settings,
        UsageRecorder recorder,
        IUsageHistoryStore history,
        ILogger<TimelineRecorder> logger)
        : this(ledger, traffic, services, store, namer, identities, settings, () => recorder.IsRecording, history, TimeProvider.System, logger)
    {
        ArgumentNullException.ThrowIfNull(recorder);
    }

    internal TimelineRecorder(
        IFlowLedger ledger,
        IProcessTrafficSource traffic,
        ServiceHosts services,
        ITimelineStore store,
        FarEndNamer namer,
        IProcessIdentityResolver identities,
        WindsockSettings settings,
        Func<bool> recording,
        IUsageHistoryStore? history,
        TimeProvider clock,
        ILogger<TimelineRecorder> logger)
    {
        ArgumentNullException.ThrowIfNull(ledger);
        ArgumentNullException.ThrowIfNull(traffic);
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(namer);
        ArgumentNullException.ThrowIfNull(identities);
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(recording);
        ArgumentNullException.ThrowIfNull(clock);
        ArgumentNullException.ThrowIfNull(logger);

        _ledger = ledger;
        _traffic = traffic;
        _services = services;
        _store = store;
        _namer = namer;
        _identities = identities;
        _settings = settings;
        _recording = recording;
        _history = history;
        _clock = clock;
        _logger = logger;
    }

    /// <summary>Raised on the recorder's thread after a pass kept anything in the timeline.</summary>
    public event EventHandler? Recorded;

    /// <summary>Whether the timeline is being kept now: switched on, the trace attributing, history recording.</summary>
    public bool IsRecordingTimeline => _ledger.CollectFlows;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (_history is not null)
        {
            _history.Erased += OnErased;
        }

        try
        {
            while (!stoppingToken.IsCancellationRequested)
            {
                await Task.Delay(Step, _clock, stoppingToken).ConfigureAwait(false);
                Tick();
            }
        }
        catch (OperationCanceledException)
        {
        }
        finally
        {
            if (_history is not null)
            {
                _history.Erased -= OnErased;
            }

            _ledger.CollectFlows = false;
        }
    }

    /// <summary>One pass. Internal so tests can drive it.</summary>
    internal void Tick()
    {
        DateTimeOffset now = _clock.GetUtcNow();

        // The retention holds whether or not anything is being kept now:
        // unelevated, paused or switched off, what was kept still ages out.
        // Pruning never creates the file.
        PruneIfDue(now);

        bool measuring = _ledger.CanAttribute && _recording();

        if (!measuring)
        {
            _ledger.CollectFlows = false;
            _measuredLast = false;
            _before.Clear();
            return;
        }

        try
        {
            KeepServiceDays(now);

            bool wanted = _settings.Insight.Timeline;

            if (wanted && _ledger.CollectFlows)
            {
                KeepFlows();
            }

            _ledger.CollectFlows = wanted;
        }
        catch (Exception ex) when (HistoryFailure.IsStorage(ex))
        {
            LogStoreFailed(ex);
        }

        _measuredLast = true;
    }

    private void PruneIfDue(DateTimeOffset now)
    {
        if (now - _prunedAt < PruneEvery && now >= _prunedAt)
        {
            return;
        }

        _prunedAt = now;

        try
        {
            _store.Prune(now - TimeSpan.FromDays(_settings.Insight.KeptDays));
        }
        catch (Exception ex) when (HistoryFailure.IsStorage(ex))
        {
            LogStoreFailed(ex);
        }
    }

    /// <summary>Adds each service host's bytes since the last pass to today.</summary>
    private void KeepServiceDays(DateTimeOffset now)
    {
        _traffic.CopyTo(_running);

        if (!_measuredLast)
        {
            // The first pass after a pause only sets where counting starts:
            // the bytes before it belong to time nobody was keeping.
            Rebase();
            return;
        }

        ServiceHostMap map = _services.Current;
        Dictionary<string, (long Received, long Sent)> moved = new(StringComparer.OrdinalIgnoreCase);

        foreach ((int processId, ProcessTraffic current) in _running)
        {
            if (map.KeyOf(processId) is not { } key)
            {
                continue;
            }

            ProcessTraffic before = _before.GetValueOrDefault(processId);

            // Counters that went backwards are a new process with an old id.
            if (current.BytesReceived < before.BytesReceived || current.BytesSent < before.BytesSent)
            {
                before = default;
            }

            long received = current.BytesReceived - before.BytesReceived;
            long sent = current.BytesSent - before.BytesSent;

            if (received <= 0 && sent <= 0)
            {
                continue;
            }

            (long r, long s) = moved.GetValueOrDefault(key);
            moved[key] = (r + received, s + sent);
        }

        Rebase();

        if (moved.Count == 0)
        {
            return;
        }

        DateOnly today = DateOnly.FromDateTime(now.ToLocalTime().DateTime);
        _store.AddServiceDays([.. moved.Select(each => new ServiceDayBytes(today, each.Key, each.Value.Received, each.Value.Sent))]);
    }

    private void Rebase()
    {
        _before.Clear();

        foreach ((int processId, ProcessTraffic traffic) in _running)
        {
            _before[processId] = traffic;
        }
    }

    /// <summary>Takes the trace's flows, puts each to its app and names its far end, and keeps them.</summary>
    private void KeepFlows()
    {
        _ledger.TakeFlows(_samples);

        if (_samples.Count == 0)
        {
            return;
        }

        List<TimelineFlow> flows = new(_samples.Count);
        HashSet<int> seen = [];

        foreach (IGrouping<int, FlowSample> process in _samples.GroupBy(static each => each.ProcessId))
        {
            seen.Add(process.Key);
            (string app, string? name) = AppOf(process.Key, process.First().Image);

            if (string.IsNullOrEmpty(app))
            {
                continue;
            }

            foreach (FlowSample sample in Capped(process))
            {
                if (sample.Sent + sample.Received <= 0)
                {
                    continue;
                }

                flows.Add(Describe(sample, app, name));
            }
        }

        // Ids of processes no longer seen may be handed to another program.
        foreach (int gone in _apps.Keys.Where(id => !seen.Contains(id)).ToList())
        {
            _apps.Remove(gone);
        }

        if (flows.Count > 0)
        {
            _store.Record(flows);
            Recorded?.Invoke(this, EventArgs.Empty);
        }
    }

    /// <summary>
    /// A process's flows, the busiest <see cref="PerAppCap"/> as they are and
    /// the rest folded into one with no address, so a torrent client's
    /// thousand peers make one "other places" row a pass rather than a thousand.
    /// </summary>
    internal static IEnumerable<FlowSample> Capped(IEnumerable<FlowSample> flows)
    {
        List<FlowSample> all = [.. flows.OrderByDescending(static each => each.Sent + each.Received)];

        if (all.Count <= PerAppCap)
        {
            return all;
        }

        List<FlowSample> rest = all.GetRange(PerAppCap, all.Count - PerAppCap);
        FlowSample folded = new(
            all[0].ProcessId,
            default,
            rest.Sum(static each => each.Sent),
            rest.Sum(static each => each.Received),
            rest.Sum(static each => each.Packets),
            rest.Min(static each => each.FirstTicks),
            rest.Max(static each => each.LastTicks),
            all[0].Image);

        return [.. all.GetRange(0, PerAppCap), folded];
    }

    private TimelineFlow Describe(FlowSample sample, string app, string? name)
    {
        string? address = null;
        string? host = null;
        string? country = null;
        string? organisation = null;
        bool local = false;

        if (sample.Remote.HasAddress)
        {
            IPAddress ip = sample.Remote.ToAddress();
            address = ip.ToString();
            local = TrafficReach.IsLocal(ip);
            host = _namer.HostOf?.Invoke(sample.ProcessId, address);

            if (AddressScopes.Of(ip) == AddressScope.Public)
            {
                country = Clean(_namer.Country(ip));
                organisation = Clean(_namer.Organisation?.Invoke(ip));
            }
        }

        return new TimelineFlow(
            app,
            name,
            address,
            sample.Remote.Port,
            host,
            country,
            organisation,
            local,
            new DateTimeOffset(Math.Max(sample.FirstTicks, DateTimeOffset.UnixEpoch.UtcTicks), TimeSpan.Zero),
            new DateTimeOffset(Math.Max(sample.LastTicks, sample.FirstTicks), TimeSpan.Zero),
            sample.Received,
            sample.Sent,
            sample.Packets);
    }

    private static string? Clean(string? text) => string.IsNullOrWhiteSpace(text) ? null : text.Trim();

    private (string App, string? Name) AppOf(int processId, string? image)
    {
        if (_apps.TryGetValue(processId, out (string App, string? Name) known))
        {
            return known;
        }

        (string? native, string? family) = ProcessIdentityResolver.ReadCountedAs(processId);
        string app = AppMinuteTally.CountedAs(processId, family, native, image);
        string? name = null;

        if (native is not null || family is not null)
        {
            ProcessIdentity identity = _identities.Resolve(processId);
            name = string.IsNullOrWhiteSpace(identity.Description) ? identity.Name : identity.Description;
        }
        else if (!string.IsNullOrEmpty(image))
        {
            name = Path.GetFileNameWithoutExtension(image);
        }

        _apps[processId] = (app, name);
        return (app, name);
    }

    /// <summary>All history erased: the timeline goes with it.</summary>
    private void OnErased(object? sender, EventArgs e)
    {
        try
        {
            if (_history is null || _history.Extent().HasData)
            {
                return;
            }

            _store.RemoveEverything();
        }
        catch (Exception ex) when (HistoryFailure.IsStorage(ex))
        {
            LogStoreFailed(ex);
        }
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "Could not keep the connection timeline")]
    private partial void LogStoreFailed(Exception exception);
}
