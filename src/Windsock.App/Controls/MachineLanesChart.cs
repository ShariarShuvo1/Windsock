using System.Globalization;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using Windsock.Core.History;

namespace Windsock.App.Controls;

/// <summary>A stretch the internet was unreachable.</summary>
public readonly record struct OutageSpan(DateTimeOffset Start, DateTimeOffset End);

/// <summary>Which bucket of the lanes the pointer is over, if any.</summary>
public sealed class LaneHoverEventArgs(bool hasValue, Point position, DateTimeOffset start) : EventArgs
{
    public bool HasValue { get; } = hasValue;

    public Point Position { get; } = position;

    public DateTimeOffset Start { get; } = start;
}

/// <summary>
/// The machine and the connection over a period, one lane each, on the same
/// time axis as the usage chart above them.
/// </summary>
/// <remarks>
/// Each lane draws its bucket's average as a bar and its peak as a fainter bar
/// behind it. Percentages are drawn against a whole hundred, so a quiet
/// machine looks quiet; the disk and the response time against the busiest
/// bucket in the period, since neither has a natural top. Outages are laid
/// across the connection lane where they happened.
/// </remarks>
public sealed class MachineLanesChart : FrameworkElement
{
    // The usage chart's gutter, so a moment is at the same x in both.
    private const double LeftGutter = 58;
    private const double RightPadding = 8;
    private const double LaneHeight = 30;
    private const double LaneGap = 8;
    private const double TopPadding = 6;
    private const double BottomPadding = 4;
    private const double LabelSize = 10.5;
    private const double PeakOpacity = 0.3;

    private static readonly string[] Names = ["Processor", "Graphics", "Memory", "Disk", "Ping"];

    private double _offset;
    private double _span = 1;

    public static readonly DependencyProperty SystemBucketsProperty = Register<IReadOnlyList<SystemBucket>>(nameof(SystemBuckets), []);
    public static readonly DependencyProperty ConnectionBucketsProperty = Register<IReadOnlyList<ConnectionBucket>>(nameof(ConnectionBuckets), []);
    public static readonly DependencyProperty OutagesProperty = Register<IReadOnlyList<OutageSpan>>(nameof(Outages), []);
    public static readonly DependencyProperty RevisionProperty = Register(nameof(Revision), 0);
    public static readonly DependencyProperty RangeStartProperty = Register(nameof(RangeStart), default(DateTimeOffset));
    public static readonly DependencyProperty RangeEndProperty = Register(nameof(RangeEnd), default(DateTimeOffset));
    public static readonly DependencyProperty GranularityProperty = Register(nameof(Granularity), UsageGranularity.Hour);
    public static readonly DependencyProperty HighlightedStartProperty = Register<DateTimeOffset?>(nameof(HighlightedStart), null);
    public static readonly DependencyProperty ProcessorBrushProperty = Register<Brush?>(nameof(ProcessorBrush), null);
    public static readonly DependencyProperty GraphicsBrushProperty = Register<Brush?>(nameof(GraphicsBrush), null);
    public static readonly DependencyProperty MemoryBrushProperty = Register<Brush?>(nameof(MemoryBrush), null);
    public static readonly DependencyProperty StorageBrushProperty = Register<Brush?>(nameof(StorageBrush), null);
    public static readonly DependencyProperty ConnectionBrushProperty = Register<Brush?>(nameof(ConnectionBrush), null);
    public static readonly DependencyProperty OutageBrushProperty = Register<Brush?>(nameof(OutageBrush), null);
    public static readonly DependencyProperty GridBrushProperty = Register<Brush?>(nameof(GridBrush), null);
    public static readonly DependencyProperty AxisBrushProperty = Register<Brush?>(nameof(AxisBrush), null);
    public static readonly DependencyProperty HighlightBrushProperty = Register<Brush?>(nameof(HighlightBrush), null);

    public MachineLanesChart()
    {
        Height = TopPadding + (Names.Length * LaneHeight) + ((Names.Length - 1) * LaneGap) + BottomPadding;
    }

