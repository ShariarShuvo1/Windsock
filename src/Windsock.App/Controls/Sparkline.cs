using System.Windows;
using System.Windows.Media;

namespace Windsock.App.Controls;

/// <summary>
/// The recent past of one figure, drawn small.
/// </summary>
public sealed class Sparkline : FrameworkElement
{
    private const double LeastCeiling = 1;

    public static readonly DependencyProperty ValuesProperty = DependencyProperty.Register(
        nameof(Values),
        typeof(IReadOnlyList<double>),
        typeof(Sparkline),
        new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty RevisionProperty = DependencyProperty.Register(
        nameof(Revision),
        typeof(int),
        typeof(Sparkline),
        new FrameworkPropertyMetadata(0, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty CeilingProperty = DependencyProperty.Register(
        nameof(Ceiling),
        typeof(double),
        typeof(Sparkline),
        new FrameworkPropertyMetadata(100d, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty StrokeProperty = DependencyProperty.Register(
        nameof(Stroke),
        typeof(Brush),
        typeof(Sparkline),
        new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty FillProperty = DependencyProperty.Register(
        nameof(Fill),
        typeof(Brush),
        typeof(Sparkline),
        new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));

    /// <summary>The readings, oldest first.</summary>
    public IReadOnlyList<double>? Values
    {
        get => (IReadOnlyList<double>?)GetValue(ValuesProperty);
        set => SetValue(ValuesProperty, value);
    }

    /// <summary>Changed by the view model when a reading has been added.</summary>
    public int Revision
    {
        get => (int)GetValue(RevisionProperty);
        set => SetValue(RevisionProperty, value);
    }

    /// <summary>
    /// The top of the scale, or zero to take it from the readings themselves.
    /// </summary>
    public double Ceiling
    {
        get => (double)GetValue(CeilingProperty);
        set => SetValue(CeilingProperty, value);
    }

    /// <summary>The line along the top of the area.</summary>
    public Brush? Stroke
    {
        get => (Brush?)GetValue(StrokeProperty);
        set => SetValue(StrokeProperty, value);
    }

    /// <summary>What the area under the line is painted with.</summary>
    public Brush? Fill
    {
        get => (Brush?)GetValue(FillProperty);
        set => SetValue(FillProperty, value);
    }

    protected override void OnRender(DrawingContext drawingContext)
    {
        ArgumentNullException.ThrowIfNull(drawingContext);

        double width = ActualWidth;
        double height = ActualHeight;

        if (Values is not { Count: > 1 } values || width <= 0 || height <= 0)
        {
            return;
        }

        double ceiling = Ceiling > 0 ? Ceiling : Peak(values);
        double step = width / (values.Count - 1);

        StreamGeometry line = new();

        using (StreamGeometryContext draw = line.Open())
        {
            draw.BeginFigure(new Point(0, height), isFilled: true, isClosed: true);

            for (int index = 0; index < values.Count; index++)
            {
                draw.LineTo(new Point(index * step, Level(values[index], ceiling, height)), true, false);
            }

            draw.LineTo(new Point(width, height), true, false);
        }

        line.Freeze();

        if (Fill is { } fill)
        {
            drawingContext.DrawGeometry(fill, null, line);
        }

        if (Stroke is { } stroke)
        {
            drawingContext.DrawGeometry(null, new Pen(stroke, 1.4) { LineJoin = PenLineJoin.Round }, line);
        }
    }

    private static double Level(double value, double ceiling, double height)
    {
        double share = Math.Clamp(value / ceiling, 0, 1);

        return height - (share * (height - 1)) - 0.5;
    }

    private static double Peak(IReadOnlyList<double> values)
    {
        double peak = LeastCeiling;

        foreach (double value in values)
        {
            peak = Math.Max(peak, value);
        }

        return peak;
    }
}
