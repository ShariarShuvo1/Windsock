using System.Windows.Threading;
using Windsock.Core.Alerts;
using Windsock.Core.Notifications;
using Windsock.Core.Settings;

namespace Windsock.App.Services.Alerts;

/// <summary>
/// Tells the person when a newer Windsock can be installed - which the hint in
/// Settings always promised and, until 0.2.0, only a line in Settings did.
/// </summary>
/// <remarks>
/// <para>
/// The full edition only: the Store edition is updated by the Store. Each
/// version is told once - the alert's cooldown, kept in the record, spans
/// restarts - whether it was found by the check at start-up, by Check now, or
/// by the daily look this adds for a Windsock left running for days. That look
/// is the same request to GitHub's releases page the start-up check makes, and
/// is made only while the automatic check is switched on.
/// </para>
/// <para>
/// One found in the first minute is told once the minute is over, like every
/// other alert.
/// </para>
/// </remarks>
public sealed class UpdateAlerts : IAlertSource
{
    /// <summary>How often a Windsock left running looks again.</summary>
    public static readonly TimeSpan LookEvery = TimeSpan.FromDays(1);

    private readonly UpdateService _updates;
    private readonly WindsockSettings _settings;
    private readonly INoticeSink _notices;
    private readonly AlertSettling _settling;
    private readonly Dispatcher _dispatcher;
    private readonly TimeProvider _clock;
    private readonly Lock _gate = new();
    private ITimer? _daily;
    private string? _told;

    public UpdateAlerts(
        UpdateService updates,
        WindsockSettings settings,
        INoticeSink notices,
        AlertSettling settling,
        Dispatcher dispatcher,
        TimeProvider clock)
    {
        ArgumentNullException.ThrowIfNull(updates);
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(notices);
        ArgumentNullException.ThrowIfNull(settling);
        ArgumentNullException.ThrowIfNull(dispatcher);
        ArgumentNullException.ThrowIfNull(clock);

        _updates = updates;
        _settings = settings;
        _notices = notices;
        _settling = settling;
        _dispatcher = dispatcher;
        _clock = clock;
        _updates.Changed += OnChanged;
    }

    /// <inheritdoc />
    public void Apply()
    {
        lock (_gate)
        {
            bool wanted = _settings.CheckForUpdates && _updates.CanUpdate;

            if (wanted && _daily is null)
            {
                _daily = _clock.CreateTimer(_ => _dispatcher.BeginInvoke(new Action(Look)), null, LookEvery, LookEvery);
            }
            else if (!wanted && _daily is not null)
            {
                _daily.Dispose();
                _daily = null;
            }
        }
    }

    public void Dispose()
    {
        _updates.Changed -= OnChanged;

        lock (_gate)
        {
            _daily?.Dispose();
            _daily = null;
        }
    }

    private void Look()
    {
        if (_settings.CheckForUpdates && _updates.CanUpdate)
        {
            _ = _updates.CheckAsync(quietly: true);
        }
    }

    private void OnChanged(object? sender, EventArgs e)
    {
        if (_updates.Stage is not (UpdateStage.Available or UpdateStage.Ready)
            || _updates.NewVersion is not { Length: > 0 } version
            || string.Equals(version, _told, StringComparison.Ordinal)
            || !_settings.Alerts.UpdateReady)
        {
            return;
        }

        _told = version;
        Notice notice = AlertNotices.Update(version, _updates.Stage == UpdateStage.Ready);
        TimeSpan wait = _settling.Began + AlertSettling.Grace - _clock.GetUtcNow();

        if (wait <= TimeSpan.Zero)
        {
            _notices.Send(notice);
            return;
        }

        _ = SendLaterAsync(notice, wait);
    }

    private async Task SendLaterAsync(Notice notice, TimeSpan wait)
    {
        await Task.Delay(wait, _clock).ConfigureAwait(false);

        if (_settings.Alerts.UpdateReady)
        {
            _notices.Send(notice);
        }
    }
}
