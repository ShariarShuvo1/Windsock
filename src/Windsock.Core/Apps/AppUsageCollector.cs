using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Windsock.Core.History;

namespace Windsock.Core.Apps;

/// <summary>Where app usage collection stands.</summary>
public enum AppUsageState
{
    /// <summary>Nothing has been tried yet.</summary>
    Waiting,

    /// <summary>Windows is answering, and what it says is being kept.</summary>
    Collecting,

    /// <summary>History recording is off, so nothing is being kept.</summary>
    Paused,

    /// <summary>Windows would not answer, or the history file could not take it.</summary>
    Unavailable,
}

/// <summary>What changed in a pass of app usage collection.</summary>
[Flags]
public enum AppUsageChanges
{
    /// <summary>Nothing.</summary>
    None = 0,

    /// <summary>The state or its reason.</summary>
    State = 1,

    /// <summary>Windows were stored; <see cref="AppUsageChangedEventArgs.KeptFrom"/> says which.</summary>
    Kept = 2,

    /// <summary>The live figure was read again, or became unknown.</summary>
    Recent = 4,
}

/// <summary>Says what a pass of collection changed, so a page reloads only what it shows.</summary>
public sealed class AppUsageChangedEventArgs(AppUsageChanges changes, DateTimeOffset? keptFrom, DateTimeOffset? keptUntil) : EventArgs
{
    /// <summary>What changed.</summary>
    public AppUsageChanges Changes { get; } = changes;

    /// <summary>The start of the earliest window stored, when any were.</summary>
    public DateTimeOffset? KeptFrom { get; } = keptFrom;

    /// <summary>The end of the latest window stored, when any were.</summary>
    public DateTimeOffset? KeptUntil { get; } = keptUntil;

    /// <summary>Whether anything stored falls in a period.</summary>
    public bool KeptWithin(DateTimeOffset from, DateTimeOffset until) =>
        KeptFrom is { } start && KeptUntil is { } end && start < until && end > from;
}

/// <summary>Where collection stands, all read at one moment.</summary>
/// <param name="State">Where collection stands.</param>
/// <param name="Detail">Why collection is not happening, when it is not.</param>
/// <param name="CollectedUntil">The end of the last settled minute kept, or null before anything has been.</param>
/// <param name="Recent">Each app's use over the last few minutes, keyed by Windows' identifier; empty when unknown.</param>
/// <param name="RecentStart">When the stretch <paramref name="Recent"/> covers begins, or null when unknown.</param>
public sealed record AppUsageStatus(
    AppUsageState State,
    string? Detail,
    DateTimeOffset? CollectedUntil,
    IReadOnlyDictionary<string, AppUsageRecord> Recent,
    DateTimeOffset? RecentStart)
{
    internal static AppUsageStatus Empty { get; } = new(
        AppUsageState.Waiting,
        null,
        null,
        new Dictionary<string, AppUsageRecord>(StringComparer.Ordinal),
        null);
}

/// <summary>
/// Copies each app's use of the network out of Windows' own data usage and
/// into Windsock's history, and keeps a live figure for the last few minutes.
/// </summary>
/// <remarks>
/// <para>
/// Windows already measures this, for every app, with no privileges asked of
/// anybody; it is what Settings shows under Data usage. Copying it is what makes
/// per-app history possible in an edition with no kernel trace, and for time
/// Windsock was not running at all.
/// </para>
/// <para>
/// History walks forward from where it last stopped, in the windows
/// <see cref="AppUsagePlan"/> lays out, only through minutes that have settled,
/// and stores the windows read together with how far that got. It honours the
/// history switch: while recording is off it moves past the time without
/// keeping any of it, so switching back on does not quietly fetch the period
/// somebody chose not to record.
/// </para>
/// <para>
/// The live figure is separate and never stored: one read of the last few
/// minutes as a single window, for the rates of apps nothing measures live and the
/// Apps page's recent column. It is read whether or not recording is on, the
/// way live rates are shown whether or not history is written.
/// </para>
/// </remarks>
public sealed partial class AppUsageCollector : BackgroundService
{
    private static readonly TimeSpan FirstDelay = TimeSpan.FromSeconds(15);
    private static readonly TimeSpan IntoMinute = TimeSpan.FromSeconds(20);
    private static readonly TimeSpan BetweenBatches = TimeSpan.FromMilliseconds(250);
    private static readonly TimeSpan RetryRecent = TimeSpan.FromSeconds(15);

