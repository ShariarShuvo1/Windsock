using System.Collections.ObjectModel;
using System.Globalization;
using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Threading;
using Windsock.App.Services;
using Windsock.App.Views;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using Microsoft.Win32;
using Windsock.Core.Apps;
using Windsock.Core.Connectivity;
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
public sealed partial class HistoryPanelViewModel : ObservableObject, IDisposable
{
    private const int Batch = 12;
    private int _fills;
    private const double ReadoutMaximumFontSize = 34;
    private static readonly TimeSpan LongestMinuteSpan = TimeSpan.FromDays(7);
    private static readonly TimeSpan ReloadPause = TimeSpan.FromSeconds(1);

    private const string Unreadable =
        "The history file could not be read, so there is nothing to show. It may be damaged.";

    private readonly IUsageHistoryStore _store;
    private readonly IAppUsageStore _windows;
    private readonly AppUsageCollector _collector;
    private readonly ILogger<HistoryPanelViewModel> _logger;
    private readonly UsageRecorder _recorder;
    private readonly Dispatcher _dispatcher;
    private readonly WindsockSettings _settings;
    private readonly ISettingsStore _settingsStore;
    private readonly ReadoutSizer _downloadSizer = new(ReadoutMaximumFontSize);
    private readonly ReadoutSizer _uploadSizer = new(ReadoutMaximumFontSize);
    private readonly AppHistory _apps;
    private readonly AppNames _names;
    private readonly AppIconCache _icons;
    private readonly Dictionary<(DateTimeOffset, DateTimeOffset), IReadOnlyList<HoverAppRow>> _appsRead = [];
    private (DateTimeOffset Start, DateTimeOffset End)? _appsFor;
    private int _appLoads;

    /// <summary>Bumped with each load, so an apps read started before it is not cached after it.</summary>
    private int _appGeneration;

    /// <summary>How many apps the popover names; the rest are counted, not listed.</summary>
    private const int HoverAppCount = 6;

    private int _loads;
    private bool _live;
    private bool _missedWhileHidden;
    private bool _stale;
    private bool _reloadQueued;
    private UsageGranularity _shown;
    private HistoryRowViewModel? _lit;
    private IReadOnlyList<UsageBucket> _read = [];
    private bool _adjusting;
    private readonly bool _ready;
    private bool _mirroring;
    private bool _unreadable;

    public HistoryPanelViewModel(
        IUsageHistoryStore store,
        IAppUsageStore windows,
        AppHistory apps,
        AppNames names,
        AppIconCache icons,
        IMachineHistoryStore machine,
        IOutageStore outages,
        AppUsageCollector collector,
        UsageRecorder recorder,
        WindsockSettings settings,
        ISettingsStore settingsStore,
        Dispatcher dispatcher,
        ILogger<HistoryPanelViewModel> logger)
    {
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(recorder);
        ArgumentNullException.ThrowIfNull(apps);
        ArgumentNullException.ThrowIfNull(names);
        ArgumentNullException.ThrowIfNull(icons);

        ArgumentNullException.ThrowIfNull(machine);
        ArgumentNullException.ThrowIfNull(outages);

        _apps = apps;
        _names = names;
        _icons = icons;
        _machine = machine;
        _outages = outages;
        _store = store;
        _windows = windows;
        _collector = collector;
        _logger = logger;
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
        _collector.Changed += OnCollected;
        _store.Erased += OnErased;
        _icons.Loaded += OnIconLoaded;

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
        BuildPager();

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
        IsDetailExpanded = true;
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

    /// <summary>
    /// Stops listening to the recorder and the store, which outlive the window
    /// this panel belongs to and would otherwise keep it.
    /// </summary>
    public void Dispose()
    {
        Stop();
        _recorder.RecordingChanged -= OnRecordingChanged;
        _recorder.Recorded -= OnRecorded;
        _collector.Changed -= OnCollected;
        _store.Erased -= OnErased;
        _icons.Loaded -= OnIconLoaded;
    }

    // Icons are read in the background; the popover's list takes each as it lands.
    private void OnIconLoaded(object? sender, EventArgs e) =>
        _dispatcher.BeginInvoke(DispatcherPriority.Background, new Action(() =>
        {
            foreach (HoverAppRow app in HoverApps)
            {
                app.Icon ??= _icons.Find(app.App);
            }
        }));

    /// <summary>
    /// Windows' figures can fill any period, the day before yesterday included,
    /// when collection catches up after Windsock was closed; so, unlike a newly
    /// recorded minute, they are worth a reload whenever what was kept falls in
    /// the period on screen - and only then.
    /// </summary>
    private void OnCollected(object? sender, AppUsageChangedEventArgs e)
    {
        if (!e.Changes.HasFlag(AppUsageChanges.Kept))
        {
            return;
        }

        // Raised on the collection thread; everything below reads the page.
        _dispatcher.BeginInvoke(DispatcherPriority.Background, new Action(() =>
        {
            if (!e.KeptWithin(RangeStart, RangeEnd))
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
                HoldBack();
                return;
            }

            _ = ReloadSoon();
        }));
    }

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

