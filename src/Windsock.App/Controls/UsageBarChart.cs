using System.Globalization;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using Windsock.Core.Formatting;
using Windsock.Core.History;

namespace Windsock.App.Controls;

/// <summary>What the pointer is over, if anything.</summary>
public sealed class UsageHoverEventArgs(bool hasValue, Point position, UsageBucket bucket) : EventArgs
{
    public bool HasValue { get; } = hasValue;

    public Point Position { get; } = position;

    public UsageBucket Bucket { get; } = bucket;
}

/// <summary>
/// Draws recorded usage as one bar per bucket, upload stacked on download.
/// </summary>
public sealed class UsageBarChart : FrameworkElement
{
    private const double LeftGutter = 58;
    private const double BottomGutter = 20;
    private const double TopPadding = 10;
    private const double LabelSize = 10.5;
    private const double MinimumBarWidth = 1.0;
    private const double ZoomPerNotch = 1.35;
    private const double MinimumBucketsVisible = 2;
    private double _span = 1;
    private double _offset;
    private Point _dragFrom;
    private double _dragOffset;
    private bool _dragging;

    public static readonly DependencyProperty BucketsProperty = Register<IReadOnlyList<UsageBucket>>(
        nameof(Buckets), []);

    public static readonly DependencyProperty RevisionProperty = Register(nameof(Revision), 0);

    public static readonly DependencyProperty RangeStartProperty = Register(
        nameof(RangeStart), default(DateTimeOffset), resetsZoom: true);

    public static readonly DependencyProperty RangeEndProperty = Register(
        nameof(RangeEnd), default(DateTimeOffset), resetsZoom: true);

    public static readonly DependencyProperty GranularityProperty = Register(
        nameof(Granularity), UsageGranularity.Hour, resetsZoom: true);

    public static readonly DependencyProperty DownloadBrushProperty = Register<Brush?>(
        nameof(DownloadBrush), null);

    public static readonly DependencyProperty UploadBrushProperty = Register<Brush?>(
        nameof(UploadBrush), null);

    public static readonly DependencyProperty GridBrushProperty = Register<Brush?>(
        nameof(GridBrush), null);

    public static readonly DependencyProperty AxisBrushProperty = Register<Brush?>(
        nameof(AxisBrush), null);

    public static readonly DependencyProperty SurfaceBrushProperty = Register<Brush?>(
        nameof(SurfaceBrush), null);

    public static readonly DependencyProperty HighlightBrushProperty = Register<Brush?>(
        nameof(HighlightBrush), null);

    public static readonly DependencyProperty HighlightedStartProperty = Register<DateTimeOffset?>(
        nameof(HighlightedStart), null);

    /// <summary>Raised as the pointer moves over the plot.</summary>
    public event EventHandler<UsageHoverEventArgs>? HoverChanged;

    /// <summary>The buckets to draw, in time order.</summary>
    public IReadOnlyList<UsageBucket> Buckets
    {
        get => (IReadOnlyList<UsageBucket>)GetValue(BucketsProperty);
        set => SetValue(BucketsProperty, value);
    }

    /// <summary>Bumped by the owner to force a redraw.</summary>
    public int Revision
    {
        get => (int)GetValue(RevisionProperty);
        set => SetValue(RevisionProperty, value);
    }

    /// <summary>Left edge of the time axis.</summary>
    public DateTimeOffset RangeStart
    {
        get => (DateTimeOffset)GetValue(RangeStartProperty);
        set => SetValue(RangeStartProperty, value);
    }

    /// <summary>Right edge of the time axis.</summary>
    public DateTimeOffset RangeEnd
    {
        get => (DateTimeOffset)GetValue(RangeEndProperty);
        set => SetValue(RangeEndProperty, value);
    }

    /// <summary>How wide one bucket is.</summary>
    public UsageGranularity Granularity
    {
        get => (UsageGranularity)GetValue(GranularityProperty);
        set => SetValue(GranularityProperty, value);
    }