    // How often the same stretch is read again for a network that answered,
    // and then refused again reading that stretch, before it is given up on.
    private const int MostRereads = 3;

    private readonly IAppUsageReader _reader;
    private readonly IAppUsageStore _store;
    private readonly UsageRecorder _recorder;
    private readonly TimeProvider _clock;
    private readonly TimeZoneInfo _zone;
    private readonly ILogger<AppUsageCollector> _logger;
    private readonly Lock _gate = new();

    private volatile AppUsageStatus _latest = AppUsageStatus.Empty;
    private bool _failing;
    private long? _rereadFrom;
    private int _rereads;

    public AppUsageCollector(
        IAppUsageReader reader,
        IAppUsageStore store,
        UsageRecorder recorder,
        TimeProvider clock,
        ILogger<AppUsageCollector> logger)
        : this(reader, store, recorder, clock, TimeZoneInfo.Local, logger)
    {
    }

    internal AppUsageCollector(
        IAppUsageReader reader,
        IAppUsageStore store,
        UsageRecorder recorder,
        TimeProvider clock,
        TimeZoneInfo zone,
        ILogger<AppUsageCollector> logger)
    {
        ArgumentNullException.ThrowIfNull(reader);
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(recorder);
        ArgumentNullException.ThrowIfNull(clock);
        ArgumentNullException.ThrowIfNull(zone);

        _reader = reader;
        _store = store;
        _recorder = recorder;
        _clock = clock;
        _zone = zone;
        _logger = logger;
    }

    /// <summary>
    /// Raised on the collection thread when a pass kept something, read the
    /// live figure, or changed the state; never for a pass that changed nothing.
    /// </summary>
    public event EventHandler<AppUsageChangedEventArgs>? Changed;

    /// <summary>
    /// Everything below at one moment. Read this rather than two of the
    /// properties in turn, which a pass can change in between.
    /// </summary>
    public AppUsageStatus Status => _latest;

    /// <summary>Where collection stands.</summary>
    public AppUsageState State => _latest.State;

    /// <summary>Why collection is not happening, when it is not.</summary>
    public string? Detail => _latest.Detail;

    /// <summary>
    /// The end of the last settled minute kept in history, or null before
    /// anything has been.
    /// </summary>
    public DateTimeOffset? CollectedUntil => _latest.CollectedUntil;

    /// <summary>
    /// Each app's use over the last few minutes, keyed by Windows' identifier,
    /// or empty when it could not be read.
    /// </summary>
    public IReadOnlyDictionary<string, AppUsageRecord> Recent => _latest.Recent;

    /// <summary>
    /// When the stretch <see cref="Recent"/> covers begins, or null when unknown.
    /// It is <see cref="AppUsagePlan.RecentSpan"/> minutes long.
    /// </summary>
    public DateTimeOffset? RecentStart => _latest.RecentStart;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        LogStarted();

        try
        {
            await Task.Delay(FirstDelay, _clock, stoppingToken).ConfigureAwait(false);

            bool retried = false;

            while (!stoppingToken.IsCancellationRequested)
            {
                bool more = await CollectAsync(stoppingToken).ConfigureAwait(false);

                // A live reading that came back empty, with none before it to
                // stand in, is most likely a read that landed while Windows was
                // rewriting; one more try shortly after, rather than a minute of
                // dashes. Only once, so a machine that really is silent is not
                // asked every few seconds.
                bool retry = !more && !retried && RecentStart is null && State == AppUsageState.Collecting;

                if (retry)
                {
                    retried = true;
                }
                else if (RecentStart is not null)
                {
                    retried = false;
                }

                await Task.Delay(more ? BetweenBatches : retry ? RetryRecent : UntilNextPass(), _clock, stoppingToken)
                    .ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // Shutdown. The next start carries on from the stored position.
        }
    }

