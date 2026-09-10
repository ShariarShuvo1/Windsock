using System.Collections.ObjectModel;
using System.Globalization;
using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Threading;
using Windsock.App.Views;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Win32;
using Windsock.Core.Formatting;
using Windsock.Core.History;
using Windsock.Core.Settings;

namespace Windsock.App.ViewModels;

/// <summary>The spans of time the History tab can show.</summary>
public enum HistoryRange
{
    Today,
    Yesterday,
    LastSevenDays,
    LastThirtyDays,
    ThisMonth,
    Everything,
    Custom,
}

/// <summary>One entry in the range picker.</summary>
public sealed record HistoryRangeOption(HistoryRange Range, string Label)
{
    public override string ToString() => Label;
}

/// <summary>One entry in the resolution picker.</summary>
public sealed record HistoryResolutionOption(UsageGranularity Granularity, string Label)
{
    public override string ToString() => Label;
}

/// <summary>
/// Backs the History tab: totals for a span of time, and the rows behind them.
/// </summary>
public sealed partial class HistoryPanelViewModel : ObservableObject
{
    private const int Batch = 12;
    private int _fills;
    private const double ReadoutMaximumFontSize = 34;
    private static readonly TimeSpan LongestMinuteSpan = TimeSpan.FromDays(7);

    private readonly IUsageHistoryStore _store;
    private readonly UsageRecorder _recorder;
    private readonly Dispatcher _dispatcher;
    private readonly WindsockSettings _settings;
    private readonly ISettingsStore _settingsStore;
    private readonly ReadoutSizer _downloadSizer = new(ReadoutMaximumFontSize);
    private readonly ReadoutSizer _uploadSizer = new(ReadoutMaximumFontSize);

    private int _loads;
    private bool _live;
    private bool _missedWhileHidden;
    private bool _stale;
    private UsageGranularity _shown;
    private HistoryRowViewModel? _lit;
    private IReadOnlyList<UsageBucket> _read = [];
    private bool _adjusting;
    private readonly bool _ready;
    private bool _mirroring;

    public HistoryPanelViewModel(
        IUsageHistoryStore store,
        UsageRecorder recorder,
        WindsockSettings settings,
        ISettingsStore settingsStore,
        Dispatcher dispatcher)
    {
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(recorder);

        _store = store;
        _recorder = recorder;
        _settings = settings;
        _settingsStore = settingsStore;
        _dispatcher = dispatcher;
        IsRecording = recorder.IsRecording;
        recorder.RecordingChanged += OnRecordingChanged;

        FromColumn = Column.Text<HistoryRowViewModel>("From", row => row.From);
        ToColumn = Column.Text<HistoryRowViewModel>("To", row => row.To);
        FromColumn.DescendingFirst = true;
        ToColumn.DescendingFirst = true;
        DownloadColumn = Column.Number<HistoryRowViewModel>("Download", ColumnKind.Volume, row => row.BytesDown);
        UploadColumn = Column.Number<HistoryRowViewModel>("Upload", ColumnKind.Volume, row => row.BytesUp);
        TotalColumn = Column.Number<HistoryRowViewModel>("Total", ColumnKind.Volume, row => row.BytesTotal);
        PeakDownColumn = Column.Number<HistoryRowViewModel>("Peak down", ColumnKind.Rate, row => row.PeakDownBytes);
        PeakUpColumn = Column.Number<HistoryRowViewModel>("Peak up", ColumnKind.Rate, row => row.PeakUpBytes);
        RecordedColumn = Column.Number<HistoryRowViewModel>("Minutes", ColumnKind.Count, row => row.MinutesRecorded);

        Columns =
        [
            FromColumn,
            ToColumn,
            DownloadColumn,
            UploadColumn,
            TotalColumn,
            PeakDownColumn,
            PeakUpColumn,
            RecordedColumn,
        ];

        foreach (TableColumn column in Columns)
        {
            column.Changed += (_, _) =>
            {
                Rebuild();
                OnPropertyChanged(nameof(IsCustomised));
            };
        }

        _recorder.Recorded += OnRecorded;
        _store.Erased += OnErased;

        Ranges =
        [
            new HistoryRangeOption(HistoryRange.Today, "Today"),
            new HistoryRangeOption(HistoryRange.Yesterday, "Yesterday"),
            new HistoryRangeOption(HistoryRange.LastSevenDays, "Last 7 days"),
            new HistoryRangeOption(HistoryRange.LastThirtyDays, "Last 30 days"),
            new HistoryRangeOption(HistoryRange.ThisMonth, "This month"),
            new HistoryRangeOption(HistoryRange.Everything, "All time"),
            new HistoryRangeOption(HistoryRange.Custom, "Custom range"),
        ];
        CustomTo = DateTime.Today.AddDays(1).AddMinutes(-1);
        CustomFrom = DateTime.Today.AddDays(-6);

        Resolutions = [];
        Rows = [];
        Buckets = [];

        DownloadValue = "0";
        DownloadUnit = "B";
        UploadValue = "0";
        UploadUnit = "B";
        DownloadFontSize = ReadoutMaximumFontSize;
        UploadFontSize = ReadoutMaximumFontSize;
        TotalText = "0 B";
        PeakText = string.Empty;
        CoverageText = string.Empty;
        RangeLabel = string.Empty;
        Notice = string.Empty;
        IsDetailExpanded = false;
        SelectedRange = HistoryRange.Today;
        SelectedResolution = DefaultFor(HistoryRange.Today);

        OfferResolutionsFor(HistoryRange.Today);

        _ready = true;
        _ = LoadAsync();
    }