    public Brush? DownloadBrush
    {
        get => (Brush?)GetValue(DownloadBrushProperty);
        set => SetValue(DownloadBrushProperty, value);
    }

    public Brush? UploadBrush
    {
        get => (Brush?)GetValue(UploadBrushProperty);
        set => SetValue(UploadBrushProperty, value);
    }

    public Brush? GridBrush
    {
        get => (Brush?)GetValue(GridBrushProperty);
        set => SetValue(GridBrushProperty, value);
    }

    public Brush? AxisBrush
    {
        get => (Brush?)GetValue(AxisBrushProperty);
        set => SetValue(AxisBrushProperty, value);
    }

    public Brush? SurfaceBrush
    {
        get => (Brush?)GetValue(SurfaceBrushProperty);
        set => SetValue(SurfaceBrushProperty, value);
    }

    /// <summary>Wash drawn behind the bar under the pointer.</summary>
    public Brush? HighlightBrush
    {
        get => (Brush?)GetValue(HighlightBrushProperty);
        set => SetValue(HighlightBrushProperty, value);
    }

    /// <summary>
    /// Which bucket to wash, whether or not this chart is what is being
    /// pointed at.
    /// </summary>
    public DateTimeOffset? HighlightedStart
    {
        get => (DateTimeOffset?)GetValue(HighlightedStartProperty);
        set => SetValue(HighlightedStartProperty, value);
    }

    /// <summary>Whether the time axis is showing less than the whole period.</summary>
    public bool IsZoomed => _span < 0.999;

    /// <summary>Returns the whole period to view.</summary>
    public void ResetZoom()
    {
        _span = 1;
        _offset = 0;
        InvalidateVisual();
    }

    private static DependencyProperty Register<T>(string name, T fallback, bool resetsZoom = false) =>
        DependencyProperty.Register(
            name,
            typeof(T),
            typeof(UsageBarChart),
            new FrameworkPropertyMetadata(
                fallback,
                FrameworkPropertyMetadataOptions.AffectsRender,
                resetsZoom ? OnRangeChanged : null));

