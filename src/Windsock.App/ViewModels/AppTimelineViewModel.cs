using System.Globalization;
using System.Windows.Media;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using Windsock.App.Controls;
using Windsock.App.Services;
using Windsock.Core.Apps;
using Windsock.Core.History;
using Windsock.Core.Reports;
using Windsock.Core.Settings;

namespace Windsock.App.ViewModels;

/// <summary>How far back one app's history looks, and in what steps.</summary>
public enum AppSpan
{
    TwoDays,
    Month,
    Quarter,
    Year,
}

/// <summary>
/// One app over time, opened from the Apps page's period table: what Windsock
/// measured of it, and what Windows counted where Windsock did not measure,
/// step by step.
/// </summary>
public sealed partial class AppTimelineViewModel : ObservableObject, IDisposable
{
    private readonly UsageReports _reports;
    private readonly AppIconCache _icons;
    private readonly IUsageHistoryStore _history;
    private readonly WindsockSettings _settings;
    private readonly Dispatcher _dispatcher;
    private readonly ILogger<AppTimelineViewModel> _logger;
    private AppDescription? _app;
    private int _loads;

    public AppTimelineViewModel(
        UsageReports reports,
        AppIconCache icons,
        IUsageHistoryStore history,
        WindsockSettings settings,
        Dispatcher dispatcher,
        ILogger<AppTimelineViewModel> logger)
    {
        ArgumentNullException.ThrowIfNull(icons);
        ArgumentNullException.ThrowIfNull(history);

        _reports = reports;
        _icons = icons;
        _history = history;
        _settings = settings;
        _dispatcher = dispatcher;
        _logger = logger;

        Spans =
        [
            new(AppSpan.TwoDays, "Last 48 hours"),
            new(AppSpan.Month, "Last 30 days"),
            new(AppSpan.Quarter, "Last 12 weeks"),
            new(AppSpan.Year, "Last 12 months"),
        ];

        _history.Erased += OnErased;
        _icons.Loaded += OnIconLoaded;
    }

    /// <summary>Raised when an app is opened, so the page can bring it into view.</summary>
    public event EventHandler? Opened;

    public IReadOnlyList<EditorOption<AppSpan>> Spans { get; }

    [ObservableProperty]
    public partial AppSpan Span { get; set; } = AppSpan.Month;

    [ObservableProperty]
    public partial bool IsOpen { get; private set; }

    [ObservableProperty]
    public partial string AppName { get; private set; } = string.Empty;

    [ObservableProperty]
    public partial ImageSource? AppIcon { get; private set; }

    [ObservableProperty]
    public partial IReadOnlyList<ChartBar> Bars { get; private set; } = [];

    [ObservableProperty]
    public partial string TotalText { get; private set; } = "—";

    [ObservableProperty]
    public partial string MeasuredText { get; private set; } = string.Empty;

    [ObservableProperty]
    public partial string WindowsText { get; private set; } = string.Empty;

    [ObservableProperty]
    public partial string BusiestText { get; private set; } = string.Empty;

    [ObservableProperty]
    public partial bool IsEmpty { get; private set; }

    [ObservableProperty]
    public partial bool IsBusy { get; private set; }

    /// <summary>Opens an app's history.</summary>
    public void Show(AppDescription app)
    {
        ArgumentNullException.ThrowIfNull(app);

        _app = app;
        AppName = app.Name;
        AppIcon = _icons.Find(app);
        IsOpen = true;
        _ = LoadAsync();
        Opened?.Invoke(this, EventArgs.Empty);
    }

    public void Dispose()
    {
        _history.Erased -= OnErased;
        _icons.Loaded -= OnIconLoaded;
    }

    [RelayCommand]
    private void Close()
    {
        IsOpen = false;
        _app = null;
        Bars = [];
    }

    partial void OnSpanChanged(AppSpan value)
    {
        if (IsOpen)
        {
            _ = LoadAsync();
        }
    }

    private void OnErased(object? sender, EventArgs e) =>
        _dispatcher.BeginInvoke(DispatcherPriority.Background, new Action(() =>
        {
            if (IsOpen)
            {
                _ = LoadAsync();
            }
        }));

    private void OnIconLoaded(object? sender, EventArgs e) =>
        _dispatcher.BeginInvoke(DispatcherPriority.Background, new Action(() =>
        {
            if (_app is { } app)
            {
                AppIcon ??= _icons.Find(app);
            }
        }));

