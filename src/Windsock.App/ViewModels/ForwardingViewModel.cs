using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Windsock.App.Services.Alerts;
using Windsock.Core.Alerts;
using Windsock.Core.History;
using Windsock.Core.Settings;

namespace Windsock.App.ViewModels;

/// <summary>
/// Where alerts are forwarded, on the Alerts page: the switch, the destination
/// as a draft until saved, a test, and how the last one went.
/// </summary>
/// <remarks>
/// <para>
/// The switch takes effect at once; the destination when Save is pressed, after
/// it is checked. Turning the switch on with nothing saved yet saves the draft,
/// so somebody who fills in a topic and ticks the box is not left wondering.
/// </para>
/// <para>
/// Kept by the alert desk in the record of alerts, never in the settings file
/// (see <see cref="AlertSettings.Forward"/>), so an exported settings file never
/// carries a webhook's secret or an ntfy topic.
/// </para>
/// </remarks>
public sealed partial class ForwardingViewModel : ObservableObject, IDisposable
{
    private readonly AlertDesk _desk;
    private readonly HttpAlertForwarder _forwarder;
    private readonly WindsockSettings _settings;
    private readonly Dispatcher _dispatcher;
    private bool _loaded;
    private bool _pending;
    private bool _disposed;

    public ForwardingViewModel(AlertDesk desk, HttpAlertForwarder forwarder, WindsockSettings settings, Dispatcher dispatcher)
    {
        ArgumentNullException.ThrowIfNull(desk);
        ArgumentNullException.ThrowIfNull(forwarder);
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(dispatcher);

        _desk = desk;
        _forwarder = forwarder;
        _settings = settings;
        _dispatcher = dispatcher;

        Destinations =
        [
            new(ForwardKind.Ntfy, "An ntfy topic"),
            new(ForwardKind.Webhook, "A webhook"),
        ];

        Read();
        _desk.Changed += OnDeskChanged;
        _desk.Log.Changed += OnLogChanged;
    }

    /// <summary>Raised after forwarding was switched or saved, for the kinds' forward choices.</summary>
    public event EventHandler? Saved;

    /// <summary>The kinds of destination on offer.</summary>
    public IReadOnlyList<ListOption<ForwardKind>> Destinations { get; }

    /// <summary>Whether alerts are forwarded.</summary>
    [ObservableProperty]
    public partial bool IsEnabled { get; set; }

    /// <summary>Where they go, in the draft.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsNtfy), nameof(IsWebhook))]
    public partial ForwardKind To { get; set; }

    /// <summary>Whether the draft is for ntfy, which asks for a server and a topic.</summary>
    public bool IsNtfy => To == ForwardKind.Ntfy;

    /// <summary>Whether the draft is for a webhook, which asks for an address.</summary>
    public bool IsWebhook => To == ForwardKind.Webhook;

    /// <summary>The ntfy server, in the draft.</summary>
    [ObservableProperty]
    public partial string NtfyServer { get; set; } = string.Empty;

    /// <summary>The ntfy topic, in the draft. A topic's whole address pasted here is split into server and topic.</summary>
    [ObservableProperty]
    public partial string NtfyTopic { get; set; } = string.Empty;

    /// <summary>The ntfy access token, in the draft; the view keeps it in a password box.</summary>
    public string NtfyToken { get; set; } = string.Empty;

    /// <summary>The webhook's address, in the draft.</summary>
    [ObservableProperty]
    public partial string WebhookUrl { get; set; } = string.Empty;

    /// <summary>The name sent for this PC, in the draft; empty sends none.</summary>
    [ObservableProperty]
    public partial string SenderName { get; set; } = string.Empty;

    /// <summary>A name to suggest for this PC.</summary>
    public string MachineName { get; } = Environment.MachineName;

    /// <summary>Where alerts go now, in words, or why they do not.</summary>
    [ObservableProperty]
    public partial string Status { get; private set; } = string.Empty;

    /// <summary>Where alerts go, in a word or two for the page's figures: "ntfy.sh", or "Off".</summary>
    [ObservableProperty]
    public partial string Destination { get; private set; } = "Off";

    /// <summary>What is wrong with the draft, or what a save or a test found; empty when there is nothing to say.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasProblem))]
    public partial string Problem { get; private set; } = string.Empty;