    // Raised on the writer thread, so everything, the checks included, runs on the page's.
    private void OnRecorded(object? sender, EventArgs e) =>
        _dispatcher.BeginInvoke(DispatcherPriority.Background, new Action(() =>
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
                HoldBack();
                return;
            }

            _ = LoadAsync();
        }));

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

    /// <summary>The fastest download in the span, under its total.</summary>
    [ObservableProperty]
    public partial string PeakDownText { get; private set; } = string.Empty;

    /// <summary>The fastest upload in the span, under its total.</summary>
    [ObservableProperty]
    public partial string PeakUpText { get; private set; } = string.Empty;

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

    /// <summary>Bytes Windsock recorded coming in, in the bucket under the pointer.</summary>
    [ObservableProperty]
    public partial string HoverDownload { get; private set; } = string.Empty;

    /// <summary>Bytes Windsock recorded going out, in the bucket under the pointer.</summary>
    [ObservableProperty]
    public partial string HoverUpload { get; private set; } = string.Empty;

    /// <summary>Bytes Windows counted coming in while Windsock was closed.</summary>
    [ObservableProperty]
    public partial string HoverWindowsDownload { get; private set; } = string.Empty;

    /// <summary>Bytes Windows counted going out while Windsock was closed.</summary>
    [ObservableProperty]
    public partial string HoverWindowsUpload { get; private set; } = string.Empty;

    /// <summary>Whether Windsock recorded any of the bucket under the pointer.</summary>
    [ObservableProperty]
    public partial bool HoverHasRecorded { get; private set; }

    /// <summary>Whether Windows counted any of the bucket under the pointer.</summary>
    [ObservableProperty]
    public partial bool HoverHasWindows { get; private set; }

    /// <summary>
    /// Whether the bucket holds both, so each part is named; a bucket of one
    /// kind only needs naming when it is Windows'.
    /// </summary>
    [ObservableProperty]
    public partial bool HoverHasBoth { get; private set; }

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

        // Windsock's own figures and Windows' are shown apart, each in the
        // colours its part of the bar is drawn in, so the popover reads the
        // same way the chart does.
        HoverLabel = string.Format(CultureInfo.CurrentCulture, "{0} – {1}", row.From, row.To);
        HoverHasRecorded = bucket.Minutes > 0 || !bucket.HasEstimate;
        HoverHasWindows = bucket.HasEstimate;
        HoverHasBoth = HoverHasRecorded && HoverHasWindows;
        HoverDownload = SizeFormatter.Format(bucket.BytesDown);
        HoverUpload = SizeFormatter.Format(bucket.BytesUp);
        HoverWindowsDownload = SizeFormatter.Format(bucket.EstimatedDown);
        HoverWindowsUpload = SizeFormatter.Format(bucket.EstimatedUp);
        HoverTotal = row.Total;
        HoverRecorded = Recorded(bucket, _shown);

        ShowAppsFor(row.Start, row.End, bucket.AllTotal);
    }

    /// <summary>The apps behind the bucket under the pointer, busiest first.</summary>
    [ObservableProperty]
    public partial IReadOnlyList<HoverAppRow> HoverApps { get; private set; } = [];

    /// <summary>Whether any app is known for the bucket under the pointer.</summary>
    [ObservableProperty]
    public partial bool HoverHasApps { get; private set; }

    /// <summary>
    /// What the listed apps leave out: the apps not listed, and the bytes no
    /// app was given - packet headers above all, which only the adapter counts.
    /// </summary>
    [ObservableProperty]
    public partial string HoverAppsRest { get; private set; } = string.Empty;

    /// <summary>Whether the listed apps come from both sources, so the dots need their key.</summary>
    [ObservableProperty]
    public partial bool HoverAppsMixed { get; private set; }

    /// <summary>
    /// Reads the apps behind a bucket away from the UI thread, once a bucket,
    /// and shows them if the pointer is still there.
    /// </summary>
    private void ShowAppsFor(DateTimeOffset start, DateTimeOffset end, long bucketTotal)
    {
        if (_appsFor is { } showing && showing.Start == start && showing.End == end)
        {
            return;
        }

        _appsFor = (start, end);

        if (_appsRead.TryGetValue((start, end), out IReadOnlyList<HoverAppRow>? known))
        {
            PresentApps(known, bucketTotal);
            return;
        }

        // Cleared rather than left showing the last bar's apps under this one's figures.
        PresentApps([], bucketTotal);
        int token = ++_appLoads;
        int generation = _appGeneration;
        DateTimeOffset periodUntil = _query.Until;

        _ = Task.Run(() =>
        {
            IReadOnlyList<AppShare> shares = _apps.Breakdown(start, end, periodUntil);
            return (IReadOnlyList<HoverAppRow>)[.. shares.Select(share =>
            {
                AppDescription app = _names.Describe(share.AttributionId);
                return new HoverAppRow(app, share);
            })];
        }).ContinueWith(
            read =>
            {
                if (read.IsFaulted)
                {
                    // Only the popover's list goes without; the figures above it stand.
                    _ = read.Exception;
                    return;
                }

                // Read before a reload: the figures may have changed since, so
                // neither cached nor shown.
                if (generation != _appGeneration)
                {
                    return;
                }

                _appsRead[(start, end)] = read.Result;

                if (token == _appLoads && _appsFor is { } still && still.Start == start && still.End == end)
                {
                    PresentApps(read.Result, bucketTotal);
                }
            },
            CancellationToken.None,
            TaskContinuationOptions.None,
            TaskScheduler.FromCurrentSynchronizationContext());
    }

    private void PresentApps(IReadOnlyList<HoverAppRow> apps, long bucketTotal)
    {
        foreach (HoverAppRow app in apps.Take(HoverAppCount))
        {
            app.Icon ??= _icons.Find(app.App);
        }

        HoverApps = [.. apps.Take(HoverAppCount)];
        HoverHasApps = apps.Count > 0;
        HoverAppsMixed = apps.Any(app => app.HasMeasured) && apps.Any(app => app.HasWindows);

        long listed = apps.Take(HoverAppCount).Sum(app => app.Total);
        long everyApp = apps.Sum(app => app.Total);
        int unlisted = Math.Max(0, apps.Count - HoverAppCount);
        long unattributed = bucketTotal - everyApp;

        List<string> rest = [];

        if (unlisted > 0)
        {
            rest.Add(string.Format(
                CultureInfo.CurrentCulture,
                "{0:N0} more {1}, {2}",
                unlisted,
                unlisted == 1 ? "app" : "apps",
                SizeFormatter.Format(everyApp - listed)));
        }

        // Only worth saying when it is a real share: the adapter counts headers
        // the apps' figures do not, a tenth of a busy download or so.
        if (apps.Count > 0 && unattributed > Math.Max(1024, bucketTotal / 50))
        {
            rest.Add(string.Format(
                CultureInfo.CurrentCulture,
                "{0} not in any app's figure (packet headers, mostly)",
                SizeFormatter.Format(unattributed)));
        }

        HoverAppsRest = string.Join(Environment.NewLine, rest);
    }

    /// <summary>
    /// How much of a bucket Windsock recorded, when it recorded some but not
    /// all of it. Where the rest came from is said by the popover's parts.
    /// </summary>
    private static string Recorded(UsageBucket bucket, UsageGranularity granularity) =>
        bucket.Minutes == 0 || granularity == UsageGranularity.Minute || bucket.Minutes >= MinutesIn(granularity)
            ? string.Empty
            : string.Format(
                CultureInfo.CurrentCulture,
                "{0:N0} of {1:N0} minutes recorded",
                bucket.Minutes,
                MinutesIn(granularity));

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
        DateTimeOffset? first;

        try
        {
            first = _store.Extent().First;
        }
        catch (Exception ex) when (HistoryFailure.IsStorage(ex))
        {
            // Asked on the UI thread while working out the span. The load that
            // follows reads the same file, and it is the one that says so.
            return fallback;
        }

        return first is null
            ? fallback
            : new DateTimeOffset(first.Value.Date, first.Value.Offset);
    }

    [RelayCommand]
    private async Task LoadAsync()
    {
        HistoryRange range = SelectedRange;
        UsageGranularity granularity = SelectedResolution;
        int token = ++_loads;
        IsBusy = true;

        try
        {
            // "All time" starts where the file does, which is asked of the file
            // itself: away from the UI thread, since the file can be busy being
            // rewritten.
            (DateTimeOffset from, DateTimeOffset until) = range == HistoryRange.Everything
                ? await Task.Run(() => Bounds(range)).ConfigureAwait(true)
                : Bounds(range);

            if (token != _loads)
            {
                return;
            }

            IReadOnlyList<UsageBucket> buckets;
            UsageSummary summary;
            TimeSpan watched;

            try
            {
                (buckets, summary, watched) = await Task.Run(() =>
                {
                    // What Windows counted in windows holding no recorded minute:
                    // the time Windsock was closed. Kept apart from the recorded
                    // figures all the way to the screen.
                    IReadOnlyList<UsageSpan> missed = MovedTraffic.Fill(_store, _windows, from, until);
                    IReadOnlyList<UsageBucket> read = HistoryFill.Merge(_store.Read(from, until, granularity), missed, granularity);
                    UsageSummary totals = HistoryFill.Add(_store.Summarise(from, until), missed);
                    TimeSpan covered = Watched(_store.ReadCoverage(from, until), from, until);
                    return (read, totals, covered);
                }).ConfigureAwait(true);
            }
            catch (Exception ex) when (HistoryFailure.IsStorage(ex))
            {
                if (token == _loads)
                {
                    ApplyUnreadable(ex, from, until, granularity);
                }

                return;
            }

            if (token != _loads)
            {
                return;
            }

            Apply(buckets, summary, watched, from, until, granularity);
            await LoadMachineAsync(from, until, granularity, token).ConfigureAwait(true);
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
        _query = (from, until, granularity);
        HasNewerRows = false;
        Rebuild();

        // The apps read for bars before this load may have changed under them.
        _appsRead.Clear();
        _appsFor = null;
        _appGeneration++;

        Buckets = buckets;
        RangeStart = from;
        RangeEnd = until;
        Revision++;

        (string downValue, string downUnit) = SizeFormatter.Split(summary.AllDown);
        (string upValue, string upUnit) = SizeFormatter.Split(summary.AllUp);

        DownloadValue = downValue;
        DownloadUnit = downUnit;
        DownloadFontSize = _downloadSizer.Update(downValue.Length);
        UploadValue = upValue;
        UploadUnit = upUnit;
        UploadFontSize = _uploadSizer.Update(upValue.Length);

        TotalText = SizeFormatter.Format(summary.AllTotal);

        PeakText = summary.Minutes == 0
            ? string.Empty
            : string.Format(
                CultureInfo.CurrentCulture,
                "Fastest {0}/s down, {1}/s up",
                SizeFormatter.Format(summary.PeakDown),
                SizeFormatter.Format(summary.PeakUp));

        // A fastest second is only known for time Windsock was watching; Windows
        // counts minutes, not seconds.
        string unmeasured = summary.EstimatedMinutes > 0 ? "No second measured" : "Nothing recorded";

        PeakDownText = summary.Minutes == 0
            ? unmeasured
            : string.Format(CultureInfo.CurrentCulture, "Fastest {0}/s", SizeFormatter.Format(summary.PeakDown));

        PeakUpText = summary.Minutes == 0
            ? unmeasured
            : string.Format(CultureInfo.CurrentCulture, "Fastest {0}/s", SizeFormatter.Format(summary.PeakUp));

        CoverageText = Coverage(watched, from, until, summary, IsRecording);
        RangeLabel = Label(from, until);
        IsEmpty = summary.Minutes == 0 && summary.EstimatedMinutes == 0;

        if (_unreadable)
        {
            _unreadable = false;

            if (Notice == Unreadable)
            {
                Notice = string.Empty;
            }
        }
    }

    /// <summary>
    /// Shows that the period could not be read, rather than showing it as a
    /// period in which nothing was recorded. The totals read as a dash, not a
    /// zero, because nothing is known about them.
    /// </summary>
    private void ApplyUnreadable(
        Exception failure,
        DateTimeOffset from,
        DateTimeOffset until,
        UsageGranularity granularity)
    {
        // Every new minute reloads, so only the first failure in a run is logged.
        if (!_unreadable)
        {
            _unreadable = true;
            LogLoadFailed(failure);
        }

        _read = [];
        _shown = granularity;
        _query = (from, until, granularity);
        Rebuild();

        Buckets = [];
        RangeStart = from;
        RangeEnd = until;
        Revision++;

        DownloadValue = MeterReadout.Unknown;
        DownloadUnit = string.Empty;
        DownloadFontSize = _downloadSizer.Update(MeterReadout.Unknown.Length);
        UploadValue = MeterReadout.Unknown;
        UploadUnit = string.Empty;
        UploadFontSize = _uploadSizer.Update(MeterReadout.Unknown.Length);

        TotalText = MeterReadout.Unknown;
        PeakText = string.Empty;
        PeakDownText = string.Empty;
        PeakUpText = string.Empty;
        CoverageText = string.Empty;
        RangeLabel = Label(from, until);
        IsEmpty = true;
        Notice = Unreadable;
    }

    private static string Coverage(
        TimeSpan watched,
        DateTimeOffset from,
        DateTimeOffset until,
        UsageSummary summary,
        bool recording)
    {
        string recorded = Coverage(watched, from, until, summary.Minutes, summary.EstimatedMinutes > 0, recording);

        if (summary.EstimatedMinutes == 0)
        {
            return recorded;
        }

        string windows = string.Format(
            CultureInfo.CurrentCulture,
            "Windows counted {0} of it while Windsock was closed, and that is included.",
            SizeFormatter.Format(summary.EstimatedDown + summary.EstimatedUp));

        return recorded.Length == 0 ? windows : recorded + " " + windows;
    }

    private static string Coverage(
        TimeSpan watched,
        DateTimeOffset from,
        DateTimeOffset until,
        int minutes,
        bool counted,
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
            if (counted)
            {
                return "Windsock was not running during this period.";
            }

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

        int removed;

        try
        {
            removed = await Task.Run(() => _store.Delete(from, until)).ConfigureAwait(true);
        }
        catch (Exception ex) when (HistoryFailure.IsStorage(ex))
        {
            LogDeleteFailed(ex);
            Notice = string.Format(
                CultureInfo.CurrentCulture,
                "Could not delete {0}, because the history file could not be written. It may be damaged.",
                row.From);
            return;
        }

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
        IReadOnlyList<UsageBucket> buckets;

        // Read before the file is created, so a history that cannot be read
        // does not leave an empty export behind.
        try
        {
            buckets = await Task.Run(() => _store.Read(from, until, granularity)).ConfigureAwait(true);
        }
        catch (Exception ex) when (HistoryFailure.IsStorage(ex))
        {
            LogExportFailed(ex);
            Notice = "Nothing was exported, because the history file could not be read. It may be damaged.";
            return;
        }

        try
        {
            await Task.Run(() =>
            {
                using StreamWriter writer = new(path, false, new UTF8Encoding(true));
                UsageCsv.Write(writer, granularity, buckets);
            }).ConfigureAwait(true);

            Notice = string.Format(
                CultureInfo.CurrentCulture,
                "Exported {0:N0} {1} to {2}",
                buckets.Count,
                buckets.Count == 1 ? "row" : "rows",
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
        UsageImport imported;

        try
        {
            imported = await Task.Run(() =>
            {
                using StreamReader reader = new(path, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
                return UsageCsv.Read(reader);
            }).ConfigureAwait(true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Notice = "Could not read that file: " + ex.Message;
            return;
        }

        IReadOnlyList<UsageMinute> minutes = imported.Minutes;

        if (minutes.Count == 0)
        {
            Notice = "Nothing in that file could be read as minute-by-minute history.";
            return;
        }

        int conflicts;

        try
        {
            conflicts = await Task.Run(() => _store.CountExisting(minutes)).ConfigureAwait(true);
        }
        catch (Exception ex) when (HistoryFailure.IsStorage(ex))
        {
            LogImportFailed(ex);
            Notice = "Nothing was imported, because the history file could not be read. It may be damaged.";
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

        int written;

        try
        {
            written = await Task.Run(() => choice == ImportChoice.Overwrite
                ? _store.Replace(minutes)
                : _store.Merge(minutes)).ConfigureAwait(true);
        }
        catch (Exception ex) when (HistoryFailure.IsStorage(ex))
        {
            // Each import is one transaction, so a failure part-way keeps none of it.
            LogImportFailed(ex);
            Notice = "Nothing was imported, because the history file could not be written. It may be damaged.";
            return;
        }

        Notice = Describe(choice, written, minutes.Count, conflicts, imported.Skipped);
        await LoadAsync().ConfigureAwait(true);
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "Could not read the usage history for the History tab")]
    private partial void LogLoadFailed(Exception exception);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Could not delete a row of usage history")]
    private partial void LogDeleteFailed(Exception exception);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Could not read the usage history to export it")]
    private partial void LogExportFailed(Exception exception);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Could not import into the usage history")]
    private partial void LogImportFailed(Exception exception);

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
