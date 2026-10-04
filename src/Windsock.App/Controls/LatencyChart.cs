using System.Globalization;
using System.Windows;
using System.Windows.Media;
using Windsock.Core.Connectivity;

namespace Windsock.App.Controls;

/// <summary>
/// How long the router and the internet took to answer over the last stretch of
/// time, with outages shaded and unanswered checks marked along the bottom.
/// </summary>
/// <remarks>
/// The line breaks where a check went unanswered and where no check was taken at
/// all, rather than drawing a slope across a gap nobody measured.
/// </remarks>
public sealed class LatencyChart : FrameworkElement
{
    private const double LabelSize = 11;
    private const double AxisRoom = 44;
    private const double FootRoom = 18;
    private const double LossRoom = 5;

    public static readonly DependencyProperty PointsProperty = Register<IReadOnlyList<LatencyPoint>?>(nameof(Points), null);
    public static readonly DependencyProperty RevisionProperty = Register(nameof(Revision), 0);
    public static readonly DependencyProperty WindowEndProperty = Register(nameof(WindowEnd), DateTimeOffset.MinValue);
    public static readonly DependencyProperty SpanProperty = Register(nameof(Span), TimeSpan.FromHours(1));
    public static readonly DependencyProperty RouterBrushProperty = Register<Brush?>(nameof(RouterBrush), null);
    public static readonly DependencyProperty InternetBrushProperty = Register<Brush?>(nameof(InternetBrush), null);
    public static readonly DependencyProperty OutageBrushProperty = Register<Brush?>(nameof(OutageBrush), null);
    public static readonly DependencyProperty GridBrushProperty = Register<Brush?>(nameof(GridBrush), null);
    public static readonly DependencyProperty AxisBrushProperty = Register<Brush?>(nameof(AxisBrush), null);
    public static readonly DependencyProperty EmptyTextProperty = Register(nameof(EmptyText), string.Empty);

    /// <summary>The checks, oldest first.</summary>
    public IReadOnlyList<LatencyPoint>? Points
    {
        get => (IReadOnlyList<LatencyPoint>?)GetValue(PointsProperty);
        set => SetValue(PointsProperty, value);
    }

    /// <summary>Changed when checks have been added to the same list.</summary>
    public int Revision
    {
        get => (int)GetValue(RevisionProperty);
        set => SetValue(RevisionProperty, value);
    }

    /// <summary>The moment at the right-hand edge.</summary>
    public DateTimeOffset WindowEnd
    {
        get => (DateTimeOffset)GetValue(WindowEndProperty);
        set => SetValue(WindowEndProperty, value);
    }

    /// <summary>How much time the width covers.</summary>
    public TimeSpan Span
    {
        get => (TimeSpan)GetValue(SpanProperty);
        set => SetValue(SpanProperty, value);
    }

    public Brush? RouterBrush
    {
        get => (Brush?)GetValue(RouterBrushProperty);
        set => SetValue(RouterBrushProperty, value);
    }

    public Brush? InternetBrush
    {
        get => (Brush?)GetValue(InternetBrushProperty);
        set => SetValue(InternetBrushProperty, value);
    }

    /// <summary>What outages and unanswered checks are marked with.</summary>
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

    /// <summary>Said in the middle while there is nothing to draw.</summary>
    public string EmptyText
    {
        get => (string)GetValue(EmptyTextProperty);
        set => SetValue(EmptyTextProperty, value);
    }

    private static DependencyProperty Register<T>(string name, T fallback) => DependencyProperty.Register(
        name,
        typeof(T),
        typeof(LatencyChart),
        new FrameworkPropertyMetadata(fallback, FrameworkPropertyMetadataOptions.AffectsRender));

