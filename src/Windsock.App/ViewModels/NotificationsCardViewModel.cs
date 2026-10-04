using System.ComponentModel;
using System.Diagnostics;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Windsock.App.Services.Notifications;
using Windsock.Core.Settings;

namespace Windsock.App.ViewModels;

/// <summary>
/// Backs the Notifications card in Settings.
/// </summary>
/// <remarks>
/// Built for two readers. Most people see one switch, a test button, and nothing
/// else while things work. When something is in the way - Windows has
/// notifications off, or Windsock is running as administrator - the card says
/// so in a sentence and offers the one button that fixes it, rather than leaving
/// the switch on and the notifications silently missing.
/// </remarks>
public sealed partial class NotificationsCardViewModel : ObservableObject, IDisposable
{
    private readonly NotificationService _notices;
    private readonly WindsockSettings _settings;
    private readonly ISettingsStore _store;
    private readonly bool _loaded;

    public NotificationsCardViewModel(NotificationService notices, WindsockSettings settings, ISettingsStore store)
    {
        ArgumentNullException.ThrowIfNull(notices);
        ArgumentNullException.ThrowIfNull(settings);

        _notices = notices;
        _settings = settings;
        _store = store;

        IsEnabled = settings.Notifications.Enabled;
        PlaysSound = settings.Notifications.Sound;
        _loaded = true;

        _notices.Changed += OnNoticesChanged;
        Describe();
    }

    /// <summary>Whether Windsock sends notifications at all.</summary>
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(SendTestCommand))]
    public partial bool IsEnabled { get; set; }

    /// <summary>Whether they make a sound.</summary>
    [ObservableProperty]
    public partial bool PlaysSound { get; set; }

    /// <summary>What is in the way, in a sentence, or empty when nothing is.</summary>
    [ObservableProperty]
    public partial string Status { get; private set; } = string.Empty;

    /// <summary>Whether <see cref="Status"/> describes something wrong rather than a choice.</summary>
    [ObservableProperty]
    public partial bool IsWarning { get; private set; }

    /// <summary>How notifications are travelling, for the curious.</summary>
    [ObservableProperty]
    public partial string Delivery { get; private set; } = string.Empty;

    /// <summary>What happened to the last test, or empty before one.</summary>
    [ObservableProperty]
    public partial string TestResult { get; private set; } = string.Empty;

    /// <summary>Asks Windows again, for when the card comes into view.</summary>
    public void Refresh()
    {
        _notices.Refresh();
        Describe();
    }

    public void Dispose() => _notices.Changed -= OnNoticesChanged;

    partial void OnIsEnabledChanged(bool value)
    {
        if (!_loaded)
        {
            return;
        }

        _settings.Notifications.Enabled = value;
        _store.Save(_settings);
        TestResult = string.Empty;
        Refresh();
    }

    partial void OnPlaysSoundChanged(bool value)
    {
        if (!_loaded)
        {
            return;
        }

        _settings.Notifications.Sound = value;
        _store.Save(_settings);
    }

    [RelayCommand(CanExecute = nameof(IsEnabled))]
    private void SendTest()
    {
        bool sent = _notices.SendTest();
        Describe();

        TestResult = sent
            ? "Sent. If nothing appeared, Windows may be holding it back for Do not disturb; it will be waiting in the notification centre."
            : _notices.Detail.Length > 0
                ? string.Concat("Not sent. ", _notices.Detail)
                : "Not sent. Windows did not accept it.";
    }

    [RelayCommand]
    private static void OpenWindowsSettings()
    {
        try
        {
            Process.Start(new ProcessStartInfo("ms-settings:notifications") { UseShellExecute = true });
        }
        catch (Exception error) when (error is Win32Exception or InvalidOperationException)
        {
            // Nothing to open on an edition of Windows without the Settings app.
        }
    }

    private void OnNoticesChanged(object? sender, EventArgs e) => Describe();

    private void Describe()
    {
        NotificationHealth health = _notices.Health;

        Status = health == NotificationHealth.SwitchedOff ? string.Empty : _notices.Detail;
        IsWarning = health is not (NotificationHealth.Ready or NotificationHealth.SwitchedOff or NotificationHealth.Fallback);

        Delivery = _notices.Route switch
        {
            NotificationRoute.Windows => "Sent as Windows notifications, which wait in the notification centre if you miss them.",
            NotificationRoute.Balloon => "Sent as balloons from the notification-area icon, which do not wait in the notification centre.",
            _ => "Sent as Windows notifications.",
        };
    }
}
