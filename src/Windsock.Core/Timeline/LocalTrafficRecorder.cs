using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Windsock.Core.History;

namespace Windsock.Core.Timeline;

/// <summary>
/// Keeps each recorded minute's local-network share, measured by the kernel
/// trace, so plans, forecasts and reports can leave a NAS copy out.
/// </summary>
/// <remarks>
/// <para>
/// Interface counters cannot tell a copy from the NAS from a download: both
/// cross the same adapter. The trace sees each packet's far end, so it can.
/// Readings fall on the clock's ten-second marks like each app's minutes, and
/// a minute's share is handed to history through
/// <see cref="ILocalTrafficStore.WriteLocalTraffic"/>, the one way in that
/// store offers. The minute's own row is written when the next minute begins,
/// so a share history does not take yet is offered again for a few minutes.
/// </para>
/// <para>
/// The share is payload, the minute's figure is the adapter's (headers
/// included), so a minute's internet part is slightly overstated, never
/// understated. Nothing is kept while history recording is off or the trace
/// is not attributing.
/// </para>
/// </remarks>
public sealed partial class LocalTrafficRecorder : BackgroundService
{
    private static readonly TimeSpan Step = TimeSpan.FromSeconds(10);

    /// <summary>How many minutes a share waits for its minute's row before it is let go.</summary>
    private const int Patience = 10;

    private readonly ITrafficSplitSource _split;
    private readonly ILocalTrafficStore _store;
    private readonly Func<bool> _recording;
    private readonly TimeProvider _clock;
    private readonly ILogger<LocalTrafficRecorder> _logger;
    private readonly WriteFailureLog _failures = new();
    private readonly LocalMinuteTally _tally = new();
    private readonly List<LocalMinute> _waiting = [];

    public LocalTrafficRecorder(ITrafficSplitSource split, ILocalTrafficStore store, UsageRecorder recorder, ILogger<LocalTrafficRecorder> logger)
        : this(split, store, () => recorder.IsRecording, TimeProvider.System, logger)
    {
        ArgumentNullException.ThrowIfNull(recorder);
    }

    internal LocalTrafficRecorder(ITrafficSplitSource split, ILocalTrafficStore store, Func<bool> recording, TimeProvider clock, ILogger<LocalTrafficRecorder> logger)
    {
        ArgumentNullException.ThrowIfNull(split);
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(recording);
        ArgumentNullException.ThrowIfNull(clock);
        ArgumentNullException.ThrowIfNull(logger);

        _split = split;
        _store = store;
        _recording = recording;
        _clock = clock;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            while (!stoppingToken.IsCancellationRequested)
            {
                await Task.Delay(AppMinuteRecorder.UntilNextMark(_clock.GetUtcNow()), _clock, stoppingToken).ConfigureAwait(false);
                Tick();
            }
        }
        catch (OperationCanceledException)
        {
        }
    }

    /// <summary>One reading, and whatever it finishes. Internal so tests can drive it.</summary>
    internal void Tick()
    {
        DateTimeOffset now = _clock.GetUtcNow();
        bool measuring = _split.CanAttribute && _recording();

        if (_tally.Observe(now, measuring, _split.ReadSplit()) is { } finished)
        {
            _waiting.Add(finished);
        }

        if (_waiting.Count == 0)
        {
            return;
        }

        try
        {
            HashSet<long> taken = [.. _store.WriteLocalTraffic(_waiting)];
            long oldest = EpochMinute.From(now) - Patience;
            _waiting.RemoveAll(each => taken.Contains(each.Minute) || each.Minute < oldest);
        }
        catch (Exception ex) when (HistoryFailure.IsStorage(ex))
        {
            if (_failures.ShouldLog(ex))
            {
                LogStoreFailed(ex);
            }

            _waiting.Clear();
        }
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "Could not keep the local share of a minute")]
    private partial void LogStoreFailed(Exception exception);
}