    protected override void OnRender(DrawingContext drawingContext)
    {
        ArgumentNullException.ThrowIfNull(drawingContext);

        double width = ActualWidth;
        double height = ActualHeight;

        // Hit testing across the whole face, so the tooltip answers anywhere on it.
        drawingContext.DrawRectangle(Brushes.Transparent, null, new Rect(0, 0, Math.Max(0, width), Math.Max(0, height)));

        double plotLeft = AxisRoom;
        double plotWidth = width - plotLeft;
        double plotHeight = height - FootRoom - LossRoom;

        if (plotWidth < 40 || plotHeight < 30)
        {
            return;
        }

        IReadOnlyList<LatencyPoint> points = Points ?? [];
        DateTimeOffset end = WindowEnd == DateTimeOffset.MinValue ? DateTimeOffset.Now : WindowEnd;
        TimeSpan span = Span > TimeSpan.Zero ? Span : TimeSpan.FromHours(1);
        DateTimeOffset start = end - span;

        double peak = 0;

        foreach (LatencyPoint point in points)
        {
            if (point.At >= start)
            {
                peak = Math.Max(peak, Math.Max(point.RouterMs ?? 0, point.InternetMs ?? 0));
            }
        }

        double ceiling = LatencyScale.Ceiling(peak);

        double X(DateTimeOffset at) => plotLeft + ((at - start).TotalMilliseconds / span.TotalMilliseconds * plotWidth);
        double Y(double ms) => plotHeight - (Math.Clamp(ms / ceiling, 0, 1) * (plotHeight - 1)) + 0.5;

        DrawGrid(drawingContext, ceiling, plotLeft, width, plotHeight, Y);
        DrawOutages(drawingContext, points, start, end, plotHeight, X);

        TimeSpan gapLimit = TypicalGap(points) * 3;
        DrawLine(drawingContext, points, start, gapLimit, point => point.InternetMs, InternetBrush, X, Y);
        DrawLine(drawingContext, points, start, gapLimit, point => point.RouterMs, RouterBrush, X, Y);
        DrawLosses(drawingContext, points, start, plotHeight, X);
        DrawFoot(drawingContext, span, plotLeft, width, height);

        if (!points.Any(point => point.At >= start) && EmptyText.Length > 0)
        {
            FormattedText empty = Text(EmptyText, AxisBrush);
            drawingContext.DrawText(empty, new Point(plotLeft + ((plotWidth - empty.Width) / 2), (plotHeight - empty.Height) / 2));
        }
    }

    private void DrawGrid(DrawingContext context, double ceiling, double left, double right, double bottom, Func<double, double> y)
    {
        Pen? grid = GridBrush is { } brush ? new Pen(brush, 1) : null;

        for (int step = 0; step <= 2; step++)
        {
            double value = ceiling * step / 2;
            double at = Math.Round(y(value)) + 0.5;

            if (grid is not null)
            {
                context.DrawLine(grid, new Point(left, at), new Point(right, at));
            }

            FormattedText label = Text(
                step == 0 ? "0" : string.Create(CultureInfo.CurrentCulture, $"{value:0.#} ms"),
                AxisBrush);

            double top = Math.Clamp(at - (label.Height / 2), 0, Math.Max(0, bottom - label.Height));
            context.DrawText(label, new Point(left - 8 - label.Width, top));
        }
    }

    private void DrawOutages(
        DrawingContext context,
        IReadOnlyList<LatencyPoint> points,
        DateTimeOffset start,
        DateTimeOffset end,
        double bottom,
        Func<DateTimeOffset, double> x)
    {
        if (OutageBrush is not { } brush)
        {
            return;
        }

        context.PushOpacity(0.16);

        DateTimeOffset? from = null;
        DateTimeOffset last = start;

        foreach (LatencyPoint point in points)
        {
            if (point.At < start)
            {
                continue;
            }

            if (point.Offline)
            {
                from ??= point.At;
            }
            else if (from is { } began)
            {
                Band(began, point.At);
                from = null;
            }

            last = point.At;
        }

        if (from is { } open)
        {
            Band(open, last < end ? last : end);
        }

        context.Pop();

        void Band(DateTimeOffset from, DateTimeOffset until)
        {
            double left = x(from);
            double right = Math.Max(left + 2, x(until));
            context.DrawRectangle(brush, null, new Rect(left, 0, right - left, bottom));
        }
    }

