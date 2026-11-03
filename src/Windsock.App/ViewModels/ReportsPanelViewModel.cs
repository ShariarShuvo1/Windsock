using System.Collections.ObjectModel;
using System.Globalization;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using Windsock.App.Controls;
using Windsock.App.Services;
using Windsock.Core.Apps;
using Windsock.Core.History;
using Windsock.Core.Networking;
using Windsock.Core.Reports;
using Windsock.Core.Settings;

namespace Windsock.App.ViewModels;

/// <summary>
/// Backs Usage › Reports: a day, week, month or year with its peaks, busiest
/// times, networks, apps and outages and the change from the one before; two
/// periods compared; a year's calendar; the hours of the week; and everything
/// since recording began.
/// </summary>
/// <remarks>
/// Everything is read off the interface thread from <see cref="UsageReports"/>,
/// and a reading that a newer one overtook is dropped. Reports are of the past
/// and of today so far: they are read when the page is shown or a choice
/// changes, not every minute.
/// </remarks>
public sealed partial class ReportsPanelViewModel : ObservableObject, IDisposable
{
    private const int TopApps = 8;

    private readonly UsageReports _reports;
    private readonly IUsageAggregates _aggregates;
    private readonly IUsageHistoryStore _history;
    private readonly CurrentNetwork _network;
    private readonly AppNames _names;
    private readonly AppIconCache _icons;
    private readonly WindsockSettings _settings;
    private readonly Dispatcher _dispatcher;
    private readonly ILogger<ReportsPanelViewModel> _logger;
    private readonly bool _ready;
    private int _loads;
    private bool _shown;
    private bool _stale = true;
    private long _loadedRevision = long.MinValue;

    public ReportsPanelViewModel(
        UsageReports reports,
        IUsageAggregates aggregates,
        IUsageHistoryStore history,
        CurrentNetwork network,
        AppNames names,
        AppIconCache icons,
        WindsockSettings settings,
        Dispatcher dispatcher,
        ILogger<ReportsPanelViewModel> logger)
    {
        _reports = reports;
        _aggregates = aggregates;
        _history = history;
        _network = network;
        _names = names;
        _icons = icons;
        _settings = settings;
        _dispatcher = dispatcher;
        _logger = logger;

        Views =
        [
            new(ReportView.Report, "Report"),
            new(ReportView.Compare, "Compare"),
            new(ReportView.Calendar, "Year"),
            new(ReportView.Week, "Week by hour"),
            new(ReportView.Lifetime, "All time"),
        ];

        Kinds =
        [
            new(PeriodKind.Day, "Day"),
            new(PeriodKind.Week, "Week"),
            new(PeriodKind.Month, "Month"),
            new(PeriodKind.Year, "Year"),
        ];

        CompareOptions =
        [
            new(CompareWith.PeriodBefore, "With the one before"),
            new(CompareWith.SameLastYear, "With a year before"),
        ];

        HeatSpans =
        [
            new(HeatSpan.FourWeeks, "Last 4 weeks"),
            new(HeatSpan.TwelveWeeks, "Last 12 weeks"),
            new(HeatSpan.Year, "Last year"),
        ];

        Networks = [];
        Apps = [];
        AppChanges = [];

        SelectedView = ReportView.Report;
        Kind = PeriodKind.Month;
        Period = ReportPeriod.Holding(PeriodKind.Month, DateTimeOffset.Now, _reports.Zone, FirstDay);
        CalendarYear = DateTime.Today.Year;
        _history.Erased += OnErased;
        _icons.Loaded += OnIconLoaded;
        _ready = true;
    }

    public IReadOnlyList<ReportViewOption> Views { get; }

    public IReadOnlyList<EditorOption<PeriodKind>> Kinds { get; }

    public IReadOnlyList<EditorOption<CompareWith>> CompareOptions { get; }

