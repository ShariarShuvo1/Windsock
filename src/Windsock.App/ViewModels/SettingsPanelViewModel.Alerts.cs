using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Windsock.Core.Alerts;
using Windsock.Core.History;
using Windsock.Core.Settings;

namespace Windsock.App.ViewModels;

/// <summary>
/// The alerts' part of Data and privacy: forwarding, the one thing alerts send
/// off this PC, listed with the rest; and the record of alerts going with the
/// history when everything is deleted.
/// </summary>
public sealed partial class SettingsPanelViewModel
{
    private AlertDesk? _alerts;
    private bool _readingForwarding;

    /// <summary>Where alerts are forwarded, in a sentence, or that they are not.</summary>
    [ObservableProperty]
    public partial string ForwardingNote { get; private set; } = string.Empty;

    /// <summary>
    /// Whether alerts are forwarded: the same setting as the switch on the Alerts
    /// page. Off can always be chosen here; on only once a destination is set up
    /// there.
    /// </summary>
    [ObservableProperty]
    public partial bool ForwardsAlerts { get; set; }

    /// <summary>Whether a destination is set up, so forwarding can be switched on from here.</summary>
    [ObservableProperty]
    public partial bool CanForwardAlerts { get; private set; }

    /// <summary>Asks for the Alerts page, where forwarding is set up; the view turns there.</summary>
    public event EventHandler? AlertsPageRequested;

    private void AttachAlerts(AlertDesk alerts)
    {
        _alerts = alerts;
        _alerts.Changed += OnAlertsChanged;
        ReadForwarding();
    }

    private void DetachAlerts()
    {
        if (_alerts is not null)
        {
            _alerts.Changed -= OnAlertsChanged;
        }
    }

    /// <summary>The record of alerts, the networks remembered and what was told, deleted with the history.</summary>
    private void ForgetAlerts()
    {
        if (_alerts is null)
        {
            return;
        }

        try
        {
            _alerts.Log.Clear();
            _alerts.Log.ForgetPrints();
            _alerts.Log.ForgetTold();
        }
        catch (Exception ex) when (HistoryFailure.IsStorage(ex))
        {
            LogEraseFailed(ex);
        }
    }

    private void OnAlertsChanged(object? sender, EventArgs e)
    {
        if (_dispatcher.CheckAccess())
        {
            ReadForwarding();
        }
        else
        {
            _dispatcher.BeginInvoke(new Action(ReadForwarding));
        }
    }

    private void ReadForwarding()
    {
        if (_alerts is null || _disposed)
        {
            return;
        }

        ForwardSettings forward = _settings.Alerts.Forward;
        bool usable = ForwardTarget.Problem(forward) is null;

        _readingForwarding = true;
        CanForwardAlerts = usable;
        ForwardsAlerts = forward.Enabled && usable;
        _readingForwarding = false;

        ForwardingNote = ForwardsAlerts
            ? string.Concat("Each alert is sent to ", ForwardTarget.Describe(forward), " as it is raised: its kind, title, message and time, nothing else.")
            : usable
                ? string.Concat("Set up for ", ForwardTarget.Describe(forward), ", and switched off. Nothing is sent.")
                : "Not set up. Nothing is sent. Set it up at the foot of the Alerts page.";
    }

    partial void OnForwardsAlertsChanged(bool value)
    {
        if (_readingForwarding || _alerts is null)
        {
            return;
        }

        ForwardSettings was = _settings.Alerts.Forward;

        _alerts.SaveForwarding(new ForwardSettings
        {
            Enabled = value,
            To = was.To,
            NtfyServer = was.NtfyServer,
            NtfyTopic = was.NtfyTopic,
            NtfyToken = was.NtfyToken,
            WebhookUrl = was.WebhookUrl,
            SenderName = was.SenderName,
        });

        ReadForwarding();
    }

    [RelayCommand]
    private void OpenAlertsPage() => AlertsPageRequested?.Invoke(this, EventArgs.Empty);
}