    private static void DrawLine(
        DrawingContext context,
        IReadOnlyList<LatencyPoint> points,
        DateTimeOffset start,
        TimeSpan gapLimit,
        Func<LatencyPoint, double?> value,
        Brush? brush,
        Func<DateTimeOffset, double> x,
        Func<double, double> y)
    {
        if (brush is null)
        {
            return;
        }

        StreamGeometry geometry = new();
        bool any = false;

        using (StreamGeometryContext draw = geometry.Open())
        {
            bool open = false;
            DateTimeOffset previous = start;

            foreach (LatencyPoint point in points)
            {
                if (point.At < start)
                {
                    continue;
                }

                if (value(point) is not { } ms)
                {
                    open = false;
                    previous = point.At;
                    continue;
                }

                if (open && point.At - previous > gapLimit)
                {
                    open = false;
                }

                Point here = new(x(point.At), y(ms));

                if (!open)
                {
                    draw.BeginFigure(here, isFilled: false, isClosed: false);
                    // A lone reading between two gaps still shows as a dot.
                    draw.LineTo(new Point(here.X + 0.1, here.Y), isStroked: true, isSmoothJoin: true);
                    open = true;
                }
                else
                {
                    draw.LineTo(here, isStroked: true, isSmoothJoin: true);
                }

                any = true;
                previous = point.At;
            }
        }

        if (!any)
        {
            return;
        }

        geometry.Freeze();
        context.DrawGeometry(null, new Pen(brush, 1.5) { LineJoin = PenLineJoin.Round, StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round }, geometry);
    }

    private void DrawLosses(
        DrawingContext context,
        IReadOnlyList<LatencyPoint> points,
        DateTimeOffset start,
        double bottom,
        Func<DateTimeOffset, double> x)
    {
        if (OutageBrush is not { } brush)
        {
            return;
        }

        Pen mark = new(brush, 1.5);

        foreach (LatencyPoint point in points)
        {
            if (point.At < start)
            {
                continue;
            }

            bool lost = (point.RouterTried && point.RouterMs is null) || (point.InternetTried && point.InternetMs is null);

            if (lost)
            {
                double at = Math.Round(x(point.At)) + 0.5;
                context.DrawLine(mark, new Point(at, bottom + 1), new Point(at, bottom + LossRoom));
            }
        }
    }

    private void DrawFoot(DrawingContext context, TimeSpan span, double left, double right, double height)
    {
        FormattedText first = Text(Ago(span), AxisBrush);
        FormattedText middle = Text(Ago(span / 2), AxisBrush);
        FormattedText now = Text("Now", AxisBrush);
        double top = height - first.Height;

        context.DrawText(first, new Point(left, top));
        context.DrawText(now, new Point(right - now.Width, top));

        double centre = left + ((right - left) / 2) - (middle.Width / 2);

        if (centre > left + first.Width + 12 && centre + middle.Width < right - now.Width - 12)
        {
            context.DrawText(middle, new Point(centre, top));
        }
    }

    private static string Ago(TimeSpan span) => span.TotalMinutes >= 60 && span.TotalMinutes % 60 == 0
        ? string.Create(CultureInfo.CurrentCulture, $"{span.TotalHours:0} h ago")
        : string.Create(CultureInfo.CurrentCulture, $"{span.TotalMinutes:0} min ago");

    private static TimeSpan TypicalGap(IReadOnlyList<LatencyPoint> points)
    {
        TimeSpan shortest = TimeSpan.MaxValue;

        for (int index = Math.Max(1, points.Count - 20); index < points.Count; index++)
        {
            TimeSpan gap = points[index].At - points[index - 1].At;

            if (gap > TimeSpan.Zero && gap < shortest)
            {
                shortest = gap;
            }
        }

        return shortest == TimeSpan.MaxValue
            ? TimeSpan.FromSeconds(10)
            : TimeSpan.FromTicks(Math.Max(shortest.Ticks, TimeSpan.FromSeconds(2).Ticks));
    }

    private FormattedText Text(string text, Brush? brush) => new(
        text,
        CultureInfo.CurrentCulture,
        FlowDirection.LeftToRight,
        new Typeface(new FontFamily("Segoe UI Variable Text, Segoe UI"), FontStyles.Normal, FontWeights.Normal, FontStretches.Normal),
        LabelSize,
        brush ?? Brushes.Gray,
        VisualTreeHelper.GetDpi(this).PixelsPerDip);
}