    public IReadOnlyList<EditorOption<HeatSpan>> HeatSpans { get; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsReport), nameof(IsCompare), nameof(IsCalendar), nameof(IsWeek), nameof(IsLifetime), nameof(UsesPeriod), nameof(CanSaveCard))]
    public partial ReportView SelectedView { get; set; }

    public bool IsReport => SelectedView == ReportView.Report;

    public bool IsCompare => SelectedView == ReportView.Compare;

    public bool IsCalendar => SelectedView == ReportView.Calendar;

    public bool IsWeek => SelectedView == ReportView.Week;

    public bool IsLifetime => SelectedView == ReportView.Lifetime;

    /// <summary>Whether the tab steps through periods.</summary>
    public bool UsesPeriod => IsReport || IsCompare;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanSaveCard))]
    public partial PeriodKind Kind { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(PeriodText), nameof(CanGoNext))]
    public partial ReportPeriod Period { get; private set; }

    /// <summary>The period in words: "October 2026".</summary>
    public string PeriodText => IsCalendar
        ? CalendarYear.ToString(CultureInfo.CurrentCulture)
        : Period.Describe();

    /// <summary>Whether there is a later period to go to: not past today.</summary>
    public bool CanGoNext => IsCalendar ? CalendarYear < DateTime.Today.Year : Period.Until <= DateTimeOffset.Now;

    [ObservableProperty]
    public partial CompareWith CompareWith { get; set; }

    [ObservableProperty]
    public partial HeatSpan HeatSpan { get; set; } = HeatSpan.TwelveWeeks;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(PeriodText), nameof(CanGoNext))]
    public partial int CalendarYear { get; private set; }

    /// <summary>Whether to rank and compare by internet bytes alone, leaving out what stayed local.</summary>
    [ObservableProperty]
    public partial bool InternetOnly { get; set; }

    /// <summary>Whether any of history knows what stayed on the local network, so the switch means something.</summary>
    [ObservableProperty]
    public partial bool HasLocalData { get; private set; }

    [ObservableProperty]
    public partial bool IsBusy { get; private set; }

    /// <summary>Why a report could not be made, or empty.</summary>
    [ObservableProperty]
    public partial string Notice { get; private set; } = string.Empty;

    /// <summary>Whether the month on show can be saved as a picture.</summary>
    public bool CanSaveCard => IsReport && Kind == PeriodKind.Month && LastReport is { Totals.HasData: true };

    /// <summary>The report on show, for the month's card.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanSaveCard))]
    public partial PeriodReport? LastReport { get; private set; }

    // ---------------- Report ----------------

    [ObservableProperty]
    public partial string TotalText { get; private set; } = "—";

    [ObservableProperty]
    public partial string TotalNote { get; private set; } = string.Empty;

    [ObservableProperty]
    public partial string DownText { get; private set; } = "—";

    [ObservableProperty]
    public partial string UpText { get; private set; } = "—";

    [ObservableProperty]
    public partial string PeakDownText { get; private set; } = string.Empty;

    [ObservableProperty]
    public partial string PeakUpText { get; private set; } = string.Empty;

    [ObservableProperty]
    public partial string AverageText { get; private set; } = "—";

    [ObservableProperty]
    public partial string AverageNote { get; private set; } = string.Empty;

    [ObservableProperty]
    public partial string ChangeText { get; private set; } = "—";

    [ObservableProperty]
    public partial string ChangeNote { get; private set; } = string.Empty;

    [ObservableProperty]
    public partial string BusiestText { get; private set; } = string.Empty;

    [ObservableProperty]
    public partial string OutagesText { get; private set; } = string.Empty;

    [ObservableProperty]
    public partial string EstimateNote { get; private set; } = string.Empty;

    [ObservableProperty]
    public partial IReadOnlyList<ChartBar> SeriesBars { get; private set; } = [];

    [ObservableProperty]
    public partial IReadOnlyList<ChartBar> HourBars { get; private set; } = [];

    [ObservableProperty]
    public partial IReadOnlyList<ChartBar> WeekdayBars { get; private set; } = [];

    /// <summary>Whether the day-of-week profile is worth showing: a period longer than a week.</summary>
    [ObservableProperty]
    public partial bool HasWeekdays { get; private set; }

    [ObservableProperty]
    public partial bool IsEmpty { get; private set; }

    public ObservableCollection<ShareRow> Networks { get; }

    public ObservableCollection<AppReportRow> Apps { get; }

    [ObservableProperty]
    public partial bool HasNetworks { get; private set; }

    [ObservableProperty]
    public partial bool HasApps { get; private set; }

    // ---------------- Compare ----------------

    [ObservableProperty]
    public partial string BaselineName { get; private set; } = string.Empty;

    [ObservableProperty]
    public partial string BaselineText { get; private set; } = "—";

    /// <summary>What the baseline had come to by the same point, while the period compared is under way.</summary>
    [ObservableProperty]
    public partial string BaselineNote { get; private set; } = string.Empty;

    [ObservableProperty]
    public partial string ComparedName { get; private set; } = string.Empty;

    [ObservableProperty]
    public partial string ComparedText { get; private set; } = "—";

    [ObservableProperty]
    public partial string DifferenceText { get; private set; } = "—";

    [ObservableProperty]
    public partial string DifferenceNote { get; private set; } = string.Empty;

    [ObservableProperty]
    public partial IReadOnlyList<ChartBar> CompareBars { get; private set; } = [];

    public ObservableCollection<AppReportRow> AppChanges { get; }

    [ObservableProperty]
    public partial bool HasAppChanges { get; private set; }

    // ---------------- Calendar ----------------

    [ObservableProperty]
    public partial IReadOnlyList<CalendarCell> CalendarCells { get; private set; } = [];

    [ObservableProperty]
    public partial string CalendarText { get; private set; } = string.Empty;

    // ---------------- Week by hour ----------------

    [ObservableProperty]
    public partial IReadOnlyList<WeekCell> HeatCells { get; private set; } = [];

    [ObservableProperty]
    public partial IReadOnlyList<string> HeatRows { get; private set; } = [];

    [ObservableProperty]
    public partial string HeatText { get; private set; } = string.Empty;

    // ---------------- Lifetime ----------------

    [ObservableProperty]
    public partial string LifetimeText { get; private set; } = "—";

    [ObservableProperty]
    public partial string LifetimeNote { get; private set; } = string.Empty;

    [ObservableProperty]
    public partial string LifetimeBusiest { get; private set; } = string.Empty;

    [ObservableProperty]
    public partial string LifetimePeak { get; private set; } = string.Empty;

    [ObservableProperty]
    public partial IReadOnlyList<ChartBar> YearBars { get; private set; } = [];

    /// <summary>The day weeks start on: the digest's choice, else the region's.</summary>
    public DayOfWeek FirstDay => _settings.Usage.Digest.WeekStarts ?? CultureInfo.CurrentCulture.DateTimeFormat.FirstDayOfWeek;

    /// <summary>Reads what is on show, as the page comes into sight.</summary>
    public void Show()
    {
        _shown = true;

        // Read again when something might have changed while away: an erase,
        // an import (history's revision), or a view that reaches up to now.
        if (_stale || _aggregates.Revision != _loadedRevision || ReachesNow())
        {
            _stale = false;
            _ = LoadAsync();
        }
    }

    /// <summary>Whether what is on show runs up to now, so time passing changes it.</summary>
    private bool ReachesNow() => SelectedView switch
    {
        ReportView.Calendar => CalendarYear >= DateTime.Today.Year,
        ReportView.Week or ReportView.Lifetime => true,
        _ => Period.Until > DateTimeOffset.Now,
    };

    public void Hide() => _shown = false;

    /// <summary>The month on show as its picture would say it, or null when there is no month to draw.</summary>
    public MonthCardModel? Card()
    {
        if (!CanSaveCard || LastReport is not { } report)
        {
            return null;
        }

        string change = ChangeText == "—" ? ChangeNote : string.Concat(ChangeText, ". ", ChangeNote);
        string footer = string.Format(
            CultureInfo.CurrentCulture,
            "Measured by Windsock on this PC. {0} {1}.",
            report.IsUnderWay ? "So far, on" : "Made",
            UsageText.FullDate(DateOnly.FromDateTime(DateTime.Today)));

        return new MonthCardModel(
            PeriodText,
            report.InternetOnly ? "Internet traffic, leaving out the local network" : "Everything this PC sent and received",
            TotalText,
            change,
            DownText,
            UpText,
            AverageText,
            SeriesBars,
            BusiestText,
            [.. Apps.Take(3).Select(app => new ShareRow(app.Name, app.Amount, app.Share))],
            footer);
    }

    /// <summary>Says something about the page across its top, such as why a picture was not saved.</summary>
    public void Tell(string message) => Notice = message;

    /// <summary>The file name a month's picture is offered under: "Windsock-2026-10.png".</summary>
    public string CardFileName() => string.Create(
        CultureInfo.InvariantCulture,
        $"Windsock-{Period.First:yyyy-MM}.png");

    public void Dispose()
    {
        _history.Erased -= OnErased;
        _icons.Loaded -= OnIconLoaded;
    }

    private void OnErased(object? sender, EventArgs e) =>
        _dispatcher.BeginInvoke(DispatcherPriority.Background, new Action(() =>
        {
            if (_shown)
            {
                _ = LoadAsync();
            }
            else
            {
                _stale = true;
            }
        }));

    private void OnIconLoaded(object? sender, EventArgs e) =>
        _dispatcher.BeginInvoke(DispatcherPriority.Background, new Action(() =>
        {
            foreach (AppReportRow row in Apps.Concat(AppChanges))
            {
                row.Icon ??= _icons.Find(row.App);
            }
        }));

    partial void OnSelectedViewChanged(ReportView value)
    {
        OnPropertyChanged(nameof(PeriodText));
        OnPropertyChanged(nameof(CanGoNext));
        Reload();
    }

    partial void OnKindChanged(PeriodKind value)
    {
        if (!_ready)
        {
            return;
        }

        // Keep to the same moment: the month holding the day on show, and so on.
        DateTimeOffset at = Period.Until <= DateTimeOffset.Now ? Period.Until.AddMinutes(-1) : DateTimeOffset.Now;
        Period = ReportPeriod.Holding(value, at, _reports.Zone, FirstDay);
        Reload();
    }

    partial void OnCompareWithChanged(CompareWith value) => Reload();

    partial void OnHeatSpanChanged(HeatSpan value) => Reload();

    partial void OnInternetOnlyChanged(bool value) => Reload();

    private void Reload()
    {
        if (_ready && _shown)
        {
            _ = LoadAsync();
        }
        else
        {
            _stale = true;
        }
    }

    [RelayCommand]
    private void Earlier()
    {
        if (IsCalendar)
        {
            CalendarYear--;
        }
        else
        {
            Period = Period.Previous(_reports.Zone);
        }

        Reload();
    }

    [RelayCommand]
    private void Later()
    {
        if (!CanGoNext)
        {
            return;
        }

        if (IsCalendar)
        {
            CalendarYear++;
        }
        else
        {
            Period = Period.Next(_reports.Zone);
        }

        Reload();
    }

    [RelayCommand]
    private void Latest()
    {
        CalendarYear = DateTime.Today.Year;
        Period = ReportPeriod.Holding(Kind, DateTimeOffset.Now, _reports.Zone, FirstDay);
        Reload();
    }

    private async Task LoadAsync()
    {
        int token = ++_loads;
        _loadedRevision = _aggregates.Revision;
        IsBusy = true;
        Notice = string.Empty;
        ReportView view = SelectedView;
        ReportPeriod period = Period;
        bool internet = InternetOnly;

        try
        {
            switch (view)
            {
                case ReportView.Report:
                    PeriodReport report = await Task.Run(() => _reports.Report(period, TopApps, withChange: true, internet)).ConfigureAwait(true);
                    if (token == _loads)
                    {
                        ShowReport(report);
                    }

                    break;

                case ReportView.Compare:
                    ReportPeriod baseline = CompareWith == CompareWith.SameLastYear ? period.SameLastYear(_reports.Zone) : period.Previous(_reports.Zone);
                    PeriodComparison comparison = await Task.Run(() => _reports.Compare(baseline, period, 12, internet)).ConfigureAwait(true);
                    if (token == _loads)
                    {
                        ShowComparison(comparison);
                    }

                    break;

                case ReportView.Calendar:
                    int year = CalendarYear;
                    YearCalendar calendar = await Task.Run(() => _reports.Calendar(year, internet)).ConfigureAwait(true);
                    if (token == _loads)
                    {
                        ShowCalendar(calendar);
                    }

                    break;

                case ReportView.Week:
                    DateTimeOffset until = DateTimeOffset.Now;
                    DateTimeOffset from = HeatSpan switch
                    {
                        HeatSpan.FourWeeks => until.AddDays(-28),
                        HeatSpan.Year => until.AddYears(-1),
                        _ => until.AddDays(-84),
                    };
                    WeekHeat heat = await Task.Run(() => _reports.WeekHeat(from, until, internet)).ConfigureAwait(true);
                    if (token == _loads)
                    {
                        ShowHeat(heat);
                    }

                    break;

                default:
                    LifetimeReport lifetime = await Task.Run(() => _reports.Lifetime(internet)).ConfigureAwait(true);
                    if (token == _loads)
                    {
                        ShowLifetime(lifetime);
                    }

                    break;
            }

            if (token == _loads)
            {
                await CheckLocalAsync().ConfigureAwait(true);
            }
        }
        catch (Exception ex) when (HistoryFailure.IsStorage(ex))
        {
            if (token == _loads)
            {
                LogLoadFailed(ex);
                Notice = "The history file could not be read, so there is no report to show. It may be in use by a newer version of Windsock, or damaged.";
            }
        }
        finally
        {
            if (token == _loads)
            {
                IsBusy = false;
            }
        }
    }

    /// <summary>Whether the last year holds any local-network share, which offers the internet-only switch.</summary>
    private async Task CheckLocalAsync()
    {
        if (HasLocalData)
        {
            return;
        }

        long until = EpochMinute.From(DateTimeOffset.Now) + 1;
        HasLocalData = await Task.Run(() => _aggregates.ReadQuarters(until - (366L * 1440), until).Any(quarter => quarter.LocalMinutes > 0)).ConfigureAwait(true);
    }

    private void ShowReport(PeriodReport report)
    {
        LastReport = report;
        UsageTotals totals = report.Totals;
        IsEmpty = !totals.HasData;

        TotalText = totals.HasData ? UsageText.Bytes(totals.All) : "—";
        TotalNote = report.IsUnderWay ? "So far" : string.Empty;
        DownText = totals.HasData ? UsageText.Bytes(totals.AllDown) : "—";
        UpText = totals.HasData ? UsageText.Bytes(totals.AllUp) : "—";
        PeakDownText = report.PeakDown is { } down
            ? string.Format(CultureInfo.CurrentCulture, "Fastest {0}/s, {1}", UsageText.Bytes(down.BytesPerSecond), UsageText.When(down.At))
            : string.Empty;
        PeakUpText = report.PeakUp is { } up
            ? string.Format(CultureInfo.CurrentCulture, "Fastest {0}/s, {1}", UsageText.Bytes(up.BytesPerSecond), UsageText.When(up.At))
            : string.Empty;
        AverageText = totals.HasData && report.Period.Kind != PeriodKind.Day ? UsageText.Bytes(report.DailyAverage) : "—";
        AverageNote = report.Period.Kind == PeriodKind.Day ? "A single day" : "A day, on average";

        if (report.Change is { } change && change.Before.HasData)
        {
            ChangeText = change.Percent is { } percent
                ? string.Format(CultureInfo.CurrentCulture, "{0}{1}", percent >= 0 ? "+" : "−", UsageText.Percent(Math.Abs(percent)))
                : "—";
            ChangeNote = string.Format(
                CultureInfo.CurrentCulture,
                "{0} {1}{2}",
                change.Difference >= 0 ? "Up " + UsageText.Bytes(change.Difference) : "Down " + UsageText.Bytes(-change.Difference),
                change.SameElapsed ? "on the same stretch of " : "on ",
                report.Period.Previous(_reports.Zone).Describe());
        }
        else
        {
            ChangeText = "—";
            ChangeNote = "Nothing recorded in the period before";
        }

        List<string> busiest = [];

        if (report.BusiestDay is { } day)
        {
            busiest.Add(string.Format(CultureInfo.CurrentCulture, "The busiest day was {0}, with {1}.", day.Start.ToString("dddd d MMMM", CultureInfo.CurrentCulture), UsageText.Bytes(day.Totals.Measure(report.InternetOnly))));
        }

        if (report.BusiestHour is { } hour)
        {
            busiest.Add(string.Format(CultureInfo.CurrentCulture, "The busiest hour began {0}, with {1}.", UsageText.When(hour.Start), UsageText.Bytes(hour.Totals.Measure(report.InternetOnly))));
        }

        BusiestText = string.Join(" ", busiest);
        OutagesText = report.Outages.Count switch
        {
            0 => "No outages were logged.",
            1 => string.Format(CultureInfo.CurrentCulture, "1 outage, {0} offline.", Core.Formatting.DurationLabel.Format(report.Outages.Offline.TotalSeconds)),
            _ => string.Format(CultureInfo.CurrentCulture, "{0} outages, {1} offline in all.", report.Outages.Count, Core.Formatting.DurationLabel.Format(report.Outages.Offline.TotalSeconds)),
        };

        EstimateNote = totals.Estimated > 0
            ? string.Format(CultureInfo.CurrentCulture, "Includes {0} Windows counted while Windsock was closed, drawn fainter.", UsageText.Bytes(totals.Estimated))
            : string.Empty;

        SeriesBars = [.. report.Series.Select(point => Bar(point, report.Period.Step, report.InternetOnly))];
        HourBars = [.. report.HourOfDay.Select((bytes, hour) => new ChartBar(
            hour % 6 == 0 ? UsageText.Hour(hour) : string.Empty,
            string.Format(CultureInfo.CurrentCulture, "{0} to {1}: {2}", UsageText.Time(new TimeOnly(hour, 0)), UsageText.Time(new TimeOnly((hour + 1) % 24, 0)), UsageText.Bytes(bytes)),
            bytes))];

        DayOfWeek first = report.Period.FirstDayOfWeek;
        WeekdayBars = [.. Enumerable.Range(0, 7).Select(offset =>
        {
            DayOfWeek day = (DayOfWeek)(((int)first + offset) % 7);
            long bytes = report.DayOfWeek[(int)day];
            return new ChartBar(
                CultureInfo.CurrentCulture.DateTimeFormat.GetAbbreviatedDayName(day),
                string.Format(CultureInfo.CurrentCulture, "{0}s: {1}", CultureInfo.CurrentCulture.DateTimeFormat.GetDayName(day), UsageText.Bytes(bytes)),
                bytes);
        })];
        HasWeekdays = report.Period.Length > TimeSpan.FromDays(7.5);

        long all = Math.Max(1, report.Networks.Sum(network => network.Total));
        Networks.Clear();

        foreach (NetworkShare network in report.Networks)
        {
            string name = !network.IsKnown || network.Key is null
                ? "Network not known"
                : _network.NameOf(new NetworkTag(network.Key, network.Name ?? network.Key, network.Kind ?? Core.Connectivity.NetworkKind.Other));
            Networks.Add(new ShareRow(name, UsageText.Bytes(network.Total), (double)network.Total / all));
        }

        HasNetworks = Networks.Count > 1 || Networks.Any(row => row.Name == "Network not known");

        long appsAll = Math.Max(1, report.TopApps.Sum(app => app.Total));
        Apps.Clear();

        foreach (AppShare share in report.TopApps)
        {
            AppDescription app = _names.Describe(share.AttributionId);
            string note = share.Measured > 0 && share.Windows > 0 ? "Measured and counted by Windows"
                : share.Measured > 0 ? "Measured by Windsock"
                : "Counted by Windows";
            Apps.Add(new AppReportRow(app, UsageText.Bytes(share.Total), (double)share.Total / Math.Max(appsAll, totals.All), note) { Icon = _icons.Find(app) });
        }

        HasApps = Apps.Count > 0;
    }

    private static ChartBar Bar(ReportPoint point, CalendarStep step, bool internetOnly)
    {
        DateTime start = point.Start.LocalDateTime;
        string label = step switch
        {
            CalendarStep.Hour => start.Hour % 6 == 0 ? UsageText.Hour(start.Hour) : string.Empty,
            CalendarStep.Month => start.ToString("MMM", CultureInfo.CurrentCulture),
            _ => start.Day.ToString(CultureInfo.CurrentCulture),
        };

        string when = step switch
        {
            CalendarStep.Hour => UsageText.When(point.Start),
            CalendarStep.Month => start.ToString("MMMM yyyy", CultureInfo.CurrentCulture),
            _ => UsageText.Day(DateOnly.FromDateTime(start)),
        };

        UsageTotals totals = point.Totals;
        string detail = !totals.HasData
            ? when + ": nothing recorded"
            : string.Format(
                CultureInfo.CurrentCulture,
                "{0}: {1} down, {2} up{3}{4}",
                when,
                UsageText.Bytes(totals.Down),
                UsageText.Bytes(totals.Up),
                totals.Estimated > 0 ? string.Format(CultureInfo.CurrentCulture, ", and {0} counted by Windows", UsageText.Bytes(totals.Estimated)) : string.Empty,
                totals.Local > 0 ? string.Format(CultureInfo.CurrentCulture, "; {0} stayed local", UsageText.Bytes(totals.Local)) : string.Empty);

        // On internet bytes alone, what stayed local comes off the download first.
        long localDown = internetOnly ? Math.Min(totals.Down, totals.LocalDown) : 0;
        long localUp = internetOnly ? Math.Min(totals.Up, totals.LocalUp) : 0;

        return new ChartBar(label, detail, totals.Down - localDown, totals.Up - localUp, totals.Estimated, IsFuture: point.Start > DateTimeOffset.Now);
    }

    private void ShowComparison(PeriodComparison comparison)
    {
        PeriodReport first = comparison.Baseline;
        PeriodReport second = comparison.Compared;
        bool internet = second.InternetOnly;

        BaselineName = first.Period.Describe();
        ComparedName = second.Period.Describe() + (second.IsUnderWay ? " so far" : string.Empty);
        BaselineText = first.Totals.HasData ? UsageText.Bytes(first.Totals.Measure(internet)) : "—";
        ComparedText = second.Totals.HasData ? UsageText.Bytes(second.Totals.Measure(internet)) : "—";
        DifferenceText = comparison.Percent is { } percent
            ? string.Format(CultureInfo.CurrentCulture, "{0}{1}", percent >= 0 ? "+" : "−", UsageText.Percent(Math.Abs(percent)))
            : "—";
        long soFar = second.Totals.Measure(internet) - comparison.Difference;
        BaselineNote = comparison.SameElapsed && first.Totals.HasData
            ? UsageText.Bytes(soFar) + " by the same point"
            : string.Empty;
        DifferenceNote = !first.Totals.HasData
            ? "Nothing was recorded in " + BaselineName
            : string.Format(
                CultureInfo.CurrentCulture,
                "{0} {1} than {2}{3}",
                UsageText.Bytes(Math.Abs(comparison.Difference)),
                comparison.Difference >= 0 ? "more" : "less",
                comparison.SameElapsed ? "the same stretch of " : string.Empty,
                BaselineName);

        CompareBars = [.. comparison.Aligned.Select((pair, index) =>
        {
            ReportPoint? now = pair.Compared;
            ReportPoint? then = pair.Baseline;
            long nowBytes = now?.Totals.Measure(internet) ?? 0;
            long thenBytes = then?.Totals.Measure(internet) ?? 0;
            DateTimeOffset? at = now?.Start ?? then?.Start;
            string label = second.Period.Step switch
            {
                CalendarStep.Hour => index % 6 == 0 ? UsageText.Hour(index) : string.Empty,
                CalendarStep.Month => at?.LocalDateTime.ToString("MMM", CultureInfo.CurrentCulture) ?? string.Empty,
                _ => (index + 1).ToString(CultureInfo.CurrentCulture),
            };

            string detail = string.Format(
                CultureInfo.CurrentCulture,
                "{0}: {1} in {2}, {3} in {4}",
                second.Period.Step == CalendarStep.Hour ? UsageText.Time(new TimeOnly(index % 24, 0)) : second.Period.Step == CalendarStep.Month ? label : "Day " + label,
                now is null ? "—" : UsageText.Bytes(nowBytes),
                ComparedName,
                then is null ? "—" : UsageText.Bytes(thenBytes),
                BaselineName);

            return new ChartBar(label, detail, nowBytes, Paired: thenBytes, IsFuture: now is { } point && point.Start > DateTimeOffset.Now);
        })];

        AppChanges.Clear();

        foreach (AppChange change in comparison.Apps.Where(change => change.Difference != 0).Take(10))
        {
            AppDescription app = _names.Describe(change.AttributionId);
            string note = string.Format(CultureInfo.CurrentCulture, "{0} then, {1} now", UsageText.Bytes(change.Before), UsageText.Bytes(change.After));
            string amount = (change.Difference >= 0 ? "+" : "−") + UsageText.Bytes(Math.Abs(change.Difference));
            AppChanges.Add(new AppReportRow(app, amount, 0, note) { Icon = _icons.Find(app) });
        }

        HasAppChanges = AppChanges.Count > 0;
    }

    private void ShowCalendar(YearCalendar calendar)
    {
        CalendarCells = [.. calendar.Days.Select(day => new CalendarCell(
            day.Date,
            day.Shade,
            day.Totals.HasData
                ? string.Format(CultureInfo.CurrentCulture, "{0}: {1}{2}", day.Date.ToString("dddd d MMMM yyyy", CultureInfo.CurrentCulture), UsageText.Bytes(day.Totals.All), day.Totals.Estimated > 0 ? " (part counted by Windows)" : string.Empty)
                : day.Date.ToString("dddd d MMMM yyyy", CultureInfo.CurrentCulture) + ": nothing recorded"))];

        CalendarText = calendar.Totals.HasData
            ? string.Format(
                CultureInfo.CurrentCulture,
                "{0} in {1}{2}",
                UsageText.Bytes(calendar.Totals.All),
                calendar.Year,
                calendar.Busiest is { } busiest ? string.Format(CultureInfo.CurrentCulture, ". The busiest day was {0}, with {1}.", busiest.Date.ToString("d MMMM", CultureInfo.CurrentCulture), UsageText.Bytes(busiest.Totals.All)) : ".")
            : string.Format(CultureInfo.CurrentCulture, "Nothing was recorded in {0}.", calendar.Year);
    }

    private void ShowHeat(WeekHeat heat)
    {
        DayOfWeek first = FirstDay;
        double highest = heat.Highest;
        List<WeekCell> cells = [];
        WeekCell? busiest = null;
        double most = 0;

        for (int row = 0; row < 7; row++)
        {
            DayOfWeek day = (DayOfWeek)(((int)first + row) % 7);

            for (int hour = 0; hour < 24; hour++)
            {
                double average = heat.Average(day, hour);
                string detail = heat.Hours[(int)day][hour] == 0
                    ? string.Format(CultureInfo.CurrentCulture, "{0}s at {1}: nothing recorded", CultureInfo.CurrentCulture.DateTimeFormat.GetDayName(day), UsageText.Time(new TimeOnly(hour, 0)))
                    : string.Format(CultureInfo.CurrentCulture, "{0}s at {1}: {2} on average, over {3}", CultureInfo.CurrentCulture.DateTimeFormat.GetDayName(day), UsageText.Time(new TimeOnly(hour, 0)), UsageText.Bytes((long)average), UsageText.Count(heat.Hours[(int)day][hour], "hour", "hours"));
                WeekCell cell = new(row, hour, highest > 0 ? average / highest : 0, detail);
                cells.Add(cell);

                if (average > most)
                {
                    most = average;
                    busiest = cell;
                }
            }
        }

        HeatCells = cells;
        HeatRows = [.. Enumerable.Range(0, 7).Select(row => CultureInfo.CurrentCulture.DateTimeFormat.GetAbbreviatedDayName((DayOfWeek)(((int)first + row) % 7)))];
        HeatText = busiest is null
            ? "Nothing was recorded in this stretch."
            : "The busiest hour of the week: " + busiest.Detail + ".";
    }

    private void ShowLifetime(LifetimeReport lifetime)
    {
        LifetimeText = lifetime.Totals.HasData ? UsageText.Bytes(lifetime.Totals.All) : "—";
        LifetimeNote = lifetime.First is { } first
            ? string.Format(CultureInfo.CurrentCulture, "Since recording began, {0}", UsageText.FullDate(DateOnly.FromDateTime(first.LocalDateTime)))
            : "Nothing has been recorded yet";
        LifetimeBusiest = lifetime.BusiestDay is { } day
            ? string.Format(CultureInfo.CurrentCulture, "The busiest day ever was {0}, with {1}.", day.Start.ToString("dddd d MMMM yyyy", CultureInfo.CurrentCulture), UsageText.Bytes(day.Totals.All))
            : string.Empty;
        LifetimePeak = lifetime.PeakDown is { } peak
            ? string.Format(CultureInfo.CurrentCulture, "The fastest second in was {0}/s, {1}.", UsageText.Bytes(peak.BytesPerSecond), UsageText.When(peak.At))
            : string.Empty;
        YearBars = [.. lifetime.Years.Select(year => new ChartBar(
            year.Year.ToString(CultureInfo.CurrentCulture),
            string.Format(CultureInfo.CurrentCulture, "{0}: {1}", year.Year, UsageText.Bytes(year.Totals.All)),
            year.Totals.Down,
            year.Totals.Up,
            year.Totals.Estimated))];
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "Could not read history for a report")]
    private partial void LogLoadFailed(Exception exception);
}
