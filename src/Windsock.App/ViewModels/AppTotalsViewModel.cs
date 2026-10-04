using System.Collections.ObjectModel;
using System.Globalization;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using Windsock.App.Services;
using Windsock.Core.Apps;
using Windsock.Core.Formatting;
using Windsock.Core.History;

namespace Windsock.App.ViewModels;

/// <summary>The spans of time the Apps page can total.</summary>
public enum AppsRange
{
    LastHour,
    Today,
    Yesterday,
    LastSevenDays,
    LastThirtyDays,
    ThisMonth,
}

/// <summary>One entry in the period picker.</summary>
public sealed record AppsRangeOption(AppsRange Range, string Label)
{
    public override string ToString() => Label;
}

/// <summary>
/// Backs the Apps page when a period is chosen rather than Live: how much each
/// app used the network over that period, as Windows counted it.
/// </summary>
/// <remarks>
/// Everything on the page comes from Windows' own data usage, copied into
/// history by <see cref="AppUsageCollector"/>. It needs no privilege, so it is
/// the same in both editions, and it covers time Windsock was not running for
/// as far back as Windows kept it - about a week.
/// </remarks>
public sealed partial class AppTotalsViewModel : ObservableObject, IDisposable
{
    private readonly AppHistory _store;
    private readonly IUsageHistoryStore _history;
    private readonly AppUsageCollector _collector;
    private readonly AppNames _names;
    private readonly AppIconCache _icons;
    private readonly Dispatcher _dispatcher;
    private readonly ILogger<AppTotalsViewModel> _logger;
    private readonly Dictionary<string, AppRowViewModel> _known = new(StringComparer.Ordinal);
    private readonly bool _ready;

    private static readonly TimeSpan ReloadPause = TimeSpan.FromSeconds(1);

    private List<AppRowViewModel> _current = [];
    private int _loads;
    private bool _live;
    private bool _missedWhileHidden;
    private bool _unreadable;
    private bool _reloadQueued;

    public AppTotalsViewModel(
        AppHistory store,
        IUsageHistoryStore history,
        AppUsageCollector collector,
        AppNames names,
        AppIconCache icons,
        Dispatcher dispatcher,
        ILogger<AppTotalsViewModel> logger)
    {
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(history);
        ArgumentNullException.ThrowIfNull(collector);
        ArgumentNullException.ThrowIfNull(names);
        ArgumentNullException.ThrowIfNull(icons);

        _store = store;
        _history = history;
        _collector = collector;
        _names = names;
        _icons = icons;
        _dispatcher = dispatcher;
        _logger = logger;

        NameColumn = Column.Text<AppRowViewModel>("App", row => row.Name);
        KindColumn = Column.Text<AppRowViewModel>("Kind", row => row.KindText, visible: false);
        DownloadColumn = Column.Number<AppRowViewModel>("Download", ColumnKind.Volume, row => row.BytesReceived);
        UploadColumn = Column.Number<AppRowViewModel>("Upload", ColumnKind.Volume, row => row.BytesSent);
        TotalColumn = Column.Number<AppRowViewModel>("Total", ColumnKind.Volume, row => row.BytesTotal);
        ShareColumn = Column.Number<AppRowViewModel>("Share", ColumnKind.Count, row => Math.Round(row.Share * 100, 1));
        RecentColumn = Column.Number<AppRowViewModel>("Last 5 minutes", ColumnKind.Rate, row => Math.Max(0, row.RecentRate));
        PathColumn = Column.Text<AppRowViewModel>("Path", row => row.FilePath, visible: false);

        Columns =
        [
            NameColumn,
            KindColumn,
            DownloadColumn,
            UploadColumn,
            TotalColumn,
            ShareColumn,
            RecentColumn,
            PathColumn,
        ];

        foreach (TableColumn column in Columns)
        {
            column.Changed += (_, _) =>
            {
                Rebuild();
                OnPropertyChanged(nameof(IsCustomised));
            };
        }

        Ranges =
        [
            new AppsRangeOption(AppsRange.LastHour, "Last hour"),
            new AppsRangeOption(AppsRange.Today, "Today"),
            new AppsRangeOption(AppsRange.Yesterday, "Yesterday"),
            new AppsRangeOption(AppsRange.LastSevenDays, "Last 7 days"),
            new AppsRangeOption(AppsRange.LastThirtyDays, "Last 30 days"),
            new AppsRangeOption(AppsRange.ThisMonth, "This month"),
        ];

        SelectedRange = AppsRange.Today;

        _collector.Changed += OnCollected;
        _icons.Loaded += OnIconLoaded;
        _history.Erased += OnErased;

        _ready = true;
        _ = LoadAsync();
    }