    /// <summary>Raised as the pointer moves over the lanes.</summary>
    public event EventHandler<LaneHoverEventArgs>? HoverChanged;

    public IReadOnlyList<SystemBucket> SystemBuckets
    {
        get => (IReadOnlyList<SystemBucket>)GetValue(SystemBucketsProperty);
        set => SetValue(SystemBucketsProperty, value);
    }

    public IReadOnlyList<ConnectionBucket> ConnectionBuckets
    {
        get => (IReadOnlyList<ConnectionBucket>)GetValue(ConnectionBucketsProperty);
        set => SetValue(ConnectionBucketsProperty, value);
    }

    public IReadOnlyList<OutageSpan> Outages
    {
        get => (IReadOnlyList<OutageSpan>)GetValue(OutagesProperty);
        set => SetValue(OutagesProperty, value);
    }

    /// <summary>Bumped by the owner to force a redraw.</summary>
    public int Revision
    {
        get => (int)GetValue(RevisionProperty);
        set => SetValue(RevisionProperty, value);
    }

    public DateTimeOffset RangeStart
    {
        get => (DateTimeOffset)GetValue(RangeStartProperty);
        set => SetValue(RangeStartProperty, value);
    }

    public DateTimeOffset RangeEnd
    {
        get => (DateTimeOffset)GetValue(RangeEndProperty);
        set => SetValue(RangeEndProperty, value);
    }

    public UsageGranularity Granularity
    {
        get => (UsageGranularity)GetValue(GranularityProperty);
        set => SetValue(GranularityProperty, value);
    }

    public DateTimeOffset? HighlightedStart
    {
        get => (DateTimeOffset?)GetValue(HighlightedStartProperty);
        set => SetValue(HighlightedStartProperty, value);
    }

    public Brush? ProcessorBrush
    {
        get => (Brush?)GetValue(ProcessorBrushProperty);
        set => SetValue(ProcessorBrushProperty, value);
    }

    public Brush? GraphicsBrush
    {
        get => (Brush?)GetValue(GraphicsBrushProperty);
        set => SetValue(GraphicsBrushProperty, value);
    }

    public Brush? MemoryBrush
    {
        get => (Brush?)GetValue(MemoryBrushProperty);
        set => SetValue(MemoryBrushProperty, value);
    }

    public Brush? StorageBrush
    {
        get => (Brush?)GetValue(StorageBrushProperty);
        set => SetValue(StorageBrushProperty, value);
    }

    public Brush? ConnectionBrush
    {
        get => (Brush?)GetValue(ConnectionBrushProperty);
        set => SetValue(ConnectionBrushProperty, value);
    }

    public Brush? OutageBrush
    {
        get => (Brush?)GetValue(OutageBrushProperty);
        set => SetValue(OutageBrushProperty, value);
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

    public Brush? HighlightBrush
    {
        get => (Brush?)GetValue(HighlightBrushProperty);
        set => SetValue(HighlightBrushProperty, value);
    }

    /// <summary>Shows the same part of the period as the usage chart does.</summary>
    public void Follow((double Offset, double Span) view)
    {
        _offset = Math.Clamp(view.Offset, 0, 1);
        _span = Math.Clamp(view.Span, 0.000001, 1);
        InvalidateVisual();
    }

    private static DependencyProperty Register<T>(string name, T fallback) =>
        DependencyProperty.Register(
            name,
            typeof(T),
            typeof(MachineLanesChart),
            new FrameworkPropertyMetadata(fallback, FrameworkPropertyMetadataOptions.AffectsRender));

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        ArgumentNullException.ThrowIfNull(e);

        Point at = e.GetPosition(this);
        double total = (RangeEnd - RangeStart).TotalSeconds;
        double width = ActualWidth - LeftGutter - RightPadding;

        if (total <= 0 || width <= 0 || at.X < LeftGutter)
        {
            Clear();
            return;
        }

        double seconds = (_offset + (Math.Clamp((at.X - LeftGutter) / width, 0, 1) * _span)) * total;
        DateTimeOffset moment = RangeStart + TimeSpan.FromSeconds(seconds);
        double bucketSeconds = SecondsPer(Granularity);

        foreach (DateTimeOffset start in Starts())
        {
            if (moment >= start && moment < start + TimeSpan.FromSeconds(bucketSeconds))
            {
                HoverChanged?.Invoke(this, new LaneHoverEventArgs(true, at, start));
                return;
            }
        }

        Clear();
    }

