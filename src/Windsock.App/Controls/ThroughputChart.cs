using System.Globalization;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using Windsock.Core.Formatting;

namespace Windsock.App.Controls;

/// <summary>Values under the pointer, as drawn.</summary>
public sealed class ChartHoverEventArgs(
    bool hasValue,
    Point position,
    double downloadBytesPerSecond,
    double uploadBytesPerSecond,
    double secondsAgo) : EventArgs
{
    public bool HasValue { get; } = hasValue;

    public Point Position { get; } = position;

    public double DownloadBytesPerSecond { get; } = downloadBytesPerSecond;

    public double UploadBytesPerSecond { get; } = uploadBytesPerSecond;

    public double SecondsAgo { get; } = secondsAgo;
}

/// <summary>
/// A lightweight two-series area chart for live throughput, with labelled axes
/// and wheel zoom over the time axis.
/// </summary>
public sealed class ThroughputChart : FrameworkElement
{
    private const double PixelsPerPoint = 2.5;
    private const double ZoomFactorPerNotch = 1.35;
    private const double ScaleFloorBytesPerSecond = 64 * 1024;
    private const double AxisFontSize = 10;
    private const double ValueAxisWidth = 54;
    private const double TimeAxisHeight = 18;

    private static readonly double[] TickCandidates =
        [1, 2, 5, 10, 15, 30, 60, 120, 300, 600, 900, 1800];

    private const int MaximumTicks = 6;

    private static readonly Duration ZoomDuration = new(TimeSpan.FromMilliseconds(220));

