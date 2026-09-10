using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using Windsock.Core.Formatting;
using Windsock.Core.Networking;
using Windsock.Core.Processes;

namespace Windsock.App.ViewModels;

/// <summary>
/// One entry in the scale picker. Its label depends on the selected family.
/// </summary>
public sealed partial class ScaleOption : ObservableObject
{
    public ScaleOption(RateScale scale, string label)
    {
        Scale = scale;
        Label = label;
    }

    /// <summary>The scale this entry selects.</summary>
    public RateScale Scale { get; }

    /// <summary>Label for the current family, for example <c>MB/s</c>.</summary>
    [ObservableProperty]
    public partial string Label { get; set; }

    /// <summary>
    /// Returns the label, because UI Automation names a list item by its item's
    /// ToString. Without this a screen reader announces the type name.
    /// </summary>
    public override string ToString() => Label;
}

/// <summary>One entry in the family picker.</summary>
public sealed record FamilyOption(RateFamily Family, string Label)
{
    /// <summary>
    /// Returns the label. A record's generated ToString prints every member,
    /// which is what UI Automation would otherwise announce for the item.
    /// </summary>
    public override string ToString() => Label;
}

/// <summary>One process named under a hovered point on the chart.</summary>
public sealed record HoverShare(string Name, string Rate);

/// <summary>
/// Backs the throughput section: the live readouts, the unit pickers, and the
/// rolling chart with its hover readout.
/// </summary>
public sealed partial class ThroughputOverviewViewModel : ObservableObject, IDisposable
{
    private const double Smoothing = 0.4;
    private const double SettleFraction = 0.01;
    private const double SettleFloorBytesPerSecond = 64;
    private const double ReadoutMaximumFontSize = 40;

    private static readonly TimeSpan AnimationInterval = TimeSpan.FromMilliseconds(83);

    private readonly ThroughputMonitor _monitor;
    private readonly Dispatcher _dispatcher;
    private readonly DispatcherTimer _animator;
    private readonly ThroughputSample[] _scratch;
    private readonly TimeSpan _liveInterval;
    private readonly ReadoutSizer _downloadSizer = new(ReadoutMaximumFontSize);
    private readonly ReadoutSizer _uploadSizer = new(ReadoutMaximumFontSize);

    private double _hoverDownloadBytes;
    private double _hoverUploadBytes;
    private double _targetDownload;
    private double _targetUpload;
    private double _displayDownload;
    private double _displayUpload;
#if !STORE
    private readonly ProcessAttributionHistory _attribution;
    private readonly ProcessUsageMonitor _processes;
#endif
    private readonly ProcessShare[] _shares;
    private IDisposable? _watch;
    private bool _running;
    private int _shareCount;
    private bool _disposed;

    public ThroughputOverviewViewModel(
        ThroughputMonitor monitor,
#if !STORE
        ProcessAttributionHistory attribution,
        ProcessUsageMonitor processes,
#endif
        Dispatcher dispatcher)
    {
        _monitor = monitor;
        _dispatcher = dispatcher;

#if STORE
        _shares = [];
#else
        _attribution = attribution;
        _processes = processes;
        _shares = new ProcessShare[attribution.Width];
#endif

        int capacity = monitor.History.Capacity;
        _scratch = new ThroughputSample[capacity];
        DownloadSeries = new SeriesView(new double[capacity]);
        UploadSeries = new SeriesView(new double[capacity]);

        _liveInterval = monitor.Interval;
        SampleIntervalSeconds = monitor.Interval.TotalSeconds;
        DefaultVisibleSamples = Math.Min(capacity, monitor.InitialVisibleSamples);
        MinimumSampleInterval = ThroughputMonitor.MinimumInterval;
        MaximumSampleInterval = ThroughputMonitor.MaximumInterval;
        monitor.IntervalChanged += OnMonitorIntervalChanged;

        DownloadValue = "0";
        DownloadUnit = "B/s";
        UploadValue = "0";
        UploadUnit = "B/s";
        HoverTimeLabel = string.Empty;
        HoverDownload = string.Empty;
        HoverUpload = string.Empty;
        IsDownloadVisible = true;
        IsUploadVisible = true;
        IsExpanded = true;
        DownloadFontSize = ReadoutMaximumFontSize;
        UploadFontSize = ReadoutMaximumFontSize;

        Families = [.. RateUnits.Families.Select(family => new FamilyOption(family, RateUnits.Name(family)))];
        Scales = [.. RateUnits.Scales.Select(scale => new ScaleOption(scale, RateUnits.Name(SelectedFamily, scale)))];

        _animator = new DispatcherTimer(DispatcherPriority.Normal, dispatcher)
        {
            Interval = AnimationInterval,
        };
        _animator.Tick += OnAnimationTick;
    }

    /// <summary>Bytes or bits. Also decides what Auto ladders through.</summary>
    [ObservableProperty]
    public partial RateFamily SelectedFamily { get; set; }

    /// <summary>Auto, or a pinned magnitude within the selected family.</summary>
    [ObservableProperty]
    public partial RateScale SelectedScale { get; set; }

