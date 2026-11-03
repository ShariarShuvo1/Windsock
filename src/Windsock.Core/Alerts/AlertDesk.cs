using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;
using Windsock.Core.History;
using Windsock.Core.Notifications;
using Windsock.Core.Settings;

namespace Windsock.Core.Alerts;

/// <summary>Where the desk shows a notice: the app's notification service.</summary>
public interface INoticeDisplay
{
    /// <summary>Shows a notice now, skipping every rule; false when Windows would not show it.</summary>
    bool Show(Notice notice);

    /// <summary>Why notices are not being shown, in a sentence; empty while they are.</summary>
    string WhyNotShown { get; }

    /// <summary>Runs something on the thread notices are shown from.</summary>
    void Post(Action action);
}

/// <summary>Sends alerts on to the webhook or ntfy topic the person set up.</summary>
public interface IAlertForwarder
{
    /// <summary>Whether forwarding is set up to somewhere it can send.</summary>
    bool IsReady { get; }

    /// <summary>
    /// Hands over an alert already kept in the record. The forwarder sends it in
    /// the background and notes in the record whether it went.
    /// </summary>
    void Forward(AlertRecord record);
}

/// <summary>What the desk did with one alert.</summary>
public enum AlertRoute
{
    /// <summary>Shown now.</summary>
    Shown,

    /// <summary>Held for the end of quiet hours or the snooze.</summary>
    Held,

    /// <summary>Kept in the record but not shown: notifications are off, Windows refused it, or too many went out at once.</summary>
    NotShown,

    /// <summary>Dropped: the same alert went out a moment ago, or within its kind's cooldown.</summary>
    Repeat,
}

/// <summary>
/// Every alert passes through here on its way to the screen: flood control,
/// quiet hours and the snooze, the person's sound for its kind, the record,
/// and forwarding.
/// </summary>
/// <remarks>
/// <para>
/// The notification service hands every notice it is asked to send to
/// <see cref="Route"/>, so features keep calling <c>Send</c> as before and
/// every alert - the connection's included - is recorded the same way.
/// </para>
/// <para>
/// Held alerts are deferred, never lost: they are kept with the outcome
/// <see cref="AlertOutcome.Held"/>, so they survive a restart, and when quiet
/// hours or the snooze end they are shown together as one summary.
/// </para>
/// <para>
/// Used from one thread, the one <see cref="INoticeDisplay.Post"/> runs on;
/// the notification service marshals there before calling.
/// </para>
/// </remarks>
public sealed partial class AlertDesk : IDisposable
{
    /// <summary>The state key the snooze is kept under, so it survives a restart.</summary>
    internal const string SnoozeKey = "snoozed_until";

    /// <summary>The state key forwarding is kept under, out of the settings file.</summary>
    internal const string ForwardKey = "forwarding";

    private static readonly JsonSerializerOptions ForwardFormat = new()
    {
        Converters = { new JsonStringEnumConverter() },
    };

    /// <summary>How soon after starting the alerts a previous run held are looked at.</summary>
    private static readonly TimeSpan StartDelay = TimeSpan.FromSeconds(15);

    /// <summary>How often the record is pruned of what it no longer keeps.</summary>
    private static readonly TimeSpan PruneEvery = TimeSpan.FromHours(6);

    private readonly WindsockSettings _settings;
    private readonly IAlertLog _log;
    private readonly IAlertForwarder _forwarder;
    private readonly TimeProvider _clock;
    private readonly ILogger<AlertDesk> _logger;
    private readonly NoticePacer _pacer;
    private readonly Lock _gate = new();

    private INoticeDisplay? _display;
    private ITimer? _wake;
    private AlertRoute? _lostRoute;
    private DateTimeOffset? _snoozedUntil;
    private DateTimeOffset _pruned;
    private int _held;
    private bool _logFailed;
    private bool _disposed;

    public AlertDesk(
        WindsockSettings settings,
        IAlertLog log,
        IAlertForwarder forwarder,
        TimeProvider clock,
        ILogger<AlertDesk> logger)
    {
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(log);
        ArgumentNullException.ThrowIfNull(forwarder);
        ArgumentNullException.ThrowIfNull(clock);
        ArgumentNullException.ThrowIfNull(logger);

        _settings = settings;
        _log = log;
        _forwarder = forwarder;
        _clock = clock;
        _logger = logger;
        _pacer = new NoticePacer(clock);
    }

    /// <summary>Raised when the snooze, quiet hours or the number held changes.</summary>
    public event EventHandler? Changed;

    /// <summary>The record every alert is kept in.</summary>
    public IAlertLog Log => _log;

