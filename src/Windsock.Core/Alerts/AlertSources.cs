using Microsoft.Extensions.Logging;
using Windsock.Core.Connectivity;
using Windsock.Core.Hardware;
using Windsock.Core.Networking;
using Windsock.Core.Notifications;
using Windsock.Core.Plans;
using Windsock.Core.Settings;

namespace Windsock.Core.Alerts;

/// <summary>
/// Something that raises alerts. It watches only while its alerts are on:
/// <see cref="Apply"/> starts or stops it to match the settings, at start-up
/// and whenever the Alerts page changes them.
/// </summary>
public interface IAlertSource : IDisposable
{
    /// <summary>Starts or stops watching to match the settings.</summary>
    void Apply();
}

/// <summary>The screen locking and unlocking, and the PC waking, as Windows reports them.</summary>
public interface ISessionEvents
{
    /// <summary>Raised when this session's screen locks.</summary>
    event EventHandler? Locked;

    /// <summary>Raised when this session's screen unlocks.</summary>
    event EventHandler? Unlocked;

    /// <summary>Raised when the PC wakes from sleep.</summary>
    event EventHandler? Resumed;
}

/// <summary>Response time or loss above a level for a while, from the connection checks.</summary>
/// <remarks>
/// Rides on the checks the connection watching already makes and never asks for
/// more: with watching in the background switched off, it works only while a
/// Connection page is open, like the other connection alerts.
/// </remarks>
public sealed class PingAlerts : IAlertSource
{
    private readonly ConnectionMonitor _monitor;
    private readonly WindsockSettings _settings;
    private readonly INoticeSink _notices;
    private readonly AlertSettling _settling;
    private readonly PingWatch _watch = new();
    private readonly Lock _gate = new();
    private bool _listening;

    public PingAlerts(ConnectionMonitor monitor, WindsockSettings settings, INoticeSink notices, AlertSettling settling)
    {
        ArgumentNullException.ThrowIfNull(monitor);
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(notices);
        ArgumentNullException.ThrowIfNull(settling);

        _monitor = monitor;
        _settings = settings;
        _notices = notices;
        _settling = settling;
        _settling.Woke += OnWoke;
    }

    /// <inheritdoc />
    public void Apply()
    {
        lock (_gate)
        {
            bool wanted = _settings.Alerts.Ping.Enabled;

            if (wanted == _listening)
            {
                return;
            }

            _listening = wanted;

            if (wanted)
            {
                _monitor.Sampled += OnSampled;
            }
            else
            {
                _monitor.Sampled -= OnSampled;
                _watch.Reset();
            }
        }
    }

    public void Dispose()
    {
        _settling.Woke -= OnWoke;
        _monitor.Sampled -= OnSampled;
    }

    private void OnSampled(object? sender, ConnectionSample sample)
    {
        if (_settling.IsSettling)
        {
            return;
        }

        ConnectionSettings connection = _settings.Connection;
        // A router counts only once it has answered on this network: one that
        // drops every ping is not losing them, and with no internet address
        // chosen - the default - it would otherwise read as 100% loss.
        LatencyPoint point = new(
            sample.At,
            sample.Router?.TotalMilliseconds,
            sample.Internet?.TotalMilliseconds,
            sample.RouterChecked && (sample.Router is not null || _monitor.RouterAnswers),
            sample.InternetChecked,
            _monitor.State.IsOffline);

        PingAlarm? alarm;

        lock (_gate)
        {
            alarm = _watch.Observe(point, _settings.Alerts.Ping, TrackerRules.For(connection.CheckEvery, connection.OutageAfter).GapLimit);
        }

        if (alarm is not null)
        {
            _notices.Send(AlertNotices.Ping(alarm));
        }
    }

    private void OnWoke(object? sender, EventArgs e)
    {
        lock (_gate)
        {
            _watch.Reset();
        }
    }
}

/// <summary>A slow download and an unusual upload, from the meter's own readings.</summary>
/// <remarks>The meter reads the counters every second whatever happens, so these cost a comparison a second.</remarks>
public sealed class SpeedAlerts : IAlertSource
{
    private readonly ThroughputMonitor _monitor;
    private readonly WindsockSettings _settings;
    private readonly INoticeSink _notices;
    private readonly AlertSettling _settling;
    private readonly SpeedDropWatch _slow = new();
    private readonly UploadWatch _upload = new();
    private readonly Lock _gate = new();
    private bool _listening;

