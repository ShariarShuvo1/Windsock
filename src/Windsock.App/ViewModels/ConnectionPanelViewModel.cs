using System.Collections.ObjectModel;
using System.Globalization;
using System.IO;
using System.Text;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using Microsoft.Win32;
using Windsock.App.Services.Connectivity;
using Windsock.Core.Connectivity;
using Windsock.Core.Formatting;
using Windsock.Core.History;
using Windsock.Core.Settings;

namespace Windsock.App.ViewModels;

/// <summary>How the connection looks at a glance, for the colour of the dot beside it.</summary>
public enum ConnectionMood
{
    Checking,

    Good,

    Bad,
}

/// <summary>The spans of time the outage log can show.</summary>
public enum OutageRange
{
    Day,
    Week,
    Month,
    Everything,
}

/// <summary>One entry in the outage log's period picker.</summary>
public sealed record OutageRangeOption(OutageRange Range, string Label, string Period)
{
    public override string ToString() => Label;
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

/// <summary>One outage in the log.</summary>
public sealed class OutageRowViewModel(Outage outage, DateTimeOffset now)
{
    public Outage Outage { get; } = outage;

    /// <summary>When it happened, in words.</summary>
    public string When { get; } = OutageWords.When(outage, now, CultureInfo.CurrentCulture);

    /// <summary>How long it lasted, or has lasted so far.</summary>
    public string Duration { get; } = OutageWords.Brief((outage.End ?? now) - outage.Start);

    /// <summary>What was probably at fault.</summary>
    public string Cause { get; } = OutageWords.Blame(outage.Cause);

    /// <summary>The network it happened on, or nothing when that was not known.</summary>
    public string Network { get; } = outage.Network;

    /// <summary>Whether it is still going.</summary>
    public bool IsOngoing { get; } = outage.End is null;

    /// <summary>A word about how far to trust the end, or empty.</summary>
    public string Remark { get; } = outage.End is null
        ? "Still going"
        : outage.Interrupted
            ? "Windsock stopped watching before it ended"
            : string.Empty;

    /// <summary>Why, where, and the remark, as one line.</summary>
    public string Details => string.Join(
        " · ",
        new[] { Cause, Network, Remark }.Where(part => part.Length > 0));
}

/// <summary>
/// Backs the Connection section: whether the internet is there, how quickly it
/// answers, every time it went away, and how all of that is checked and told.
/// </summary>
/// <remarks>
/// The top of the section is written for anybody: a dot, a sentence, and what to
/// try. The figures under it and the options at the bottom are for whoever wants
/// them, and nothing above depends on reading them.
/// </remarks>
public sealed partial class ConnectionPanelViewModel : ObservableObject, IDisposable
{
    private static readonly TimeSpan ChartSpan = TimeSpan.FromHours(1);

    // The log a page at a time, so years of a flaky connection never become
    // thousands of rows on the page at once. It used to stop at the newest 300.
    private readonly PagedView<Outage> _outagePages;
    private OutageRange? _pagedRange;

    private readonly ConnectionMonitor _monitor;
    private readonly ConnectionAlerts _alerts;
    private readonly IOutageStore _store;
    private readonly WindsockSettings _settings;
    private readonly ISettingsStore _settingsStore;
    private readonly Dispatcher _dispatcher;
    private readonly ILogger<ConnectionPanelViewModel> _logger;
    private readonly DispatcherTimer _clock;
    private readonly bool _loaded;

    private IDisposable? _watch;
    private bool _drawPending;
    private bool _logPending;
    private bool _disposed;

