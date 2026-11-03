using System.Windows.Threading;
using Microsoft.Extensions.Logging;
using Windows.UI.Notifications;
using Windsock.Core;
using Windsock.Core.Alerts;
using Windsock.Core.Notifications;
using Windsock.Core.Settings;
using Windsock.Core.Startup;

namespace Windsock.App.Services.Notifications;

/// <summary>How a notification reaches the person.</summary>
public enum NotificationRoute
{
    /// <summary>Nothing has been tried yet.</summary>
    None,

    /// <summary>Windows' own notifications.</summary>
    Windows,

    /// <summary>A balloon from the notification-area icon.</summary>
    Balloon,
}

/// <summary>Whether notifications will reach the person, and if not, why.</summary>
public enum NotificationHealth
{
    /// <summary>They will.</summary>
    Ready,

    /// <summary>Switched off in Windsock.</summary>
    SwitchedOff,

    /// <summary>Windows has them off for Windsock.</summary>
    BlockedForWindsock,

    /// <summary>Windows has them off for every app.</summary>
    BlockedForEveryone,

    /// <summary>A policy has them off.</summary>
    BlockedByOrganisation,

    /// <summary>They will, as balloons, because Windows' own could not be used.</summary>
    Fallback,

    /// <summary>Windows could not be asked.</summary>
    Unknown,
}

/// <summary>The section a clicked notification asked for, if any.</summary>
public sealed class NoticeOpenedEventArgs(string? section) : EventArgs
{
    public string? Section { get; } = section;
}

/// <summary>
/// Sends notifications, and says plainly whether they will arrive.
/// </summary>
/// <remarks>
/// <para>
/// Windows' own notifications are the way, with one exception it imposes:
/// Windows does not deliver them from a process running as administrator, and
/// it does not say so - the call succeeds and nothing appears. So an elevated,
/// unpackaged Windsock uses the notification-area balloon instead, which Windows
/// does show, and says that it is doing so. The balloon is also the fallback if
/// Windows refuses the notification for any other reason.
/// </para>
/// <para>
/// It is never used to get round a choice somebody made. If Windows has
/// notifications turned off for Windsock, Windsock sends nothing and says where
/// to turn them back on.
/// </para>
/// <para>
/// Every notice passes through the <see cref="AlertDesk"/> on its way out: flood
/// control, quiet hours and the snooze, the sound chosen for its kind, the
/// record of alerts, and forwarding. So a feature still just calls
/// <see cref="Send"/>, and everything sent is recorded the same way.
/// </para>
/// </remarks>
public sealed partial class NotificationService : IDisposable, INoticeDisplay
{
    private readonly WindsockSettings _settings;
    private readonly TrayIcon _tray;
    private readonly Dispatcher _dispatcher;
    private readonly ILogger<NotificationService> _logger;
    private readonly AlertDesk _desk;

    private ToastChannel? _toasts;
    private bool _prepared;
    private bool _elevated;
    private bool _toastsRefused;
    private string? _balloonOpens;
    private bool _started;
    private bool _disposed;

    public NotificationService(
        WindsockSettings settings,
        TrayIcon tray,
        Dispatcher dispatcher,
        AlertDesk desk,
        ILogger<NotificationService> logger)
    {
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(tray);
        ArgumentNullException.ThrowIfNull(desk);

        _settings = settings;
        _tray = tray;
        _dispatcher = dispatcher;
        _desk = desk;
        _logger = logger;

        // Attached at once rather than at Start: a notice can be sent before
        // the app gets round to starting this, and it must still be recorded.
        _desk.Attach(this);
    }

    /// <summary>Raised on the UI thread when <see cref="Health"/> or <see cref="Route"/> changes.</summary>
    public event EventHandler? Changed;

    /// <summary>Raised on the UI thread when somebody clicks a notification.</summary>
    public event EventHandler<NoticeOpenedEventArgs>? Opened;

    /// <summary>How notifications are being delivered.</summary>
    public NotificationRoute Route { get; private set; }

