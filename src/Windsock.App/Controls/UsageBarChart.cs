using System.Globalization;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using Windsock.Core.Formatting;
using Windsock.Core.History;
using System.Windows.Automation.Peers;

namespace Windsock.App.Controls;

/// <summary>What the pointer is over, if anything.</summary>
public sealed class UsageHoverEventArgs(bool hasValue, Point position, UsageBucket bucket) : EventArgs
{
    public bool HasValue { get; } = hasValue;

    public Point Position { get; } = position;

    public UsageBucket Bucket { get; } = bucket;
}

/// <summary>
/// Draws recorded usage as one bar per bucket, upload stacked on download, and
/// what Windows counted while Windsock was closed stacked above that, fainter.
/// </summary>
/// <remarks>
/// It takes the keyboard as it takes the pointer. With focus, the arrow keys
/// move from bar to bar (Home and End to the ends, Page Up and Page Down a
/// dozen at a time), the bar chosen is pointed at as a hover would point at it
/// - its popover, its row lit - and is said in <see cref="SelectedDetail"/>,
/// which the page shows under the chart and the chart gives a screen reader as
/// its help text; Enter or Space goes to the bar's row, as a click does. The
/// view pans to keep the chosen bar on screen when it is zoomed.
/// </remarks>
public sealed class UsageBarChart : FrameworkElement
{
    private const int PageStep = 12;
    private const double LeftGutter = 58;
    private const double BottomGutter = 20;
    private const double TopPadding = 10;
    private const double LabelSize = 10.5;
    private const double MinimumBarWidth = 1.0;
    private const double ZoomPerNotch = 1.35;
    private const double MinimumBucketsVisible = 2;

    // Windows' count is drawn in the same colours, fainter: the same kind of
    // figure, measured by somebody else.
    private const double EstimateOpacity = 0.4;
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

    private static readonly DependencyPropertyKey SelectedDetailKey = DependencyProperty.RegisterReadOnly(
        nameof(SelectedDetail),
        typeof(string),
        typeof(UsageBarChart),
        new PropertyMetadata(string.Empty));

    public static readonly DependencyProperty SelectedDetailProperty = SelectedDetailKey.DependencyProperty;

    // The bar chosen with the keyboard, by when it starts, so a reload of the
    // same period keeps it; null for none.
    private DateTimeOffset? _chosen;

    public UsageBarChart() => Focusable = true;

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

    /// <summary>
    /// The bar chosen with the keyboard in a sentence, for the line under the
    /// chart and a screen reader; empty while the keyboard is elsewhere.
    /// </summary>
    public string SelectedDetail => (string)GetValue(SelectedDetailProperty);

    /// <summary>Whether the time axis is showing less than the whole period.</summary>
    public bool IsZoomed => _span < 0.999;

    /// <summary>Where the view starts, as a fraction of the period, and how much of it shows.</summary>
    public (double Offset, double Span) View => (_offset, _span);

    /// <summary>Raised when the view is zoomed, panned or reset, so a chart beside it can follow.</summary>
    public event EventHandler? ViewChanged;

    /// <summary>Raised when a bar is clicked, rather than the view dragged.</summary>
    public event EventHandler<UsageHoverEventArgs>? BucketClicked;

    private Point _pressedAt;

    /// <summary>Returns the whole period to view.</summary>
    public void ResetZoom()
    {
        _span = 1;
        _offset = 0;
        InvalidateVisual();
        ViewChanged?.Invoke(this, EventArgs.Empty);
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
            chart.ViewChanged?.Invoke(chart, EventArgs.Empty);
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
        ViewChanged?.Invoke(this, EventArgs.Empty);
        Track(e.GetPosition(this));
        e.Handled = true;
    }

    protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonDown(e);
        ArgumentNullException.ThrowIfNull(e);

        _pressedAt = e.GetPosition(this);

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
        ArgumentNullException.ThrowIfNull(e);
        EndDrag();

        // A press that barely moved is a click on the bar under it.
        Point at = e.GetPosition(this);