    protected override void OnMouseLeave(MouseEventArgs e)
    {
        base.OnMouseLeave(e);
        Clear();
    }

    private void Clear() => HoverChanged?.Invoke(this, new LaneHoverEventArgs(false, default, default));

    private IEnumerable<DateTimeOffset> Starts()
    {
        foreach (SystemBucket bucket in SystemBuckets)
        {
            yield return bucket.Start;
        }

        foreach (ConnectionBucket bucket in ConnectionBuckets)
        {
            yield return bucket.Start;
        }
    }

    protected override void OnRender(DrawingContext drawingContext)
    {
        ArgumentNullException.ThrowIfNull(drawingContext);

        double width = ActualWidth;
        double plotWidth = width - LeftGutter - RightPadding;
        double total = (RangeEnd - RangeStart).TotalSeconds;

        drawingContext.DrawRectangle(Brushes.Transparent, null, new Rect(0, 0, width, ActualHeight));

        if (plotWidth <= 0 || total <= 0)
        {
            return;
        }

        Pen? grid = GridBrush is { } gridBrush ? new Pen(gridBrush, 1) : null;
        grid?.Freeze();

        for (int lane = 0; lane < Names.Length; lane++)
        {
            double top = Top(lane);
            DrawLabel(drawingContext, Names[lane], LeftGutter - 8, top + (LaneHeight / 2));

            if (grid is not null)
            {
                double y = Math.Round(top + LaneHeight) + 0.5;
                drawingContext.DrawLine(grid, new Point(LeftGutter, y), new Point(LeftGutter + plotWidth, y));
            }
        }

        drawingContext.PushClip(new RectangleGeometry(new Rect(LeftGutter, 0, plotWidth, ActualHeight)));

        double visible = total * _span;
        double from = total * _offset;
        double bucketSeconds = SecondsPer(Granularity);
        double slot = plotWidth * bucketSeconds / visible;
        double barWidth = Math.Max(1, slot > 4 ? slot - 1 : slot);

        double X(DateTimeOffset start) => LeftGutter + (plotWidth * ((start - RangeStart).TotalSeconds - from) / visible);
        bool Shown(DateTimeOffset start)
        {
            double offset = (start - RangeStart).TotalSeconds - from;
            return offset >= -bucketSeconds && offset <= visible;
        }

        IReadOnlyList<SystemBucket> system = SystemBuckets;
        IReadOnlyList<ConnectionBucket> connection = ConnectionBuckets;
        DateTimeOffset? lit = HighlightedStart;

        if (lit is { } highlighted && HighlightBrush is { } highlight && Shown(highlighted))
        {
            drawingContext.DrawRectangle(highlight, null, new Rect(X(highlighted), 0, barWidth, ActualHeight));
        }

        double disks = 1;

        foreach (SystemBucket bucket in system)
        {
            disks = Math.Max(disks, bucket.DiskRead + bucket.DiskWrite);
        }

        foreach (SystemBucket bucket in system)
        {
            if (!Shown(bucket.Start))
            {
                continue;
            }

            double x = X(bucket.Start);
            double memory = bucket.MemoryTotal > 0 ? 100.0 * bucket.MemoryAverage / bucket.MemoryTotal : 0;
            double memoryPeak = bucket.MemoryTotal > 0 ? 100.0 * bucket.MemoryPeak / bucket.MemoryTotal : 0;

            Bar(drawingContext, ProcessorBrush, 0, x, barWidth, bucket.ProcessorAverage / 100, bucket.ProcessorPeak / 100);
            Bar(drawingContext, GraphicsBrush, 1, x, barWidth, bucket.GraphicsAverage / 100, bucket.GraphicsPeak / 100);
            Bar(drawingContext, MemoryBrush, 2, x, barWidth, memory / 100, memoryPeak / 100);
            Bar(drawingContext, StorageBrush, 3, x, barWidth, (bucket.DiskRead + bucket.DiskWrite) / disks, null);
        }

        double slowest = 1;

        foreach (ConnectionBucket bucket in connection)
        {
            slowest = Math.Max(slowest, bucket.InternetAverageMs ?? bucket.RouterAverageMs ?? 0);
        }

        foreach (ConnectionBucket bucket in connection)
        {
            if (!Shown(bucket.Start))
            {
                continue;
            }

            double x = X(bucket.Start);
            double? ms = bucket.InternetAverageMs ?? bucket.RouterAverageMs;

            if (ms is { } answered)
            {
                Bar(drawingContext, ConnectionBrush, 4, x, barWidth, answered / slowest, null);
            }

            // A bucket with checks that found the internet gone is marked
            // across its lane in the outage colour, as much of it as was out.
            if (bucket.Offline > 0 && bucket.Checks > 0 && OutageBrush is { } outage)
            {
                double share = (double)bucket.Offline / bucket.Checks;
                drawingContext.PushOpacity(0.35 + (0.5 * share));
                drawingContext.DrawRectangle(outage, null, new Rect(x, Top(4), barWidth, LaneHeight));
                drawingContext.Pop();
            }
        }

        if (OutageBrush is { } band)
        {
            foreach (OutageSpan span in Outages)
            {
                double left = X(span.Start);
                double right = X(span.End);

                if (right < LeftGutter || left > LeftGutter + plotWidth)
                {
                    continue;
                }

                drawingContext.PushOpacity(0.5);
                drawingContext.DrawRectangle(band, null, new Rect(left, Top(4), Math.Max(2, right - left), 3));
                drawingContext.Pop();
            }
        }

        drawingContext.Pop();
    }