    /// <summary>Entries for the family picker.</summary>
    public IReadOnlyList<FamilyOption> Families { get; }

    /// <summary>Entries for the scale picker, relabelled when the family changes.</summary>
    public IReadOnlyList<ScaleOption> Scales { get; }

    /// <summary>Seconds between samples, passed to the chart for its time axis.</summary>
    [ObservableProperty]
    public partial double SampleIntervalSeconds { get; set; }

    /// <summary>
    /// Sampling interval, or <see langword="null"/> to sample at the app's live
    /// default.
    /// </summary>
    [ObservableProperty]
    public partial TimeSpan? SampleInterval { get; set; }

    /// <summary>Shortest interval the sampler accepts.</summary>
    public TimeSpan MinimumSampleInterval { get; }

    /// <summary>Longest interval the sampler accepts.</summary>
    public TimeSpan MaximumSampleInterval { get; }

    /// <summary>
    /// Raised when the sampling cadence changes, so the view can return the
    /// chart to its default zoom - the old viewport counted samples that no
    /// longer exist.
    /// </summary>
    public event EventHandler? SamplingChanged;

    /// <summary>Samples the chart shows before any zooming.</summary>
    public int DefaultVisibleSamples { get; }

    /// <summary>Download history, oldest first. Written in place.</summary>
    public SeriesView DownloadSeries { get; }

    /// <summary>Upload history, oldest first. Written in place.</summary>
    public SeriesView UploadSeries { get; }

    /// <summary>
    /// Bumped whenever the series buffers change, so the chart redraws even
    /// though the array references never do.
    /// </summary>
    [ObservableProperty]
    public partial int SeriesRevision { get; set; }

    /// <summary>Download rate, formatted without its unit.</summary>
    [ObservableProperty]
    public partial string DownloadValue { get; set; }

    /// <summary>Unit for <see cref="DownloadValue"/>, shown at a smaller size.</summary>
    [ObservableProperty]
    public partial string DownloadUnit { get; set; }

    /// <summary>Upload rate, formatted without its unit.</summary>
    [ObservableProperty]
    public partial string UploadValue { get; set; }

    /// <summary>Unit for <see cref="UploadValue"/>.</summary>
    [ObservableProperty]
    public partial string UploadUnit { get; set; }

    /// <summary>Font size for the download readout.</summary>
    [ObservableProperty]
    public partial double DownloadFontSize { get; set; }

    /// <summary>Font size for the upload readout.</summary>
    [ObservableProperty]
    public partial double UploadFontSize { get; set; }

    /// <summary>
    /// Whether the download series is plotted. Purely a view concern - the
    /// series keeps being sampled and recorded either way.
    /// </summary>
    [ObservableProperty]
    public partial bool IsDownloadVisible { get; set; }

    /// <summary>Whether the upload series is plotted.</summary>
    [ObservableProperty]
    public partial bool IsUploadVisible { get; set; }

    /// <summary>Whether the section body is shown. Purely a view concern.</summary>
    [ObservableProperty]
    public partial bool IsExpanded { get; set; }

    /// <summary>Whether the hover readout should be shown.</summary>
    [ObservableProperty]
    public partial bool IsHoverVisible { get; set; }

    /// <summary>How long ago the hovered point was.</summary>
    [ObservableProperty]
    public partial string HoverTimeLabel { get; set; }

    /// <summary>Download rate at the hovered point.</summary>
    [ObservableProperty]
    public partial string HoverDownload { get; set; }

    /// <summary>Upload rate at the hovered point.</summary>
    [ObservableProperty]
    public partial string HoverUpload { get; set; }

    /// <summary>
    /// The processes behind the hovered point, busiest first.
    /// </summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasHoverShares))]
    public partial IReadOnlyList<HoverShare> HoverShares { get; set; } = [];

    /// <summary>Whether the hovered point can name any process.</summary>
    public bool HasHoverShares => HoverShares.Count > 0;

    /// <summary>Starts listening for samples. Called when the view is shown.</summary>
    public void Start()
    {
        if (_running)
        {
            return;
        }

        _running = true;
        RebuildSeries();
        _monitor.SampleTaken += OnSampleTaken;
#if !STORE
        _watch ??= _processes.Watch();
#endif
    }

    /// <summary>
    /// Stops listening and halts the animation, so a view nobody can see costs
    /// nothing at all.
    /// </summary>
    public void Stop()
    {
        if (!_running)
        {
            return;
        }

        _running = false;
        _monitor.SampleTaken -= OnSampleTaken;
        _animator.Stop();

        _watch?.Dispose();
        _watch = null;
    }