    /// <summary>Whether they will arrive.</summary>
    public NotificationHealth Health { get; private set; } = NotificationHealth.Unknown;

    /// <summary>
    /// What to tell the person about <see cref="Health"/>, in a sentence. Empty
    /// when everything is working and there is nothing to say.
    /// </summary>
    public string Detail { get; private set; } = string.Empty;

    /// <summary>Whether the fix, if there is one, is in Windows' own settings.</summary>
    public bool IsBlockedInWindows =>
        Health is NotificationHealth.BlockedForWindsock or NotificationHealth.BlockedForEveryone;

    /// <summary>
    /// Gets ready to send. Called once at startup, early: an unpackaged Windsock
    /// started by a click on an old notification has to be listening for that
    /// click before Windows gives up on it.
    /// </summary>
    public void Start()
    {
        if (_started || _disposed)
        {
            return;
        }

        _started = true;
        _elevated = Elevation.IsElevated();
        _tray.AnnouncementClicked += OnBalloonClicked;

        if (_settings.Notifications.Enabled)
        {
            Prepare();
        }

        Assess();
    }

    /// <summary>
    /// Asks Windows again. Its setting can change at any time from its own
    /// Settings app, so the Settings card calls this whenever it is shown.
    /// </summary>
    public void Refresh()
    {
        if (_disposed)
        {
            return;
        }

        if (_settings.Notifications.Enabled)
        {
            Prepare();
        }

        Assess();
    }

    /// <inheritdoc />
    string INoticeDisplay.WhyNotShown => Health == NotificationHealth.Ready || Health == NotificationHealth.Fallback ? string.Empty : Detail;

    /// <summary>
    /// Sends a notification through the alert desk: kept in the record, then
    /// shown, held for the end of quiet hours, or held back when the same one
    /// just went out or too many have lately.
    /// </summary>
    /// <returns>
    /// Whether it was shown, or will be when quiet hours end. Called from
    /// another thread, it is sent on the UI thread and the answer is true -
    /// only that it was queued - so nothing may be decided on it there. The
    /// one pairing that needs to know, "Back online" after a told "lost", the
    /// desk makes itself.
    /// </returns>
    public bool Send(Notice notice)
    {
        ArgumentNullException.ThrowIfNull(notice);

        if (!_dispatcher.CheckAccess())
        {
            _dispatcher.BeginInvoke(new Action(() => Send(notice)));
            return true;
        }

        if (_disposed)
        {
            return false;
        }

        AlertRoute route = _desk.Route(notice);

        if (route == AlertRoute.Repeat)
        {
            LogHeldBack(notice.Topic);
        }

        return route is AlertRoute.Shown or AlertRoute.Held;
    }

    /// <inheritdoc />
    /// <remarks>For the desk only: everything else sends through <see cref="Send"/>.</remarks>
    bool INoticeDisplay.Show(Notice notice)
    {
        ArgumentNullException.ThrowIfNull(notice);

        if (!_dispatcher.CheckAccess())
        {
            return _dispatcher.Invoke(() => ((INoticeDisplay)this).Show(notice));
        }

        return !_disposed && _settings.Notifications.Enabled && Deliver(notice);
    }

    /// <inheritdoc />
    void INoticeDisplay.Post(Action action) => _dispatcher.BeginInvoke(action);

    /// <summary>
    /// Sends the test notification from the Alerts page, which skips flood control:
    /// somebody pressing the button twice wants to see it twice.
    /// </summary>
    public bool SendTest()
    {
        if (_disposed || !_settings.Notifications.Enabled)
        {
            return false;
        }

        Notice test = new(
            NoticeTopics.Test,
            "Notifications are working",
            "This is how Windsock will let you know when something needs your attention.")
        {
            Tag = NoticeTopics.Test,
            Opens = "alerts",
        };

        return Deliver(test);
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _tray.AnnouncementClicked -= OnBalloonClicked;

        if (_toasts is not null)
        {
            _toasts.Clicked -= OnToastClicked;
            _toasts.Dispose();
            _toasts = null;
        }
    }