    /// <summary>Whether <see cref="Problem"/> says something.</summary>
    public bool HasProblem => Problem.Length > 0;

    /// <summary>What the last test or save said, when all went well.</summary>
    [ObservableProperty]
    public partial string Result { get; private set; } = string.Empty;

    /// <summary>The last alert that could not be forwarded, and why; empty when none failed lately.</summary>
    [ObservableProperty]
    public partial string LastFailure { get; private set; } = string.Empty;

    /// <summary>Whether a test is on its way.</summary>
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(TestCommand))]
    public partial bool IsTesting { get; private set; }

    /// <summary>Whether the draft differs from what is saved.</summary>
    [ObservableProperty]
    public partial bool HasChanges { get; private set; }

    /// <summary>The page comes into view: look for the last failure.</summary>
    public void Show() => _ = ReadLastFailureAsync();

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _desk.Changed -= OnDeskChanged;
        _desk.Log.Changed -= OnLogChanged;
    }

    /// <summary>The token typed into the password box.</summary>
    public void SetToken(string token)
    {
        NtfyToken = token ?? string.Empty;
        Compare();
    }

    partial void OnIsEnabledChanged(bool value)
    {
        if (!_loaded)
        {
            return;
        }

        ForwardSettings saved = _settings.Alerts.Forward;

        if (!value)
        {
            Keep(Copy(saved, enabled: false));
            return;
        }

        // Nothing usable saved yet: take the draft, if it is usable.
        ForwardSettings candidate = ForwardTarget.Problem(saved) is null ? Copy(saved, enabled: true) : Draft(enabled: true);

        if (ForwardTarget.Problem(candidate) is { } problem)
        {
            Problem = string.Concat(problem, " Then turn forwarding on.");
            _loaded = false;
            IsEnabled = false;
            _loaded = true;
            return;
        }

        Keep(candidate);
    }

    partial void OnToChanged(ForwardKind value) => Compare();

    partial void OnNtfyServerChanged(string value) => Compare();

    partial void OnWebhookUrlChanged(string value) => Compare();

    partial void OnSenderNameChanged(string value) => Compare();

    partial void OnNtfyTopicChanged(string value)
    {
        // A topic's whole address pasted into the topic box: take it apart.
        if (_loaded && value.Contains("://", StringComparison.Ordinal))
        {
            (string server, string topic) = ForwardTarget.SplitNtfy(value);

            if (topic.Length > 0)
            {
                NtfyServer = server;
                NtfyTopic = topic;
                return;
            }
        }

        Compare();
    }

    /// <summary>Checks the draft and keeps it.</summary>
    [RelayCommand]
    private void Save()
    {
        ForwardSettings draft = Draft(IsEnabled);

        if (ForwardTarget.Problem(draft) is { } problem)
        {
            Problem = problem;
            Result = string.Empty;
            return;
        }

        Keep(draft);
        Result = draft.Enabled
            ? string.Concat("Saved. Alerts go to ", ForwardTarget.Describe(draft), " from now on.")
            : "Saved. Turn forwarding on to start.";
    }

    /// <summary>Puts the draft back to what is saved.</summary>
    [RelayCommand]
    private void Revert()
    {
        Read();
        Problem = string.Empty;
        Result = string.Empty;
    }

    private bool CanTest => !IsTesting;

    /// <summary>Sends a test alert to the destination in the draft, saved or not.</summary>
    [RelayCommand(CanExecute = nameof(CanTest))]
    private async Task TestAsync()
    {
        ForwardSettings draft = Draft(enabled: true);

        if (ForwardTarget.Problem(draft) is { } problem)
        {
            Problem = problem;
            Result = string.Empty;
            return;
        }

        IsTesting = true;
        Problem = string.Empty;
        Result = string.Concat("Sending a test to ", ForwardTarget.Describe(draft), "…");

        try
        {
            string? failed = await _forwarder.TestAsync(draft).ConfigureAwait(true);

            if (failed is null)
            {
                Result = string.Concat("Sent to ", ForwardTarget.Describe(draft), ". Check that it arrived.");
            }
            else
            {
                Result = string.Empty;
                Problem = failed;
            }
        }
        finally
        {
            IsTesting = false;
        }
    }

    private void Keep(ForwardSettings forward)
    {
        bool kept = _desk.SaveForwarding(forward);
        Problem = kept ? string.Empty : "Could not be kept for the next start, because the record of alerts could not be written. It is used until Windsock closes.";
        Read();
        Saved?.Invoke(this, EventArgs.Empty);
    }

    private ForwardSettings Draft(bool enabled) => new()
    {
        Enabled = enabled,
        To = To,
        NtfyServer = string.IsNullOrWhiteSpace(NtfyServer) ? ForwardSettings.PublicNtfy : NtfyServer.Trim(),
        NtfyTopic = NtfyTopic.Trim(),
        NtfyToken = NtfyToken.Trim(),
        WebhookUrl = WebhookUrl.Trim(),
        SenderName = SenderName.Trim(),
    };

    private static ForwardSettings Copy(ForwardSettings saved, bool enabled) => new()
    {
        Enabled = enabled,
        To = saved.To,
        NtfyServer = saved.NtfyServer,
        NtfyTopic = saved.NtfyTopic,
        NtfyToken = saved.NtfyToken,
        WebhookUrl = saved.WebhookUrl,
        SenderName = saved.SenderName,
    };

    private void Read()
    {
        _loaded = false;

        ForwardSettings saved = _settings.Alerts.Forward;
        IsEnabled = saved.Enabled;
        To = saved.To;
        NtfyServer = saved.NtfyServer;
        NtfyTopic = saved.NtfyTopic;
        NtfyToken = saved.NtfyToken;
        WebhookUrl = saved.WebhookUrl;
        SenderName = saved.SenderName;
        HasChanges = false;

        // Not bound - the view keeps it in a password box - so the view is told.
        OnPropertyChanged(nameof(NtfyToken));

        bool forwarding = saved.Enabled && ForwardTarget.Problem(saved) is null;
        Destination = forwarding ? ForwardTarget.Describe(saved) : "Off";
        Status = forwarding
            ? string.Concat("Each alert goes to ", ForwardTarget.Describe(saved), " as it is raised, quiet hours or not.")
            : "Off. Nothing is sent anywhere.";

        _loaded = true;
    }

    private void Compare()
    {
        if (!_loaded)
        {
            return;
        }

        ForwardSettings saved = _settings.Alerts.Forward;
        ForwardSettings draft = Draft(saved.Enabled);

        HasChanges = draft.To != saved.To
            || !string.Equals(draft.NtfyServer, saved.NtfyServer, StringComparison.Ordinal)
            || !string.Equals(draft.NtfyTopic, saved.NtfyTopic, StringComparison.Ordinal)
            || !string.Equals(draft.NtfyToken, saved.NtfyToken, StringComparison.Ordinal)
            || !string.Equals(draft.WebhookUrl, saved.WebhookUrl, StringComparison.Ordinal)
            || !string.Equals(draft.SenderName, saved.SenderName, StringComparison.Ordinal);

        Result = string.Empty;
        Problem = string.Empty;
    }

    private void OnDeskChanged(object? sender, EventArgs e)
    {
        // Another place - the privacy page - may have switched forwarding.
        if (_dispatcher.CheckAccess() && !HasChanges && IsEnabled != _settings.Alerts.Forward.Enabled)
        {
            Read();
        }
    }

    private void OnLogChanged(object? sender, EventArgs e)
    {
        if (_pending || _disposed)
        {
            return;
        }

        _pending = true;
        _dispatcher.BeginInvoke(DispatcherPriority.Background, new Action(() =>
        {
            _pending = false;
            _ = ReadLastFailureAsync();
        }));
    }

    private async Task ReadLastFailureAsync()
    {
        AlertRecord? failed = await Task.Run(() =>
        {
            try
            {
                return _desk.Log
                    .Read(AlertQuery.Everything with { From = DateTimeOffset.Now.AddDays(-7), Take = 300 })
                    .FirstOrDefault(each => each.Forward is ForwardOutcome.Failed or ForwardOutcome.Sent);
            }
            catch (Exception ex) when (HistoryFailure.IsStorage(ex))
            {
                return null;
            }
        }).ConfigureAwait(true);

        if (_disposed)
        {
            return;
        }

        // Only the latest that went either way: a failure since put right is not news.
        LastFailure = failed is { Forward: ForwardOutcome.Failed } last
            ? string.Concat("Last failed ", AlertWords.When(last.At, DateTimeOffset.Now), ": ", last.ForwardDetail)
            : string.Empty;
    }
}