    public static readonly DependencyProperty DownloadSeriesProperty =
        DependencyProperty.Register(
            nameof(DownloadSeries),
            typeof(IReadOnlyList<double>),
            typeof(ThroughputChart),
            new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty UploadSeriesProperty =
        DependencyProperty.Register(
            nameof(UploadSeries),
            typeof(IReadOnlyList<double>),
            typeof(ThroughputChart),
            new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty RevisionProperty =
        DependencyProperty.Register(
            nameof(Revision),
            typeof(int),
            typeof(ThroughputChart),
            new FrameworkPropertyMetadata(0, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty SampleIntervalSecondsProperty =
        DependencyProperty.Register(
            nameof(SampleIntervalSeconds),
            typeof(double),
            typeof(ThroughputChart),
            new FrameworkPropertyMetadata(0.5, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty VisibleSampleCountProperty =
        DependencyProperty.Register(
            nameof(VisibleSampleCount),
            typeof(double),
            typeof(ThroughputChart),
            new FrameworkPropertyMetadata(120d, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty MinimumVisibleSamplesProperty =
        DependencyProperty.Register(
            nameof(MinimumVisibleSamples),
            typeof(double),
            typeof(ThroughputChart),
            new FrameworkPropertyMetadata(20d));

    public static readonly DependencyProperty ShowDownloadProperty =
        DependencyProperty.Register(
            nameof(ShowDownload),
            typeof(bool),
            typeof(ThroughputChart),
            new FrameworkPropertyMetadata(true, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty ShowUploadProperty =
        DependencyProperty.Register(
            nameof(ShowUpload),
            typeof(bool),
            typeof(ThroughputChart),
            new FrameworkPropertyMetadata(true, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty FamilyProperty =
        DependencyProperty.Register(
            nameof(Family),
            typeof(RateFamily),
            typeof(ThroughputChart),
            new FrameworkPropertyMetadata(RateFamily.Bytes, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty ScaleProperty =
        DependencyProperty.Register(
            nameof(Scale),
            typeof(RateScale),
            typeof(ThroughputChart),
            new FrameworkPropertyMetadata(RateScale.Auto, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty DownloadBrushProperty =
        DependencyProperty.Register(
            nameof(DownloadBrush),
            typeof(Brush),
            typeof(ThroughputChart),
            new FrameworkPropertyMetadata(Brushes.DodgerBlue, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty UploadBrushProperty =
        DependencyProperty.Register(
            nameof(UploadBrush),
            typeof(Brush),
            typeof(ThroughputChart),
            new FrameworkPropertyMetadata(Brushes.Orange, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty GridBrushProperty =
        DependencyProperty.Register(
            nameof(GridBrush),
            typeof(Brush),
            typeof(ThroughputChart),
            new FrameworkPropertyMetadata(Brushes.Gray, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty AxisBrushProperty =
        DependencyProperty.Register(
            nameof(AxisBrush),
            typeof(Brush),
            typeof(ThroughputChart),
            new FrameworkPropertyMetadata(Brushes.Gray, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty SurfaceBrushProperty =
        DependencyProperty.Register(
            nameof(SurfaceBrush),
            typeof(Brush),
            typeof(ThroughputChart),
            new FrameworkPropertyMetadata(Brushes.Transparent, FrameworkPropertyMetadataOptions.AffectsRender));
    private double[] _downloadBuckets = [];
    private double[] _uploadBuckets = [];
    private Point[] _downloadPoints = [];
    private Point[] _uploadPoints = [];

    private readonly Typeface _typeface = new(
        SystemFonts.MessageFontFamily,
        FontStyles.Normal,
        FontWeights.Normal,
        FontStretches.Normal);

    private int _bucketCount;
    private double _renderedMaximum = ScaleFloorBytesPerSecond;
    private int _hoveredBucket = -1;

    public ThroughputChart()
    {
        // A crosshair signals that the plot itself is readable, not decorative.
        Cursor = Cursors.Cross;
        Focusable = false;
    }

    /// <summary>Raised when the values under the pointer change.</summary>
    public event EventHandler<ChartHoverEventArgs>? HoverChanged;

    /// <summary>Inbound series, oldest first.</summary>
    public IReadOnlyList<double>? DownloadSeries
    {
        get => (IReadOnlyList<double>?)GetValue(DownloadSeriesProperty);
        set => SetValue(DownloadSeriesProperty, value);
    }

    /// <summary>Outbound series, oldest first.</summary>
    public IReadOnlyList<double>? UploadSeries
    {
        get => (IReadOnlyList<double>?)GetValue(UploadSeriesProperty);
        set => SetValue(UploadSeriesProperty, value);
    }

    /// <summary>Bumped by the source whenever the series contents change.</summary>
    public int Revision
    {
        get => (int)GetValue(RevisionProperty);
        set => SetValue(RevisionProperty, value);
    }

    /// <summary>Seconds between consecutive samples, used to label the time axis.</summary>
    public double SampleIntervalSeconds
    {
        get => (double)GetValue(SampleIntervalSecondsProperty);
        set => SetValue(SampleIntervalSecondsProperty, value);
    }

    /// <summary>
    /// How many trailing samples are drawn. Animated by the wheel, which is why
    /// it is a double rather than an int.
    /// </summary>
    public double VisibleSampleCount
    {
        get => (double)GetValue(VisibleSampleCountProperty);
        set => SetValue(VisibleSampleCountProperty, value);
    }

    /// <summary>Closest zoom permitted, in samples.</summary>
    public double MinimumVisibleSamples
    {
        get => (double)GetValue(MinimumVisibleSamplesProperty);
        set => SetValue(MinimumVisibleSamplesProperty, value);
    }

    /// <summary>
    /// Whether the download series is plotted. Hiding it also removes it from
    /// the vertical scale, so the remaining series fills the plot.
    /// </summary>
    public bool ShowDownload
    {
        get => (bool)GetValue(ShowDownloadProperty);
        set => SetValue(ShowDownloadProperty, value);
    }

    /// <summary>Whether the upload series is plotted.</summary>
    public bool ShowUpload
    {
        get => (bool)GetValue(ShowUploadProperty);
        set => SetValue(ShowUploadProperty, value);
    }

    /// <summary>Unit family the value axis is labelled in.</summary>
    public RateFamily Family
    {
        get => (RateFamily)GetValue(FamilyProperty);
        set => SetValue(FamilyProperty, value);
    }

    /// <summary>Unit scale the value axis is labelled in.</summary>
    public RateScale Scale
    {
        get => (RateScale)GetValue(ScaleProperty);
        set => SetValue(ScaleProperty, value);
    }

    public Brush DownloadBrush
    {
        get => (Brush)GetValue(DownloadBrushProperty);
        set => SetValue(DownloadBrushProperty, value);
    }

    public Brush UploadBrush
    {
        get => (Brush)GetValue(UploadBrushProperty);
        set => SetValue(UploadBrushProperty, value);
    }

    public Brush GridBrush
    {
        get => (Brush)GetValue(GridBrushProperty);
        set => SetValue(GridBrushProperty, value);
    }

    /// <summary>Colour of the axis tick labels.</summary>
    public Brush AxisBrush
    {
        get => (Brush)GetValue(AxisBrushProperty);
        set => SetValue(AxisBrushProperty, value);
    }

    /// <summary>Card colour behind the chart, used to ring the hover markers.</summary>
    public Brush SurfaceBrush
    {
        get => (Brush)GetValue(SurfaceBrushProperty);
        set => SetValue(SurfaceBrushProperty, value);
    }

    protected override void OnRender(DrawingContext drawingContext)
    {
        base.OnRender(drawingContext);

        double width = ActualWidth;
        double height = ActualHeight;
        if (width <= 0 || height <= 0)
        {
            return;
        }
        drawingContext.DrawRectangle(Brushes.Transparent, null, new Rect(0, 0, width, height));

        double plotWidth = width - ValueAxisWidth;
        double plotHeight = height - TimeAxisHeight;
        if (plotWidth <= 8 || plotHeight <= 8)
        {
            return;
        }

        bool hasData = Resample(plotWidth);

        DrawValueAxis(drawingContext, plotHeight);
        DrawTimeAxis(drawingContext, plotWidth, plotHeight);

        if (!hasData)
        {
            return;
        }

        Project(_uploadBuckets, _uploadPoints, plotWidth, plotHeight);
        Project(_downloadBuckets, _downloadPoints, plotWidth, plotHeight);
        if (ShowUpload)
        {
            DrawSeries(drawingContext, _uploadPoints, UploadBrush, plotWidth, plotHeight);
        }

        if (ShowDownload)
        {
            DrawSeries(drawingContext, _downloadPoints, DownloadBrush, plotWidth, plotHeight);
        }

        DrawHover(drawingContext, plotWidth, plotHeight);
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        UpdateHover(e.GetPosition(this));
    }

    protected override void OnMouseLeave(MouseEventArgs e)
    {
        base.OnMouseLeave(e);
        ClearHover();
    }

    protected override void OnMouseWheel(MouseWheelEventArgs e)
    {
        base.OnMouseWheel(e);

        int total = SampleCount();
        if (total < 2)
        {
            return;
        }

        double notches = e.Delta / (double)Mouse.MouseWheelDeltaForOneLine;
        double current = ResolveVisible(total);
        double target = Math.Clamp(
            current / Math.Pow(ZoomFactorPerNotch, notches),
            Math.Min(MinimumVisibleSamples, total),
            total);
        var animation = new DoubleAnimation
        {
            To = target,
            Duration = ZoomDuration,
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut },
            FillBehavior = FillBehavior.HoldEnd,
        };
        animation.Completed += (_, _) =>
        {
            if (IsMouseOver)
            {
                UpdateHover(Mouse.GetPosition(this));
            }
        };

        BeginAnimation(VisibleSampleCountProperty, animation);
        e.Handled = true;
    }

    private double ResolveVisible(int total) =>
        Math.Clamp(VisibleSampleCount, Math.Min(MinimumVisibleSamples, total), total);

    private int SampleCount()
    {
        int download = DownloadSeries?.Count ?? 0;
        int upload = UploadSeries?.Count ?? 0;
        return Math.Min(download, upload);
    }

    private bool Resample(double plotWidth)
    {
        IReadOnlyList<double>? download = DownloadSeries;
        IReadOnlyList<double>? upload = UploadSeries;

        int total = SampleCount();
        if (download is null || upload is null || total < 2)
        {
            _bucketCount = 0;
            return false;
        }

        int visible = Math.Clamp((int)Math.Round(ResolveVisible(total)), 2, total);
        int start = total - visible;
        int buckets = Math.Clamp((int)(plotWidth / PixelsPerPoint), 2, visible);

        EnsureCapacity(ref _downloadBuckets, buckets);
        EnsureCapacity(ref _uploadBuckets, buckets);
        EnsureCapacity(ref _downloadPoints, buckets);
        EnsureCapacity(ref _uploadPoints, buckets);

        double peak = 0;
        for (int i = 0; i < buckets; i++)
        {
            int from = start + (int)((long)i * visible / buckets);
            int to = start + (int)((long)(i + 1) * visible / buckets);
            if (to <= from)
            {
                to = from + 1;
            }

            double maxDownload = 0;
            double maxUpload = 0;
            for (int j = from; j < to && j < total; j++)
            {
                maxDownload = Math.Max(maxDownload, download[j]);
                maxUpload = Math.Max(maxUpload, upload[j]);
            }

            _downloadBuckets[i] = maxDownload;
            _uploadBuckets[i] = maxUpload;
            if (ShowDownload)
            {
                peak = Math.Max(peak, maxDownload);
            }

            if (ShowUpload)
            {
                peak = Math.Max(peak, maxUpload);
            }
        }

        _bucketCount = buckets;
        _renderedMaximum = ChartScale.NiceMaximum(peak, ScaleFloorBytesPerSecond);
        return true;
    }

    private static void EnsureCapacity<T>(ref T[] buffer, int required)
    {
        if (buffer.Length < required)
        {
            buffer = new T[required];
        }
    }

    private void Project(double[] values, Point[] points, double plotWidth, double plotHeight)
    {
        double maximum = _renderedMaximum > 0 ? _renderedMaximum : 1;
        double step = _bucketCount > 1 ? plotWidth / (_bucketCount - 1) : plotWidth;

        for (int i = 0; i < _bucketCount; i++)
        {
            double normalised = Math.Clamp(values[i] / maximum, 0, 1);
            points[i] = new Point(ValueAxisWidth + (i * step), plotHeight - (normalised * plotHeight));
        }
    }

    private void UpdateHover(Point position)
    {
        double plotWidth = ActualWidth - ValueAxisWidth;
        if (_bucketCount < 2 || plotWidth <= 0)
        {
            ClearHover();
            return;
        }

        double step = plotWidth / (_bucketCount - 1);
        int bucket = Math.Clamp(
            (int)Math.Round((position.X - ValueAxisWidth) / step),
            0,
            _bucketCount - 1);
        if (bucket != _hoveredBucket)
        {
            _hoveredBucket = bucket;
            InvalidateVisual();
        }

        double visible = ResolveVisible(SampleCount());
        double samplesPerBucket = visible / _bucketCount;
        double samplesAgo = (_bucketCount - 1 - bucket) * samplesPerBucket;

        HoverChanged?.Invoke(
            this,
            new ChartHoverEventArgs(
                hasValue: true,
                position,
                _downloadBuckets[bucket],
                _uploadBuckets[bucket],
                samplesAgo * SampleIntervalSeconds));
    }

    private void ClearHover()
    {
        if (_hoveredBucket == -1)
        {
            return;
        }

        _hoveredBucket = -1;
        InvalidateVisual();
        HoverChanged?.Invoke(this, new ChartHoverEventArgs(false, default, 0, 0, 0));
    }

    private void DrawValueAxis(DrawingContext drawingContext, double plotHeight)
    {
        var pen = new Pen(GridBrush, 1);
        pen.Freeze();

        for (int i = 0; i <= 2; i++)
        {
            double fraction = i / 2d;
            double y = Math.Round(plotHeight * fraction) + 0.5;
            if (i > 0)
            {
                drawingContext.DrawLine(pen, new Point(ValueAxisWidth, y), new Point(ActualWidth, y));
            }

            double value = _renderedMaximum * (1 - fraction);
            string text = value <= 0
                ? "0"
                : RateFormatter.Format(value, Family, Scale);

            DrawLabel(drawingContext, text, ValueAxisWidth - 8, y, HorizontalAlignment.Right);
        }
    }

    private void DrawTimeAxis(DrawingContext drawingContext, double plotWidth, double plotHeight)
    {
        double span = ResolveVisible(Math.Max(SampleCount(), 2)) * SampleIntervalSeconds;
        if (span <= 0)
        {
            return;
        }

        double labelY = plotHeight + (TimeAxisHeight / 2);
        DrawLabel(drawingContext, "now", ActualWidth, labelY, HorizontalAlignment.Right);

        double tick = ChooseTick(span);
        var pen = new Pen(GridBrush, 1);
        pen.Freeze();

        double right = ValueAxisWidth + plotWidth;
        for (double seconds = tick; seconds < span; seconds += tick)
        {
            double x = Math.Round(right - (seconds / span * plotWidth)) + 0.5;
            if (x < ValueAxisWidth + 12)
            {
                break;
            }

            drawingContext.DrawLine(pen, new Point(x, 0), new Point(x, plotHeight));
            DrawLabel(drawingContext, DurationLabel.Format(seconds), x, labelY, HorizontalAlignment.Center);
        }
    }

    private static double ChooseTick(double span)
    {
        foreach (double candidate in TickCandidates)
        {
            if (span / candidate <= MaximumTicks)
            {
                return candidate;
            }
        }

        return TickCandidates[^1];
    }

    private void DrawLabel(DrawingContext drawingContext, string text, double x, double y, HorizontalAlignment alignment)
    {
        var formatted = new FormattedText(
            text,
            CultureInfo.CurrentCulture,
            FlowDirection.LeftToRight,
            _typeface,
            AxisFontSize,
            AxisBrush,
            VisualTreeHelper.GetDpi(this).PixelsPerDip);

        double offset = alignment switch
        {
            HorizontalAlignment.Right => -formatted.Width,
            HorizontalAlignment.Center => -formatted.Width / 2,
            _ => 0,
        };

        drawingContext.DrawText(formatted, new Point(x + offset, y - (formatted.Height / 2)));
    }

    private void DrawHover(DrawingContext drawingContext, double plotWidth, double plotHeight)
    {
        if (_hoveredBucket < 0 || _hoveredBucket >= _bucketCount || _bucketCount < 2)
        {
            return;
        }

        double x = Math.Round(ValueAxisWidth + (_hoveredBucket * (plotWidth / (_bucketCount - 1)))) + 0.5;

        var guide = new Pen(GridBrush, 1) { DashStyle = new DashStyle([3, 3], 0) };
        guide.Freeze();
        drawingContext.DrawLine(guide, new Point(x, 0), new Point(x, plotHeight));

        if (ShowUpload)
        {
            DrawMarker(drawingContext, _uploadPoints[_hoveredBucket], UploadBrush, x);
        }

        if (ShowDownload)
        {
            DrawMarker(drawingContext, _downloadPoints[_hoveredBucket], DownloadBrush, x);
        }
    }

    private void DrawMarker(DrawingContext drawingContext, Point point, Brush brush, double x)
    {
        var ring = new Pen(SurfaceBrush, 2.5);
        ring.Freeze();
        drawingContext.DrawEllipse(brush, ring, new Point(x, point.Y), 4, 4);
    }

    private void DrawSeries(
        DrawingContext drawingContext,
        Point[] points,
        Brush brush,
        double plotWidth,
        double plotHeight)
    {
        if (_bucketCount < 2 || brush is not SolidColorBrush solid)
        {
            return;
        }

        StreamGeometry line = BuildSmoothLine(points, _bucketCount, plotHeight);
        StreamGeometry area = BuildSmoothArea(points, _bucketCount, plotWidth, plotHeight);
        var fill = new SolidColorBrush(solid.Color) { Opacity = 0.12 };
        fill.Freeze();

        var stroke = new Pen(brush, 2)
        {
            LineJoin = PenLineJoin.Round,
            StartLineCap = PenLineCap.Round,
            EndLineCap = PenLineCap.Round,
        };
        stroke.Freeze();

        drawingContext.DrawGeometry(fill, null, area);
        drawingContext.DrawGeometry(null, stroke, line);
    }

    private static StreamGeometry BuildSmoothLine(Point[] points, int count, double plotHeight)
    {
        var geometry = new StreamGeometry();
        using (StreamGeometryContext context = geometry.Open())
        {
            context.BeginFigure(points[0], isFilled: false, isClosed: false);
            AppendSmoothSegments(context, points, count, plotHeight);
        }

        geometry.Freeze();
        return geometry;
    }

    private static StreamGeometry BuildSmoothArea(Point[] points, int count, double plotWidth, double plotHeight)
    {
        var geometry = new StreamGeometry();
        using (StreamGeometryContext context = geometry.Open())
        {
            context.BeginFigure(new Point(points[0].X, plotHeight), isFilled: true, isClosed: true);
            context.LineTo(points[0], isStroked: false, isSmoothJoin: false);
            AppendSmoothSegments(context, points, count, plotHeight);
            context.LineTo(new Point(points[count - 1].X, plotHeight), isStroked: false, isSmoothJoin: false);
        }

        geometry.Freeze();
        return geometry;
    }

    private static void AppendSmoothSegments(
        StreamGeometryContext context,
        Point[] points,
        int count,
        double plotHeight)
    {
        for (int i = 0; i < count - 1; i++)
        {
            Point previous = points[Math.Max(i - 1, 0)];
            Point current = points[i];
            Point next = points[i + 1];
            Point following = points[Math.Min(i + 2, count - 1)];

            var control1 = new Point(
                current.X + ((next.X - previous.X) / 6),
                Math.Clamp(current.Y + ((next.Y - previous.Y) / 6), 0, plotHeight));

            var control2 = new Point(
                next.X - ((following.X - current.X) / 6),
                Math.Clamp(next.Y - ((following.Y - current.Y) / 6), 0, plotHeight));

            context.BezierTo(control1, control2, next, isStroked: true, isSmoothJoin: true);
        }
    }
}