    /// <summary>The periods on offer.</summary>
    public IReadOnlyList<AppsRangeOption> Ranges { get; }

    /// <summary>The period being totalled.</summary>
    [ObservableProperty]
    public partial AppsRange SelectedRange { get; set; }

    /// <summary>The rows, after search and filters.</summary>
    public ObservableCollection<AppRowViewModel> Rows { get; } = [];

    /// <summary>Every column, in the order the grid declares them.</summary>
    public IReadOnlyList<TableColumn<AppRowViewModel>> Columns { get; }

    public TableColumn<AppRowViewModel> NameColumn { get; }

    public TableColumn<AppRowViewModel> KindColumn { get; }

    public TableColumn<AppRowViewModel> DownloadColumn { get; }

    public TableColumn<AppRowViewModel> UploadColumn { get; }

    public TableColumn<AppRowViewModel> TotalColumn { get; }

    public TableColumn<AppRowViewModel> ShareColumn { get; }

    public TableColumn<AppRowViewModel> RecentColumn { get; }

    public TableColumn<AppRowViewModel> PathColumn { get; }

    /// <summary>Free text matched against each app's name, kind and path.</summary>
    [ObservableProperty]
    public partial string SearchText { get; set; } = string.Empty;

    /// <summary>The dates the period covers.</summary>
    [ObservableProperty]
    public partial string RangeLabel { get; private set; } = string.Empty;

    [ObservableProperty]
    public partial string DownloadValue { get; private set; } = "0";

    [ObservableProperty]
    public partial string DownloadUnit { get; private set; } = "B";

    [ObservableProperty]
    public partial string UploadValue { get; private set; } = "0";

    [ObservableProperty]
    public partial string UploadUnit { get; private set; } = "B";

    /// <summary>How many apps used the network in the period.</summary>
    [ObservableProperty]
    public partial string AppsText { get; private set; } = "0";

    /// <summary>Which app used the most.</summary>
    [ObservableProperty]
    public partial string TopText { get; private set; } = string.Empty;

    /// <summary>How far Windows' figures reach, or why they are not being read.</summary>
    [ObservableProperty]
    public partial string FreshnessText { get; private set; } = string.Empty;

    /// <summary>A standing problem worth a banner, or empty.</summary>
    [ObservableProperty]
    public partial string Notice { get; private set; } = string.Empty;

    /// <summary>Whether a read is in flight.</summary>
    [ObservableProperty]
    public partial bool IsBusy { get; private set; }

    /// <summary>Whether the table has nothing to show.</summary>
    [ObservableProperty]
    public partial bool IsEmpty { get; private set; } = true;

    /// <summary>What to say when the table is empty, which depends on why.</summary>
    [ObservableProperty]
    public partial string EmptyText { get; private set; } = string.Empty;

    /// <summary>Whether the period reaches back further than Windows keeps app usage.</summary>
    [ObservableProperty]
    public partial bool ReachesPastWindows { get; private set; }

    /// <summary>Whether search, a filter, a column or the sort differs from the start.</summary>
    public bool IsCustomised =>
        !string.IsNullOrWhiteSpace(SearchText)
        || Columns.Any(column => !column.IsDefault)
        || IsSorted;