    public SpeedAlerts(ThroughputMonitor monitor, WindsockSettings settings, INoticeSink notices, AlertSettling settling)
    {
        ArgumentNullException.ThrowIfNull(monitor);
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(notices);
        ArgumentNullException.ThrowIfNull(settling);

        _monitor = monitor;
        _settings = settings;
        _notices = notices;
        _settling = settling;
        _settling.Woke += OnWoke;
    }

    /// <inheritdoc />
    public void Apply()
    {
        lock (_gate)
        {
            bool wanted = _settings.Alerts.SpeedDrop.Enabled || _settings.Alerts.Upload.Enabled;

            if (wanted == _listening)
            {
                return;
            }

            _listening = wanted;

            if (wanted)
            {
                _monitor.SampleTaken += OnSample;
            }
            else
            {
                _monitor.SampleTaken -= OnSample;
                _slow.Reset();
                _upload.Reset();
            }
        }
    }

    public void Dispose()
    {
        _settling.Woke -= OnWoke;
        _monitor.SampleTaken -= OnSample;
    }

    private void OnSample(object? sender, ThroughputSample sample)
    {
        if (_settling.IsSettling)
        {
            return;
        }

        AlertSettings alerts = _settings.Alerts;
        SpeedDropAlarm? slow;
        UploadAlarm? upload;

        lock (_gate)
        {
            slow = _slow.Observe(sample, alerts.SpeedDrop);
            upload = _upload.Observe(sample, alerts.Upload);
        }

        if (slow is not null)
        {
            _notices.Send(AlertNotices.SpeedDrop(slow, alerts.SpeedDrop.BelowBytesPerSecond, _settings.ChartFamily));
        }

        if (upload is not null)
        {
            _notices.Send(AlertNotices.Upload(upload, _settings.ChartFamily));
        }
    }

    private void OnWoke(object? sender, EventArgs e)
    {
        lock (_gate)
        {
            _slow.Reset();
            _upload.Reset();
        }
    }
}

/// <summary>Temperatures, fullness and wear, from the machine's readings.</summary>
/// <remarks>
/// While one of these is on it asks the machine to be read quietly - every
/// five seconds, about 0.2% of one core - the same quiet reading history
/// already asks for while it records, so with recording on it costs nothing
/// more. With every one of them off it asks for nothing.
/// </remarks>
public sealed class HardwareAlerts : IAlertSource
{
    private readonly SystemMonitor _monitor;
    private readonly WindsockSettings _settings;
    private readonly INoticeSink _notices;
    private readonly AlertSettling _settling;
    private readonly HardwareWatch _watch = new();
    private readonly Lock _gate = new();
    private IDisposable? _reading;

    public HardwareAlerts(SystemMonitor monitor, WindsockSettings settings, INoticeSink notices, AlertSettling settling)
    {
        ArgumentNullException.ThrowIfNull(monitor);
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(notices);
        ArgumentNullException.ThrowIfNull(settling);

        _monitor = monitor;
        _settings = settings;
        _notices = notices;
        _settling = settling;
        _settling.Woke += OnWoke;
    }

    /// <summary>Whether the machine is being read for these alerts.</summary>
    public bool IsReading
    {
        get
        {
            lock (_gate)
            {
                return _reading is not null;
            }
        }
    }

    /// <inheritdoc />
    public void Apply()
    {
        lock (_gate)
        {
            bool wanted = Wanted();

            if (wanted && _reading is null)
            {
                _monitor.Sampled += OnSampled;
                _reading = _monitor.WatchQuietly();
            }
            else if (!wanted && _reading is not null)
            {
                _monitor.Sampled -= OnSampled;
                _reading.Dispose();
                _reading = null;
            }
        }
    }

    public void Dispose()
    {
        _settling.Woke -= OnWoke;
        _monitor.Sampled -= OnSampled;

        lock (_gate)
        {
            _reading?.Dispose();
            _reading = null;
        }
    }

