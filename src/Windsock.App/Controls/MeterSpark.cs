using System.Globalization;
using System.Windows;
using System.Windows.Automation.Peers;
using System.Windows.Media;
using Windsock.Core.Settings;

namespace Windsock.App.Controls;

/// <summary>
/// The last minute of a reading, drawn small: a line, or a bar a second.
/// </summary>
/// <remarks>
/// Made for the taskbar, so it does as little as it can: it draws only when
/// <see cref="Version"/> moves, which the meter moves once a second, and each
/// drawing is one frozen geometry. Gaps in the readings (NaN) are left as gaps.
/// The scale runs from nothing to the highest value shown, or to a fixed top
/// for a share, so a quiet minute is drawn quiet rather than stretched to fill.
/// </remarks>
public sealed class MeterSpark : FrameworkElement
{
    /// <summary>The readings, oldest first; only the first <see cref="Count"/> are drawn.</summary>
    public static readonly DependencyProperty ValuesProperty = DependencyProperty.Register(
        nameof(Values),
        typeof(IReadOnlyList<double>),
        typeof(MeterSpark),
        new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));

    /// <summary>How many readings to draw.</summary>
    public static readonly DependencyProperty CountProperty = DependencyProperty.Register(
        nameof(Count),
        typeof(int),
        typeof(MeterSpark),
        new FrameworkPropertyMetadata(0, FrameworkPropertyMetadataOptions.AffectsRender));

    /// <summary>Moved whenever the readings change, which is what makes it draw again.</summary>
    public static readonly DependencyProperty VersionProperty = DependencyProperty.Register(
        nameof(Version),
        typeof(int),
        typeof(MeterSpark),
        new FrameworkPropertyMetadata(0, FrameworkPropertyMetadataOptions.AffectsRender));

    /// <summary>A line or bars.</summary>
    public static readonly DependencyProperty ModeProperty = DependencyProperty.Register(
        nameof(Mode),
        typeof(MeterGraph),
        typeof(MeterSpark),
        new FrameworkPropertyMetadata(MeterGraph.Line, FrameworkPropertyMetadataOptions.AffectsRender));

    /// <summary>The top of the scale, or NaN to take the highest value shown.</summary>
    public static readonly DependencyProperty CeilingProperty = DependencyProperty.Register(
        nameof(Ceiling),
        typeof(double),
        typeof(MeterSpark),
        new FrameworkPropertyMetadata(double.NaN, FrameworkPropertyMetadataOptions.AffectsRender));

    /// <summary>The colour it is drawn in.</summary>
    public static readonly DependencyProperty InkProperty = DependencyProperty.Register(
        nameof(Ink),
        typeof(Brush),
        typeof(MeterSpark),
        new FrameworkPropertyMetadata(Brushes.White, FrameworkPropertyMetadataOptions.AffectsRender));

    /// <remarks>
    /// A list rather than an array: the XAML compiler refuses a property of
    /// array type set inside a template, binding or not (MC4102).
    /// </remarks>
    public IReadOnlyList<double>? Values
    {
        get => (IReadOnlyList<double>?)GetValue(ValuesProperty);
        set => SetValue(ValuesProperty, value);
    }

    public int Count
    {
        get => (int)GetValue(CountProperty);
        set => SetValue(CountProperty, value);
    }

    public int Version
    {
        get => (int)GetValue(VersionProperty);
        set => SetValue(VersionProperty, value);
    }

    public MeterGraph Mode
    {
        get => (MeterGraph)GetValue(ModeProperty);
        set => SetValue(ModeProperty, value);
    }

    public double Ceiling
    {
        get => (double)GetValue(CeilingProperty);
        set => SetValue(CeilingProperty, value);
    }

    public Brush Ink
    {
        get => (Brush)GetValue(InkProperty);
        set => SetValue(InkProperty, value);
    }

    /// <inheritdoc />
    protected override AutomationPeer OnCreateAutomationPeer() => new ChartAutomationPeer(this, Summary);

    /// <summary>What the graph covers, for a screen reader: the figures beside it say what it reads.</summary>
    private string Summary() => Count <= 0
        ? "Nothing drawn yet."
        : string.Format(CultureInfo.CurrentCulture, "The last {0} seconds, newest on the right.", Count);

    protected override void OnRender(DrawingContext drawingContext)
    {
        ArgumentNullException.ThrowIfNull(drawingContext);

        IReadOnlyList<double>? values = Values;
        int count = Math.Min(Count, values?.Count ?? 0);
        double width = ActualWidth;
        double height = ActualHeight;

        if (values is null || count < 2 || width <= 1 || height <= 1 || Mode == MeterGraph.None)
        {
            return;
        }

        double top = Ceiling;

        if (!double.IsFinite(top) || top <= 0)
        {
            top = 0;

            for (int each = 0; each < count; each++)
            {
                if (double.IsFinite(values[each]))
                {
                    top = Math.Max(top, values[each]);
                }
            }

            if (top <= 0)
            {
                return;
            }
        }

        // The whole minute spans the width whatever has been kept of it, so
        // the newest reading always sits at the right edge.
        double step = width / Math.Max(1, Core.Meter.MeterHistory.Span - 1);
        double start = width - (step * (count - 1));
        StreamGeometry geometry = new();

        using (StreamGeometryContext context = geometry.Open())
        {
            if (Mode == MeterGraph.Bars)
            {
                double bar = Math.Max(1, step - 1);

                for (int each = 0; each < count; each++)
                {
                    if (!double.IsFinite(values[each]) || values[each] <= 0)
                    {
                        continue;
                    }

                    double tall = Math.Max(1, Math.Min(1, values[each] / top) * height);
                    double left = start + (step * each) - (bar / 2);

                    context.BeginFigure(new Point(left, height), isFilled: true, isClosed: true);
                    context.LineTo(new Point(left, height - tall), isStroked: false, isSmoothJoin: false);
                    context.LineTo(new Point(left + bar, height - tall), isStroked: false, isSmoothJoin: false);
                    context.LineTo(new Point(left + bar, height), isStroked: false, isSmoothJoin: false);
                }
            }
            else
            {
                bool drawing = false;

                for (int each = 0; each < count; each++)
                {
                    if (!double.IsFinite(values[each]))
                    {
                        drawing = false;
                        continue;
                    }

                    Point point = new(start + (step * each), height - (Math.Clamp(values[each] / top, 0, 1) * (height - 1)) - 0.5);

                    if (!drawing)
                    {
                        context.BeginFigure(point, isFilled: false, isClosed: false);
                        drawing = true;
                    }
                    else
                    {
                        context.LineTo(point, isStroked: true, isSmoothJoin: true);
                    }
                }
            }
        }

        geometry.Freeze();

        if (Mode == MeterGraph.Bars)
        {
            drawingContext.DrawGeometry(Ink, null, geometry);
        }
        else
        {
            Pen pen = new(Ink, 1.25) { LineJoin = PenLineJoin.Round };
            pen.Freeze();
            drawingContext.DrawGeometry(null, pen, geometry);
        }
    }
}