    private static void OnRangeChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is UsageBarChart chart)
        {
            chart._span = 1;
            chart._offset = 0;
        }
    }

    protected override void OnMouseWheel(MouseWheelEventArgs e)
    {
        base.OnMouseWheel(e);
        ArgumentNullException.ThrowIfNull(e);

        double minimum = MinimumSpan();

        if (minimum >= 1)
        {
            return;
        }

        double notches = e.Delta / (double)Mouse.MouseWheelDeltaForOneLine;
        double span = Math.Clamp(_span / Math.Pow(ZoomPerNotch, notches), minimum, 1);

        if (Math.Abs(span - _span) < 0.0001)
        {
            return;
        }
        double at = _offset + (Fraction(e.GetPosition(this).X) * _span);
        _offset = Math.Clamp(at - ((at - _offset) * (span / _span)), 0, 1 - span);
        _span = span;

        InvalidateVisual();
        Track(e.GetPosition(this));
        e.Handled = true;
    }

    protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonDown(e);
        ArgumentNullException.ThrowIfNull(e);

        if (!IsZoomed)
        {
            return;
        }

        _dragging = true;
        _dragFrom = e.GetPosition(this);
        _dragOffset = _offset;
        CaptureMouse();
        Cursor = Cursors.ScrollWE;
    }

    protected override void OnMouseLeftButtonUp(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonUp(e);
        EndDrag();
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        ArgumentNullException.ThrowIfNull(e);

        Point at = e.GetPosition(this);

        if (_dragging)
        {
            double width = Math.Max(1, ActualWidth - LeftGutter - 8);
            double moved = (at.X - _dragFrom.X) / width * _span;
            _offset = Math.Clamp(_dragOffset - moved, 0, 1 - _span);
            InvalidateVisual();
            return;
        }

        Track(at);
    }

    protected override void OnMouseLeave(MouseEventArgs e)
    {
        base.OnMouseLeave(e);
        EndDrag();
        Clear();
    }

    private void EndDrag()
    {
        if (!_dragging)
        {
            return;
        }

        _dragging = false;
        ReleaseMouseCapture();
        Cursor = null;
    }

    private void Clear() =>
        HoverChanged?.Invoke(this, new UsageHoverEventArgs(false, default, default));

    private void Track(Point at)
    {
        IReadOnlyList<UsageBucket> buckets = Buckets;
        double total = (RangeEnd - RangeStart).TotalSeconds;

        if (buckets.Count == 0 || total <= 0 || at.X < LeftGutter || at.Y > ActualHeight - BottomGutter)
        {
            Clear();
            return;
        }
        double seconds = (_offset + (Fraction(at.X) * _span)) * total;
        double width = SecondsPer(Granularity);
        int found = -1;

        for (int i = 0; i < buckets.Count; i++)
        {
            double start = (buckets[i].Start - RangeStart).TotalSeconds;

            if (seconds >= start - (width / 2) && seconds < start + width + (width / 2))
            {
                found = i;

                if (seconds >= start && seconds < start + width)
                {
                    break;
                }
            }
        }

        if (found < 0)
        {
            Clear();
            return;
        }

        HoverChanged?.Invoke(this, new UsageHoverEventArgs(true, at, buckets[found]));
    }

    private double Fraction(double x) =>
        Math.Clamp((x - LeftGutter) / Math.Max(1, ActualWidth - LeftGutter - 8), 0, 1);

    private double MinimumSpan()
    {
        double total = (RangeEnd - RangeStart).TotalSeconds;

        return total <= 0
            ? 1
            : Math.Min(1, SecondsPer(Granularity) * MinimumBucketsVisible / total);
    }

    protected override void OnRender(DrawingContext drawingContext)
    {
        ArgumentNullException.ThrowIfNull(drawingContext);

        double width = ActualWidth;
        double height = ActualHeight;

        if (width <= LeftGutter + 8 || height <= BottomGutter + 8)
        {
            return;
        }
        drawingContext.DrawRectangle(Brushes.Transparent, null, new Rect(0, 0, width, height));

        if (SurfaceBrush is { } surface)
        {
            drawingContext.DrawRoundedRectangle(surface, null, new Rect(0, 0, width, height), 6, 6);
        }

        double plotLeft = LeftGutter;
        double plotWidth = width - LeftGutter - 8;
        double plotTop = TopPadding;
        double plotHeight = height - TopPadding - BottomGutter;

        IReadOnlyList<UsageBucket> buckets = Buckets;
        long ceiling = Ceiling(buckets);

        DrawGrid(drawingContext, plotLeft, plotTop, plotWidth, plotHeight, ceiling);

        if (buckets.Count == 0 || ceiling <= 0)
        {
            return;
        }
        drawingContext.PushClip(new RectangleGeometry(new Rect(plotLeft, 0, plotWidth, height)));
        DrawBars(drawingContext, buckets, plotLeft, plotTop, plotWidth, plotHeight, ceiling);
        drawingContext.Pop();

        DrawTimeLabels(drawingContext, plotLeft, plotWidth, height);
    }

    private static long Ceiling(IReadOnlyList<UsageBucket> buckets)
    {
        long tallest = 0;

        foreach (UsageBucket bucket in buckets)
        {
            if (bucket.BytesTotal > tallest)
            {
                tallest = bucket.BytesTotal;
            }
        }

        return tallest;
    }

    private void DrawGrid(
        DrawingContext context,
        double left,
        double top,
        double width,
        double height,
        long ceiling)
    {
        if (GridBrush is not { } grid)
        {
            return;
        }

        Pen pen = new(grid, 1);
        pen.Freeze();
        for (int i = 0; i <= 2; i++)
        {
            double y = Snap(top + (height * i / 2.0));
            context.DrawLine(pen, new Point(left, y), new Point(left + width, y));

            if (ceiling > 0)
            {
                DrawAxisLabel(context, SizeFormatter.Format(ceiling * (2 - i) / 2), left - 8, y, right: true);
            }
        }
    }

    private void DrawBars(
        DrawingContext context,
        IReadOnlyList<UsageBucket> buckets,
        double left,
        double top,
        double width,
        double height,
        long ceiling)
    {
        double total = (RangeEnd - RangeStart).TotalSeconds;

        if (total <= 0)
        {
            return;
        }

        double visible = total * _span;
        double from = total * _offset;
        double bucketSeconds = SecondsPer(Granularity);
        double slot = width * bucketSeconds / visible;
        double barWidth = Math.Max(MinimumBarWidth, slot > 4 ? slot - 1 : slot);

        Brush? download = DownloadBrush;
        Brush? upload = UploadBrush;
        DateTimeOffset? lit = HighlightedStart;
        double bottom = top + height;

        for (int i = 0; i < buckets.Count; i++)
        {
            UsageBucket bucket = buckets[i];
            double offset = (bucket.Start - RangeStart).TotalSeconds - from;

            if (offset < -bucketSeconds || offset > visible)
            {
                continue;
            }

            double x = left + (width * offset / visible);
            double downHeight = height * bucket.BytesDown / ceiling;
            double upHeight = height * bucket.BytesUp / ceiling;
            double stacked = downHeight + upHeight;

            if (stacked > 0 && stacked < 1)
            {
                downHeight = bucket.BytesDown > 0 ? 1 : 0;
                upHeight = bucket.BytesUp > 0 ? 1 : 0;
            }

            if (lit == bucket.Start && HighlightBrush is { } highlight)
            {
                context.DrawRectangle(highlight, null, new Rect(x, top, barWidth, height));
            }

            if (download is not null && downHeight > 0)
            {
                context.DrawRectangle(
                    download,
                    null,
                    new Rect(x, bottom - downHeight, barWidth, downHeight));
            }

            if (upload is not null && upHeight > 0)
            {
                context.DrawRectangle(
                    upload,
                    null,
                    new Rect(x, bottom - downHeight - upHeight, barWidth, upHeight));
            }
        }
    }

    private void DrawTimeLabels(DrawingContext context, double left, double width, double height)
    {
        DateTimeOffset start = RangeStart;
        DateTimeOffset end = RangeEnd;

        if (end <= start)
        {
            return;
        }

        double total = (end - start).TotalSeconds;
        DateTimeOffset from = start + TimeSpan.FromSeconds(total * _offset);
        TimeSpan visible = TimeSpan.FromSeconds(total * _span);
        string pattern = visible > TimeSpan.FromDays(2) ? "d MMM" : "h:mm tt";
        double y = height - BottomGutter + 4;
        for (int i = 0; i <= 3; i++)
        {
            double fraction = i / 3.0;
            DateTimeOffset at = from + TimeSpan.FromSeconds(visible.TotalSeconds * fraction);
            double x = left + (width * fraction);

            DrawAxisLabel(
                context,
                at.ToString(pattern, CultureInfo.CurrentCulture),
                x,
                y,
                right: i == 3,
                centred: i is > 0 and < 3);
        }
    }

    private void DrawAxisLabel(
        DrawingContext context,
        string text,
        double x,
        double y,
        bool right = false,
        bool centred = false)
    {
        if (AxisBrush is not { } brush)
        {
            return;
        }

        FormattedText formatted = new(
            text,
            CultureInfo.CurrentCulture,
            FlowDirection.LeftToRight,
            new Typeface(
                new FontFamily("Segoe UI Variable Text, Segoe UI"),
                FontStyles.Normal,
                FontWeights.Normal,
                FontStretches.Normal),
            LabelSize,
            brush,
            VisualTreeHelper.GetDpi(this).PixelsPerDip);

        double offset = right ? formatted.Width : centred ? formatted.Width / 2 : 0;
        context.DrawText(formatted, new Point(x - offset, y - (formatted.Height / 2)));
    }

    private static double SecondsPer(UsageGranularity granularity) => granularity switch
    {
        UsageGranularity.Minute => 60,
        UsageGranularity.Hour => 3600,
        _ => 86400,
    };

    private static double Snap(double value) => Math.Round(value) + 0.5;
}