    /// <summary>The spans on offer.</summary>
    public IReadOnlyList<HistoryRangeOption> Ranges { get; }

    /// <summary>Bucket widths available for the current span.</summary>
    [ObservableProperty]
    public partial IReadOnlyList<HistoryResolutionOption> Resolutions { get; private set; }

    /// <summary>The rows behind the totals.</summary>
    public ObservableCollection<HistoryRowViewModel> Rows { get; }

    /// <summary>Whether rows are still being handed to the table.</summary>
    [ObservableProperty]
    public partial bool IsFilling { get; private set; }

    /// <summary>The table's columns, each owning its own filter.</summary>
    public IReadOnlyList<TableColumn<HistoryRowViewModel>> Columns { get; }

    public TableColumn<HistoryRowViewModel> FromColumn { get; }

    public TableColumn<HistoryRowViewModel> ToColumn { get; }

    public TableColumn<HistoryRowViewModel> DownloadColumn { get; }

    public TableColumn<HistoryRowViewModel> UploadColumn { get; }

    public TableColumn<HistoryRowViewModel> TotalColumn { get; }

    public TableColumn<HistoryRowViewModel> PeakDownColumn { get; }

    public TableColumn<HistoryRowViewModel> PeakUpColumn { get; }

    public TableColumn<HistoryRowViewModel> RecordedColumn { get; }

    /// <summary>Whether a filter or a sort is narrowing what the table shows.</summary>
    public bool IsCustomised => Columns.Any(column => !column.IsDefault) || IsSorted;

    /// <summary>Set by the view when the grid is sorting by something.</summary>
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

    /// <summary>Raised when the view should drop its own sort.</summary>
    public event EventHandler? ViewReset;

    [RelayCommand]
    private void ResetView()
    {
        foreach (TableColumn column in Columns)
        {
            column.Reset();
        }

        ViewReset?.Invoke(this, EventArgs.Empty);
        OnPropertyChanged(nameof(IsCustomised));
    }

    /// <summary>
    /// Whether the table is at the top, where a new row would appear.
    /// </summary>
    public bool IsFollowing
    {
        get;

        set
        {
            if (field == value)
            {
                return;
            }

            field = value;

            if (value && _stale)
            {
                _stale = false;
                _ = LoadAsync();
            }
        }
    } = true;

    /// <summary>Begins following newly recorded minutes.</summary>
    public void Start()
    {
        _live = true;
        if (_missedWhileHidden)
        {
            _missedWhileHidden = false;
            _ = LoadAsync();
        }
    }

    /// <summary>Stops following while the panel is out of sight.</summary>
    public void Stop() => _live = false;

    private void OnErased(object? sender, EventArgs e) =>
        _dispatcher.BeginInvoke(DispatcherPriority.Background, new Action(() => _ = LoadAsync()));