    private void Prepare()
    {
        if (_prepared)
        {
            return;
        }

        _prepared = true;

        if (_elevated && !PackageIdentity.Exists)
        {
            Route = NotificationRoute.Balloon;
            LogElevated();
            return;
        }

        _toasts = ToastChannel.Open(_logger);

        if (_toasts is null)
        {
            _toastsRefused = true;
            Route = NotificationRoute.Balloon;
            return;
        }

        _toasts.Clicked += OnToastClicked;
        Route = NotificationRoute.Windows;
    }

    private bool Deliver(Notice notice)
    {
        Prepare();

        bool sound = _settings.Notifications.Sound;

        if (Route == NotificationRoute.Windows && _toasts is not null)
        {
            Assess();

            if (Health is NotificationHealth.BlockedForWindsock
                or NotificationHealth.BlockedForEveryone
                or NotificationHealth.BlockedByOrganisation)
            {
                return false;
            }

            if (_toasts.TryShow(notice, sound))
            {
                return true;
            }

            _toastsRefused = true;
            Route = NotificationRoute.Balloon;
            Assess();
        }

        _balloonOpens = notice.Opens;
        return _tray.Announce(notice.Title, notice.Message, ToastXml.Audible(notice, sound));
    }

    private void Assess()
    {
        (NotificationHealth health, string detail) = Judge();

        if (health == Health && string.Equals(detail, Detail, StringComparison.Ordinal))
        {
            return;
        }

        Health = health;
        Detail = detail;
        Changed?.Invoke(this, EventArgs.Empty);
    }

    private (NotificationHealth Health, string Detail) Judge()
    {
        if (!_settings.Notifications.Enabled)
        {
            return (NotificationHealth.SwitchedOff, "Windsock will not send notifications.");
        }

        if (Route == NotificationRoute.Balloon)
        {
            if (_toastsRefused && WindsockPaths.IsOverridden && !PackageIdentity.Exists)
            {
                return (NotificationHealth.Fallback,
                    "This copy keeps its data in a separate folder, so it does not register with Windows for notifications. They appear as a balloon from its notification-area icon instead.");
            }

            return _elevated && !_toastsRefused
                ? (NotificationHealth.Fallback,
                    "Windsock is running as administrator, and Windows does not show notifications from apps running that way. Until Windsock is started normally, they appear as a balloon from its notification-area icon.")
                : (NotificationHealth.Fallback,
                    "Windows would not accept notifications from Windsock, so they appear as a balloon from its notification-area icon instead.");
        }

        return _toasts?.Setting switch
        {
            NotificationSetting.Enabled => (NotificationHealth.Ready, string.Empty),
            NotificationSetting.DisabledForApplication => (NotificationHealth.BlockedForWindsock,
                "Windows has notifications turned off for Windsock. Turn them on in Windows notification settings."),
            NotificationSetting.DisabledForUser => (NotificationHealth.BlockedForEveryone,
                "Notifications are turned off for every app in Windows. Turn them on in Windows notification settings."),
            NotificationSetting.DisabledByGroupPolicy => (NotificationHealth.BlockedByOrganisation,
                "Your organisation has turned notifications off on this PC."),
            _ => (NotificationHealth.Unknown, "Windows did not say whether it will show notifications from Windsock."),
        };
    }

    private void OnToastClicked(object? sender, string? launch) =>
        _dispatcher.BeginInvoke(new Action(() => Opened?.Invoke(this, new NoticeOpenedEventArgs(NoticeLaunch.Section(launch)))));

    private void OnBalloonClicked(object? sender, EventArgs e) =>
        Opened?.Invoke(this, new NoticeOpenedEventArgs(_balloonOpens));

    [LoggerMessage(Level = LogLevel.Information, Message = "A {Topic} notification was held back: too many lately, or the same one just went out")]
    private partial void LogHeldBack(string topic);

    [LoggerMessage(Level = LogLevel.Information, Message = "Running as administrator, so notifications go out as notification-area balloons")]
    private partial void LogElevated();
}
