using System.Globalization;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using Windsock.Core.Formatting;

namespace Windsock.App.Controls;

/// <summary>
/// The recent past of one reading, drawn large: a share of something from
/// nothing to a hundred percent, one reading a second, with the newest at the
/// right.
/// </summary>
/// <remarks>
/// Drawn in the same hand as the throughput chart beside it - the same axes,
/// the same smoothed line over a faint fill, the same guide and marker under
/// the pointer - so the five charts in the System section read as one chart
/// showing five things rather than five different drawings. Unlike that chart
/// its scale never moves: a share has a top, and a line that rescaled itself
/// would make ten percent look like a machine at full stretch.
/// </remarks>
public sealed class ShareChart : FrameworkElement
{
    private const double AxisFontSize = 10;
    private const double ValueAxisWidth = 40;
    private const double TimeAxisHeight = 18;
    private const double PixelsPerPoint = 2.5;

    private static readonly double[] TickCandidates = [10, 15, 30, 60, 120, 300];

    public static readonly DependencyProperty ValuesProperty = Register<IReadOnlyList<double>?>(nameof(Values), null);

    public static readonly DependencyProperty RevisionProperty = Register(nameof(Revision), 0);

    public static readonly DependencyProperty SampleSecondsProperty = Register(nameof(SampleSeconds), 1d);

    public static readonly DependencyProperty StrokeProperty = Register<Brush?>(nameof(Stroke), null);

    public static readonly DependencyProperty GridBrushProperty = Register<Brush?>(nameof(GridBrush), null);

    public static readonly DependencyProperty AxisBrushProperty = Register<Brush?>(nameof(AxisBrush), null);

    public static readonly DependencyProperty SurfaceBrushProperty = Register<Brush?>(nameof(SurfaceBrush), null);

    public static readonly DependencyProperty LabelBrushProperty = Register<Brush?>(nameof(LabelBrush), null);

    public static readonly DependencyProperty PlateBrushProperty = Register<Brush?>(nameof(PlateBrush), null);

    private readonly Typeface _typeface = new("Segoe UI");
    private Point[] _points = [];
    private double[] _buckets = [];
    private int _bucketCount;
    private int _first = -1;
    private int _hovered = -1;

    /// <summary>The readings, oldest first, each from nothing to a hundred.</summary>
    public IReadOnlyList<double>? Values
    {
        get => (IReadOnlyList<double>?)GetValue(ValuesProperty);
        set => SetValue(ValuesProperty, value);
    }

    /// <summary>Changed whenever a reading has been added, so the chart redraws.</summary>
    public int Revision
    {
        get => (int)GetValue(RevisionProperty);
        set => SetValue(RevisionProperty, value);
    }

    /// <summary>How far apart the readings are, for the time axis.</summary>
    public double SampleSeconds
    {
        get => (double)GetValue(SampleSecondsProperty);
        set => SetValue(SampleSecondsProperty, value);
    }