    public ConnectionPanelViewModel(
        ConnectionMonitor monitor,
        ConnectionAlerts alerts,
        IOutageStore store,
        WindsockSettings settings,
        ISettingsStore settingsStore,
        Dispatcher dispatcher,
        ILogger<ConnectionPanelViewModel> logger)
    {
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(dispatcher);

        _monitor = monitor;
        _alerts = alerts;
        _store = store;
        _settings = settings;
        _settingsStore = settingsStore;
        _dispatcher = dispatcher;
        _logger = logger;

        int pageSize = PagerViewModel.Normalise(settings.OutageRowsPerPage);
        _outagePages = new PagedView<Outage>(pageSize);
        _outagePages.Changed += (_, _) => FillLogPage();
        LogPager = new PagerViewModel(
            pageSize,
            page => _outagePages.GoTo(page),
            size =>
            {
                _outagePages.PageSize = size;
                _settings.OutageRowsPerPage = size;
                _settingsStore.Save(_settings);
            },
            "outage",
            "outages");

        Ranges =
        [
            new(OutageRange.Day, "Last 24 hours", "the last 24 hours"),
            new(OutageRange.Week, "Last 7 days", "the last 7 days"),
            new(OutageRange.Month, "Last 30 days", "the last 30 days"),
            new(OutageRange.Everything, "Everything", "everything Windsock has kept"),
        ];

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

        ConnectionSettings choices = settings.Connection;
        WatchInBackground = choices.WatchInBackground;
        NotifyLostAndRestored = choices.NotifyLostAndRestored;
        NotifyNetworkChanges = choices.NotifyNetworkChanges;
        SelectedCheck = choices.InternetCheck;
        CustomTarget = choices.CustomTarget;
        SelectedInterval = Nearest(Intervals, choices.CheckEverySeconds);
        SelectedDelay = Nearest(Delays, choices.OutageAfterSeconds);
        SelectedRange = OutageRange.Week;
        _loaded = true;

        _clock = new DispatcherTimer(DispatcherPriority.Background, dispatcher) { Interval = TimeSpan.FromSeconds(1) };
        _clock.Tick += (_, _) => Describe();

        _monitor.Sampled += OnSampled;
        _monitor.StateChanged += OnStateChanged;
        _store.Changed += OnLogChanged;

        Describe();
    }

    public IReadOnlyList<OutageRangeOption> Ranges { get; }

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

    // ---- The log ----

    [ObservableProperty]
    public partial OutageRange SelectedRange { get; set; }

    public ObservableCollection<OutageRowViewModel> Rows { get; } = [];

    [ObservableProperty]
    public partial string LogSummary { get; private set; } = string.Empty;

    /// <summary>Said after an export or a clear, or when the log could not be read.</summary>
    [ObservableProperty]
    public partial string LogNotice { get; private set; } = string.Empty;

    [ObservableProperty]
    public partial bool HasRows { get; private set; }

    /// <summary>Whether Clear has been pressed once and is waiting to be confirmed.</summary>
    [ObservableProperty]
    public partial bool IsConfirmingClear { get; private set; }

    // ---- The options ----

    [ObservableProperty]
    public partial bool WatchInBackground { get; set; }

    [ObservableProperty]
    public partial bool NotifyLostAndRestored { get; set; }

    [ObservableProperty]
    public partial bool NotifyNetworkChanges { get; set; }

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

    /// <summary>Starts watching, for as long as the section is on screen.</summary>
    public void Show()
    {
        if (_disposed)
        {
            return;
        }

        _watch ??= _monitor.Watch();
        _clock.Start();
        Draw();
        _ = LoadLogAsync();
    }