    /// <summary>
    /// Runs one pass: keeps the next settled windows, then reads the live figure.
    /// </summary>
    /// <returns>Whether there is more history to catch up on straight away.</returns>
    internal async Task<bool> CollectAsync(CancellationToken token)
    {
        long now = EpochMinute.From(_clock.GetUtcNow());
        (bool more, AppUsageState state, string? detail, DateTimeOffset? until, UsageWindow? keptFrom, UsageWindow? keptUntil) =
            await KeepAsync(now, token).ConfigureAwait(false);

        DateTimeOffset? from = keptFrom is { } first ? EpochMinute.ToLocal(first.StartMinute) : null;
        DateTimeOffset? to = keptUntil is { } last ? EpochMinute.ToLocal(last.EndMinute) : null;

        // The live figure is not wanted while history is still catching up in
        // batches; it is read once collection has reached the present.
        if (more)
        {
            detail = state == AppUsageState.Collecting ? LeftOutDetail() : detail;
            Publish(_latest with { State = state, Detail = detail, CollectedUntil = until }, from, to);
            return true;
        }

        (IReadOnlyDictionary<string, AppUsageRecord> recent, DateTimeOffset? recentStart) = await ReadRecentAsync(now, token).ConfigureAwait(false);

        // Windows rewrites its newest records every few minutes, and a read that
        // lands in the middle of it came back with nothing at all (04:19:20 on
        // 2026-09-15, between two full readings). The previous reading, which
        // says when it began, stands in while it is still recent, rather than
        // every program flashing to a dash for a minute.
        if (recentStart is null
            && _latest.RecentStart is { } previous
            && previous >= EpochMinute.ToLocal(now - (2 * AppUsagePlan.RecentSpan)))
        {
            recent = _latest.Recent;
            recentStart = previous;
        }

        if (state == AppUsageState.Collecting)
        {
            detail = LeftOutDetail();
        }

        Publish(new AppUsageStatus(state, detail, until, recent, recentStart), from, to);
        return false;
    }

    /// <summary>Says which networks Windows is not answering for, when any.</summary>
    private string? LeftOutDetail()
    {
        IReadOnlyList<LeftOutNetwork> leftOut = _reader.LeftOut;

        if (leftOut.Count == 0)
        {
            return null;
        }

        return string.Format(
            System.Globalization.CultureInfo.CurrentCulture,
            "Windows is not answering for {0} since {1}, so what went over it is missing for now. It is filled in when Windows answers again.",
            string.Join(", ", leftOut.Select(network => network.Name)),
            EpochMinute.ToLocal(leftOut.Min(network => network.SinceMinute)).ToString("h:mm tt", System.Globalization.CultureInfo.CurrentCulture));
    }