    private void OnRecorded(object? sender, EventArgs e)
    {
        // Only periods that run up to now can gain a row. Yesterday is settled.
        if (RangeEnd <= DateTimeOffset.Now)
        {
            return;
        }

        if (!_live)
        {
            _missedWhileHidden = true;
            return;
        }

        if (!IsFollowing)
        {
            _stale = true;
            return;
        }

        // Raised on the writer thread, so it cannot touch the collection here.
        _dispatcher.BeginInvoke(DispatcherPriority.Background, new Action(() => _ = LoadAsync()));
    }

    /// <summary>The same buckets, for the chart.</summary>
    [ObservableProperty]
    public partial IReadOnlyList<UsageBucket> Buckets { get; private set; }

    /// <summary>The span being shown.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsCustom))]
    public partial HistoryRange SelectedRange { get; set; }

    /// <summary>Whether the dates that bound a custom period are in use.</summary>
    public bool IsCustom => SelectedRange == HistoryRange.Custom;

    /// <summary>First minute of a custom period, included.</summary>
    [ObservableProperty]
    public partial DateTime CustomFrom { get; set; }

    /// <summary>Last minute of a custom period, also included.</summary>
    [ObservableProperty]
    public partial DateTime CustomTo { get; set; }

    /// <summary>How wide each row and bar is.</summary>
    [ObservableProperty]
    public partial UsageGranularity SelectedResolution { get; set; }

    /// <summary>Start of the span being shown, for the chart's axis.</summary>
    [ObservableProperty]
    public partial DateTimeOffset RangeStart { get; private set; }

    /// <summary>End of the span being shown, for the chart's axis.</summary>
    [ObservableProperty]
    public partial DateTimeOffset RangeEnd { get; private set; }

    /// <summary>Bumped whenever the buckets change, so the chart redraws.</summary>
    [ObservableProperty]
    public partial int Revision { get; private set; }

    [ObservableProperty]
    public partial string DownloadValue { get; private set; }

    [ObservableProperty]
    public partial string DownloadUnit { get; private set; }

    [ObservableProperty]
    public partial double DownloadFontSize { get; private set; }

    [ObservableProperty]
    public partial string UploadValue { get; private set; }

    [ObservableProperty]
    public partial string UploadUnit { get; private set; }

    [ObservableProperty]
    public partial double UploadFontSize { get; private set; }

    /// <summary>Both directions added together.</summary>
    [ObservableProperty]
    public partial string TotalText { get; private set; }

    /// <summary>The fastest second in the span, both ways.</summary>
    [ObservableProperty]
    public partial string PeakText { get; private set; }

    /// <summary>How much of the span Windsock was actually running for.</summary>
    [ObservableProperty]
    public partial string CoverageText { get; private set; }

    /// <summary>The dates the span covers.</summary>
    [ObservableProperty]
    public partial string RangeLabel { get; private set; }

    /// <summary>What an import or export just did, if anything.</summary>
    [ObservableProperty]
    public partial string Notice { get; private set; }

    /// <summary>Whether a read is in flight.</summary>
    [ObservableProperty]
    public partial bool IsBusy { get; private set; }

    /// <summary>Whether the span has nothing in it.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsChartVisible))]
    public partial bool IsEmpty { get; private set; }

    /// <summary>
    /// Whether the totals and the chart are showing.
    /// </summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsChartVisible))]
    public partial bool IsDetailExpanded { get; set; }

    /// <summary>
    /// Whether the chart should be drawn at all.
    /// </summary>
    public bool IsChartVisible => IsDetailExpanded && !IsEmpty;

    /// <summary>The period the pointer is over, written out.</summary>
    [ObservableProperty]
    public partial string HoverLabel { get; private set; } = string.Empty;

    /// <summary>Bytes received in the bucket under the pointer.</summary>
    [ObservableProperty]
    public partial string HoverDownload { get; private set; } = string.Empty;

    /// <summary>Bytes sent in the bucket under the pointer.</summary>
    [ObservableProperty]
    public partial string HoverUpload { get; private set; } = string.Empty;

    /// <summary>Both directions, for the bucket under the pointer.</summary>
    [ObservableProperty]
    public partial string HoverTotal { get; private set; } = string.Empty;