    /// <summary>When the snooze ends, or nothing when there is none.</summary>
    public DateTimeOffset? SnoozedUntil
    {
        get
        {
            lock (_gate)
            {
                return _snoozedUntil is { } until && until > _clock.GetUtcNow() ? until : null;
            }
        }
    }

    /// <summary>When alerts stop being held - quiet hours or the snooze - or nothing while they are shown.</summary>
    public DateTimeOffset? HeldUntil => QuietHours.HeldUntil(_settings.Alerts.Quiet, SnoozedUntil, _clock.GetLocalNow(), _clock.LocalTimeZone);

    /// <summary>How many alerts are waiting for quiet hours or the snooze to end.</summary>
    public int HeldCount
    {
        get
        {
            lock (_gate)
            {
                return _held;
            }
        }
    }

    /// <summary>
    /// Connects the desk to the screen and looks at what a previous run held,
    /// a little after starting so the window is not greeted by a summary.
    /// </summary>
    public void Attach(INoticeDisplay display)
    {
        ArgumentNullException.ThrowIfNull(display);

        lock (_gate)
        {
            if (_disposed || _display is not null)
            {
                return;
            }

            _display = display;
        }

        if (Try(() => _log.ReadState(SnoozeKey), null) is { } stored
            && DateTimeOffset.TryParse(stored, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out DateTimeOffset until))
        {
            lock (_gate)
            {
                _snoozedUntil = until;
            }
        }

        LoadForwarding();
        Try(() => _log.AbandonForwards("Windsock closed before it was sent."), 0);
        Prune();
        CountHeld();
        WakeIn(StartDelay);
    }

    /// <summary>
    /// Keeps where alerts are forwarded, in the record beside the settings file
    /// rather than in it, and uses it from now on.
    /// </summary>
    /// <returns>Whether it was kept; false when the record could not be written, though it is used this run.</returns>
    public bool SaveForwarding(ForwardSettings forward)
    {
        ArgumentNullException.ThrowIfNull(forward);

        _settings.Alerts.Forward = forward;

        bool kept = false;
        Try(() =>
        {
            _log.WriteState(ForwardKey, JsonSerializer.Serialize(forward, ForwardFormat));
            kept = true;
        });

        Changed?.Invoke(this, EventArgs.Empty);
        return kept;
    }

    private void LoadForwarding()
    {
        if (Try(() => _log.ReadState(ForwardKey), null) is not { } stored)
        {
            return;
        }

        try
        {
            if (JsonSerializer.Deserialize<ForwardSettings>(stored, ForwardFormat) is { } forward)
            {
                _settings.Alerts.Forward = forward;
            }
        }
        catch (JsonException)
        {
            // Unreadable, it is as if forwarding were never set up.
        }
    }

    /// <summary>
    /// Takes one alert: drops it, holds it, or shows it, and keeps it in the
    /// record either way unless it was a repeat.
    /// </summary>
    /// <returns>What happened to it.</returns>
    public AlertRoute Route(Notice notice)
    {
        ArgumentNullException.ThrowIfNull(notice);

        DateTimeOffset now = _clock.GetLocalNow();
        AlertSettings alerts = _settings.Alerts;
        AlertChoice choice = alerts.ChoiceFor(notice.Topic);
        Notice shaped = choice.Sound == NoticeSound.Default ? notice : notice with { Sound = choice.Sound };

        if (now - _pruned >= PruneEvery)
        {
            Prune();
        }

        TimeSpan cooldown = AlertCatalogue.Find(notice.Topic)?.Cooldown ?? TimeSpan.Zero;

        if (cooldown > TimeSpan.Zero
            && Try(() => _log.LastTold(notice.Topic, notice.Tag), null) is { } last
            && now - last < cooldown)
        {
            LogRepeat(notice.Topic);
            return AlertRoute.Repeat;
        }

        // "Back online" only follows a "lost" that was shown or held: alone, it
        // says the internet came back from an outage nobody was told of.
        if (string.Equals(notice.Topic, NoticeTopics.ConnectionRestored, StringComparison.Ordinal))
        {
            AlertRoute? lost = _lostRoute;
            _lostRoute = null;

            if (lost is null or AlertRoute.Repeat)
            {
                LogUnpaired();
                return AlertRoute.Repeat;
            }

            if (lost == AlertRoute.NotShown)
            {
                Keep(shaped, now, AlertOutcome.NotShown, "Not shown, as the notice that it was lost was not.", forward: false);
                return AlertRoute.NotShown;
            }
        }

        AlertRoute route = Decide(notice, shaped, choice, alerts, now);

        if (string.Equals(notice.Topic, NoticeTopics.ConnectionLost, StringComparison.Ordinal))
        {
            _lostRoute = route;
        }

        if (cooldown > TimeSpan.Zero && route != AlertRoute.Repeat)
        {
            Try(() => _log.NoteTold(notice.Topic, notice.Tag, now));
        }

        return route;
    }