    /// <summary>Set by the view when the grid is sorted by something other than the default.</summary>
    public bool IsSorted
    {
        get;
        set
        {
            if (field == value)
            {
                return;
            }

            field = value;
            OnPropertyChanged(nameof(IsCustomised));
        }
    }

    /// <summary>Raised when the view should put its sort back.</summary>
    public event EventHandler? ViewReset;

    /// <summary>Begins following new figures, catching up on any missed while hidden.</summary>
    public void Start()
    {
        _live = true;

        if (_missedWhileHidden)
        {
            _missedWhileHidden = false;
            _ = LoadAsync();
        }
    }

    /// <summary>Stops following while the page is out of sight.</summary>
    public void Stop() => _live = false;

    public void Dispose()
    {
        Stop();
        _collector.Changed -= OnCollected;
        _icons.Loaded -= OnIconLoaded;
        _history.Erased -= OnErased;
    }

    [RelayCommand]
    private void ResetView()
    {
        SearchText = string.Empty;

        foreach (TableColumn column in Columns)
        {
            column.Reset();
        }

        ViewReset?.Invoke(this, EventArgs.Empty);
        OnPropertyChanged(nameof(IsCustomised));
    }

    [RelayCommand]
    private static void Reveal(AppRowViewModel? row)
    {
        if (row is { CanReveal: true })
        {
            FileReveal.Show(row.FilePath);
        }
    }

    partial void OnSelectedRangeChanged(AppsRange value)
    {
        if (_ready)
        {
            _ = LoadAsync();
        }
    }

    partial void OnSearchTextChanged(string value)
    {
        Rebuild();
        OnPropertyChanged(nameof(IsCustomised));
    }

    private void OnCollected(object? sender, AppUsageChangedEventArgs e) =>
        _dispatcher.BeginInvoke(DispatcherPriority.Background, new Action(() =>
        {
            (DateTimeOffset from, DateTimeOffset until) = Bounds(SelectedRange);

            // What was kept is outside the period, and the live column is not
            // shown beside a period that has ended: nothing on the page moved.
            bool wanted = e.Changes.HasFlag(AppUsageChanges.State)
                || e.KeptWithin(from, until)
                || (e.Changes.HasFlag(AppUsageChanges.Recent) && until > DateTimeOffset.Now);

            if (!wanted)
            {
                return;
            }

            if (!_live)
            {
                _missedWhileHidden = true;
                return;
            }

            _ = ReloadSoon();
        }));

    /// <summary>
    /// Reloads once shortly, however many times it is asked meanwhile, so a
    /// catch-up storing a batch four times a second reloads about once a second.
    /// </summary>
    private async Task ReloadSoon()
    {
        if (_reloadQueued)
        {
            return;
        }

        _reloadQueued = true;

        try
        {
            await Task.Delay(ReloadPause).ConfigureAwait(true);
        }
        finally
        {
            _reloadQueued = false;
        }

        if (_live)
        {
            await LoadAsync().ConfigureAwait(true);
        }
        else
        {
            // Hidden while waiting: the next showing reloads instead.
            _missedWhileHidden = true;
        }
    }

    private void OnErased(object? sender, EventArgs e) =>
        _dispatcher.BeginInvoke(DispatcherPriority.Background, new Action(() => _ = LoadAsync()));

    private void OnIconLoaded(object? sender, EventArgs e) =>
        _dispatcher.BeginInvoke(DispatcherPriority.Background, new Action(Illustrate));