    /// <summary>How much of that bucket was actually recorded.</summary>
    [ObservableProperty]
    public partial string HoverRecorded { get; private set; } = string.Empty;

    /// <summary>Whether the pointer is over a bar.</summary>
    [ObservableProperty]
    public partial bool HasHover { get; private set; }

    /// <summary>
    /// The bucket being pointed at, wherever the pointer happens to be.
    /// </summary>
    [ObservableProperty]
    public partial DateTimeOffset? HighlightedStart { get; set; }

    partial void OnHighlightedStartChanged(DateTimeOffset? value)
    {
        if (_lit is not null)
        {
            _lit.IsHighlighted = false;
            _lit = null;
        }

        if (value is null)
        {
            return;
        }

        foreach (HistoryRowViewModel row in Rows)
        {
            if (row.Start == value)
            {
                row.IsHighlighted = true;
                _lit = row;
                return;
            }
        }
    }

    /// <summary>
    /// Describes the bucket under the pointer, for the chart's readout.
    /// </summary>
    public void SetHover(bool hasValue, UsageBucket bucket)
    {
        HasHover = hasValue;
        HighlightedStart = hasValue ? bucket.Start : null;

        if (!hasValue)
        {
            return;
        }

        HistoryRowViewModel row = new(bucket, _shown);

        HoverLabel = string.Format(CultureInfo.CurrentCulture, "{0} – {1}", row.From, row.To);
        HoverDownload = row.Download;
        HoverUpload = row.Upload;
        HoverTotal = row.Total;
        HoverRecorded = _shown == UsageGranularity.Minute || bucket.Minutes >= MinutesIn(_shown)
            ? string.Empty
            : string.Format(
                CultureInfo.CurrentCulture,
                "{0:N0} of {1:N0} minutes recorded",
                bucket.Minutes,
                MinutesIn(_shown));
    }

    private static int MinutesIn(UsageGranularity granularity) => granularity switch
    {
        UsageGranularity.Minute => 1,
        UsageGranularity.Hour => 60,
        _ => 1440,
    };

    /// <summary>Whether new minutes are being written to history.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(RecordingLabel))]
    public partial bool IsRecording { get; set; }

    /// <summary>
    /// What the button offers to do, which is the opposite of what is
    /// happening.
    /// </summary>
    public string RecordingLabel => IsRecording ? "Pause recording" : "Resume recording";

    [RelayCommand]
    private void ToggleRecording() => IsRecording = !IsRecording;

    private void OnRecordingChanged(object? sender, EventArgs e) =>
        _dispatcher.BeginInvoke(new Action(() =>
        {
            _mirroring = true;
            IsRecording = _recorder.IsRecording;
            _mirroring = false;
        }));

    partial void OnIsRecordingChanged(bool value)
    {
        if (!_ready || _mirroring)
        {
            return;
        }

        _recorder.IsRecording = value;
        _settings.RecordHistory = value;
        _settingsStore.Save(_settings);
        _ = LoadAsync();
    }

    partial void OnSelectedRangeChanged(HistoryRange value)
    {
        if (!_ready)
        {
            return;
        }
        _adjusting = true;
        OfferResolutionsFor(value);
        _adjusting = false;

        _ = LoadAsync();
    }

    partial void OnSelectedResolutionChanged(UsageGranularity value)
    {
        if (_ready && !_adjusting)
        {
            _ = LoadAsync();
        }
    }

    partial void OnCustomFromChanged(DateTime value)
    {
        if (CustomTo < value)
        {
            Adjust(() => CustomTo = value);
        }

        ReloadCustom();
    }

    partial void OnCustomToChanged(DateTime value)
    {
        if (value < CustomFrom)
        {
            Adjust(() => CustomFrom = value);
        }

        ReloadCustom();
    }

    private void Adjust(Action change)
    {
        _adjusting = true;

        try
        {
            change();
        }
        finally
        {
            _adjusting = false;
        }
    }

    private void ReloadCustom()
    {
        if (!_ready || _adjusting || !IsCustom)
        {
            return;
        }

        // The span has changed, so what resolutions make sense may have too.
        _adjusting = true;
        OfferResolutionsFor(HistoryRange.Custom);
        _adjusting = false;

        _ = LoadAsync();
    }