    /// <summary>
    /// Whether anything here needs the machine read: a hardware alert that is on
    /// and has something to read. The processor's temperature needs PawnIO and
    /// administrator rights; without them that alert alone keeps nothing running.
    /// </summary>
    private bool Wanted()
    {
        HardwareAlertSettings rule = _settings.Alerts.Hardware;
        return rule.GraphicsHot || rule.DriveHot || rule.DriveFull || rule.DriveWear || (rule.ProcessorHot && ProcessorReadable());
    }

    /// <summary>Whether the processor's temperature can be read here at all, as far as is known yet.</summary>
#if STORE
    private static bool ProcessorReadable() => false;
#else
    private bool ProcessorReadable() =>
        Startup.Elevation.IsElevated()
        && _monitor.CpuTemperature.State is not (PawnIoState.NotInstalled or PawnIoState.RequiresElevation or PawnIoState.Unavailable);
#endif

    private void OnSampled(object? sender, SystemSnapshot snapshot)
    {
        // The first readings say whether the processor's temperature can be read;
        // when it cannot and nothing else here is on, let the machine rest.
        if (!Wanted())
        {
            Apply();
            return;
        }

        if (_settling.IsSettling)
        {
            return;
        }

        IReadOnlyList<HardwareAlarm> alarms;

        lock (_gate)
        {
            alarms = _watch.Observe(snapshot, _settings.Alerts.Hardware);
        }

        foreach (HardwareAlarm alarm in alarms)
        {
            _notices.Send(AlertNotices.Hardware(alarm, _settings.Temperature));
        }
    }

    private void OnWoke(object? sender, EventArgs e)
    {
        lock (_gate)
        {
            _watch.Reset();
        }
    }
}

/// <summary>A data plan passing one of its levels, as the plan monitor reports it.</summary>
/// <remarks>
/// The monitor raises each level once per cycle and remembers it in the history
/// file, so a crossing dropped here would be lost for good. One raised in the
/// first minute - a level passed while Windsock was closed - is therefore held
/// until the minute is over rather than dropped.
/// </remarks>
public sealed partial class PlanAlerts : IAlertSource
{
    private readonly PlanMonitor _plans;
    private readonly WindsockSettings _settings;
    private readonly INoticeSink _notices;
    private readonly AlertSettling _settling;
    private readonly TimeProvider _clock;
    private readonly ILogger<PlanAlerts> _logger;

    public PlanAlerts(PlanMonitor plans, WindsockSettings settings, INoticeSink notices, AlertSettling settling, TimeProvider clock, ILogger<PlanAlerts> logger)
    {
        ArgumentNullException.ThrowIfNull(plans);
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(notices);
        ArgumentNullException.ThrowIfNull(settling);
        ArgumentNullException.ThrowIfNull(clock);
        ArgumentNullException.ThrowIfNull(logger);

        _plans = plans;
        _settings = settings;
        _notices = notices;
        _settling = settling;
        _clock = clock;
        _logger = logger;
        _plans.LevelCrossed += OnLevelCrossed;
    }

    /// <inheritdoc />
    /// <remarks>Always listening: the crossings are raised once, so the switch is read as each arrives.</remarks>
    public void Apply()
    {
    }

    public void Dispose() => _plans.LevelCrossed -= OnLevelCrossed;

    private void OnLevelCrossed(object? sender, PlanLevelCrossing crossing)
    {
        if (!_settings.Alerts.PlanLevels)
        {
            return;
        }

        Notice notice = AlertNotices.Plan(crossing);
        TimeSpan wait = _settling.Began + AlertSettling.Grace - _clock.GetUtcNow();

        if (wait <= TimeSpan.Zero)
        {
            _notices.Send(notice);
            return;
        }

        LogHeldForSettling(crossing.Level);
        _ = SendLaterAsync(notice, wait);
    }

    private async Task SendLaterAsync(Notice notice, TimeSpan wait)
    {
        await Task.Delay(wait, _clock).ConfigureAwait(false);

        if (_settings.Alerts.PlanLevels)
        {
            _notices.Send(notice);
        }
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "A data plan passed {Level}% in Windsock's first minute; the alert waits for the minute to pass")]
    private partial void LogHeldForSettling(int level);
}

/// <summary>
/// Keeps every network and VPN change in the record as an event, for the charts
/// to mark, whether or not anyone asked to be told.
/// </summary>
public sealed class ConnectionEvents : IAlertSource
{
    private readonly ConnectionMonitor _monitor;
    private readonly AlertDesk _desk;

