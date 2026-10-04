using System.IO;
using Microsoft.Extensions.Logging;
using Microsoft.Win32;
using Windows.Data.Xml.Dom;
using Windows.UI.Notifications;
using Windsock.Core;
using Windsock.Core.Notifications;

namespace Windsock.App.Services.Notifications;

/// <summary>
/// Windows' own notifications: the pop-up in the corner, kept afterwards in the
/// notification centre.
/// </summary>
/// <remarks>
/// A packaged Windsock is already known to Windows by its package. An unpackaged
/// one registers itself under the current user first (see
/// <see cref="NotificationRegistration"/>) and serves the click callback, so the
/// same notices work from the installer, the MSI and the portable zip.
/// </remarks>
internal sealed partial class ToastChannel : IDisposable
{
    private const string Group = "windsock";

    private readonly ToastNotifier _notifier;
    private readonly ILogger _logger;
    private NoticeActivator? _activator;

    private ToastChannel(ToastNotifier notifier, ILogger logger)
    {
        _notifier = notifier;
        _logger = logger;
    }

    /// <summary>Raised with the launch text of a notice that was clicked.</summary>
    public event EventHandler<string?>? Clicked;

    /// <summary>
    /// Whether clicks on notices in the notification centre will reach Windsock.
    /// Always true when packaged; for an unpackaged copy, whether Windows took
    /// the click class.
    /// </summary>
    public bool HearsOlderClicks { get; private set; }

    /// <summary>
    /// Opens the channel, or nothing when Windows will not have it.
    /// </summary>
    public static ToastChannel? Open(ILogger logger)
    {
        ArgumentNullException.ThrowIfNull(logger);

        try
        {
            if (PackageIdentity.Exists)
            {
                return new ToastChannel(ToastNotificationManager.CreateToastNotifier(), logger)
                {
                    HearsOlderClicks = true,
                };
            }

            string program = Environment.ProcessPath
                ?? throw new InvalidOperationException("The path of the running program is unknown.");

            using (RegistryKey classes = Registry.CurrentUser.CreateSubKey(@"Software\Classes", writable: true))
            {
                NotificationRegistration.Write(classes, program, Icon());
            }

            ToastChannel channel = new(ToastNotificationManager.CreateToastNotifier(NotificationRegistration.AppId), logger);
            channel._activator = NoticeActivator.Register(channel.OnClassActivated);
            channel.HearsOlderClicks = channel._activator is not null;

            if (!channel.HearsOlderClicks)
            {
                LogNoActivator(logger);
            }

            return channel;
        }
#pragma warning disable CA1031 // Registry, WinRT activation and COM each fail in their own
        // exception types, and every one of them means the same thing: no Windows notifications.
        catch (Exception error)
#pragma warning restore CA1031
        {
            LogOpenFailed(logger, error);
            return null;
        }
    }

    /// <summary>
    /// What Windows currently allows: on, or turned off and by whom. Nothing when
    /// Windows could not say.
    /// </summary>
    public NotificationSetting? Setting
    {
        get
        {
            try
            {
                return _notifier.Setting;
            }
#pragma warning disable CA1031 // Asking can fail in any of the ways activation can.
            catch (Exception error)
#pragma warning restore CA1031
            {
                LogSettingUnreadable(_logger, error);
                return null;
            }
        }
    }

    /// <summary>
    /// Hands a notice to Windows. False when Windows refused it, which is the
    /// cue to try another way.
    /// </summary>
    public bool TryShow(Notice notice, bool sound)
    {
        ArgumentNullException.ThrowIfNull(notice);

        try
        {
            XmlDocument document = new();
            document.LoadXml(ToastXml.Build(notice, sound));

            ToastNotification toast = new(document)
            {
                Group = Group,
                ExpirationTime = DateTimeOffset.Now.AddDays(1),
            };

            if (notice.Tag is { Length: > 0 } tag)
            {
                // Windows caps a tag at 64 characters and throws past it.
                toast.Tag = tag.Length > 64 ? tag[..64] : tag;
            }

            toast.Activated += OnToastActivated;
            toast.Failed += OnToastFailed;
            _notifier.Show(toast);

            return true;
        }
#pragma warning disable CA1031 // As above: any failure here is "Windows would not show it".
        catch (Exception error)
#pragma warning restore CA1031
        {
            LogShowFailed(_logger, error, notice.Topic);
            return false;
        }
    }

    public void Dispose()
    {
        _activator?.Dispose();
        _activator = null;
    }

    /// <summary>
    /// Removes what an unpackaged copy registered. Called by the uninstaller,
    /// before anything else has started, so it takes nothing from the host.
    /// </summary>
    public static void Unregister()
    {
        if (PackageIdentity.Exists)
        {
            return;
        }

        try
        {
            using RegistryKey classes = Registry.CurrentUser.CreateSubKey(@"Software\Classes", writable: true);
            NotificationRegistration.Remove(classes);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or System.Security.SecurityException)
        {
            // Uninstalling carries on regardless; a stale key names a program that is gone.
        }
    }

    private void OnToastActivated(ToastNotification sender, object args) =>
        Clicked?.Invoke(this, (args as ToastActivatedEventArgs)?.Arguments);

    private void OnToastFailed(ToastNotification sender, ToastFailedEventArgs args) =>
        LogDeliveryFailed(_logger, args.ErrorCode);

    private void OnClassActivated(string? launch) => Clicked?.Invoke(this, launch);

    /// <summary>The picture Windows puts in the notification's header, where it is shipped.</summary>
    private static string? Icon()
    {
        string path = Path.Combine(AppContext.BaseDirectory, "Assets", "Notification.png");
        return File.Exists(path) ? path : null;
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "Windows notifications are unavailable")]
    private static partial void LogOpenFailed(ILogger logger, Exception error);

    [LoggerMessage(Level = LogLevel.Information, Message = "Notifications will show, but clicks from the notification centre will not reach Windsock")]
    private static partial void LogNoActivator(ILogger logger);

    [LoggerMessage(Level = LogLevel.Information, Message = "Could not read whether Windows allows notifications")]
    private static partial void LogSettingUnreadable(ILogger logger, Exception error);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Windows would not show a {Topic} notification")]
    private static partial void LogShowFailed(ILogger logger, Exception error, string topic);

    [LoggerMessage(Level = LogLevel.Warning, Message = "A notification failed after it was handed to Windows")]
    private static partial void LogDeliveryFailed(ILogger logger, Exception error);
}