    /// <summary>Stops watching on the section's behalf. Background watching carries on if it is on.</summary>
    public void Hide()
    {
        _clock.Stop();
        _watch?.Dispose();
        _watch = null;
        IsConfirmingClear = false;
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
        _store.Changed -= OnLogChanged;
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

    private void OnLogChanged(object? sender, EventArgs e)
    {
        if (_logPending)
        {
            return;
        }

        _logPending = true;
        _dispatcher.BeginInvoke(DispatcherPriority.Background, new Action(() =>
        {
            _logPending = false;

            if (_watch is not null)
            {
                _ = LoadLogAsync();
            }
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
                    ? string.Create(CultureInfo.CurrentCulture, $"Out for {OutageWords.Spoken(now - down)}, since {down.ToLocalTime():t}.")
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

    // ---- The log ----

    partial void OnSelectedRangeChanged(OutageRange value)
    {
        if (_loaded && _watch is not null)
        {
            _ = LoadLogAsync();
        }
    }

    private (DateTimeOffset From, DateTimeOffset Until, string Period) Bounds()
    {
        DateTimeOffset now = DateTimeOffset.Now;
        OutageRangeOption option = Ranges.First(each => each.Range == SelectedRange);

        DateTimeOffset from = SelectedRange switch
        {
            OutageRange.Day => now.AddDays(-1),
            OutageRange.Week => now.AddDays(-7),
            OutageRange.Month => now.AddDays(-30),
            _ => DateTimeOffset.FromUnixTimeMilliseconds(0),
        };

        return (from, now.AddMinutes(1), option.Period);
    }

    private async Task LoadLogAsync()
    {
        (DateTimeOffset from, DateTimeOffset until, string period) = Bounds();
        IReadOnlyList<Outage> outages;

        try
        {
            outages = await Task.Run(() => _store.Read(from, until)).ConfigureAwait(true);
        }
        catch (Exception ex) when (HistoryFailure.IsStorage(ex))
        {
            LogReadFailed(ex);
            _pagedRange = null;
            _outagePages.SetSource([], toFirstPage: true);
            LogSummary = string.Empty;
            LogNotice = "The outage log could not be read. Outages are still watched for and shown above.";
            return;
        }

        if (_disposed)
        {
            return;
        }

        DateTimeOffset now = DateTimeOffset.Now;

        // Newest first, as the store reads them. A reload of the same period
        // keeps the page on screen; another period starts at its first.
        bool otherPeriod = _pagedRange != SelectedRange;
        _pagedRange = SelectedRange;
        _outagePages.SetSource(outages, toFirstPage: otherPeriod);

        LogSummary = OutageWords.Summary([.. outages], period, now);

        if (!WatchInBackground)
        {
            LogSummary = string.Concat(LogSummary, " Outages are only logged while this section is open, because watching in the background is off.");
        }
    }

    /// <summary>The bar under the outage log.</summary>
    public PagerViewModel LogPager { get; }

    /// <summary>Puts the log's page on screen into the list.</summary>
    private void FillLogPage()
    {
        DateTimeOffset now = DateTimeOffset.Now;

        Rows.Clear();

        foreach (Outage outage in _outagePages.Current)
        {
            Rows.Add(new OutageRowViewModel(outage, now));
        }

        HasRows = Rows.Count > 0;
        LogPager.Show(_outagePages.Window);
    }

    [RelayCommand]
    private async Task ExportAsync()
    {
        (DateTimeOffset from, DateTimeOffset until, _) = Bounds();

        SaveFileDialog dialog = new()
        {
            Title = "Export the outage log",
            Filter = "CSV file (*.csv)|*.csv|All files (*.*)|*.*",
            DefaultExt = ".csv",
            FileName = string.Format(CultureInfo.InvariantCulture, "Windsock-outages-{0:yyyy-MM-dd}.csv", DateTimeOffset.Now),
        };

        if (dialog.ShowDialog() != true)
        {
            return;
        }

        string path = dialog.FileName;
        IReadOnlyList<Outage> outages;

        try
        {
            outages = await Task.Run(() => _store.Read(from, until)).ConfigureAwait(true);
        }
        catch (Exception ex) when (HistoryFailure.IsStorage(ex))
        {
            LogReadFailed(ex);
            LogNotice = "Nothing was exported, because the outage log could not be read.";
            return;
        }

        try
        {
            // Oldest first in a file, which is how a spreadsheet expects time to run.
            Outage[] ordered = [.. outages.OrderBy(outage => outage.Start)];

            await Task.Run(() =>
            {
                using StreamWriter writer = new(path, false, new UTF8Encoding(true));
                OutageCsv.Write(writer, ordered);
            }).ConfigureAwait(true);

            LogNotice = string.Format(
                CultureInfo.CurrentCulture,
                "Exported {0:N0} {1} to {2}",
                ordered.Length,
                ordered.Length == 1 ? "outage" : "outages",
                Path.GetFileName(path));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            LogNotice = "Could not write that file: " + ex.Message;
        }
    }

    [RelayCommand]
    private void AskClear() => IsConfirmingClear = true;

    [RelayCommand]
    private void CancelClear() => IsConfirmingClear = false;

    [RelayCommand]
    private async Task ClearAsync()
    {
        IsConfirmingClear = false;

        try
        {
            int gone = await Task.Run(_store.Clear).ConfigureAwait(true);
            LogNotice = gone == 1 ? "Cleared 1 outage." : string.Create(CultureInfo.CurrentCulture, $"Cleared {gone:N0} outages.");
        }
        catch (Exception ex) when (HistoryFailure.IsStorage(ex))
        {
            LogReadFailed(ex);
            LogNotice = "The outage log could not be cleared.";
        }

        await LoadLogAsync().ConfigureAwait(true);
    }

    // ---- The options ----

    partial void OnWatchInBackgroundChanged(bool value) => Save(choices => choices.WatchInBackground = value, apply: true);

    partial void OnNotifyLostAndRestoredChanged(bool value) => Save(choices => choices.NotifyLostAndRestored = value);

    partial void OnNotifyNetworkChangesChanged(bool value) => Save(choices => choices.NotifyNetworkChanges = value);

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

    [LoggerMessage(Level = LogLevel.Warning, Message = "The outage log could not be read or written")]
    private partial void LogReadFailed(Exception error);
}
