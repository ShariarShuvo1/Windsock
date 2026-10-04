using Windsock.App.Services.Notifications;
using Windsock.Core.Connectivity;
using Windsock.Core.Settings;

namespace Windsock.App.Services.Connectivity;

/// <summary>
/// Keeps the connection watched in the background when that is switched on, and
/// turns what it finds into notifications.
/// </summary>
public sealed class ConnectionAlerts : IDisposable
{
    private readonly ConnectionMonitor _monitor;
    private readonly NotificationService _notices;
    private readonly WindsockSettings _settings;
    private readonly Lock _gate = new();

    private IDisposable? _watch;
    private bool _toldLost;
    private bool _disposed;

    public ConnectionAlerts(ConnectionMonitor monitor, NotificationService notices, WindsockSettings settings)
    {
        ArgumentNullException.ThrowIfNull(monitor);

        _monitor = monitor;
        _notices = notices;
        _settings = settings;

        _monitor.Happened += OnHappened;
    }

    /// <summary>Starts or stops watching in the background, as the setting says.</summary>
    public void Apply()
    {
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            bool wanted = _settings.Connection.WatchInBackground;

            if (wanted && _watch is null)
            {
                _watch = _monitor.Watch();
            }
            else if (!wanted && _watch is not null)
            {
                _watch.Dispose();
                _watch = null;
            }
        }
    }

    private void OnHappened(object? sender, ConnectionEvent happened)
    {
        ConnectionSettings choices = _settings.Connection;

        switch (happened)
        {
            case OutageBegan began when choices.NotifyLostAndRestored:
                _toldLost = _notices.Send(ConnectionNotices.Lost(began.Outage)) || _toldLost;
                break;

            case OutageEnded ended:
                bool told = _toldLost;
                _toldLost = false;

                // After a sleep nobody knows when it came back, so nothing is claimed.
                if (told && choices.NotifyLostAndRestored && !ended.Outage.Interrupted)
                {
                    _notices.Send(ConnectionNotices.Restored(ended.Outage));
                }

                break;

            case NetworkChanged change when choices.NotifyNetworkChanges:
                _notices.Send(ConnectionNotices.NetworkSwitched(change));
                break;

            case VpnChanged change when choices.NotifyNetworkChanges:
                _notices.Send(ConnectionNotices.VpnSwitched(change));
                break;
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            _monitor.Happened -= OnHappened;
            _watch?.Dispose();
            _watch = null;
        }
    }
}