    private void OfferResolutionsFor(HistoryRange range)
    {
        (DateTimeOffset from, DateTimeOffset until) = Bounds(range);

        List<HistoryResolutionOption> offered = [];

        if (until - from <= LongestMinuteSpan)
        {
            offered.Add(new HistoryResolutionOption(UsageGranularity.Minute, "By minute"));
        }

        offered.Add(new HistoryResolutionOption(UsageGranularity.Hour, "By hour"));
        offered.Add(new HistoryResolutionOption(UsageGranularity.Day, "By day"));

        Resolutions = offered;
        if (!offered.Any(option => option.Granularity == SelectedResolution))
        {
            SelectedResolution = DefaultFor(range);
        }
    }

    private static UsageGranularity DefaultFor(HistoryRange range) => range switch
    {
        HistoryRange.Today or HistoryRange.Yesterday => UsageGranularity.Hour,
        _ => UsageGranularity.Day,
    };

    private (DateTimeOffset From, DateTimeOffset Until) Bounds(HistoryRange range)
    {
        DateTimeOffset midnight = new(DateTime.Today, TimeZoneInfo.Local.GetUtcOffset(DateTime.Today));
        DateTimeOffset tomorrow = midnight.AddDays(1);

        return range switch
        {
            HistoryRange.Today => (midnight, tomorrow),
            HistoryRange.Yesterday => (midnight.AddDays(-1), midnight),
            HistoryRange.LastSevenDays => (midnight.AddDays(-6), tomorrow),
            HistoryRange.LastThirtyDays => (midnight.AddDays(-29), tomorrow),
            HistoryRange.ThisMonth => (midnight.AddDays(1 - midnight.Day), tomorrow),
            HistoryRange.Custom => (Moment(CustomFrom), Moment(CustomTo).AddMinutes(1)),
            _ => (Beginning(midnight), tomorrow),
        };
    }

    private static DateTimeOffset Moment(DateTime at)
    {
        DateTime minute = new(at.Year, at.Month, at.Day, at.Hour, at.Minute, 0, DateTimeKind.Unspecified);
        return new DateTimeOffset(minute, TimeZoneInfo.Local.GetUtcOffset(minute));
    }

    private DateTimeOffset Beginning(DateTimeOffset fallback)
    {
        DateTimeOffset? first = _store.Extent().First;

        return first is null
            ? fallback
            : new DateTimeOffset(first.Value.Date, first.Value.Offset);
    }

    [RelayCommand]
    private async Task LoadAsync()
    {
        (DateTimeOffset from, DateTimeOffset until) = Bounds(SelectedRange);
        UsageGranularity granularity = SelectedResolution;
        int token = ++_loads;
        IsBusy = true;

        try
        {
            (IReadOnlyList<UsageBucket> buckets, UsageSummary summary, TimeSpan watched) =
                await Task.Run(() =>
                {
                    IReadOnlyList<UsageBucket> read = _store.Read(from, until, granularity);
                    UsageSummary totals = _store.Summarise(from, until);
                    TimeSpan covered = Watched(_store.ReadCoverage(from, until), from, until);
                    return (read, totals, covered);
                }).ConfigureAwait(true);

            if (token != _loads)
            {
                return;
            }

            Apply(buckets, summary, watched, from, until, granularity);
        }
        finally
        {
            if (token == _loads)
            {
                IsBusy = false;
            }
        }
    }

    private static TimeSpan Watched(
        IReadOnlyList<CoverageWindow> windows,
        DateTimeOffset from,
        DateTimeOffset until)
    {
        TimeSpan total = TimeSpan.Zero;

        foreach (CoverageWindow window in windows)
        {
            DateTimeOffset start = window.Start > from ? window.Start : from;
            DateTimeOffset end = window.End + TimeSpan.FromMinutes(1);
            end = end < until ? end : until;

            if (end > start)
            {
                total += end - start;
            }
        }

        return total;
    }

