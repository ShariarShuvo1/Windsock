using Microsoft.Extensions.Logging;
using Windsock.Core.Processes;

namespace Windsock.Core.Apps;

/// <summary>
/// Windows' count of each running app over the last few minutes, ready to be
/// matched to processes - for everything that shows an app's rate without a
/// kernel trace: the Apps page, the network map and an app's own window.
/// </summary>
/// <remarks>
/// <para>
/// Matching needs each of Windows' identifiers named, which reads files and
/// package manifests, so the indexes are built on a worker thread, once for
/// each new reading, and shared.
/// </para>
/// <para>
/// An app missing from the last few minutes is idle only if Windows counts it
/// at all. Windows puts some traffic down to itself rather than to the program
/// that caused it - services in a shared host among them - and such a program
/// never appears under its own name; a zero for it would claim what nobody
/// measured. So a zero is given only to an app Windows counted at some point in
/// the past week, and anything else is unknown.
/// </para>
/// </remarks>
public sealed partial class RecentAppFigures : IDisposable
{
    private static readonly TimeSpan KnownFreshness = TimeSpan.FromMinutes(10);
    private static readonly TimeSpan KnownReach = TimeSpan.FromDays(7);

    private readonly AppUsageCollector _collector;
    private readonly IAppUsageStore _store;
    private readonly AppNames _names;
    private readonly TimeProvider _clock;
    private readonly ILogger<RecentAppFigures> _logger;
    private readonly Lock _gate = new();

    private Built _built = new(ProgramUsageIndex.Empty, ProgramUsageIndex.Empty, null, AppUsageState.Waiting);
    private DateTimeOffset _knownAt = DateTimeOffset.MinValue;
    private int _building;
    private AppUsageStatus? _wanted;
    private bool _disposed;

    public RecentAppFigures(AppUsageCollector collector, IAppUsageStore store, AppNames names, TimeProvider clock, ILogger<RecentAppFigures> logger)
    {
        ArgumentNullException.ThrowIfNull(collector);
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(names);
        ArgumentNullException.ThrowIfNull(clock);

        _collector = collector;
        _store = store;
        _names = names;
        _clock = clock;
        _logger = logger;

        _collector.Changed += OnCollected;
        Rebuild(_collector.Status);
    }

    /// <summary>Raised, on a worker thread, when a new reading has been indexed or the state changed.</summary>
    public event EventHandler? Changed;

    /// <summary>How long the figures cover, in seconds, for turning them into rates.</summary>
    public static double Seconds => AppUsagePlan.RecentSpan * 60.0;

    /// <summary>Whether there is a reading to match against at all.</summary>
    public bool IsKnown => _built.Start is not null;

    /// <summary>When the stretch the figures cover begins, or null when unknown.</summary>
    public DateTimeOffset? Start => _built.Start;

    /// <summary>Where collection stood when the figures were indexed.</summary>
    public AppUsageState State => _built.State;

    /// <summary>
    /// Windows' figure for the app a process belongs to over the last few
    /// minutes: its record, a zero record for an app Windows counts that moved
    /// nothing lately, or null when there is no reading or the app is not one
    /// Windows counts under its own name.
    /// </summary>
    public AppUsageRecord? For(ProcessIdentity identity)
    {
        ArgumentNullException.ThrowIfNull(identity);

        Built built = _built;
        string key = RunningApps.KeyOf(identity);

        if (built.Start is null || !RunningApps.IsCountable(key))
        {
            return null;
        }

        if (built.Recent.TryFind(identity.FilePath, out AppUsageRecord record))
        {
            return record;
        }

        return built.Counted.TryFind(identity.FilePath, out AppUsageRecord counted)
            ? new AppUsageRecord(counted.AttributionId, 0, 0)
            : null;
    }

    public void Dispose()
    {
        _disposed = true;
        _collector.Changed -= OnCollected;
    }

    private void OnCollected(object? sender, AppUsageChangedEventArgs e)
    {
        if ((e.Changes & (AppUsageChanges.Recent | AppUsageChanges.State)) != AppUsageChanges.None)
        {
            Rebuild(_collector.Status);
        }
    }

    private void Rebuild(AppUsageStatus status)
    {
        lock (_gate)
        {
            _wanted = status;

            // One build at a time; a reading that arrives meanwhile is built next.
            if (_building != 0)
            {
                return;
            }

            _building = 1;
        }

        _ = Task.Run(Build);
    }

    private void Build()
    {
        while (!_disposed)
        {
            AppUsageStatus? status;

            lock (_gate)
            {
                status = _wanted;
                _wanted = null;

                if (status is null)
                {
                    _building = 0;
                    return;
                }
            }

            ProgramUsageIndex recent = ProgramUsageIndex.Empty;
            ProgramUsageIndex counted = _built.Counted;
            DateTimeOffset? start = status.RecentStart;

            try
            {
                if (start is not null)
                {
                    recent = ProgramUsageIndex.Build(status.Recent, _names);
                }

                DateTimeOffset now = _clock.GetUtcNow();

                if (now - _knownAt >= KnownFreshness)
                {
                    _knownAt = now;

                    try
                    {
                        counted = ProgramUsageIndex.Build(
                            _store.ReadAppTotals(now - KnownReach, now).ToDictionary(
                                total => total.AttributionId,
                                total => new AppUsageRecord(total.AttributionId, total.BytesReceived, total.BytesSent),
                                StringComparer.Ordinal),
                            _names);
                    }
                    catch (Exception ex) when (Windsock.Core.History.HistoryFailure.IsStorage(ex))
                    {
                        // Unreadable history leaves what was known; the recent figures still stand.
                        LogBuildFailed(ex);
                    }
                }
            }
#pragma warning disable CA1031 // Naming reads other programs' files, and the store may be unreadable; either way the figures are unknown, not the app gone.
            catch (Exception ex)
#pragma warning restore CA1031
            {
                LogBuildFailed(ex);
                recent = ProgramUsageIndex.Empty;
                start = null;
            }

            _built = new Built(recent, counted, start, status.State);
            Changed?.Invoke(this, EventArgs.Empty);
        }
    }

    private sealed record Built(ProgramUsageIndex Recent, ProgramUsageIndex Counted, DateTimeOffset? Start, AppUsageState State);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Windows' recent per-app figures could not be matched to programs")]
    private partial void LogBuildFailed(Exception error);
}
