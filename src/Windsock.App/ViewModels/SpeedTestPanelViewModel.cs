using System.Collections.ObjectModel;
using System.Globalization;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using Windsock.App.Controls;
using Windsock.Core.Formatting;
using Windsock.Core.History;
using Windsock.Core.Settings;
using Windsock.Core.SpeedTests;

namespace Windsock.App.ViewModels;

/// <summary>One choice of server.</summary>
public sealed record SpeedServerOption(SpeedTestServer Server, string Label)
{
    public override string ToString() => Label;
}

/// <summary>One choice of how many automatic tests a day.</summary>
public sealed record TestsADayOption(int Tests, string Label)
{
    public override string ToString() => Label;
}

/// <summary>One kept speed test, as a tile.</summary>
public sealed partial class SpeedTestRow(SpeedTestResult result, DateTimeOffset now, Func<SpeedTestRow, Task> delete)
{
    public SpeedTestResult Result { get; } = result;

    public string When { get; } = string.Concat(NetworkWords.Ago(result.At, now), result.Scheduled ? " · automatic" : string.Empty);

    public string Speeds { get; } = result.Succeeded
        ? string.Concat(SpeedGrades.Mbps(result.DownloadBitsPerSecond), " down · ", SpeedGrades.Mbps(result.UploadBitsPerSecond), " up")
        : "Did not finish";

    public string Details { get; } = string.Join(" · ", new[]
    {
        result.IdleMs is { } idle ? string.Create(CultureInfo.CurrentCulture, $"{idle:0} ms quiet") : string.Empty,
        result.LoadedIncreaseMs is { } more ? string.Create(CultureInfo.CurrentCulture, $"+{more:0} ms under load ({SpeedGrades.Letter(result.Bloat)})") : string.Empty,
        result.Server,
        result.Network ?? string.Empty,
        result.Problem ?? string.Empty,
    }.Where(part => part.Length > 0));

    public bool Failed => !Result.Succeeded;

    public string DeleteName => string.Concat("Delete the test from ", When);

    [RelayCommand]
    private Task DeleteAsync() => delete(this);
}

/// <summary>
/// Backs Connection › Speed test: run a test now or on a schedule, against
/// Measurement Lab - which publishes every result with this PC's public address
/// - or the person's own LibreSpeed server; every result kept, the trend, the
/// latency under load, and the plan's promised speed held against what arrives.
/// </summary>
public sealed partial class SpeedTestPanelViewModel : ObservableObject, IDisposable
{
    private static readonly TimeSpan HistorySpan = TimeSpan.FromDays(90);
    private static readonly TimeSpan PlanSpan = TimeSpan.FromDays(30);

    private readonly SpeedTestService _tests;
    private readonly SpeedTestScheduler _scheduler;
    private readonly ISpeedTestStore _store;
    private readonly IUsageHistoryStore _usage;
    private readonly WindsockSettings _settings;
    private readonly ISettingsStore _settingsStore;
    private readonly Dispatcher _dispatcher;
    private readonly ILogger<SpeedTestPanelViewModel> _logger;

    private CancellationTokenSource? _running;
    private bool _stopAsked;
    private bool _shown;
    private bool _reading;
    private bool _disposed;

    public SpeedTestPanelViewModel(
        SpeedTestService tests,
        SpeedTestScheduler scheduler,
        ISpeedTestStore store,
        IUsageHistoryStore usage,
        WindsockSettings settings,
        ISettingsStore settingsStore,
        Dispatcher dispatcher,
        ILogger<SpeedTestPanelViewModel> logger)
    {
        ArgumentNullException.ThrowIfNull(settings);

        _tests = tests;
        _scheduler = scheduler;
        _store = store;
        _usage = usage;
        _settings = settings;
        _settingsStore = settingsStore;
        _dispatcher = dispatcher;
        _logger = logger;

        Servers =
        [
            new(SpeedTestServer.MeasurementLab, "Measurement Lab (M-Lab)"),
            new(SpeedTestServer.LibreSpeed, "My LibreSpeed server"),
        ];

        TestsADayOptions =
        [
            new(1, "Once a day"),
            new(2, "Twice a day"),
            new(3, "Three times a day"),
            new(4, "Four times a day"),
        ];

        ReadChoices();
    }

    public IReadOnlyList<SpeedServerOption> Servers { get; }

    public IReadOnlyList<TestsADayOption> TestsADayOptions { get; }