    [RelayCommand]
    private async Task LoadAsync()
    {
        (DateTimeOffset from, DateTimeOffset until) = Bounds(SelectedRange);
        int token = ++_loads;
        IsBusy = true;

        try
        {
            IReadOnlyList<(AppUsageTotal Total, AppDescription App)> read;

            try
            {
                read = await Task.Run(() =>
                {
                    // Windsock's own measurements where it measured, Windows' elsewhere.
                    IReadOnlyList<AppShare> totals = _store.Totals(from, until);
                    return (IReadOnlyList<(AppUsageTotal, AppDescription)>)
                        [.. totals.Select(total => (
                            new AppUsageTotal(total.AttributionId, total.Down, total.Up),
                            _names.Describe(total.AttributionId)))];
                }).ConfigureAwait(true);
            }
            catch (Exception ex) when (HistoryFailure.IsStorage(ex))
            {
                if (token == _loads)
                {
                    ApplyUnreadable(ex, from, until);
                }

                return;
            }

            if (token == _loads)
            {
                Apply(read, from, until);
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

    private void Apply(IReadOnlyList<(AppUsageTotal Total, AppDescription App)> read, DateTimeOffset from, DateTimeOffset until)
    {
        _unreadable = false;

        long received = 0;
        long sent = 0;

        foreach ((AppUsageTotal total, _) in read)
        {
            received += total.BytesReceived;
            sent += total.BytesSent;
        }

        long everything = received + sent;
        // The recent column is a rate now, so it means something only beside a
        // period that runs up to now.
        bool includesNow = until > DateTimeOffset.Now;
        AppUsageStatus status = _collector.Status;
        IReadOnlyDictionary<string, AppUsageRecord> recent = status.Recent;
        bool recentKnown = includesNow && status.RecentStart is not null;
        double recentSeconds = AppUsagePlan.RecentSpan * 60.0;

        List<AppRowViewModel> rows = new(read.Count);
        AppRowViewModel? top = null;

        foreach ((AppUsageTotal total, AppDescription app) in read)
        {
            if (!_known.TryGetValue(app.Id, out AppRowViewModel? row) || !Equals(row.App, app))
            {
                row = new AppRowViewModel(app);
                _known[app.Id] = row;
            }

            double rate = !recentKnown
                ? -1
                : recent.TryGetValue(app.Id, out AppUsageRecord lately) ? lately.BytesTotal / recentSeconds : 0;

            row.Update(
                total.BytesReceived,
                total.BytesSent,
                everything > 0 ? (double)total.BytesTotal / everything : 0,
                rate);

            rows.Add(row);

            if (!row.IsBucket && (top is null || row.BytesTotal > top.BytesTotal))
            {
                top = row;
            }
        }

        _current = rows;

        (DownloadValue, DownloadUnit) = SizeFormatter.Split(received);
        (UploadValue, UploadUnit) = SizeFormatter.Split(sent);
        int apps = rows.Count(row => !row.IsBucket);
        AppsText = apps.ToString("N0", CultureInfo.CurrentCulture);
        TopText = top is null
            ? string.Empty
            : string.Format(CultureInfo.CurrentCulture, "Most: {0}, {1}", top.Name, top.TotalText);

        RangeLabel = Label(from, until);
        ReachesPastWindows = SelectedRange is AppsRange.LastThirtyDays or AppsRange.ThisMonth;
        Describe();
        Rebuild();
    }

    private void ApplyUnreadable(Exception failure, DateTimeOffset from, DateTimeOffset until)
    {
        if (!_unreadable)
        {
            _unreadable = true;
            LogLoadFailed(failure);
        }

        _current = [];
        DownloadValue = MeterReadout.Unknown;
        DownloadUnit = string.Empty;
        UploadValue = MeterReadout.Unknown;
        UploadUnit = string.Empty;
        AppsText = MeterReadout.Unknown;
        TopText = string.Empty;
        RangeLabel = Label(from, until);
        Notice = "The history file could not be read, so there is nothing to show. It may be damaged.";
        Rebuild();
    }

    /// <summary>Says how far the figures reach, or why they do not.</summary>
    private void Describe()
    {
        AppUsageStatus status = _collector.Status;

        FreshnessText = status.State switch
        {
            AppUsageState.Collecting when status.CollectedUntil is { } until =>
                string.Format(
                    CultureInfo.CurrentCulture,
                    "Counted by Windows, up to {0}. Windows is still settling the last few minutes, so they join these totals shortly; the Last 5 minutes column shows them as they stand.",
                    until.ToString("h:mm tt", CultureInfo.CurrentCulture)),
            AppUsageState.Waiting => "Reading Windows' data usage…",
            _ => status.Detail ?? string.Empty,
        };

        // Collecting carries a detail only when a network is being left out,
        // which is as much worth a banner as not collecting at all.
        Notice = status.Detail ?? string.Empty;
    }

    private void Rebuild()
    {
        string term = SearchText.Trim();
        List<AppRowViewModel> shown = [];

        foreach (AppRowViewModel row in _current)
        {
            if (term.Length > 0 && !row.Contains(term))
            {
                continue;
            }

            if (Columns.All(column => column.Matches(row)))
            {
                shown.Add(row);
            }
        }

        // Rows the table already holds are kept, so a refresh each minute does
        // not throw away a selection or reset where the reader had scrolled to.
        HashSet<AppRowViewModel> wanted = [.. shown];

        for (int index = Rows.Count - 1; index >= 0; index--)
        {
            if (!wanted.Contains(Rows[index]))
            {
                Rows.RemoveAt(index);
            }
        }

        HashSet<AppRowViewModel> present = [.. Rows];

        foreach (AppRowViewModel row in shown)
        {
            if (!present.Contains(row))
            {
                Rows.Add(row);
            }
        }

        Illustrate();

        IsEmpty = Rows.Count == 0;
        EmptyText = _current.Count > 0
            ? "Nothing matches the search or the filters."
            : _unreadable
                ? "Nothing can be shown while the history file cannot be read."
                : _collector.State switch
                {
                    AppUsageState.Waiting => "Reading Windows' data usage…",
                    AppUsageState.Unavailable => "Windows' data usage could not be read, so nothing is known for this period yet.",
                    _ => "Windows counted no app using the network in this period.",
                };
    }

    private void Illustrate()
    {
        foreach (AppRowViewModel row in Rows)
        {
            row.Icon ??= _icons.Find(row.App);
        }
    }

    private static (DateTimeOffset From, DateTimeOffset Until) Bounds(AppsRange range)
    {
        DateTimeOffset midnight = new(DateTime.Today, TimeZoneInfo.Local.GetUtcOffset(DateTime.Today));
        DateTimeOffset tomorrow = midnight.AddDays(1);
        DateTimeOffset now = DateTimeOffset.Now;
        DateTimeOffset minute = new(now.Year, now.Month, now.Day, now.Hour, now.Minute, 0, now.Offset);

        return range switch
        {
            AppsRange.LastHour => (minute.AddHours(-1), minute.AddMinutes(1)),
            AppsRange.Yesterday => (midnight.AddDays(-1), midnight),
            AppsRange.LastSevenDays => (midnight.AddDays(-6), tomorrow),
            AppsRange.LastThirtyDays => (midnight.AddDays(-29), tomorrow),
            AppsRange.ThisMonth => (midnight.AddDays(1 - midnight.Day), tomorrow),
            _ => (midnight, tomorrow),
        };
    }

    private static string Label(DateTimeOffset from, DateTimeOffset until)
    {
        DateTimeOffset last = until.AddMinutes(-1);

        if (from.TimeOfDay == TimeSpan.Zero && last.TimeOfDay == new TimeSpan(23, 59, 0))
        {
            return from.Date == last.Date
                ? from.ToString("dddd d MMMM yyyy", CultureInfo.CurrentCulture)
                : string.Format(
                    CultureInfo.CurrentCulture,
                    "{0} to {1}",
                    from.ToString("d MMM yyyy", CultureInfo.CurrentCulture),
                    last.ToString("d MMM yyyy", CultureInfo.CurrentCulture));
        }

        return string.Format(
            CultureInfo.CurrentCulture,
            "{0} to {1}",
            from.ToString("d MMM, h:mm tt", CultureInfo.CurrentCulture),
            last.ToString("h:mm tt", CultureInfo.CurrentCulture));
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "App usage could not be read from history")]
    private partial void LogLoadFailed(Exception error);
}