        if (Math.Abs(at.X - _pressedAt.X) < SystemParameters.MinimumHorizontalDragDistance
            && Math.Abs(at.Y - _pressedAt.Y) < SystemParameters.MinimumVerticalDragDistance
            && Find(at) is { } bucket)
        {
            BucketClicked?.Invoke(this, new UsageHoverEventArgs(true, at, bucket));
        }
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
            ViewChanged?.Invoke(this, EventArgs.Empty);
            return;
        }

        Track(at);
    }

    protected override void OnMouseLeave(MouseEventArgs e)
    {
        base.OnMouseLeave(e);
        EndDrag();

        // The pointer gone, the bar the keyboard is on is pointed at again.
        if (IsKeyboardFocused && ChosenIndex() is var chosen and >= 0)
        {
            Choose(chosen);
            return;
        }

        Clear();
    }

    protected override void OnPropertyChanged(DependencyPropertyChangedEventArgs e)
    {
        base.OnPropertyChanged(e);

        // New bars - a reload, another period - may no longer hold the one chosen.
        if (e.Property == BucketsProperty || e.Property == RevisionProperty || e.Property == GranularityProperty)
        {
            if (ChosenIndex() < 0)
            {
                _chosen = null;
            }

            Describe();
        }
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        ArgumentNullException.ThrowIfNull(e);

        IReadOnlyList<UsageBucket> buckets = Buckets;

        if (e.Handled || buckets.Count == 0)
        {
            return;
        }

        int index = ChosenIndex();
        int last = buckets.Count - 1;

        if (e.Key is Key.Enter or Key.Space)
        {
            if (index >= 0)
            {
                BucketClicked?.Invoke(this, new UsageHoverEventArgs(true, PointOf(buckets[index]), buckets[index]));
                e.Handled = true;
            }

            return;
        }

        int moved = e.Key switch
        {
            Key.Left => index < 0 ? last : Math.Max(0, index - 1),
            Key.Right => index < 0 ? last : Math.Min(last, index + 1),
            Key.PageUp => Math.Max(0, (index < 0 ? last : index) - PageStep),
            Key.PageDown => Math.Min(last, (index < 0 ? last : index) + PageStep),
            Key.Home => 0,
            Key.End => last,
            _ => -1,
        };

        if (moved < 0)
        {
            return;
        }

        Choose(moved);
        e.Handled = true;
    }

    protected override void OnGotKeyboardFocus(KeyboardFocusChangedEventArgs e)
    {
        base.OnGotKeyboardFocus(e);

        // Arriving, the keyboard starts on the bar it left, or the newest.
        int index = ChosenIndex();

        if (index < 0 && Buckets.Count > 0)
        {
            index = Buckets.Count - 1;
        }

        if (index >= 0)
        {
            Choose(index);
        }
        else
        {
            Describe();
        }
    }

    protected override void OnLostKeyboardFocus(KeyboardFocusChangedEventArgs e)
    {
        base.OnLostKeyboardFocus(e);
        Describe();

        if (!IsMouseOver)
        {
            Clear();
        }
    }

    /// <summary>Where the bar chosen with the keyboard is in the bars, or -1.</summary>
    private int ChosenIndex()
    {
        if (_chosen is not { } chosen)
        {
            return -1;
        }

        IReadOnlyList<UsageBucket> buckets = Buckets;

        for (int i = 0; i < buckets.Count; i++)
        {
            if (buckets[i].Start == chosen)
            {
                return i;
            }
        }

        return -1;
    }

    /// <summary>Chooses a bar from the keyboard: brought into view, said, and pointed at as a hover would.</summary>
    private void Choose(int index)
    {
        UsageBucket bucket = Buckets[index];
        _chosen = bucket.Start;
        KeepInView(bucket);
        Describe();
        HoverChanged?.Invoke(this, new UsageHoverEventArgs(true, PointOf(bucket), bucket));
    }

    /// <summary>Pans a zoomed view just far enough to show a bar whole.</summary>
    private void KeepInView(UsageBucket bucket)
    {
        double total = (RangeEnd - RangeStart).TotalSeconds;

        if (total <= 0 || !IsZoomed)
        {
            return;
        }

        double start = (bucket.Start - RangeStart).TotalSeconds / total;
        double end = start + (SecondsOf(bucket, Granularity) / total);
        double offset = _offset;

        if (start < offset)
        {
            offset = start;
        }
        else if (end > offset + _span)
        {
            offset = end - _span;
        }

        offset = Math.Clamp(offset, 0, 1 - _span);

        if (Math.Abs(offset - _offset) < 0.000001)
        {
            return;
        }

        _offset = offset;
        InvalidateVisual();
        ViewChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>The top middle of a bar, where a hover over it would point.</summary>
    private Point PointOf(UsageBucket bucket)
    {
        double width = Math.Max(1, ActualWidth - LeftGutter - 8);
        double height = Math.Max(1, ActualHeight - TopPadding - BottomGutter);
        double total = (RangeEnd - RangeStart).TotalSeconds;
        double visible = Math.Max(1, total * _span);
        double offset = (bucket.Start - RangeStart).TotalSeconds - (total * _offset);
        double x = LeftGutter + (width * (offset + (SecondsOf(bucket, Granularity) / 2)) / visible);
        long ceiling = Ceiling(Buckets, Granularity);
        double bar = ceiling <= 0 ? 0 : height * DrawnBytes(bucket, Granularity) / ceiling;

        return new Point(
            Math.Clamp(x, LeftGutter, LeftGutter + width),
            Math.Clamp(TopPadding + height - bar, TopPadding, TopPadding + height));
    }

    /// <summary>Says the bar chosen with the keyboard, while the keyboard is here.</summary>
    private void Describe()
    {
        int index = IsKeyboardFocused ? ChosenIndex() : -1;
        SetValue(SelectedDetailKey, index < 0 ? string.Empty : Detail(Buckets[index], index, Buckets.Count));
    }

    /// <summary>A bar in a sentence: when, what moved each way, Windows' part, and where it is among the bars.</summary>
    private string Detail(UsageBucket bucket, int index, int count)
    {
        DateTimeOffset end = bucket.Start + TimeSpan.FromSeconds(SecondsOf(bucket, Granularity));
        string when = Granularity == UsageGranularity.Day
            ? bucket.Start.LocalDateTime.ToString("ddd d MMM", ClockFormat.Current.Format)
            : string.Concat(ClockFormat.Day(bucket.Start.LocalDateTime), " to ", ClockFormat.Time(end.LocalDateTime));

        string windows = bucket.HasEstimate
            ? string.Concat(" Windows counted ", SizeFormatter.Format(bucket.EstimatedDown + bucket.EstimatedUp), " of it while Windsock was closed.")
            : string.Empty;

        return string.Concat(
            when,
            ": ",
            SizeFormatter.Format(bucket.AllDown),
            " down, ",
            SizeFormatter.Format(bucket.AllUp),
            " up.",
            windows,
            string.Create(CultureInfo.CurrentCulture, $" Bar {index + 1} of {count}. Enter goes to its row."));
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
        if (Find(at) is { } bucket)
        {
            HoverChanged?.Invoke(this, new UsageHoverEventArgs(true, at, bucket));
        }
        else
        {
            Clear();
        }
    }

    /// <summary>The bucket under a point, if any.</summary>
    private UsageBucket? Find(Point at)
    {
        IReadOnlyList<UsageBucket> buckets = Buckets;
        double total = (RangeEnd - RangeStart).TotalSeconds;

        if (buckets.Count == 0 || total <= 0 || at.X < LeftGutter || at.Y > ActualHeight - BottomGutter)
        {
            return null;
        }
        double seconds = (_offset + (Fraction(at.X) * _span)) * total;
        int found = -1;

        for (int i = 0; i < buckets.Count; i++)
        {
            double start = (buckets[i].Start - RangeStart).TotalSeconds;
            double width = SecondsOf(buckets[i], Granularity);

            if (seconds >= start - (width / 2) && seconds < start + width + (width / 2))
            {
                found = i;

                if (seconds >= start && seconds < start + width)
                {
                    break;
                }
            }
        }

        return found < 0 ? null : buckets[found];
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

    /// <summary>
    /// A screen reader hears that this is a chart, its name, and the bar the
    /// keyboard is on - or, with none, what the period came to.
    /// </summary>
    protected override AutomationPeer OnCreateAutomationPeer() =>
        new ChartAutomationPeer(this, () => SelectedDetail.Length > 0 ? SelectedDetail : ChartWords.Usage(Buckets));

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
        long ceiling = Ceiling(buckets, Granularity);

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

    private static long Ceiling(IReadOnlyList<UsageBucket> buckets, UsageGranularity granularity)
    {
        long tallest = 0;

        foreach (UsageBucket bucket in buckets)
        {
            if (DrawnBytes(bucket, granularity) > tallest)
            {
                tallest = DrawnBytes(bucket, granularity);
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

        Brush? download = DownloadBrush;
        Brush? upload = UploadBrush;
        DateTimeOffset? lit = HighlightedStart;
        double bottom = top + height;

        for (int i = 0; i < buckets.Count; i++)
        {
            UsageBucket bucket = buckets[i];
            double bucketSeconds = SecondsOf(bucket, Granularity);
            double slot = width * bucketSeconds / visible;
            double barWidth = Math.Max(MinimumBarWidth, slot > 4 ? slot - 1 : slot);
            double offset = (bucket.Start - RangeStart).TotalSeconds - from;

            if (offset < -bucketSeconds || offset > visible)
            {
                continue;
            }

            // A stretch Windows counts only by the hour spreads its bytes over
            // the whole stretch, drawn at the height of its average minute, so
            // it neither towers over the minutes beside it nor hides among them.
            double spread = Spread(bucket, Granularity);
            double x = left + (width * offset / visible);
            double downHeight = height * bucket.BytesDown / ceiling;
            double upHeight = height * bucket.BytesUp / ceiling;
            double estimatedDownHeight = height * bucket.EstimatedDown / spread / ceiling;
            double estimatedUpHeight = height * bucket.EstimatedUp / spread / ceiling;
            double stacked = downHeight + upHeight;

            if (stacked > 0 && stacked < 1)
            {
                downHeight = bucket.BytesDown > 0 ? 1 : 0;
                upHeight = bucket.BytesUp > 0 ? 1 : 0;
            }

            if (bucket.HasEstimate && estimatedDownHeight + estimatedUpHeight is > 0 and < 1)
            {
                estimatedDownHeight = bucket.EstimatedDown > 0 ? 1 : 0;
                estimatedUpHeight = bucket.EstimatedUp > 0 ? 1 : 0;
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

            if (!bucket.HasEstimate)
            {
                continue;
            }

            double top0 = bottom - downHeight - upHeight;
            context.PushOpacity(EstimateOpacity);

            if (download is not null && estimatedDownHeight > 0)
            {
                context.DrawRectangle(
                    download,
                    null,
                    new Rect(x, top0 - estimatedDownHeight, barWidth, estimatedDownHeight));
            }

            if (upload is not null && estimatedUpHeight > 0)
            {
                context.DrawRectangle(
                    upload,
                    null,
                    new Rect(x, top0 - estimatedDownHeight - estimatedUpHeight, barWidth, estimatedUpHeight));
            }

            context.Pop();
        }
    }

    /// <summary>
    /// How many of the chart's buckets a bucket's estimate is spread across:
    /// one, except by the minute, where a stretch Windows counted by the hour
    /// covers many.
    /// </summary>
    private static double Spread(UsageBucket bucket, UsageGranularity granularity) =>
        granularity == UsageGranularity.Minute && bucket.Minutes == 0 && bucket.EstimatedMinutes > 1
            ? bucket.EstimatedMinutes
            : 1;

    /// <summary>The height a bucket is drawn to, in bytes.</summary>
    private static long DrawnBytes(UsageBucket bucket, UsageGranularity granularity) =>
        bucket.BytesTotal + (long)((bucket.EstimatedDown + bucket.EstimatedUp) / Spread(bucket, granularity));

    /// <summary>How much time a bucket spans on the axis.</summary>
    private static double SecondsOf(UsageBucket bucket, UsageGranularity granularity) =>
        SecondsPer(granularity) * Spread(bucket, granularity);

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
        string pattern = visible > TimeSpan.FromDays(2) ? "d MMM" : ClockFormat.Current.ShortTime;
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