    /// <summary>Flood control, quiet hours, then the screen, for one alert past its cooldown and pairing.</summary>
    private AlertRoute Decide(Notice notice, Notice shaped, AlertChoice choice, AlertSettings alerts, DateTimeOffset now)
    {
        bool holding = !choice.BreaksQuiet && QuietHours.HeldUntil(alerts.Quiet, SnoozedUntil, now, _clock.LocalTimeZone) is not null;

        switch (_pacer.Pace(shaped, holding))
        {
            case PaceVerdict.Repeat:
                LogRepeat(notice.Topic);
                return AlertRoute.Repeat;

            case PaceVerdict.Burst:
                Keep(shaped, now, AlertOutcome.NotShown, "Held back: too many alerts at once.", forward: false);
                return AlertRoute.NotShown;
        }

        if (holding)
        {
            Keep(shaped, now, AlertOutcome.Held, string.Empty, forward: true);

            lock (_gate)
            {
                _held++;
            }

            Changed?.Invoke(this, EventArgs.Empty);
            Reschedule();
            return AlertRoute.Held;
        }

        if (!_settings.Notifications.Enabled || _display is null)
        {
            Keep(shaped, now, AlertOutcome.NotShown, "Notifications were off.", forward: true);
            return AlertRoute.NotShown;
        }

        bool shown = _display.Show(shaped);
        string why = shown ? string.Empty : Why(_display);

        Keep(shaped, now, shown ? AlertOutcome.Shown : AlertOutcome.NotShown, why, forward: true, shownAt: shown ? now : null);
        return shown ? AlertRoute.Shown : AlertRoute.NotShown;
    }

    /// <summary>
    /// Keeps something that happened, for the charts to mark, without telling
    /// anyone: a network change nobody asked to be told about.
    /// </summary>
    public void Note(string topic, string title, string message, string? tag = null)
    {
        Try(
            () => _log.Add(new AlertRecord(0, _clock.GetLocalNow(), topic, title, message)
            {
                Tag = tag,
                Outcome = AlertOutcome.Event,
                IsRead = true,
            }),
            0L);
    }

    /// <summary>Holds alerts for a while from now.</summary>
    public void Snooze(TimeSpan length)
    {
        if (length <= TimeSpan.Zero)
        {
            EndSnooze();
            return;
        }

        SnoozeUntil(_clock.GetLocalNow() + length);
    }

    /// <summary>Holds alerts until the morning (see <see cref="QuietHours.Morning"/>).</summary>
    public void SnoozeUntilMorning() =>
        SnoozeUntil(QuietHours.Morning(_settings.Alerts.Quiet, _clock.GetLocalNow(), _clock.LocalTimeZone));

    /// <summary>Holds alerts until a moment.</summary>
    public void SnoozeUntil(DateTimeOffset until)
    {
        lock (_gate)
        {
            _snoozedUntil = until;
        }

        Try(() => _log.WriteState(SnoozeKey, until.ToString("O", CultureInfo.InvariantCulture)));
        Changed?.Invoke(this, EventArgs.Empty);
        Reschedule();
    }

    /// <summary>Ends the snooze, and shows what it held unless quiet hours still hold it.</summary>
    public void EndSnooze()
    {
        lock (_gate)
        {
            _snoozedUntil = null;
        }

        Try(() => _log.WriteState(SnoozeKey, null));
        Changed?.Invoke(this, EventArgs.Empty);
        Release();
    }

    /// <summary>
    /// Looks again after quiet hours were changed: shows what was held if they
    /// no longer hold it, and wakes at their new end otherwise.
    /// </summary>
    public void Refresh()
    {
        Changed?.Invoke(this, EventArgs.Empty);
        Release();
    }

    /// <summary>
    /// Shows what was held, as one summary, unless quiet hours or the snooze
    /// still hold it; in that case wakes again when they end.
    /// </summary>
    public void Release()
    {
        if (_disposed || _display is not { } display)
        {
            return;
        }

        if (HeldUntil is not null)
        {
            // Still held - but the list may have been cleared meanwhile.
            CountHeld();
            Reschedule();
            return;
        }

        ForgetSpentSnooze();

        IReadOnlyList<AlertRecord> held = Try<IReadOnlyList<AlertRecord>>(_log.Held, []);

        if (held.Count == 0)
        {
            SetHeld(0);
            return;
        }

        long[] ids = [.. held.Select(each => each.Id)];
        DateTimeOffset now = _clock.GetLocalNow();

        if (!_settings.Notifications.Enabled)
        {
            Try(() => _log.SetOutcome(ids, AlertOutcome.NotShown, "Notifications were off when the quiet ended.", null));
        }
        else if (QuietSummary.Compose(held, _clock.LocalTimeZone) is { } summary && display.Show(summary))
        {
            Try(() => _log.SetOutcome(ids, AlertOutcome.Summarised, string.Empty, now));
        }
        else
        {
            Try(() => _log.SetOutcome(ids, AlertOutcome.NotShown, Why(display), null));
        }

        SetHeld(0);
    }

