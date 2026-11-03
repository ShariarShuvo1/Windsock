using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Windsock.Core.Adapters;
using Windsock.Core.History;
using Windsock.Core.Settings;

namespace Windsock.Core.SpeedTests;

/// <summary>
/// Runs speed tests by themselves, only when the person switched the schedule
/// on, at random times averaging the number a day they chose, never more than
/// four in any 24 hours.
/// </summary>
/// <remarks>
/// <para>
/// A test is skipped, not delayed, when the internet is down, when another test
/// or a Wi-Fi scan is running, and - unless the person allowed it - when the
/// connection may be metered: a test moves a few hundred megabytes. "May be"
/// means the adapter carrying traffic or the physical one under a VPN is
/// metered, or Windows has not said either way; a VPN's own adapter reads as
/// unmetered over a phone's hotspot.
/// </para>
/// <para>
/// The 24-hour cap counts every automatic run noted in Outages.db's
/// <c>scheduled_run</c> table, which deleting tests does not touch, as well as
/// the automatic tests kept; and it counts runs stamped later than now, so
/// setting the clock back frees no slots.
/// </para>
/// </remarks>
public sealed partial class SpeedTestScheduler : BackgroundService
{
    private static readonly TimeSpan Idle = TimeSpan.FromMinutes(10);

    private readonly WindsockSettings _settings;
    private readonly SpeedTestService _tests;
    private readonly ISpeedTestStore _store;
    private readonly AdapterMonitor _adapters;
    private readonly TimeProvider _time;
    private readonly ILogger<SpeedTestScheduler> _logger;
    private readonly SemaphoreSlim _nudge = new(0, 1);

    public SpeedTestScheduler(
        WindsockSettings settings,
        SpeedTestService tests,
        ISpeedTestStore store,
        AdapterMonitor adapters,
        TimeProvider time,
        ILogger<SpeedTestScheduler> logger)
    {
        _settings = settings;
        _tests = tests;
        _store = store;
        _adapters = adapters;
        _time = time;
        _logger = logger;
    }

    /// <summary>When the next automatic test is due, or nothing while the schedule is off.</summary>
    public DateTimeOffset? NextRunAt { get; private set; }

    /// <summary>Raised when <see cref="NextRunAt"/> changes.</summary>
    public event EventHandler? Rescheduled;

    /// <summary>Plans again after the schedule's settings changed.</summary>
    public void Reschedule()
    {
        _nudge.Signal();
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await Task.Yield();

        try
        {
            while (!stoppingToken.IsCancellationRequested)
            {
                if (!Wanted())
                {
                    SetNext(null);
                    await _nudge.WaitAsync(Idle, stoppingToken).ConfigureAwait(false);
                    continue;
                }

                TimeSpan wait = SpeedSchedule.NextWait(_settings.ConnectionTools.TestsADay, Random.Shared.NextDouble());
                SetNext(_time.GetLocalNow() + wait);

                if (await _nudge.WaitAsync(wait, stoppingToken).ConfigureAwait(false))
                {
                    // The settings changed: plan afresh.
                    continue;
                }

                await TryRunAsync(stoppingToken).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
        }
    }

    private bool Wanted() =>
        _settings.ConnectionTools.ScheduledSpeedTests
        && _tests.CanRun() is not (SpeedTestRefusal.NotAgreed or SpeedTestRefusal.NoServer);

    private async Task TryRunAsync(CancellationToken stoppingToken)
    {
        if (!Wanted() || _tests.CanRun() != SpeedTestRefusal.None)
        {
            LogSkipped("not possible now");
            return;
        }

        DateTimeOffset now = _time.GetLocalNow();

        if (!_settings.ConnectionTools.ScheduledTestsOnMetered)
        {
            AdapterReading reading = await _adapters.ReadNowAsync().ConfigureAwait(false);

            if (SpeedSchedule.MayBeMetered(reading.Snapshot))
            {
                LogSkipped("the connection is, or may be, metered");
                return;
            }
        }

        try
        {
            // Everything from a day back, future-stamped runs included.
            IEnumerable<DateTimeOffset> kept = _store.ReadSpeedTests(now - SpeedSchedule.Day, DateTimeOffset.MaxValue)
                .Where(each => each.Scheduled)
                .Select(each => each.At);
            IReadOnlyList<DateTimeOffset> noted = _store.ReadScheduledRuns(now - SpeedSchedule.Day);
            int cap = _settings.ConnectionTools.TestsADay;

            if (!SpeedSchedule.MayRun(kept, cap, now) || !SpeedSchedule.MayRun(noted, cap, now))
            {
                LogSkipped("the day's automatic tests have run");
                return;
            }

            // Noted before it runs, where deleting tests cannot reach: an attempt counts.
            _store.NoteScheduledRun(now);
        }
        catch (Exception ex) when (HistoryFailure.IsStorage(ex))
        {
            // Without the log the cap cannot be checked, so nothing runs.
            LogSkipped("the connection log cannot be read");
            return;
        }

        try
        {
            await _tests.RunAsync(scheduled: true, stoppingToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (IsNetworkFailure(ex))
        {
            // The test answers its own failures; a surprise from the network
            // must not end the schedule, nor the host it runs in.
            LogFailed(ex);
        }
    }

    private static bool IsNetworkFailure(Exception ex) =>
        ex is HttpRequestException or IOException or System.Net.Sockets.SocketException or System.Net.WebSockets.WebSocketException or TimeoutException;

    private void SetNext(DateTimeOffset? next)
    {
        if (NextRunAt != next)
        {
            NextRunAt = next;
            Rescheduled?.Invoke(this, EventArgs.Empty);
        }
    }

    public override void Dispose()
    {
        base.Dispose();
        _nudge.Dispose();
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "An automatic speed test was skipped: {Reason}")]
    private partial void LogSkipped(string reason);

    [LoggerMessage(Level = LogLevel.Warning, Message = "An automatic speed test failed")]
    private partial void LogFailed(Exception error);
}