    private async Task LoadAsync()
    {
        if (_app is not { } app)
        {
            return;
        }

        int token = ++_loads;
        IsBusy = true;
        DayOfWeek firstDay = _settings.Usage.Digest.WeekStarts ?? CultureInfo.CurrentCulture.DateTimeFormat.FirstDayOfWeek;
        (DateTimeOffset from, DateTimeOffset until, CalendarStep step) = Window(Span, DateTimeOffset.Now, firstDay);
        IReadOnlyList<AppPoint> points;

        try
        {
            points = await Task.Run(() => _reports.AppOverTime(app.Id, from, until, step, firstDay)).ConfigureAwait(true);
        }
        catch (Exception ex) when (HistoryFailure.IsStorage(ex))
        {
            LogReadFailed(ex);
            points = [];
        }
        finally
        {
            if (token == _loads)
            {
                IsBusy = false;
            }
        }

        if (token != _loads)
        {
            return;
        }

        DateTimeOffset now = DateTimeOffset.Now;
        Bars = [.. points.Select(point => new ChartBar(
            Label(point.Start, step),
            Detail(point, step),
            point.MeasuredDown,
            point.MeasuredUp,
            point.WindowsDown + point.WindowsUp,
            IsFuture: point.Start > now))];

        long measured = points.Sum(point => point.MeasuredDown + point.MeasuredUp);
        long windows = points.Sum(point => point.WindowsDown + point.WindowsUp);
        IsEmpty = measured + windows == 0;
        TotalText = IsEmpty ? "—" : UsageText.Bytes(measured + windows);
        MeasuredText = measured > 0 ? UsageText.Bytes(measured) + " measured by Windsock" : "Nothing measured by Windsock";
        WindowsText = windows > 0 ? UsageText.Bytes(windows) + " counted by Windows while Windsock was not measuring" : string.Empty;

        BusiestText = points.Count > 0 && points.MaxBy(point => point.Total) is { Total: > 0 } busiest
            ? string.Format(CultureInfo.CurrentCulture, "Busiest: {0}, with {1}.", Describe(busiest.Start, step), UsageText.Bytes(busiest.Total))
            : string.Empty;
    }

    /// <summary>The stretch a span covers, ending with the step now is in, and its step.</summary>
    internal static (DateTimeOffset From, DateTimeOffset Until, CalendarStep Step) Window(AppSpan span, DateTimeOffset now, DayOfWeek firstDay)
    {
        DateTime local = now.LocalDateTime;
        DateTime today = local.Date;

        (DateTime from, DateTime until, CalendarStep step) = span switch
        {
            AppSpan.TwoDays => (today.AddHours(local.Hour + 1).AddHours(-48), today.AddHours(local.Hour + 1), CalendarStep.Hour),
            AppSpan.Quarter => (WeekStart(today, firstDay).AddDays(7 - 84), WeekStart(today, firstDay).AddDays(7), CalendarStep.Week),
            AppSpan.Year => (new DateTime(today.Year, today.Month, 1).AddMonths(-11), new DateTime(today.Year, today.Month, 1).AddMonths(1), CalendarStep.Month),
            _ => (today.AddDays(-29), today.AddDays(1), CalendarStep.Day),
        };

        return (Local(from), Local(until), step);
    }

    private static DateTime WeekStart(DateTime day, DayOfWeek firstDay) =>
        day.AddDays(-(((int)day.DayOfWeek - (int)firstDay + 7) % 7));

    private static DateTimeOffset Local(DateTime value) =>
        new(value, TimeZoneInfo.Local.GetUtcOffset(value));

    private static string Label(DateTimeOffset start, CalendarStep step)
    {
        DateTime local = start.LocalDateTime;

        return step switch
        {
            CalendarStep.Hour => local.Hour % 6 == 0 ? UsageText.Hour(local.Hour) : string.Empty,
            CalendarStep.Month => local.ToString("MMM", CultureInfo.CurrentCulture),
            _ => UsageText.Date(DateOnly.FromDateTime(local)),
        };
    }

    private static string Describe(DateTimeOffset start, CalendarStep step)
    {
        DateTime local = start.LocalDateTime;

        return step switch
        {
            CalendarStep.Hour => UsageText.When(start),
            CalendarStep.Week => "the week of " + UsageText.Date(DateOnly.FromDateTime(local)),
            CalendarStep.Month => local.ToString("MMMM yyyy", CultureInfo.CurrentCulture),
            _ => UsageText.Day(DateOnly.FromDateTime(local)),
        };
    }

    private static string Detail(AppPoint point, CalendarStep step)
    {
        if (point.Total == 0)
        {
            return string.Concat(Describe(point.Start, step), ": nothing");
        }

        string text = string.Format(
            CultureInfo.CurrentCulture,
            "{0}: {1} in all",
            Describe(point.Start, step),
            UsageText.Bytes(point.Total));

        if (point.MeasuredDown + point.MeasuredUp > 0)
        {
            text += string.Format(
                CultureInfo.CurrentCulture,
                ", {0} down and {1} up measured by Windsock",
                UsageText.Bytes(point.MeasuredDown),
                UsageText.Bytes(point.MeasuredUp));
        }

        if (point.WindowsDown + point.WindowsUp > 0)
        {
            text += string.Format(CultureInfo.CurrentCulture, ", {0} counted by Windows", UsageText.Bytes(point.WindowsDown + point.WindowsUp));
        }

        return text;
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "Could not read one app's history")]
    private partial void LogReadFailed(Exception exception);
}