    public ConnectionEvents(ConnectionMonitor monitor, AlertDesk desk)
    {
        ArgumentNullException.ThrowIfNull(monitor);
        ArgumentNullException.ThrowIfNull(desk);

        _monitor = monitor;
        _desk = desk;
        _monitor.Happened += OnHappened;
    }

    /// <inheritdoc />
    public void Apply()
    {
    }

    public void Dispose() => _monitor.Happened -= OnHappened;

    private void OnHappened(object? sender, ConnectionEvent happened)
    {
        Notice? said = happened switch
        {
            NetworkChanged change => ConnectionNotices.NetworkSwitched(change),
            VpnChanged change => ConnectionNotices.VpnSwitched(change),
            _ => null,
        };

        if (said is not null)
        {
            _desk.Note(said.Topic, said.Title, said.Message, said.Tag);
        }
    }
}

/// <summary>On unlocking the screen, what was used and what happened while it was locked.</summary>
public sealed class AwayAlerts : IAlertSource
{
    private readonly ISessionEvents _session;
    private readonly ThroughputMonitor _throughput;
    private readonly IOutageStore _outages;
    private readonly IAlertLog _log;
    private readonly WindsockSettings _settings;
    private readonly INoticeSink _notices;
    private readonly TimeProvider _clock;
    private readonly AwayTally _tally = new();
    private readonly Lock _gate = new();
    private bool _listening;
    private bool _counting;

    public AwayAlerts(
        ISessionEvents session,
        ThroughputMonitor throughput,
        IOutageStore outages,
        IAlertLog log,
        WindsockSettings settings,
        INoticeSink notices,
        TimeProvider clock)
    {
        ArgumentNullException.ThrowIfNull(session);
        ArgumentNullException.ThrowIfNull(throughput);
        ArgumentNullException.ThrowIfNull(outages);
        ArgumentNullException.ThrowIfNull(log);
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(notices);
        ArgumentNullException.ThrowIfNull(clock);

        _session = session;
        _throughput = throughput;
        _outages = outages;
        _log = log;
        _settings = settings;
        _notices = notices;
        _clock = clock;
    }

    /// <inheritdoc />
    public void Apply()
    {
        lock (_gate)
        {
            bool wanted = _settings.Alerts.Away.Enabled;

            if (wanted == _listening)
            {
                return;
            }

            _listening = wanted;

            if (wanted)
            {
                _session.Locked += OnLocked;
                _session.Unlocked += OnUnlocked;
            }
            else
            {
                _session.Locked -= OnLocked;
                _session.Unlocked -= OnUnlocked;
                StopCounting();
                _tally.Unlock(_clock.GetLocalNow());
            }
        }
    }

    public void Dispose()
    {
        _session.Locked -= OnLocked;
        _session.Unlocked -= OnUnlocked;

        lock (_gate)
        {
            StopCounting();
        }
    }

    private void OnLocked(object? sender, EventArgs e)
    {
        lock (_gate)
        {
            _tally.Lock(_clock.GetLocalNow());

            if (!_counting)
            {
                _counting = true;
                _throughput.DeltaMeasured += OnDelta;
            }
        }
    }

    private void OnUnlocked(object? sender, EventArgs e)
    {
        AwayReport? report;

        lock (_gate)
        {
            StopCounting();
            report = _tally.Unlock(_clock.GetLocalNow());
        }

        if (report is null || report.Duration < _settings.Alerts.Away.Minimum || !_settings.Alerts.Away.Enabled)
        {
            return;
        }

        IReadOnlyList<Outage> outages = Read(() => _outages.Read(report.Since, report.Until), []);
        int alerts = Read(() => _log.Count(new AlertQuery(report.Since, report.Until)), 0);

        _notices.Send(AlertNotices.Away(report, outages, alerts));
    }

    private void OnDelta(object? sender, ThroughputDelta delta) => _tally.Count(delta.BytesReceived, delta.BytesSent);

    private void StopCounting()
    {
        if (_counting)
        {
            _counting = false;
            _throughput.DeltaMeasured -= OnDelta;
        }
    }

    private static T Read<T>(Func<T> read, T fallback)
    {
        try
        {
            return read();
        }
        catch (Exception ex) when (History.HistoryFailure.IsStorage(ex))
        {
            return fallback;
        }
    }
}