    private static double Top(int lane) => TopPadding + (lane * (LaneHeight + LaneGap));

    private static void Bar(DrawingContext context, Brush? brush, int lane, double x, double width, double average, double? peak)
    {
        if (brush is null)
        {
            return;
        }

        double bottom = Top(lane) + LaneHeight;

        if (peak is { } highest && highest > 0)
        {
            double tall = LaneHeight * Math.Clamp(highest, 0, 1);
            context.PushOpacity(PeakOpacity);
            context.DrawRectangle(brush, null, new Rect(x, bottom - tall, width, tall));
            context.Pop();
        }

        double height = LaneHeight * Math.Clamp(average, 0, 1);

        if (average > 0 && height < 1)
        {
            height = 1;
        }

        if (height > 0)
        {
            context.DrawRectangle(brush, null, new Rect(x, bottom - height, width, height));
        }
    }

    private void DrawLabel(DrawingContext context, string text, double right, double middle)
    {
        if (AxisBrush is not { } brush)
        {
            return;
        }

        FormattedText formatted = new(
            text,
            CultureInfo.CurrentCulture,
            FlowDirection.LeftToRight,
            new Typeface(new FontFamily("Segoe UI Variable Text, Segoe UI"), FontStyles.Normal, FontWeights.Normal, FontStretches.Normal),
            LabelSize,
            brush,
            VisualTreeHelper.GetDpi(this).PixelsPerDip);

        context.DrawText(formatted, new Point(right - formatted.Width, middle - (formatted.Height / 2)));
    }

    private static double SecondsPer(UsageGranularity granularity) => granularity switch
    {
        UsageGranularity.Minute => 60,
        UsageGranularity.Hour => 3600,
        _ => 86400,
    };
}
