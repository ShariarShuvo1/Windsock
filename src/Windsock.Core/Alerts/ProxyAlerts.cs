using System.Text.Json;
using Windsock.Core.History;
using Windsock.Core.Notifications;
using Windsock.Core.Settings;

namespace Windsock.Core.Alerts;

/// <summary>The system proxy changing while Windows' own proxy settings are not in front.</summary>
/// <remarks>
/// <para>
/// The proxy as last seen is kept in the record of alerts for as long as the
/// alert is on, so a proxy set while Windsock was closed - by an installer, at
/// sign-in, by something that ran overnight - is told at the next start, as
/// the network prints are. Switched off, the alert forgets it, so switching it
/// on again only takes the proxy as it stands.
/// </para>
/// <para>
/// A change in the minute after starting or waking is held until the minute is
/// over rather than dropped: a proxy set at sign-in is the very case to tell.
/// Changes inside that minute are told as one, from what it was to what it is.
/// </para>
/// </remarks>
public sealed class ProxyAlerts : IAlertSource
{
    /// <summary>The state key the proxy as last seen is kept under.</summary>
    internal const string StateKey = "proxy_print";

    private readonly ProxyWatch _watch;
    private readonly WindsockSettings _settings;
    private readonly INoticeSink _notices;
    private readonly AlertSettling _settling;
    private readonly IAlertLog _log;
    private readonly TimeProvider _clock;
    private readonly Lock _gate = new();
    private bool _listening;
    private ProxyChange? _waiting;
    private ITimer? _later;

    public ProxyAlerts(ProxyWatch watch, WindsockSettings settings, INoticeSink notices, AlertSettling settling, IAlertLog log, TimeProvider clock)
    {
        ArgumentNullException.ThrowIfNull(watch);
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(notices);
        ArgumentNullException.ThrowIfNull(settling);
        ArgumentNullException.ThrowIfNull(log);
        ArgumentNullException.ThrowIfNull(clock);

        _watch = watch;
        _settings = settings;
        _notices = notices;
        _settling = settling;
        _log = log;
        _clock = clock;
    }

    /// <inheritdoc />
    public void Apply()
    {
        ProxyChange? whileAway = null;

        lock (_gate)
        {
            bool wanted = _settings.Alerts.Changes.Proxy;

            if (wanted == _listening)
            {
                return;
            }

            _listening = wanted;

            if (wanted)
            {
                _watch.Changed += OnChanged;
                _watch.Start();

                ProxyPrint now = _watch.Current;

                if (Remembered() is { } was && ProxyJudge.Differ(was, now))
                {
                    whileAway = new ProxyChange(was, now, ByWindowsSettings: false) { WhileAway = true };
                }

                Remember(now);
            }
            else
            {
                _watch.Changed -= OnChanged;
                _watch.Stop();
                _waiting = null;
                _later?.Dispose();
                _later = null;

                // Off, nothing watches; on again, the proxy as it stands then is the start.
                Write(null);
            }
        }

        if (whileAway is not null)
        {
            Tell(whileAway);
        }
    }

    public void Dispose()
    {
        _watch.Changed -= OnChanged;
        _watch.Stop();

        lock (_gate)
        {
            _later?.Dispose();
            _later = null;
        }
    }

    private void OnChanged(object? sender, ProxyChange change)
    {
        lock (_gate)
        {
            if (!_listening)
            {
                return;
            }

            Remember(change.Now);
        }

        if (!change.ByWindowsSettings)
        {
            Tell(change);
        }
    }

    /// <summary>Tells a change now, or once the minute after starting or waking is over.</summary>
    private void Tell(ProxyChange change)
    {
        TimeSpan wait = _settling.Began + AlertSettling.Grace - _clock.GetUtcNow();

        if (wait <= TimeSpan.Zero)
        {
            if (_settings.Alerts.Changes.Proxy)
            {
                _notices.Send(AlertNotices.Proxy(change));
            }

            return;
        }

        lock (_gate)
        {
            // Several changes inside the minute are one: from the first "was" to the latest "now".
            _waiting = _waiting is { } first
                ? first with { Now = change.Now, WhileAway = first.WhileAway || change.WhileAway }
                : change;

            _later?.Dispose();
            _later = _clock.CreateTimer(_ => TellWaiting(), null, wait, Timeout.InfiniteTimeSpan);
        }
    }

    private void TellWaiting()
    {
        ProxyChange? waiting;

        lock (_gate)
        {
            waiting = _waiting;
            _waiting = null;
        }

        if (waiting is not null && _settings.Alerts.Changes.Proxy && ProxyJudge.Differ(waiting.Was, _watch.Current))
        {
            _notices.Send(AlertNotices.Proxy(waiting with { Now = _watch.Current }));
        }
    }

    private ProxyPrint? Remembered()
    {
        try
        {
            return _log.ReadState(StateKey) is { } text ? JsonSerializer.Deserialize<ProxyPrint>(text) : null;
        }
        catch (Exception ex) when (ex is JsonException || HistoryFailure.IsStorage(ex))
        {
            return null;
        }
    }

    private void Remember(ProxyPrint print) => Write(JsonSerializer.Serialize(print));

    private void Write(string? value)
    {
        try
        {
            _log.WriteState(StateKey, value);
        }
        catch (Exception ex) when (HistoryFailure.IsStorage(ex))
        {
            // Without the record the alert still works while Windsock runs.
        }
    }
}