    private async Task<(bool More, AppUsageState State, string? Detail, DateTimeOffset? Until, UsageWindow? KeptFrom, UsageWindow? KeptUntil)> KeepAsync(long now, CancellationToken token)
    {
        AppUsagePosition position;

        try
        {
            position = _store.ReadAppUsagePosition();
        }
        catch (Exception ex) when (HistoryFailure.IsStorage(ex))
        {
            return Fail(ex, "The history file could not be read, so app usage is not being kept.");
        }

        long cursor = AppUsagePlan.Start(position.Cursor, now, _zone);

        // Where the start moved - a first run, or a gap longer than Windows
        // keeps - there is no run of minute windows behind it to revisit.
        long runStart = position.Cursor == cursor ? position.MinuteRunStart : cursor;
        DateTimeOffset? kept = position.Cursor is { } stored ? EpochMinute.ToLocal(stored) : _latest.CollectedUntil;

        if (!_recorder.IsRecording)
        {
            return Pause(position, cursor, now, kept);
        }

        IReadOnlyList<UsageWindow> recent = AppUsagePlan.Recent(cursor, runStart, now);
        IReadOnlyList<UsageWindow> next = AppUsagePlan.Next(cursor, now, _zone);

        if (recent.Count == 0 && next.Count == 0)
        {
            return (false, AppUsageState.Collecting, null, kept, null, null);
        }

        IReadOnlyList<UsageWindow> windows = [.. recent, .. next];
        IReadOnlyList<AppUsageReading> readings;

        try
        {
            readings = await _reader.ReadAsync(windows, token).ConfigureAwait(false);
        }
        catch (AppUsageUnavailableException ex)
        {
            return Fail(ex, ex.Message);
        }

        // Recording may have been switched off while Windows was being asked.
        if (!_recorder.IsRecording)
        {
            return Pause(position, cursor, now, kept);
        }

        // A network Windows refused is missing from what this read brought, and
        // from everything since the first read without it. Once every network
        // answers, collection goes back there and reads it all again, replacing
        // what was kept; erased and paused time stays out (see PassOver).
        IReadOnlyList<LeftOutNetwork> leftOut = _reader.LeftOut;
        long? missing = position.MissingSince;

        if (leftOut.Count > 0)
        {
            long since = leftOut.Min(network => network.SinceMinute);
            missing = missing is { } earlier ? Math.Min(earlier, since) : since;
        }
        else if (missing is { } from)
        {
            if (Reread(from, position, now) is { } reread)
            {
                return reread;
            }

            missing = null;
        }

        List<AppUsageSlice> slices = [];

        foreach (AppUsageReading reading in readings)
        {
            foreach (AppUsageRecord record in reading.Records)
            {
                slices.Add(new AppUsageSlice(
                    reading.Window.StartMinute,
                    reading.Window.Minutes,
                    record.AttributionId,
                    record.BytesReceived,
                    record.BytesSent));
            }
        }

        long reached = next.Count > 0 ? next[^1].EndMinute : cursor;
        long run = runStart;

        foreach (UsageWindow window in next)
        {
            if (window.Minutes > 1)
            {
                run = window.EndMinute;
            }
        }

        try
        {
            _store.WriteAppUsage(windows, slices, new AppUsagePosition(reached, run, missing));
        }
        catch (Exception ex) when (HistoryFailure.IsStorage(ex))
        {
            return Fail(ex, "The history file could not be written, so app usage is not being kept.");
        }

        if (_failing)
        {
            _failing = false;
            LogRecovered();
        }

        return (next.Count >= AppUsagePlan.Batch, AppUsageState.Collecting, null, EpochMinute.ToLocal(reached), windows[0], windows[^1]);
    }

    /// <summary>
    /// Moves collection back to where a network went missing, once it answers,
    /// or gives up on that stretch after it has been read again enough times
    /// without the network staying.
    /// </summary>
    /// <returns>The pass's result when collection was moved back; null to carry on.</returns>
    private (bool, AppUsageState, string?, DateTimeOffset?, UsageWindow?, UsageWindow?)? Reread(long from, AppUsagePosition position, long now)
    {
        // Nothing kept yet goes without it.
        if (position.Cursor is not { } stored || from >= stored)
        {
            return null;
        }

        if (_rereadFrom == from)
        {
            _rereads++;
        }
        else
        {
            _rereadFrom = from;
            _rereads = 1;
        }

        // The network answers new reads but refuses this stretch each time it
        // is read again: what it holds there cannot be had.
        if (_rereads > MostRereads)
        {
            DateTimeOffset givenUp = EpochMinute.ToLocal(from);
            LogGaveUp(givenUp);
            _rereadFrom = null;
            return null;
        }

        long back = Math.Max(from, AppUsagePlan.Start(null, now, _zone));

        try
        {
            _store.WriteAppUsage([], [], new AppUsagePosition(back, back));
        }
        catch (Exception ex) when (HistoryFailure.IsStorage(ex))
        {
            return Fail(ex, "The history file could not be written, so app usage is not being kept.");
        }

        DateTimeOffset rereading = EpochMinute.ToLocal(back);
        LogRereading(rereading);
        return (true, AppUsageState.Collecting, null, rereading, null, null);
    }

    private (bool, AppUsageState, string?, DateTimeOffset?, UsageWindow?, UsageWindow?) Pause(AppUsagePosition position, long cursor, long now, DateTimeOffset? kept)
    {
        long safeEnd = AppUsagePlan.SafeEnd(now);

        // From where collection had actually got to: the planned start can be
        // earlier, at the top of the hour, and those minutes were recorded.
        long from = Math.Max(cursor, position.Cursor ?? cursor);

        try
        {
            if (from < safeEnd)
            {
                _store.PassOver(from, new AppUsagePosition(safeEnd, safeEnd, position.MissingSince));
            }
        }
        catch (Exception ex) when (HistoryFailure.IsStorage(ex))
        {
            return Fail(ex, "The history file could not be written, so app usage is not being kept.");
        }

        return (false, AppUsageState.Paused, "History recording is off, so app usage is not being kept.", kept, null, null);
    }