    private void Apply(
        IReadOnlyList<UsageBucket> buckets,
        UsageSummary summary,
        TimeSpan watched,
        DateTimeOffset from,
        DateTimeOffset until,
        UsageGranularity granularity)
    {
        _read = buckets;
        _shown = granularity;
        Rebuild();

        Buckets = buckets;
        RangeStart = from;
        RangeEnd = until;
        Revision++;

        (string downValue, string downUnit) = SizeFormatter.Split(summary.BytesDown);
        (string upValue, string upUnit) = SizeFormatter.Split(summary.BytesUp);

        DownloadValue = downValue;
        DownloadUnit = downUnit;
        DownloadFontSize = _downloadSizer.Update(downValue.Length);
        UploadValue = upValue;
        UploadUnit = upUnit;
        UploadFontSize = _uploadSizer.Update(upValue.Length);

        TotalText = SizeFormatter.Format(summary.BytesTotal);

        PeakText = summary.Minutes == 0
            ? string.Empty
            : string.Format(
                CultureInfo.CurrentCulture,
                "Fastest {0}/s down, {1}/s up",
                SizeFormatter.Format(summary.PeakDown),
                SizeFormatter.Format(summary.PeakUp));

        CoverageText = Coverage(watched, from, until, summary.Minutes, IsRecording);
        RangeLabel = Label(from, until);
        IsEmpty = summary.Minutes == 0;
    }

    private void Rebuild()
    {
        // The lit row is about to stop existing.
        _lit = null;
        HighlightedStart = null;
        Rows.Clear();

        List<HistoryRowViewModel> made = [];

        for (int i = _read.Count - 1; i >= 0; i--)
        {
            HistoryRowViewModel row = new(_read[i], _shown);

            if (Columns.All(column => column.Matches(row)))
            {
                made.Add(row);
            }
        }

        // A new fill supersedes whatever the last one had left to do.
        int token = ++_fills;
        int at = 0;

        void Hand()
        {
            if (token != _fills)
            {
                return;
            }

            int end = Math.Min(at + Batch, made.Count);

            for (; at < end; at++)
            {
                Rows.Add(made[at]);
            }

            if (at < made.Count)
            {
                _dispatcher.BeginInvoke(Hand, DispatcherPriority.Background);
                return;
            }

            IsFilling = false;
        }

        IsFilling = made.Count > Batch;
        Hand();
    }

    private static string Coverage(
        TimeSpan watched,
        DateTimeOffset from,
        DateTimeOffset until,
        int minutes,
        bool recording)
    {
        if (!recording)
        {
            return minutes == 0
                ? "Recording is off, and nothing was recorded for this period."
                : "Recording is off, so nothing new is being added to this.";
        }
        DateTimeOffset now = DateTimeOffset.Now;
        bool stillRunning = until > now;

        if (minutes == 0)
        {
            return stillRunning
                ? "Nothing recorded for this period yet."
                : "Windsock was not running during this period, so there is nothing to show.";
        }

        TimeSpan span = until - from;

        if (stillRunning)
        {
            span = now - from;
        }

        if (span <= TimeSpan.Zero || watched >= span - TimeSpan.FromMinutes(1))
        {
            return "Windsock was running for all of this period.";
        }
        return string.Format(
            CultureInfo.CurrentCulture,
            stillRunning
                ? "Recorded {0} of the {1} so far. The rest is time Windsock was closed, not idle time."
                : "Recorded {0} out of {1}. The rest is time Windsock was closed, not idle time.",
            DurationLabel.Format(watched.TotalSeconds),
            DurationLabel.Format(span.TotalSeconds));
    }

