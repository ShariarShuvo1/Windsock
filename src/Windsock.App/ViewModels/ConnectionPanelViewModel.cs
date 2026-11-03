using System.Globalization;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Windsock.App.Services.Connectivity;
using Windsock.Core.Connectivity;
using Windsock.Core.Settings;
using Windsock.Core.Formatting;

namespace Windsock.App.ViewModels;

/// <summary>How the connection looks at a glance, for the colour of the dot beside it.</summary>
public enum ConnectionMood
{
    Checking,

    Good,

    Bad,
}

/// <summary>One way of checking the internet, for the picker.</summary>
public sealed record InternetCheckOption(InternetCheck Check, string Label)
{
    public override string ToString() => Label;
}

/// <summary>A number of seconds, for the timing pickers.</summary>
public sealed record SecondsOption(int Seconds, string Label)
{
    public override string ToString() => Label;
}

/// <summary>
/// Backs Connection › Status: whether the internet is there, how quickly it
/// answers, and how that is checked.
/// </summary>
/// <remarks>
/// <para>
/// The top of the page is written for anybody: a dot, a sentence, and what to
/// try. The figures under it and the checks at the bottom are for whoever wants
/// them, and nothing above depends on reading them.
/// </para>
/// <para>
/// Until 2026-10-05 this was the whole Connection section, the outage log and
/// the alert switches included. The log is Connection › Outages now
/// (<see cref="OutagesPanelViewModel"/>) and the switches are on the Alerts
/// page (<see cref="AlertsPanelViewModel"/>).
/// </para>
/// </remarks>
public sealed partial class ConnectionPanelViewModel : ObservableObject, IDisposable
{
    private static readonly TimeSpan ChartSpan = TimeSpan.FromHours(1);

    private readonly ConnectionMonitor _monitor;
    private readonly ConnectionAlerts _alerts;
    private readonly WindsockSettings _settings;
    private readonly ISettingsStore _settingsStore;
    private readonly Dispatcher _dispatcher;
    private readonly DispatcherTimer _clock;
    private bool _loaded;

    private IDisposable? _watch;
    private bool _drawPending;
    private bool _disposed;

    public ConnectionPanelViewModel(
        ConnectionMonitor monitor,
        ConnectionAlerts alerts,
        WindsockSettings settings,
        ISettingsStore settingsStore,
        Dispatcher dispatcher)
    {
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(dispatcher);

        _monitor = monitor;
        _alerts = alerts;
        _settings = settings;
        _settingsStore = settingsStore;
        _dispatcher = dispatcher;

        Checks =
        [
            new(InternetCheck.WindowsOnly, "Windows' own check"),
            new(InternetCheck.Cloudflare, "Ping Cloudflare (1.1.1.1)"),
            new(InternetCheck.Google, "Ping Google (8.8.8.8)"),
            new(InternetCheck.Quad9, "Ping Quad9 (9.9.9.9)"),
            new(InternetCheck.Custom, "Ping an address I choose"),
        ];

        Intervals =
        [
            new(2, "Every 2 seconds"),
            new(5, "Every 5 seconds"),
            new(10, "Every 10 seconds"),
            new(30, "Every 30 seconds"),
            new(60, "Every minute"),
        ];

        Delays =
        [
            new(5, "After 5 seconds"),
            new(15, "After 15 seconds"),
            new(30, "After 30 seconds"),
            new(60, "After a minute"),
            new(120, "After 2 minutes"),
        ];

        ReadChoices();

        _clock = new DispatcherTimer(DispatcherPriority.Background, dispatcher) { Interval = TimeSpan.FromSeconds(1) };
        _clock.Tick += (_, _) => Describe();

        _monitor.Sampled += OnSampled;
        _monitor.StateChanged += OnStateChanged;

        Describe();
    }

    public IReadOnlyList<InternetCheckOption> Checks { get; }

    public IReadOnlyList<SecondsOption> Intervals { get; }

    public IReadOnlyList<SecondsOption> Delays { get; }

    // ---- The answer ----

    /// <summary>Good, bad, or not known yet.</summary>
    [ObservableProperty]
    public partial ConnectionMood Mood { get; private set; }