    private (bool, AppUsageState, string?, DateTimeOffset?, UsageWindow?, UsageWindow?) Fail(Exception error, string detail)
    {
        if (!_failing)
        {
            _failing = true;
            LogFailed(error);
        }

        return (false, AppUsageState.Unavailable, detail, _latest.CollectedUntil, null, null);
    }

    /// <summary>
    /// Reads the last few minutes as one window. Nothing about it is kept; a
    /// failure leaves the figure unknown rather than zero.
    /// </summary>
    private async Task<(IReadOnlyDictionary<string, AppUsageRecord>, DateTimeOffset?)> ReadRecentAsync(long now, CancellationToken token)
    {
        UsageWindow window = AppUsagePlan.RecentWindow(now);

        try
        {
            IReadOnlyList<AppUsageReading> readings = await _reader.ReadAsync([window], token).ConfigureAwait(false);
            Dictionary<string, AppUsageRecord> recent = new(StringComparer.Ordinal);

            foreach (AppUsageReading reading in readings)
            {
                foreach (AppUsageRecord record in reading.Records)
                {
                    recent[record.AttributionId] = record;
                }
            }

            // A machine on a network moves something every few minutes. Nothing
            // at all in the stretch is far likelier to be Windows not having
            // written it yet than every app idle, so it is not shown as zero.
            return recent.Count == 0
                ? (AppUsageStatus.Empty.Recent, null)
                : (recent, EpochMinute.ToLocal(window.StartMinute));
        }
        catch (AppUsageUnavailableException)
        {
            return (AppUsageStatus.Empty.Recent, null);
        }
    }

    private void Publish(AppUsageStatus status, DateTimeOffset? keptFrom, DateTimeOffset? keptUntil)
    {
        AppUsageStatus previous;

        lock (_gate)
        {
            previous = _latest;
            _latest = status;
        }

        AppUsageChanges changes = AppUsageChanges.None;

        if (previous.State != status.State || !string.Equals(previous.Detail, status.Detail, StringComparison.Ordinal))
        {
            changes |= AppUsageChanges.State;
        }

        if (keptFrom is not null)
        {
            changes |= AppUsageChanges.Kept;
        }

        if (!ReferenceEquals(previous.Recent, status.Recent) || previous.RecentStart != status.RecentStart)
        {
            changes |= AppUsageChanges.Recent;
        }

        if (changes != AppUsageChanges.None)
        {
            Changed?.Invoke(this, new AppUsageChangedEventArgs(changes, keptFrom, keptUntil));
        }
    }

    private TimeSpan UntilNextPass()
    {
        DateTimeOffset now = _clock.GetUtcNow();
        DateTimeOffset minute = EpochMinute.ToUtc(EpochMinute.From(now));
        DateTimeOffset next = minute + TimeSpan.FromMinutes(1) + IntoMinute;

        if (next - now > TimeSpan.FromMinutes(1))
        {
            next -= TimeSpan.FromMinutes(1);
        }

        return next > now ? next - now : IntoMinute;
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "App usage collection started")]
    private partial void LogStarted();

    [LoggerMessage(Level = LogLevel.Warning, Message = "App usage could not be collected; trying again each minute")]
    private partial void LogFailed(Exception error);

    [LoggerMessage(Level = LogLevel.Information, Message = "App usage collection recovered")]
    private partial void LogRecovered();

    [LoggerMessage(Level = LogLevel.Information, Message = "Every network answers again; reading app usage again from {From}")]
    private partial void LogRereading(DateTimeOffset from);

    [LoggerMessage(Level = LogLevel.Warning, Message = "A network kept refusing app usage from {From} however often it was read again; that stretch goes without it")]
    private partial void LogGaveUp(DateTimeOffset from);
}