    private static string Label(DateTimeOffset from, DateTimeOffset until)
    {
        DateTimeOffset last = until.AddMinutes(-1);
        bool wholeDays = from.TimeOfDay == TimeSpan.Zero
            && last.TimeOfDay == new TimeSpan(23, 59, 0);

        if (wholeDays)
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
            last.Date == from.Date
                ? last.ToString("h:mm tt", CultureInfo.CurrentCulture)
                : last.ToString("d MMM, h:mm tt", CultureInfo.CurrentCulture));
    }

    [RelayCommand]
    private async Task DeleteRowAsync(HistoryRowViewModel? row)
    {
        if (row is null)
        {
            return;
        }

        DateTimeOffset from = row.Start;
        DateTimeOffset until = row.End;

        int removed = await Task.Run(() => _store.Delete(from, until)).ConfigureAwait(true);

        Notice = string.Format(
            CultureInfo.CurrentCulture,
            "Deleted {0:N0} {1} from {2}.",
            removed,
            removed == 1 ? "minute" : "minutes",
            row.From);

        await LoadAsync().ConfigureAwait(true);
    }

    [RelayCommand]
    private async Task ExportAsync()
    {
        (DateTimeOffset from, DateTimeOffset until) = Bounds(SelectedRange);
        UsageGranularity granularity = SelectedResolution;

        SaveFileDialog dialog = new()
        {
            Title = "Export usage history",
            Filter = "CSV file (*.csv)|*.csv|All files (*.*)|*.*",
            DefaultExt = ".csv",
            FileName = string.Format(
                CultureInfo.InvariantCulture,
                "Windsock-usage-{0:yyyy-MM-dd}-{1}.csv",
                from,
                granularity.ToString().ToLowerInvariant()),
        };

        if (dialog.ShowDialog() != true)
        {
            return;
        }

        string path = dialog.FileName;

        try
        {
            int written = await Task.Run(() =>
            {
                IReadOnlyList<UsageBucket> buckets = _store.Read(from, until, granularity);
                using StreamWriter writer = new(path, false, new UTF8Encoding(true));
                UsageCsv.Write(writer, granularity, buckets);
                return buckets.Count;
            }).ConfigureAwait(true);

            Notice = string.Format(
                CultureInfo.CurrentCulture,
                "Exported {0:N0} {1} to {2}",
                written,
                written == 1 ? "row" : "rows",
                Path.GetFileName(path));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Notice = "Could not write that file: " + ex.Message;
        }
    }

    [RelayCommand]
    private async Task ImportAsync()
    {
        OpenFileDialog dialog = new()
        {
            Title = "Import usage history",
            Filter = "CSV file (*.csv)|*.csv|All files (*.*)|*.*",
            CheckFileExists = true,
        };

        if (dialog.ShowDialog() != true)
        {
            return;
        }

        string path = dialog.FileName;

        try
        {
            (IReadOnlyList<UsageMinute> minutes, int unreadable, int conflicts) = await Task.Run(() =>
            {
                using StreamReader reader = new(path, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
                UsageImport imported = UsageCsv.Read(reader);
                return (imported.Minutes, imported.Skipped, _store.CountExisting(imported.Minutes));
            }).ConfigureAwait(true);

            if (minutes.Count == 0)
            {
                Notice = "Nothing in that file could be read as minute-by-minute history.";
                return;
            }

            // Only worth asking when there is actually something to land on.
            ImportChoice choice = conflicts == 0
                ? ImportChoice.Skip
                : ImportConflictDialog.Ask(conflicts, minutes.Count, Application.Current?.MainWindow);

            if (choice == ImportChoice.Cancel)
            {
                Notice = "Import cancelled. Nothing was changed.";
                return;
            }

            int written = await Task.Run(() => choice == ImportChoice.Overwrite
                ? _store.Replace(minutes)
                : _store.Merge(minutes)).ConfigureAwait(true);

            Notice = Describe(choice, written, minutes.Count, conflicts, unreadable);
            await LoadAsync().ConfigureAwait(true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Notice = "Could not read that file: " + ex.Message;
        }
    }

    private static string Describe(ImportChoice choice, int written, int found, int conflicts, int unreadable)
    {
        StringBuilder message = new();

        if (choice == ImportChoice.Overwrite)
        {
            message.Append(CultureInfo.CurrentCulture, $"Imported {written:N0} of {found:N0} minutes");

            if (conflicts > 0)
            {
                message.Append(CultureInfo.CurrentCulture, $", overwriting {conflicts:N0} already recorded");
            }
        }
        else
        {
            message.Append(CultureInfo.CurrentCulture, $"Imported {written:N0} of {found:N0} minutes");

            if (conflicts > 0)
            {
                message.Append(CultureInfo.CurrentCulture, $"; {conflicts:N0} were already recorded and were left alone");
            }
        }

        if (unreadable > 0)
        {
            message.Append(CultureInfo.CurrentCulture, $"; {unreadable:N0} rows could not be read");
        }

        return message.Append('.').ToString();
    }
}