    /// <summary>The state in a few words: "Connected to the internet".</summary>
    [ObservableProperty]
    public partial string Headline { get; private set; } = "Checking the connection…";

    /// <summary>What that means, or what to try, in a sentence.</summary>
    [ObservableProperty]
    public partial string Explanation { get; private set; } = string.Empty;

    /// <summary>How long it has been like this, and on which network.</summary>
    [ObservableProperty]
    public partial string Circumstance { get; private set; } = string.Empty;

    // ---- The figures ----

    [ObservableProperty]
    public partial string RouterValue { get; private set; } = "—";

    [ObservableProperty]
    public partial string RouterDetail { get; private set; } = string.Empty;

    [ObservableProperty]
    public partial bool RouterSilent { get; private set; }

    [ObservableProperty]
    public partial string InternetValue { get; private set; } = "—";

    [ObservableProperty]
    public partial string InternetDetail { get; private set; } = string.Empty;

    [ObservableProperty]
    public partial bool InternetSilent { get; private set; }

    [ObservableProperty]
    public partial string WindowsValue { get; private set; } = "—";

    [ObservableProperty]
    public partial string WindowsDetail { get; private set; } = "Its own connectivity check";

    [ObservableProperty]
    public partial string NetworkValue { get; private set; } = "—";

    [ObservableProperty]
    public partial string NetworkDetail { get; private set; } = string.Empty;

    [ObservableProperty]
    public partial string LossValue { get; private set; } = "—";

    [ObservableProperty]
    public partial string LossDetail { get; private set; } = "Last hour";

    // ---- The chart ----

    [ObservableProperty]
    public partial IReadOnlyList<LatencyPoint> Points { get; private set; } = [];

    [ObservableProperty]
    public partial int Revision { get; private set; }

    [ObservableProperty]
    public partial DateTimeOffset ChartEnd { get; private set; } = DateTimeOffset.Now;

    /// <summary>How much time the chart covers.</summary>
    public TimeSpan ChartSpanValue { get; } = ChartSpan;

    /// <summary>Whether an internet target is being pinged, so its line and legend have something to show.</summary>
    [ObservableProperty]
    public partial bool HasInternetLine { get; private set; }

    // ---- The checks ----