    public void Dispose()
    {
        ITimer? wake;

        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            wake = _wake;
            _wake = null;
        }

        wake?.Dispose();
    }

    private static string Why(INoticeDisplay display) =>
        display.WhyNotShown is { Length: > 0 } why ? why : "Windows did not show it.";

    private void Keep(Notice notice, DateTimeOffset at, AlertOutcome outcome, string detail, bool forward, DateTimeOffset? shownAt = null)
    {
        bool forwarding = forward
            && _settings.Alerts.Forward.Enabled
            && _settings.Alerts.ChoiceFor(notice.Topic).Forward
            && _forwarder.IsReady;

        AlertRecord record = new(0, at, notice.Topic, notice.Title, notice.Message)
        {
            Tag = notice.Tag,
            Opens = notice.Opens,
            Outcome = outcome,
            Detail = detail,
            ShownAt = shownAt,
            Forward = forwarding ? ForwardOutcome.Waiting : ForwardOutcome.None,
        };

        long id = Try(() => _log.Add(record), 0L);

        if (forwarding)
        {
            _forwarder.Forward(record with { Id = id });
        }
    }

    private void CountHeld() => SetHeld(Try(() => _log.Held().Count, 0));

    /// <summary>Lets go of a snooze that has run out, so the page stops saying it is snoozed.</summary>
    private void ForgetSpentSnooze()
    {
        lock (_gate)
        {
            if (_snoozedUntil is not { } until || until > _clock.GetUtcNow())
            {
                return;
            }

            _snoozedUntil = null;
        }

        Try(() => _log.WriteState(SnoozeKey, null));
        Changed?.Invoke(this, EventArgs.Empty);
    }

    private void SetHeld(int count)
    {
        bool changed;

        lock (_gate)
        {
            changed = _held != count;
            _held = count;
        }

        if (changed)
        {
            Changed?.Invoke(this, EventArgs.Empty);
        }
    }

    private void Prune()
    {
        DateTimeOffset now = _clock.GetLocalNow();
        _pruned = now;
        Try(() => _log.Prune(now - _settings.Alerts.Keep), 0);
    }

    /// <summary>Wakes when the holding ends, if anything is held.</summary>
    private void Reschedule()
    {
        if (HeldCount == 0 && SnoozedUntil is null)
        {
            return;
        }

        DateTimeOffset now = _clock.GetLocalNow();
        TimeSpan due = HeldUntil is { } until ? until - now : TimeSpan.Zero;

        // Never longer than an hour: a clock change or a sleep can move "when"
        // under a timer, and waking to look again costs nothing.
        WakeIn(TimeSpan.FromTicks(Math.Clamp(due.Ticks, TimeSpan.FromSeconds(1).Ticks, TimeSpan.FromHours(1).Ticks)));
    }

    private void WakeIn(TimeSpan due)
    {
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            _wake?.Dispose();
            _wake = _clock.CreateTimer(_ => _display?.Post(Release), null, due, Timeout.InfiniteTimeSpan);
        }
    }

    private T Try<T>(Func<T> read, T fallback)
    {
        try
        {
            T value = read();
            _logFailed = false;
            return value;
        }
        catch (Exception ex) when (HistoryFailure.IsStorage(ex))
        {
            if (!_logFailed)
            {
                _logFailed = true;
                LogStoreFailed(ex);
            }

            return fallback;
        }
    }

    private void Try(Action write) => Try(
        () =>
        {
            write();
            return 0;
        },
        0);

    [LoggerMessage(Level = LogLevel.Information, Message = "A {Topic} alert was dropped: the same one went out a moment ago, or within its cooldown")]
    private partial void LogRepeat(string topic);

    [LoggerMessage(Level = LogLevel.Information, Message = "A Back online alert was dropped: the outage it ends was not told")]
    private partial void LogUnpaired();

    [LoggerMessage(Level = LogLevel.Warning, Message = "The record of alerts could not be read or written; alerts are still shown but not kept")]
    private partial void LogStoreFailed(Exception error);
}