    /// <summary>Updates the hover readout from the values the chart drew.</summary>
    public void SetHover(bool hasValue, double downloadBytes, double uploadBytes, double secondsAgo)
    {
        IsHoverVisible = hasValue;
        if (!hasValue)
        {
            return;
        }

        _hoverDownloadBytes = downloadBytes;
        _hoverUploadBytes = uploadBytes;
#if STORE
        _shareCount = 0;
#else
        _shareCount = _attribution.Find(
            DateTimeOffset.Now.AddSeconds(-secondsAgo),
            _attribution.Resolution * 1.5,
            _shares);
#endif

        FormatHover();

        HoverTimeLabel = secondsAgo < 1 ? "now" : DurationLabel.Format(secondsAgo) + " ago";
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        Stop();
        _monitor.IntervalChanged -= OnMonitorIntervalChanged;
        _animator.Tick -= OnAnimationTick;
    }

    partial void OnSampleIntervalChanged(TimeSpan? value) =>
        _monitor.SetInterval(value ?? _liveInterval);

    private void OnMonitorIntervalChanged(object? sender, EventArgs e)
    {
        SampleIntervalSeconds = _monitor.Interval.TotalSeconds;
        RebuildSeries();
        SamplingChanged?.Invoke(this, EventArgs.Empty);
    }

    partial void OnSelectedFamilyChanged(RateFamily value)
    {
        RelabelScales();
        Reformat();
    }

    partial void OnSelectedScaleChanged(RateScale value) => Reformat();

    private void RelabelScales()
    {
        foreach (ScaleOption option in Scales)
        {
            option.Label = RateUnits.Name(SelectedFamily, option.Scale);
        }
    }

    private void Reformat()
    {
        DownloadUnit = string.Empty;
        UploadUnit = string.Empty;
        UpdateReadouts();

        if (IsHoverVisible)
        {
            FormatHover();
        }
    }

    private void FormatHover()
    {
        HoverDownload = RateFormatter.Format(_hoverDownloadBytes, SelectedFamily, SelectedScale);
        HoverUpload = RateFormatter.Format(_hoverUploadBytes, SelectedFamily, SelectedScale);
        var shares = new List<HoverShare>(_shareCount);

        for (int index = 0; index < _shareCount; index++)
        {
            ProcessShare share = _shares[index];
            shares.Add(new HoverShare(
                share.Name,
                RateFormatter.Format(share.BytesPerSecond, SelectedFamily, SelectedScale)));
        }

        HoverShares = shares;
    }

    private void OnSampleTaken(object? sender, ThroughputSample sample)
    {
        if (_disposed)
        {
            return;
        }

        _dispatcher.InvokeAsync(() => Apply(sample), DispatcherPriority.Background);
    }

    private void Apply(ThroughputSample sample)
    {
        if (_disposed)
        {
            return;
        }

        _targetDownload = sample.DownloadBytesPerSecond;
        _targetUpload = sample.UploadBytesPerSecond;

        RebuildSeries();

        if (!_animator.IsEnabled)
        {
            _animator.Start();
        }
    }

    private void RebuildSeries()
    {
        int count = _monitor.History.CopyTo(_scratch);
        if (count == 0)
        {
            return;
        }

        double[] download = DownloadSeries.Buffer;
        double[] upload = UploadSeries.Buffer;

        for (int i = 0; i < count; i++)
        {
            ThroughputSample sample = _scratch[i];
            download[i] = sample.DownloadBytesPerSecond;
            upload[i] = sample.UploadBytesPerSecond;
        }

        DownloadSeries.Count = count;
        UploadSeries.Count = count;
        SeriesRevision++;
    }

    private void OnAnimationTick(object? sender, EventArgs e)
    {
        _displayDownload += (_targetDownload - _displayDownload) * Smoothing;
        _displayUpload += (_targetUpload - _displayUpload) * Smoothing;

        bool settled =
            HasSettled(_displayDownload, _targetDownload)
            && HasSettled(_displayUpload, _targetUpload);

        if (settled)
        {
            _displayDownload = _targetDownload;
            _displayUpload = _targetUpload;
            _animator.Stop();
        }

        UpdateReadouts();
    }

    private static bool HasSettled(double display, double target)
    {
        double tolerance = Math.Max(Math.Abs(target) * SettleFraction, SettleFloorBytesPerSecond);
        return Math.Abs(target - display) < tolerance;
    }

    private void UpdateReadouts()
    {
        (string downloadValue, string downloadUnit) =
            RateFormatter.Split(_displayDownload, SelectedFamily, SelectedScale);
        (string uploadValue, string uploadUnit) =
            RateFormatter.Split(_displayUpload, SelectedFamily, SelectedScale);

        if (!string.Equals(DownloadValue, downloadValue, StringComparison.Ordinal))
        {
            DownloadValue = downloadValue;
            DownloadFontSize = _downloadSizer.Update(downloadValue.Length);
        }

        if (!string.Equals(DownloadUnit, downloadUnit, StringComparison.Ordinal))
        {
            DownloadUnit = downloadUnit;
        }

        if (!string.Equals(UploadValue, uploadValue, StringComparison.Ordinal))
        {
            UploadValue = uploadValue;
            UploadFontSize = _uploadSizer.Update(uploadValue.Length);
        }

        if (!string.Equals(UploadUnit, uploadUnit, StringComparison.Ordinal))
        {
            UploadUnit = uploadUnit;
        }
    }
}