    [ObservableProperty]
    public partial bool WatchInBackground { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsCustom))]
    public partial InternetCheck SelectedCheck { get; set; }

    [ObservableProperty]
    public partial string CustomTarget { get; set; } = string.Empty;

    /// <summary>What is wrong with the address typed, or empty.</summary>
    [ObservableProperty]
    public partial string CustomProblem { get; private set; } = string.Empty;

    [ObservableProperty]
    public partial int SelectedInterval { get; set; }

    [ObservableProperty]
    public partial int SelectedDelay { get; set; }

    /// <summary>Whether the timing options are unfolded.</summary>
    [ObservableProperty]
    public partial bool ShowsMore { get; set; }

    public bool IsCustom => SelectedCheck == InternetCheck.Custom;

    /// <summary>What checking the internet this way sends, and where.</summary>
    public string CheckNote => SelectedCheck switch
    {
        InternetCheck.WindowsOnly =>
            "Windsock asks Windows, which already checks by itself, and pings your router, which never leaves your network. Nothing is sent to the internet.",
        InternetCheck.Custom =>
            "Also pings the address you choose at every check, so an outage is caught even when Windows is slow to notice. Pick one that answers pings.",
        _ =>
            "Also pings a public address at every check - a tiny packet, nothing about you - so an outage is caught even when Windows is slow to notice.",
    };

    /// <summary>Starts watching, for as long as the page is on screen.</summary>
    public void Show()
    {
        if (_disposed)
        {
            return;
        }

        // The privacy page and an import can change how the internet is
        // checked while this page is out of sight.
        ReadChoices();

        _watch ??= _monitor.Watch();
        _clock.Start();
        Draw();
    }

    /// <summary>Stops watching on the page's behalf. Background watching carries on if it is on.</summary>
    public void Hide()
    {
        _clock.Stop();
        _watch?.Dispose();
        _watch = null;
    }

    /// <summary>Shows the checks as the settings have them, without saving them back.</summary>
    private void ReadChoices()
    {
        _loaded = false;

        ConnectionSettings choices = _settings.Connection;
        WatchInBackground = choices.WatchInBackground;
        SelectedCheck = choices.InternetCheck;
        CustomTarget = choices.CustomTarget;
        SelectedInterval = Nearest(Intervals, choices.CheckEverySeconds);
        SelectedDelay = Nearest(Delays, choices.OutageAfterSeconds);
        OnPropertyChanged(nameof(CheckNote));
        Validate();

        _loaded = true;
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        Hide();
        _monitor.Sampled -= OnSampled;
        _monitor.StateChanged -= OnStateChanged;
    }

    private void OnSampled(object? sender, ConnectionSample sample) => Queue();

    private void OnStateChanged(object? sender, ConnectionState state) => Queue();

    private void Queue()
    {
        if (_drawPending)
        {
            return;
        }

        _drawPending = true;
        _dispatcher.BeginInvoke(DispatcherPriority.Background, new Action(() =>
        {
            _drawPending = false;
            Draw();
        }));
    }

    private void Draw()
    {
        if (_disposed)
        {
            return;
        }

        DateTimeOffset now = DateTimeOffset.Now;

        Points = _monitor.History.Since(now - ChartSpan);
        ChartEnd = now;
        Revision++;

        Describe();
        Figures(now);
    }

    /// <summary>The headline, the sentence and the circumstance, which change with the clock as well as the checks.</summary>
    private void Describe()
    {
        DateTimeOffset now = DateTimeOffset.Now;
        ConnectionState state = _monitor.State;
        ConnectionSample? latest = _monitor.Latest;
        NetworkIdentity? network = latest?.Facts.Network;

        switch (state.Status)
        {
            case ConnectionStatus.Offline:
                OutageCause cause = state.Cause ?? OutageCause.Unknown;
                Mood = ConnectionMood.Bad;
                Headline = OutageWords.Headline(cause);
                Explanation = OutageWords.Explain(cause);
                Circumstance = state.Since is { } down
                    ? string.Create(CultureInfo.CurrentCulture, $"Out for {OutageWords.Spoken(now - down)}, since {ClockFormat.Time(down.ToLocalTime())}.")
                    : string.Empty;
                break;

            case ConnectionStatus.Online:
                Mood = ConnectionMood.Good;
                Headline = "Connected to the internet";
                Explanation = latest is { } sample && ConnectionJudge.Judge(sample, _monitor.RouterAnswers) is not null
                    ? "Something isn't answering right now. If it carries on, Windsock will call it an outage."
                    : "Everything Windsock checks is answering.";
                Circumstance = string.Concat(
                    network is null ? string.Empty : string.Concat("On ", network.Describe(), ". "),
                    state.Since is { } up ? string.Concat("Online for at least ", OutageWords.Spoken(now - up), ".") : string.Empty);
                break;

            default:
                Mood = ConnectionMood.Checking;
                Headline = "Checking the connection…";
                Explanation = "The first answer takes a few seconds.";
                Circumstance = string.Empty;
                break;
        }
    }

    private void Figures(DateTimeOffset now)
    {
        ConnectionSample? latest = _monitor.Latest;
        ConnectionSettings choices = _settings.Connection;

        if (latest is not { } sample)
        {
            RouterValue = InternetValue = WindowsValue = NetworkValue = LossValue = "—";
            RouterDetail = InternetDetail = NetworkDetail = string.Empty;
            RouterSilent = InternetSilent = false;
            HasInternetLine = choices.InternetTarget is not null;
            return;
        }

        ConnectionFacts facts = sample.Facts;

        if (facts.Gateway is { } gateway)
        {
            RouterSilent = sample.Router is null;
            RouterValue = sample.Router is { } router ? Milliseconds(router) : "No answer";
            RouterDetail = RouterSilent && !_monitor.RouterAnswers
                ? string.Concat(gateway, " · may not answer pings")
                : gateway;
        }
        else
        {
            RouterSilent = false;
            RouterValue = "—";
            RouterDetail = "No router found";
        }

        HasInternetLine = sample.InternetChecked;

        if (sample.InternetChecked)
        {
            InternetSilent = sample.Internet is null;
            InternetValue = sample.Internet is { } internet ? Milliseconds(internet) : "No answer";
            InternetDetail = choices.InternetTarget ?? string.Empty;
        }
        else
        {
            InternetSilent = false;
            InternetValue = "Not pinged";
            InternetDetail = "Using Windows' own check";
        }

        WindowsValue = facts.Reach switch
        {
            WindowsReach.Internet => "Internet",
            WindowsReach.LocalOnly => "No internet",
            WindowsReach.SignInRequired => "Sign-in needed",
            WindowsReach.None => "No network",
            _ => "—",
        };

        NetworkValue = facts.Network?.Name ?? "None";
        NetworkDetail = facts.Network is null
            ? facts.VpnActive ? "VPN on" : string.Empty
            : string.Concat(
                facts.Network.Kind switch
                {
                    NetworkKind.WiFi => "Wi-Fi",
                    NetworkKind.Ethernet => "Wired",
                    NetworkKind.Cellular => "Mobile",
                    _ => "Network",
                },
                facts.VpnActive ? " · VPN on" : string.Empty);

        DateTimeOffset hour = now - ChartSpan;
        double? loss = sample.InternetChecked ? _monitor.History.InternetLoss(hour) : _monitor.History.RouterLoss(hour);

        LossValue = loss switch
        {
            null => "—",
            0 => "None",
            < 0.001 => "Under 0.1%",
            _ => string.Create(CultureInfo.CurrentCulture, $"{loss.Value * 100:0.#}%"),
        };

        LossDetail = sample.InternetChecked ? "Internet, last hour" : "Router, last hour";
    }

    private static string Milliseconds(TimeSpan roundTrip) => roundTrip.TotalMilliseconds < 1
        ? "Under 1 ms"
        : string.Create(CultureInfo.CurrentCulture, $"{Math.Round(roundTrip.TotalMilliseconds):0} ms");

    // ---- The checks ----

    partial void OnWatchInBackgroundChanged(bool value) => Save(choices => choices.WatchInBackground = value, apply: true);

    partial void OnSelectedCheckChanged(InternetCheck value)
    {
        OnPropertyChanged(nameof(CheckNote));
        Save(choices => choices.InternetCheck = value, nudge: true, forget: true);
        Validate();
    }

    partial void OnSelectedIntervalChanged(int value) => Save(choices => choices.CheckEverySeconds = value, nudge: true);

    partial void OnSelectedDelayChanged(int value) => Save(choices => choices.OutageAfterSeconds = value);

    /// <summary>Takes the address typed, once the box is left or Enter is pressed.</summary>
    [RelayCommand]
    private void ApplyCustomTarget()
    {
        if (!Validate())
        {
            return;
        }

        string trimmed = CustomTarget.Trim();

        if (trimmed != CustomTarget)
        {
            CustomTarget = trimmed;
        }

        Save(choices => choices.CustomTarget = trimmed, nudge: true, forget: true);
    }

    private bool Validate()
    {
        if (!IsCustom)
        {
            CustomProblem = string.Empty;
            return true;
        }

        bool fine = ConnectionSettings.IsTarget(CustomTarget);

        CustomProblem = fine
            ? string.Empty
            : string.IsNullOrWhiteSpace(CustomTarget)
                ? "Type an address to ping. Until then only Windows' own check is used."
                : "That isn't an address. Use one like 1.1.1.1 or example.com.";

        return fine;
    }

    [RelayCommand]
    private void ToggleMore() => ShowsMore = !ShowsMore;

    private void Save(Action<ConnectionSettings> change, bool apply = false, bool nudge = false, bool forget = false)
    {
        if (!_loaded)
        {
            return;
        }

        change(_settings.Connection);
        _settingsStore.Save(_settings);

        if (apply)
        {
            _alerts.Apply();
        }

        // A different target draws a different line; the old one would be read as this one.
        if (forget)
        {
            _monitor.History.Clear();
        }

        if (nudge)
        {
            _monitor.Nudge();
            Draw();
        }
    }

    private static int Nearest(IReadOnlyList<SecondsOption> options, int seconds) =>
        options.MinBy(option => Math.Abs(option.Seconds - seconds))!.Seconds;
}