    [ObservableProperty]
    public partial string Subtitle { get; private set; } = "No tests yet";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsMeasurementLab), nameof(IsLibreSpeed))]
    public partial SpeedTestServer SelectedServer { get; set; }

    public bool IsMeasurementLab => SelectedServer == SpeedTestServer.MeasurementLab;

    public bool IsLibreSpeed => SelectedServer == SpeedTestServer.LibreSpeed;

    /// <summary>Whether the person agreed that M-Lab publishes each test with this PC's address.</summary>
    [ObservableProperty]
    public partial bool MeasurementLabAgreed { get; set; }

    /// <summary>When they agreed, in words.</summary>
    [ObservableProperty]
    public partial string AgreedNote { get; private set; } = string.Empty;

    [ObservableProperty]
    public partial string LibreSpeedUrl { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string LibreSpeedProblem { get; private set; } = string.Empty;

    /// <summary>Who a test would contact, said before it runs.</summary>
    [ObservableProperty]
    public partial string Destination { get; private set; } = string.Empty;

    /// <summary>Why a test cannot start now, or empty.</summary>
    [ObservableProperty]
    public partial string Refusal { get; private set; } = string.Empty;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(RunCommand))]
    public partial bool CanRun { get; private set; }

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(RunCommand), nameof(StopCommand))]
    public partial bool IsRunning { get; private set; }

    [ObservableProperty]
    public partial string Phase { get; private set; } = string.Empty;

    [ObservableProperty]
    public partial string LiveSpeed { get; private set; } = string.Empty;

    [ObservableProperty]
    public partial string LiveLatency { get; private set; } = string.Empty;

    /// <summary>How far through the test it is, 0 to 1.</summary>
    [ObservableProperty]
    public partial double Progress { get; private set; }

    [ObservableProperty]
    public partial string DownloadValue { get; private set; } = "—";

    [ObservableProperty]
    public partial string UploadValue { get; private set; } = "—";

    [ObservableProperty]
    public partial string QuietValue { get; private set; } = "—";

    [ObservableProperty]
    public partial string LoadedValue { get; private set; } = "—";

    [ObservableProperty]
    public partial string GradeValue { get; private set; } = "—";

    [ObservableProperty]
    public partial string GradeNote { get; private set; } = string.Empty;

    [ObservableProperty]
    public partial string ServerValue { get; private set; } = "—";

    [ObservableProperty]
    public partial string ResultNote { get; private set; } = "Run a test to see your speed here.";

    [ObservableProperty]
    public partial bool Scheduled { get; set; }

    [ObservableProperty]
    public partial int TestsADay { get; set; } = ConnectionToolsSettings.MostScheduledTestsADay;

    [ObservableProperty]
    public partial bool OnMetered { get; set; }

    [ObservableProperty]
    public partial string ScheduleNote { get; private set; } = string.Empty;

    public ObservableCollection<SpeedTestRow> Tests { get; } = [];

    [ObservableProperty]
    public partial bool HasTests { get; private set; }

    [ObservableProperty]
    public partial IReadOnlyList<MetricBar> Trend { get; private set; } = [];

    [ObservableProperty]
    public partial double? TrendReference { get; private set; }

    [ObservableProperty]
    public partial string HistoryNote { get; private set; } = string.Empty;

    [ObservableProperty]
    public partial bool IsConfirmingClear { get; private set; }

    [ObservableProperty]
    public partial string PlanDownload { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string PlanUpload { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string PlanSummary { get; private set; } = string.Empty;

    [ObservableProperty]
    public partial IReadOnlyList<MetricBar> PlanBars { get; private set; } = [];

    [ObservableProperty]
    public partial bool HasPlanBars { get; private set; }

    /// <summary>Reads the history, and follows tests, while the page is on screen.</summary>
    public void Show()
    {
        if (_disposed || _shown)
        {
            return;
        }

        _shown = true;
        _tests.Progressed += OnProgressed;
        _tests.Finished += OnFinished;
        _scheduler.Rescheduled += OnRescheduled;
        _store.SpeedTestsChanged += OnStoreChanged;

        // Settings › Data and privacy can switch these off while the page is away.
        ReadChoices();
        Reflect();
        _ = LoadAsync();

        // A test may be under way already: the schedule's, or one started before the page went.
        if (_tests.Progress.IsActive)
        {
            ApplyProgress(_tests.Progress);
        }
    }

    public void Hide()
    {
        if (!_shown)
        {
            return;
        }

        _shown = false;
        _tests.Progressed -= OnProgressed;
        _tests.Finished -= OnFinished;
        _scheduler.Rescheduled -= OnRescheduled;
        _store.SpeedTestsChanged -= OnStoreChanged;
        IsConfirmingClear = false;
    }

    private void ReadChoices()
    {
        _reading = true;

        try
        {
            ConnectionToolsSettings tools = _settings.ConnectionTools;
            SelectedServer = tools.SpeedTestServer;
            MeasurementLabAgreed = tools.MeasurementLabAgreed;
            LibreSpeedUrl = tools.LibreSpeedUrl;
            Scheduled = tools.ScheduledSpeedTests;
            TestsADay = tools.TestsADay;
            OnMetered = tools.ScheduledTestsOnMetered;
            PlanDownload = tools.PlanDownloadMbps > 0 ? tools.PlanDownloadMbps.ToString("0.##", CultureInfo.CurrentCulture) : string.Empty;
            PlanUpload = tools.PlanUploadMbps > 0 ? tools.PlanUploadMbps.ToString("0.##", CultureInfo.CurrentCulture) : string.Empty;
        }
        finally
        {
            _reading = false;
        }
    }

    partial void OnSelectedServerChanged(SpeedTestServer value) => Save(tools => tools.SpeedTestServer = value);

    partial void OnMeasurementLabAgreedChanged(bool value) => Save(tools =>
    {
        if (value)
        {
            tools.MeasurementLabConsentAt ??= DateTimeOffset.Now;
            return;
        }

        // Taking the agreement back stops the tests that would act on it.
        tools.MeasurementLabConsentAt = null;

        if (tools.SpeedTestServer == SpeedTestServer.MeasurementLab && tools.ScheduledSpeedTests)
        {
            tools.ScheduledSpeedTests = false;
            _reading = true;
            Scheduled = false;
            _reading = false;
        }
    });

    partial void OnScheduledChanged(bool value) => Save(tools => tools.ScheduledSpeedTests = value);

    partial void OnTestsADayChanged(int value) => Save(tools => tools.ScheduledTestsADay = value);

    partial void OnOnMeteredChanged(bool value) => Save(tools => tools.ScheduledTestsOnMetered = value);

    [RelayCommand]
    private void ApplyLibreSpeed()
    {
        string typed = LibreSpeedUrl.Trim();

        if (typed.Length > 0 && LibreSpeedClient.ParseRoot(typed) is null)
        {
            LibreSpeedProblem = "That is not a web address. Type it as http://… or https://…, the address you open the server's page at.";
            return;
        }

        LibreSpeedProblem = string.Empty;
        Save(tools => tools.LibreSpeedUrl = typed);
    }

    [RelayCommand]
    private void ApplyPlan()
    {
        double down = Number(PlanDownload);
        double up = Number(PlanUpload);
        Save(tools =>
        {
            tools.PlanDownloadMbps = down;
            tools.PlanUploadMbps = up;
        });
        _ = LoadAsync();
    }

    private static double Number(string text) =>
        double.TryParse(text.Trim(), NumberStyles.Float, CultureInfo.CurrentCulture, out double value) && value is > 0 and < 100_000
            ? value
            : 0;

    private void Save(Action<ConnectionToolsSettings> change)
    {
        if (_reading)
        {
            return;
        }

        change(_settings.ConnectionTools);
        _settingsStore.Save(_settings);
        _scheduler.Reschedule();
        Reflect();
    }

    /// <summary>Brings the words that follow the choices up to date.</summary>
    private void Reflect()
    {
        Destination = _tests.Destination();
        SpeedTestRefusal refusal = _tests.CanRun();
        CanRun = refusal == SpeedTestRefusal.None;
        Refusal = refusal switch
        {
            SpeedTestRefusal.NotAgreed => "Agree to M-Lab's terms below first, or choose your own LibreSpeed server.",
            SpeedTestRefusal.NoServer => "Type your LibreSpeed server's address below first.",
            SpeedTestRefusal.Offline => "The internet is down, so there is nothing to test.",
            SpeedTestRefusal.Running => "A test is running; its progress is below, and Stop ends it.",
            SpeedTestRefusal.Scanning => "A Wi-Fi scan is running on Adapters and Wi-Fi. Run the test once it has finished: each would disturb the other.",
            _ => string.Empty,
        };

        AgreedNote = _settings.ConnectionTools.MeasurementLabConsentAt is { } agreed
            ? string.Concat("You agreed on ", agreed.ToString("D", CultureInfo.CurrentCulture), ".")
            : string.Empty;

        bool canSchedule = refusal is not (SpeedTestRefusal.NotAgreed or SpeedTestRefusal.NoServer);

        ScheduleNote = !Scheduled
            ? canSchedule
                ? "Off. Tests run only when you press Run test."
                : "Choose a server you can test against first."
            : _scheduler.NextRunAt is { } next
                ? string.Concat("Next automatic test around ", ClockFormat.Time(next), next.Date == DateTimeOffset.Now.Date ? " today" : string.Concat(", ", next.ToString("ddd", ClockFormat.Current.Format)), ". At random times, never more than the number a day you chose.")
                : "Waiting for the next random time.";
    }

    [RelayCommand(CanExecute = nameof(CanStart))]
    private async Task RunAsync()
    {
        _running = new CancellationTokenSource();
        _stopAsked = false;
        IsRunning = true;
        Progress = 0;
        LiveSpeed = LiveLatency = string.Empty;

        try
        {
            SpeedTestResult? result = await Task.Run(() => _tests.RunAsync(scheduled: false, _running.Token)).ConfigureAwait(true);

            if (result is null && !_running.IsCancellationRequested)
            {
                Phase = "The test could not start.";
            }
            else if (_running.IsCancellationRequested)
            {
                Phase = "Stopped. Nothing was kept.";
            }
        }
        catch (Exception ex) when (ex is System.Net.Http.HttpRequestException or System.IO.IOException or System.Net.Sockets.SocketException or System.Net.WebSockets.WebSocketException or TimeoutException)
        {
            // The test answers its own failures; this is the backstop for one it did not foresee.
            Phase = "The test could not reach the server.";
        }
        finally
        {
            _running.Dispose();
            _running = null;
            IsRunning = _tests.Progress.IsActive;
            Reflect();
        }
    }

    private bool CanStart() => CanRun && !IsRunning;

    /// <summary>Stops the running test, whoever started it: the person, or the schedule.</summary>
    [RelayCommand(CanExecute = nameof(IsRunning))]
    private void Stop()
    {
        _stopAsked = true;
        _running?.Cancel();
        _tests.Stop();
    }

    private void OnProgressed(object? sender, SpeedTestProgress progress) => _dispatcher.BeginInvoke(() => ApplyProgress(progress));

    private void ApplyProgress(SpeedTestProgress progress)
    {
        IsRunning = progress.IsActive || _running is not null;

        if (progress.Phase == SpeedTestPhase.Idle)
        {
            Phase = _stopAsked ? "Stopped. Nothing was kept." : string.Empty;
            Progress = 0;
            Reflect();
            return;
        }

        string who = progress.Scheduled ? "Automatic test: " : string.Empty;

        Phase = who + progress.Phase switch
        {
            SpeedTestPhase.Locating => "Finding a server…",
            SpeedTestPhase.QuietLatency => string.Concat("Timing the quiet line", progress.Server is { } server ? string.Concat(" · ", server) : string.Empty, "…"),
            SpeedTestPhase.Download => "Measuring download…",
            SpeedTestPhase.Upload => "Measuring upload…",
            SpeedTestPhase.Done => "Done.",
            SpeedTestPhase.Failed => "The test did not finish.",
            _ => string.Empty,
        };

        // Each direction runs about ten seconds and reports four times a
        // second, so each report moves the bar a fortieth of its share.
        Progress = progress.Phase switch
        {
            SpeedTestPhase.Locating => 0.05,
            SpeedTestPhase.QuietLatency => 0.15,
            SpeedTestPhase.Download => Math.Min(0.58, Math.Max(Progress, 0.2) + 0.0095),
            SpeedTestPhase.Upload => Math.Min(0.98, Math.Max(Progress, 0.6) + 0.0095),
            SpeedTestPhase.Done or SpeedTestPhase.Failed => 1,
            _ => 0,
        };

        if (progress.BitsPerSecond is { } speed)
        {
            LiveSpeed = SpeedGrades.Mbps(speed);
        }

        if (progress.LatencyMs is { } ms)
        {
            LiveLatency = string.Create(CultureInfo.CurrentCulture, $"{ms:0} ms response time");
        }

        if (!progress.IsActive)
        {
            Reflect();
        }
    }

    private void OnFinished(object? sender, SpeedTestResult result) => _dispatcher.BeginInvoke(() => _ = LoadAsync());

    private void OnRescheduled(object? sender, EventArgs e) => _dispatcher.BeginInvoke(Reflect);

    private void OnStoreChanged(object? sender, EventArgs e) => _dispatcher.BeginInvoke(() => _ = LoadAsync());

    private async Task LoadAsync()
    {
        DateTimeOffset now = DateTimeOffset.Now;
        double planDown = _settings.ConnectionTools.PlanDownloadMbps;
        double planUp = _settings.ConnectionTools.PlanUploadMbps;

        (IReadOnlyList<SpeedTestResult> tests, IReadOnlyList<UsageBucket> hourly, bool failed) = await Task.Run(() =>
        {
            try
            {
                IReadOnlyList<SpeedTestResult> kept = _store.ReadSpeedTests(now - HistorySpan, now.AddMinutes(1));
                IReadOnlyList<UsageBucket> hours = planDown > 0 || planUp > 0
                    ? Read(() => _usage.Read(now - PlanSpan, now, UsageGranularity.Hour))
                    : [];
                return (kept, hours, false);
            }
            catch (Exception ex) when (HistoryFailure.IsStorage(ex) || ex is InvalidOperationException)
            {
                return ((IReadOnlyList<SpeedTestResult>)[], (IReadOnlyList<UsageBucket>)[], true);
            }
        }).ConfigureAwait(true);

        ShowLatest(tests, now);
        ShowHistory(tests, now, planDown, failed);
        ShowPlan(tests, hourly, planDown, planUp);
        Reflect();
    }

    private static IReadOnlyList<UsageBucket> Read(Func<IReadOnlyList<UsageBucket>> read)
    {
        try
        {
            return read();
        }
        catch (Exception ex) when (HistoryFailure.IsStorage(ex))
        {
            return [];
        }
    }

    private void ShowLatest(IReadOnlyList<SpeedTestResult> tests, DateTimeOffset now)
    {
        SpeedTestResult? latest = tests.FirstOrDefault(each => each.Succeeded);
        SpeedTestResult? last = tests.Count > 0 ? tests[0] : null;

        if (latest is null)
        {
            DownloadValue = UploadValue = QuietValue = LoadedValue = GradeValue = ServerValue = "—";
            GradeNote = string.Empty;
            ResultNote = last?.Problem is { } problem
                ? string.Concat("The last test did not finish: ", problem)
                : "Run a test to see your speed here.";
            Subtitle = "No tests yet";
            return;
        }

        DownloadValue = SpeedGrades.Mbps(latest.DownloadBitsPerSecond);
        UploadValue = SpeedGrades.Mbps(latest.UploadBitsPerSecond);
        QuietValue = latest.IdleMs is { } idle ? string.Create(CultureInfo.CurrentCulture, $"{idle:0} ms") : "—";
        LoadedValue = latest.LoadedIncreaseMs is { } more ? string.Create(CultureInfo.CurrentCulture, $"+{more:0} ms") : "—";
        GradeValue = SpeedGrades.Letter(latest.Bloat);
        GradeNote = latest.Bloat switch
        {
            BloatGrade.APlus or BloatGrade.A => "Calls and games stay smooth while the line is busy",
            BloatGrade.B => "A little lag when the line is busy",
            BloatGrade.C => "Calls may stutter while something downloads",
            BloatGrade.D or BloatGrade.F => "Busy moments ruin calls and games: the router's queue fills",
            _ => "Response time was not measured under load",
        };
        ServerValue = latest.Server;
        ResultNote = string.Concat(
            NetworkWords.Ago(latest.At, now),
            latest.Latency switch
            {
                LatencySource.InternetTarget => " · response times from pings to your chosen internet address",
                LatencySource.ServerTcp => " · response times from the test server's own measure",
                LatencySource.ServerRequests => " · response times from small requests to your server",
                _ => string.Empty,
            });
        Subtitle = string.Concat("Last test ", SpeedGrades.Mbps(latest.DownloadBitsPerSecond), " down, ", SpeedGrades.Mbps(latest.UploadBitsPerSecond), " up, ", NetworkWords.Ago(latest.At, now));
    }

    private void ShowHistory(IReadOnlyList<SpeedTestResult> tests, DateTimeOffset now, double planDown, bool failed)
    {
        Tests.Clear();

        foreach (SpeedTestResult test in tests.Take(100))
        {
            Tests.Add(new SpeedTestRow(test, now, DeleteAsync));
        }

        HasTests = Tests.Count > 0;

        SpeedTestResult[] good = [.. tests.Where(each => each.Succeeded).Take(30).Reverse()];
        Trend = [.. good.Select(test => new MetricBar(
            test.At.ToString("d MMM", ClockFormat.Current.Format),
            test.DownloadMbps,
            string.Concat(ClockFormat.Day(test.At), ": ", SpeedGrades.Mbps(test.DownloadBitsPerSecond), " down, ", SpeedGrades.Mbps(test.UploadBitsPerSecond), " up."),
            test.UploadMbps))];
        TrendReference = planDown > 0 ? planDown : null;

        HistoryNote = failed
            ? "The connection log could not be read."
            : tests.Count == 0
                ? "Every test you run is kept here, on this PC, for good, until you delete it."
                : string.Create(CultureInfo.CurrentCulture, $"{tests.Count} test{(tests.Count == 1 ? string.Empty : "s")} in the last 90 days. Bars are download, the thin bar beside each upload{(planDown > 0 ? "; the dashed line is your plan's download" : string.Empty)}.");
    }

    private void ShowPlan(IReadOnlyList<SpeedTestResult> tests, IReadOnlyList<UsageBucket> hourly, double planDown, double planUp)
    {
        if (planDown <= 0 && planUp <= 0)
        {
            PlanSummary = "Type your plan's speeds, as your provider sells them, to see how much of them arrives at each hour of the day.";
            PlanBars = [];
            HasPlanBars = false;
            return;
        }

        PlanSpeedSummary summary = PlanSpeed.Compare(planDown, planUp, tests.Where(each => each.At > DateTimeOffset.Now - PlanSpan), hourly);

        PlanBars = [.. summary.Hours.Select(hour => new MetricBar(
            HourName(hour.Hour),
            hour.DownloadShare ?? hour.PeakDownloadShare,
            HourDetail(hour)))];
        HasPlanBars = summary.Hours.Any(each => each.DownloadShare is not null || each.PeakDownloadShare is not null);

        PlanSummary = summary.Tests == 0
            ? "No speed tests in the last 30 days, so the bars show only the busiest second history saw each hour - what was asked of the line, not what it can do. Run tests at different times to see more."
            : string.Create(
                CultureInfo.CurrentCulture,
                $"Over {summary.Tests} test{(summary.Tests == 1 ? string.Empty : "s")} in 30 days the median reached {Share(summary.DownloadShare)} of your plan's download and {Share(summary.UploadShare)} of its upload{(summary.SlowestHour is { } slowest ? string.Concat("; slowest around ", HourName(slowest)) : string.Empty)}. Hours without a test show the busiest second history saw instead.");
    }

    /// <summary>An hour of the day as the clock writes it: "21:00", or "9 PM".</summary>
    private static string HourName(int hour) => ClockFormat.Current.Hour(DateTime.Today.AddHours(hour));

    private static string HourDetail(HourOfDay hour)
    {
        string label = string.Concat(HourName(hour.Hour), " to ", HourName((hour.Hour + 1) % 24), ": ");

        if (hour.DownloadShare is { } share)
        {
            return string.Concat(label, string.Create(CultureInfo.CurrentCulture, $"{hour.Tests} test{(hour.Tests == 1 ? string.Empty : "s")}, median {hour.DownloadMbps:0.0} Mbit/s, {share:0%} of the plan."));
        }

        return hour.PeakDownloadMbps is { } peak
            ? string.Concat(label, string.Create(CultureInfo.CurrentCulture, $"no test; busiest second {peak:0.0} Mbit/s."))
            : string.Concat(label, "nothing measured.");
    }

    private static string Share(double? share) => share is { } value ? value.ToString("0%", CultureInfo.CurrentCulture) : "—";

    private async Task DeleteAsync(SpeedTestRow row)
    {
        await Task.Run(() =>
        {
            try
            {
                _store.DeleteSpeedTest(row.Result.Id);
            }
            catch (Exception ex) when (HistoryFailure.IsStorage(ex))
            {
                LogDeleteFailed(ex);
            }
        }).ConfigureAwait(true);
    }

    [RelayCommand]
    private void AskClear() => IsConfirmingClear = true;

    [RelayCommand]
    private void CancelClear() => IsConfirmingClear = false;

    [RelayCommand]
    private async Task ClearAsync()
    {
        IsConfirmingClear = false;

        await Task.Run(() =>
        {
            try
            {
                _store.ClearSpeedTests();
            }
            catch (Exception ex) when (HistoryFailure.IsStorage(ex))
            {
                LogDeleteFailed(ex);
            }
        }).ConfigureAwait(true);
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "Speed tests could not be deleted")]
    private partial void LogDeleteFailed(Exception error);

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        Hide();
        _running?.Cancel();
        _disposed = true;
    }
}