    /// <summary>The line, and the fill under it at a fraction of its strength.</summary>
    public Brush? Stroke
    {
        get => (Brush?)GetValue(StrokeProperty);
        set => SetValue(StrokeProperty, value);
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

    /// <summary>The ground the chart is drawn on, which rings the marker.</summary>
    public Brush? SurfaceBrush
    {
        get => (Brush?)GetValue(SurfaceBrushProperty);
        set => SetValue(SurfaceBrushProperty, value);
    }

    /// <summary>The ground of the label beside the marker under the pointer.</summary>
    public Brush? PlateBrush
    {
        get => (Brush?)GetValue(PlateBrushProperty);
        set => SetValue(PlateBrushProperty, value);
    }

    /// <summary>The figure written beside the marker under the pointer.</summary>
    public Brush? LabelBrush
    {
        get => (Brush?)GetValue(LabelBrushProperty);
        set => SetValue(LabelBrushProperty, value);
    }

    protected override void OnRender(DrawingContext drawingContext)
    {
        ArgumentNullException.ThrowIfNull(drawingContext);

        double width = ActualWidth;
        double height = ActualHeight;

        if (width <= 0 || height <= 0)
        {
            return;
        }

        // Hit-testable everywhere, so the pointer is followed across the gaps.
        drawingContext.DrawRectangle(Brushes.Transparent, null, new Rect(0, 0, width, height));

        double plotWidth = width - ValueAxisWidth;
        double plotHeight = height - TimeAxisHeight;

        if (plotWidth <= 8 || plotHeight <= 8)
        {
            return;
        }

        DrawValueAxis(drawingContext, plotHeight);
        DrawTimeAxis(drawingContext, plotWidth, plotHeight);

        if (!Resample(plotWidth, plotHeight) || Stroke is not SolidColorBrush stroke)
        {
            return;
        }

        StreamGeometry line = Build(plotHeight, closed: false);
        StreamGeometry area = Build(plotHeight, closed: true);

        var fill = new SolidColorBrush(stroke.Color) { Opacity = 0.14 };
        fill.Freeze();

        var pen = new Pen(stroke, 2)
        {
            LineJoin = PenLineJoin.Round,
            StartLineCap = PenLineCap.Round,
            EndLineCap = PenLineCap.Round,
        };
        pen.Freeze();

        drawingContext.DrawGeometry(fill, null, area);
        drawingContext.DrawGeometry(null, pen, line);

        DrawHover(drawingContext, stroke, plotHeight);
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        ArgumentNullException.ThrowIfNull(e);
        base.OnMouseMove(e);

        double plotWidth = ActualWidth - ValueAxisWidth;

        if (_bucketCount < 2 || plotWidth <= 0)
        {
            return;
        }

        double step = plotWidth / (_bucketCount - 1);
        int bucket = Math.Clamp((int)Math.Round((e.GetPosition(this).X - ValueAxisWidth) / step), 0, _bucketCount - 1);

        if (bucket != _hovered)
        {
            _hovered = bucket;
            InvalidateVisual();
        }
    }

    protected override void OnMouseLeave(MouseEventArgs e)
    {
        base.OnMouseLeave(e);

        if (_hovered >= 0)
        {
            _hovered = -1;
            InvalidateVisual();
        }
    }

    /// <summary>
    /// Reduces the readings to the points the width can show, keeping the
    /// highest reading in each: a moment at full load is exactly what an
    /// average would hide.
    /// </summary>
    private bool Resample(double plotWidth, double plotHeight)
    {
        if (Values is not { Count: > 1 } values)
        {
            _bucketCount = 0;
            return false;
        }

        int total = values.Count;
        int buckets = Math.Clamp((int)(plotWidth / PixelsPerPoint), 2, total);

        if (_points.Length < buckets)
        {
            _points = new Point[buckets];
            _buckets = new double[buckets];
        }

        double step = plotWidth / (buckets - 1);
        _first = -1;

        for (int index = 0; index < buckets; index++)
        {
            int from = (int)((long)index * total / buckets);
            int to = Math.Max(from + 1, (int)((long)(index + 1) * total / buckets));
            double highest = double.NaN;

            for (int at = from; at < to && at < total; at++)
            {
                // A reading not yet taken is not a reading of nothing.
                if (double.IsFinite(values[at]))
                {
                    highest = double.IsNaN(highest) ? values[at] : Math.Max(highest, values[at]);
                }
            }

            if (_first < 0 && double.IsFinite(highest))
            {
                _first = index;
            }

            double share = double.IsFinite(highest) ? Math.Clamp(highest / 100, 0, 1) : 0;
            _buckets[index] = highest;
            _points[index] = new Point(ValueAxisWidth + (index * step), plotHeight - (share * (plotHeight - 1)) - 0.5);
        }

        _bucketCount = buckets;

        if (_hovered >= buckets)
        {
            _hovered = -1;
        }

        // Nothing to draw until two readings have been taken.
        return _first >= 0 && _first < buckets - 1;
    }

    private StreamGeometry Build(double plotHeight, bool closed)
    {
        StreamGeometry geometry = new();

        using (StreamGeometryContext context = geometry.Open())
        {
            if (closed)
            {
                context.BeginFigure(new Point(_points[_first].X, plotHeight), isFilled: true, isClosed: true);
                context.LineTo(_points[_first], isStroked: false, isSmoothJoin: false);
            }
            else
            {
                context.BeginFigure(_points[_first], isFilled: false, isClosed: false);
            }

            for (int index = _first; index < _bucketCount - 1; index++)
            {
                Point previous = _points[Math.Max(index - 1, _first)];
                Point current = _points[index];
                Point next = _points[index + 1];
                Point following = _points[Math.Min(index + 2, _bucketCount - 1)];

                Point first = new(
                    current.X + ((next.X - previous.X) / 6),
                    Math.Clamp(current.Y + ((next.Y - previous.Y) / 6), 0, plotHeight));

                Point second = new(
                    next.X - ((following.X - current.X) / 6),
                    Math.Clamp(next.Y - ((following.Y - current.Y) / 6), 0, plotHeight));

                context.BezierTo(first, second, next, isStroked: true, isSmoothJoin: true);
            }

            if (closed)
            {
                context.LineTo(new Point(_points[_bucketCount - 1].X, plotHeight), isStroked: false, isSmoothJoin: false);
            }
        }

        geometry.Freeze();
        return geometry;
    }

    private void DrawValueAxis(DrawingContext drawingContext, double plotHeight)
    {
        Pen? pen = GridBrush is { } grid ? new Pen(grid, 1) : null;
        pen?.Freeze();

        for (int step = 0; step <= 2; step++)
        {
            double fraction = step / 2d;
            double y = Math.Round(plotHeight * fraction) + 0.5;

            if (step > 0 && pen is not null)
            {
                drawingContext.DrawLine(pen, new Point(ValueAxisWidth, y), new Point(ActualWidth, y));
            }

            string text = string.Create(CultureInfo.InvariantCulture, $"{100 * (1 - fraction):F0}%");
            DrawLabel(drawingContext, text, AxisBrush, ValueAxisWidth - 8, y, HorizontalAlignment.Right);
        }
    }

    private void DrawTimeAxis(DrawingContext drawingContext, double plotWidth, double plotHeight)
    {
        int count = Values?.Count ?? 0;
        double span = Math.Max(count - 1, 1) * SampleSeconds;
        double labelY = plotHeight + (TimeAxisHeight / 2);

        DrawLabel(drawingContext, "now", AxisBrush, ActualWidth, labelY, HorizontalAlignment.Right);

        double tick = TickCandidates[^1];

        foreach (double candidate in TickCandidates)
        {
            if (span / candidate <= 6)
            {
                tick = candidate;
                break;
            }
        }

        Pen? pen = GridBrush is { } grid ? new Pen(grid, 1) : null;
        pen?.Freeze();

        double right = ValueAxisWidth + plotWidth;

        for (double seconds = tick; seconds < span; seconds += tick)
        {
            double x = Math.Round(right - (seconds / span * plotWidth)) + 0.5;

            if (x < ValueAxisWidth + 16)
            {
                break;
            }

            if (pen is not null)
            {
                drawingContext.DrawLine(pen, new Point(x, 0), new Point(x, plotHeight));
            }

            DrawLabel(drawingContext, DurationLabel.Format(seconds), AxisBrush, x, labelY, HorizontalAlignment.Center);
        }
    }

    private void DrawHover(DrawingContext drawingContext, SolidColorBrush stroke, double plotHeight)
    {
        if (_hovered < 0 || _hovered >= _bucketCount || !double.IsFinite(_buckets[_hovered]))
        {
            return;
        }

        Point point = _points[_hovered];
        double x = Math.Round(point.X) + 0.5;

        if (GridBrush is { } grid)
        {
            var guide = new Pen(grid, 1) { DashStyle = new DashStyle([3, 3], 0) };
            guide.Freeze();
            drawingContext.DrawLine(guide, new Point(x, 0), new Point(x, plotHeight));
        }

        Pen? ring = SurfaceBrush is { } surface ? new Pen(surface, 2.5) : null;
        ring?.Freeze();
        drawingContext.DrawEllipse(stroke, ring, new Point(x, point.Y), 4, 4);

        double samplesAgo = (_bucketCount - 1 - _hovered) * ((double)(Values?.Count ?? 0) / _bucketCount);
        string ago = samplesAgo * SampleSeconds < 1 ? "now" : DurationLabel.Format(samplesAgo * SampleSeconds) + " ago";
        string text = string.Create(CultureInfo.InvariantCulture, $"{_buckets[_hovered]:F0}%  ·  {ago}");

        // Beside the marker, on whichever side has room, and never over the axis.
        FormattedText formatted = Format(text, LabelBrush ?? AxisBrush, 11);
        double left = x + 10;

        if (left + formatted.Width + 12 > ActualWidth)
        {
            left = x - 10 - formatted.Width - 12;
        }

        double top = Math.Clamp(point.Y - formatted.Height - 14, 0, Math.Max(0, plotHeight - formatted.Height - 8));
        Rect plate = new(Math.Max(ValueAxisWidth, left), top, formatted.Width + 12, formatted.Height + 6);

        drawingContext.DrawRoundedRectangle(PlateBrush ?? SurfaceBrush, null, plate, 5, 5);
        drawingContext.DrawText(formatted, new Point(plate.X + 6, plate.Y + 3));
    }

    private void DrawLabel(DrawingContext drawingContext, string text, Brush? brush, double x, double y, HorizontalAlignment alignment)
    {
        FormattedText formatted = Format(text, brush, AxisFontSize);

        double offset = alignment switch
        {
            HorizontalAlignment.Right => -formatted.Width,
            HorizontalAlignment.Center => -formatted.Width / 2,
            _ => 0,
        };

        drawingContext.DrawText(formatted, new Point(x + offset, y - (formatted.Height / 2)));
    }

    private FormattedText Format(string text, Brush? brush, double size) =>
        new(
            text,
            CultureInfo.CurrentCulture,
            FlowDirection.LeftToRight,
            _typeface,
            size,
            brush ?? Brushes.Gray,
            VisualTreeHelper.GetDpi(this).PixelsPerDip);

    private static DependencyProperty Register<T>(string name, T fallback) =>
        DependencyProperty.Register(
            name,
            typeof(T),
            typeof(ShareChart),
            new FrameworkPropertyMetadata(fallback, FrameworkPropertyMetadataOptions.AffectsRender));
}
