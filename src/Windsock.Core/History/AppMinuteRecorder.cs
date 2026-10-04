using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Windsock.Core.Processes;
using Windsock.Core.Settings;

namespace Windsock.Core.History;

/// <summary>
/// Keeps each app's traffic a minute at a time, measured by Windsock itself,
/// while the kernel trace runs.
/// </summary>
/// <remarks>
/// <para>
/// The trace counts every process's bytes for as long as the app runs as
/// administrator, whether or not a page is open, so this adds only a reading
/// every ten seconds and one small write a minute. Readings fall on the clock's
/// ten-second marks, so a minute's edges are an edge between two readings.
/// </para>
/// <para>
/// Nothing is kept while history recording is off, and nothing while the
/// trace is not attributing - unelevated, or the session lost; Windows' own
/// figures stand for that time.
/// </para>
/// </remarks>
public sealed partial class AppMinuteRecorder : BackgroundService
{
    private static readonly TimeSpan Step = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan CompactEvery = TimeSpan.FromHours(6);

    private readonly IProcessTrafficLedger _ledger;
    private readonly IAppDetailStore _store;
    private readonly Func<bool> _recording;
    private readonly WindsockSettings _settings;
    private readonly TimeProvider _clock;
    private readonly ILogger<AppMinuteRecorder> _logger;
    private readonly AppMinuteTally _tally;
    private readonly Dictionary<int, ProcessTraffic> _running = [];
    private readonly List<DepartedTraffic> _departed = [];
    private DateTimeOffset _compactedAt = DateTimeOffset.MinValue;

    public AppMinuteRecorder(
        IProcessTrafficLedger ledger,
        IAppDetailStore store,
        UsageRecorder recorder,
        WindsockSettings settings,
        ILogger<AppMinuteRecorder> logger)
        : this(ledger, store, () => recorder.IsRecording, settings, TimeProvider.System, logger)
    {
        ArgumentNullException.ThrowIfNull(recorder);
    }

    internal AppMinuteRecorder(
        IProcessTrafficLedger ledger,
        IAppDetailStore store,
        Func<bool> recording,
        WindsockSettings settings,
        TimeProvider clock,
        ILogger<AppMinuteRecorder> logger)
    {
        ArgumentNullException.ThrowIfNull(ledger);
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(recording);
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(clock);
        ArgumentNullException.ThrowIfNull(logger);

        _ledger = ledger;
        _store = store;
        _recording = recording;
        _settings = settings;
        _clock = clock;
        _logger = logger;
        _tally = new AppMinuteTally(CountedAs);
    }

    /// <summary>Raised on the recorder's thread when a minute has been kept.</summary>
    public event EventHandler? Recorded;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        LogStarted();

        try
        {
            while (!stoppingToken.IsCancellationRequested)
            {
                await Task.Delay(UntilNextMark(_clock.GetUtcNow()), _clock, stoppingToken).ConfigureAwait(false);
                Tick();
            }
        }
        catch (OperationCanceledException)
        {
            // Shutdown. The open minute was not watched to its end, so it goes.
        }
    }

    /// <summary>One reading, and whatever it finishes. Internal so tests can drive it.</summary>
    internal void Tick()
    {
        DateTimeOffset now = _clock.GetUtcNow();
        _ledger.Take(_running, _departed);

        MeasuredMinute? finished = _tally.Observe(now, _ledger.CanAttribute && _recording(), _running, _departed);

        try
        {
            if (finished is not null)
            {
                _store.WriteMeasuredMinute(finished.Minute, finished.Apps);
                Recorded?.Invoke(this, EventArgs.Empty);
            }

            Compact(now);
        }
        catch (Exception ex) when (HistoryFailure.IsStorage(ex))
        {
            LogStoreFailed(ex);
        }
    }

    private void Compact(DateTimeOffset now)
    {
        int days = _settings.KeepMinuteDetailDays;

        if (days <= 0 || now - _compactedAt < CompactEvery)
        {
            return;
        }

        _compactedAt = now;
        long before = EpochMinute.From(now) - ((long)days * 24 * 60);

        if (_store.ReadAppDetailCompactedBefore() is { } done && done >= before - 60)
        {
            return;
        }

        int removed = _store.CompactAppDetail(before);
        LogCompacted(removed, days);
    }

    /// <summary>The wait until the clock's next ten-second mark, never less than a second.</summary>
    internal static TimeSpan UntilNextMark(DateTimeOffset now)
    {
        long step = Step.Ticks;
        long wait = step - (now.UtcTicks % step);
        return TimeSpan.FromTicks(wait < TimeSpan.TicksPerSecond ? wait + step : wait);
    }

    private string CountedAs(int processId)
    {
        (string? native, string? family) = ProcessIdentityResolver.ReadCountedAs(processId);
        return AppMinuteTally.CountedAs(processId, family, native, _ledger.ImageOf(processId));
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Per-app minutes are recorded while the trace runs")]
    private partial void LogStarted();

    [LoggerMessage(Level = LogLevel.Warning, Message = "Could not keep a measured minute")]
    private partial void LogStoreFailed(Exception exception);

    [LoggerMessage(Level = LogLevel.Information, Message = "Compacted {Rows} rows of per-app minutes older than {Days} days into quarter hours")]
    private partial void LogCompacted(int rows, int days);
}
