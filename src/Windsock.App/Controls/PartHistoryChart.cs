using System.Globalization;
using System.Windows;
using System.Windows.Automation.Peers;
using System.Windows.Input;
using System.Windows.Media;

namespace Windsock.App.Controls;

/// <summary>One step of a <see cref="PartHistoryChart"/>.</summary>
/// <param name="Label">What the axis says under it, or nothing; most steps say nothing.</param>
/// <param name="Detail">The step in a sentence, for the line under the chart and for screen readers.</param>
/// <param name="Average">The step's average against the chart's top, from nothing to one; nothing for a gap.</param>
/// <param name="Peak">Its highest reading against the same top; nothing where it is not kept.</param>
/// <param name="Heat">Its temperature against the heat scale, from nothing to one; nothing where none.</param>
public sealed record HistoryStep(string Label, string Detail, double? Average, double? Peak, double? Heat);

/// <summary>
/// A part of the machine over its last day or week: a bar a step for the
/// average, a fainter bar behind it for the peak, and the temperature as a
/// line across, on a scale of its own.
/// </summary>
/// <remarks>
/// <para>
/// A step nothing was recorded in is left empty - a gap, not a zero - and the
/// temperature line breaks across it. The scales and labels come from the
/// owner, already put into words, so the chart knows nothing of percent,
/// bytes or degrees.
/// </para>
/// <para>
/// Pointing at a step, or moving along with the arrow keys once the chart has
/// focus, chooses it; <see cref="SelectedDetail"/> says what it holds, for the
/// line under the chart and as the chart's help text, so nothing is only on
/// hover. Home and End go to the ends. It paints no ground of its own.
/// </para>
/// </remarks>
public sealed class PartHistoryChart : FrameworkElement
{
    private const double LeftGutter = 58;
    private const double RightGutterWithHeat = 44;
    private const double RightPadding = 8;
    private const double BottomGutter = 24;
    private const double TopPadding = 10;
    private const double LabelSize = 10.5;
    private const double PeakOpacity = 0.32;

    public static readonly DependencyProperty StepsProperty = Register<IReadOnlyList<HistoryStep>>(nameof(Steps), [], OnStepsChanged);

    public static readonly DependencyProperty BarBrushProperty = Register<Brush?>(nameof(BarBrush), null);

    public static readonly DependencyProperty HeatBrushProperty = Register<Brush?>(nameof(HeatBrush), null);

    public static readonly DependencyProperty GridBrushProperty = Register<Brush?>(nameof(GridBrush), null);

    public static readonly DependencyProperty AxisBrushProperty = Register<Brush?>(nameof(AxisBrush), null);

    public static readonly DependencyProperty HighlightBrushProperty = Register<Brush?>(nameof(HighlightBrush), null);

    public static readonly DependencyProperty TopLabelProperty = Register(nameof(TopLabel), string.Empty);

    public static readonly DependencyProperty MiddleLabelProperty = Register(nameof(MiddleLabel), string.Empty);

    public static readonly DependencyProperty HeatTopLabelProperty = Register(nameof(HeatTopLabel), string.Empty);

    public static readonly DependencyProperty HeatBottomLabelProperty = Register(nameof(HeatBottomLabel), string.Empty);

    public static readonly DependencyProperty SelectedIndexProperty = DependencyProperty.Register(
        nameof(SelectedIndex),
        typeof(int),
        typeof(PartHistoryChart),
        new FrameworkPropertyMetadata(-1, FrameworkPropertyMetadataOptions.AffectsRender, OnSelectedChanged));

    private static readonly DependencyPropertyKey SelectedDetailKey = DependencyProperty.RegisterReadOnly(
        nameof(SelectedDetail),
        typeof(string),
        typeof(PartHistoryChart),
        new PropertyMetadata(string.Empty));

    public static readonly DependencyProperty SelectedDetailProperty = SelectedDetailKey.DependencyProperty;

    public PartHistoryChart()
    {
        Focusable = true;
        Cursor = Cursors.Arrow;
    }

    /// <summary>The steps, oldest first.</summary>
    public IReadOnlyList<HistoryStep> Steps
    {
        get => (IReadOnlyList<HistoryStep>)GetValue(StepsProperty);
        set => SetValue(StepsProperty, value);
    }

    /// <summary>The part's own colour, for the bars.</summary>
    public Brush? BarBrush
    {
        get => (Brush?)GetValue(BarBrushProperty);
        set => SetValue(BarBrushProperty, value);
    }

    /// <summary>The temperature line's colour.</summary>
    public Brush? HeatBrush
    {
        get => (Brush?)GetValue(HeatBrushProperty);
        set => SetValue(HeatBrushProperty, value);
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

    /// <summary>The wash behind the chosen step.</summary>
    public Brush? HighlightBrush
    {
        get => (Brush?)GetValue(HighlightBrushProperty);
        set => SetValue(HighlightBrushProperty, value);
    }

    /// <summary>What the top line of the scale stands for, written at its left.</summary>
    public string TopLabel
    {
        get => (string)GetValue(TopLabelProperty);
        set => SetValue(TopLabelProperty, value);
    }

    /// <summary>What the middle line stands for.</summary>
    public string MiddleLabel
    {
        get => (string)GetValue(MiddleLabelProperty);
        set => SetValue(MiddleLabelProperty, value);
    }

    /// <summary>The top of the heat scale, written at the right; nothing for no heat scale.</summary>
    public string HeatTopLabel
    {
        get => (string)GetValue(HeatTopLabelProperty);
        set => SetValue(HeatTopLabelProperty, value);
    }

    /// <summary>The bottom of the heat scale.</summary>
    public string HeatBottomLabel
    {
        get => (string)GetValue(HeatBottomLabelProperty);
        set => SetValue(HeatBottomLabelProperty, value);
    }

    /// <summary>The step chosen by pointer or keyboard, or -1.</summary>
    public int SelectedIndex
    {
        get => (int)GetValue(SelectedIndexProperty);
        set => SetValue(SelectedIndexProperty, value);
    }

    /// <summary>The chosen step in a sentence; empty with none chosen.</summary>
    public string SelectedDetail => (string)GetValue(SelectedDetailProperty);

    private bool HasHeatScale => HeatTopLabel.Length > 0;

    private double RightGutter => HasHeatScale ? RightGutterWithHeat : RightPadding;

    private static DependencyProperty Register<T>(string name, T fallback, PropertyChangedCallback? changed = null) =>
        DependencyProperty.Register(
            name,
            typeof(T),
            typeof(PartHistoryChart),
            new FrameworkPropertyMetadata(fallback, FrameworkPropertyMetadataOptions.AffectsRender, changed));

    private static void OnStepsChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is PartHistoryChart chart)
        {
            if (chart.SelectedIndex >= chart.Steps.Count)
            {
                chart.SelectedIndex = -1;
            }

            chart.Describe();
        }
    }

    private static void OnSelectedChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is PartHistoryChart chart)
        {
            chart.Describe();
        }
    }

    private void Describe()
    {
        int index = SelectedIndex;
        string detail = index >= 0 && index < Steps.Count ? Steps[index].Detail : string.Empty;
        SetValue(SelectedDetailKey, detail);
    }

    protected override AutomationPeer OnCreateAutomationPeer() => new ChartAutomationPeer(this, Summary);

    /// <summary>
    /// The chart in a sentence for a screen reader: the chosen step, or else
    /// the latest step anything was recorded in.
    /// </summary>
    private string Summary()
    {
        if (SelectedDetail.Length > 0)
        {
            return SelectedDetail;
        }

        IReadOnlyList<HistoryStep> steps = Steps;

        for (int index = steps.Count - 1; index >= 0; index--)
        {
            if (steps[index].Average is not null)
            {
                return string.Concat("Latest: ", steps[index].Detail);
            }
        }

        return "Nothing recorded.";
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        ArgumentNullException.ThrowIfNull(e);

        int count = Steps.Count;

        if (count == 0)
        {
            return;
        }

        int index = SelectedIndex;
        int moved = e.Key switch
        {
            Key.Left => index <= 0 ? 0 : index - 1,
            Key.Right => index < 0 ? 0 : Math.Min(count - 1, index + 1),
            Key.PageUp => Math.Max(0, (index < 0 ? count - 1 : index) - 12),
            Key.PageDown => Math.Min(count - 1, (index < 0 ? 0 : index) + 12),
            Key.Home => 0,
            Key.End => count - 1,
            _ => int.MinValue,
        };

        if (moved == int.MinValue)
        {
            return;
        }

        SelectedIndex = moved;
        e.Handled = true;
    }

    protected override void OnGotKeyboardFocus(KeyboardFocusChangedEventArgs e)
    {
        base.OnGotKeyboardFocus(e);

        if (SelectedIndex < 0 && Steps.Count > 0)
        {
            SelectedIndex = Steps.Count - 1;
        }

        InvalidateVisual();
    }

    protected override void OnLostKeyboardFocus(KeyboardFocusChangedEventArgs e)
    {
        base.OnLostKeyboardFocus(e);
        InvalidateVisual();
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        ArgumentNullException.ThrowIfNull(e);

        int index = IndexAt(e.GetPosition(this).X);

        if (index >= 0)
        {
            SelectedIndex = index;
        }
    }

    protected override void OnMouseLeave(MouseEventArgs e)
    {
        base.OnMouseLeave(e);

        if (!IsKeyboardFocused)
        {
            SelectedIndex = -1;
        }
    }

    protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonDown(e);
        Focus();
    }

    private int IndexAt(double x)
    {
        int count = Steps.Count;
        double width = ActualWidth - LeftGutter - RightGutter;

        if (count == 0 || width <= 0 || x < LeftGutter || x > LeftGutter + width)
        {
            return -1;
        }

        return Math.Clamp((int)((x - LeftGutter) / width * count), 0, count - 1);
    }

    protected override void OnRender(DrawingContext drawingContext)
    {
        ArgumentNullException.ThrowIfNull(drawingContext);

        double width = ActualWidth;
        double height = ActualHeight;
        double right = RightGutter;

        if (width <= LeftGutter + right + 4 || height <= BottomGutter + TopPadding + 4)
        {
            return;
        }

        // Hit-testable across the whole area, so pointing between bars still chooses one.
        drawingContext.DrawRectangle(Brushes.Transparent, null, new Rect(0, 0, width, height));

        IReadOnlyList<HistoryStep> steps = Steps;
        double plotLeft = LeftGutter;
        double plotWidth = width - LeftGutter - right;
        double plotHeight = height - TopPadding - BottomGutter;
        double bottom = TopPadding + plotHeight;

        DrawGrid(drawingContext, plotLeft, TopPadding, plotWidth, plotHeight);

        if (steps.Count == 0)
        {
            return;
        }

        double slot = plotWidth / steps.Count;
        double barWidth = Math.Max(1, slot > 3 ? slot - 1 : slot);
        int selected = SelectedIndex;

        if (selected >= 0 && selected < steps.Count && HighlightBrush is { } highlight)
        {
            drawingContext.DrawRectangle(highlight, null, new Rect(plotLeft + (slot * selected), TopPadding, Math.Max(slot, 2), plotHeight));
        }

        if (BarBrush is { } bars)
        {
            for (int index = 0; index < steps.Count; index++)
            {
                HistoryStep step = steps[index];
                double x = plotLeft + (slot * index);

                if (step.Peak is { } peak && peak > 0)
                {
                    double tall = Tall(peak, plotHeight);
                    drawingContext.PushOpacity(PeakOpacity);
                    drawingContext.DrawRectangle(bars, null, new Rect(x, bottom - tall, barWidth, tall));
                    drawingContext.Pop();
                }

                if (step.Average is { } average && average > 0)
                {
                    double tall = Tall(average, plotHeight);
                    drawingContext.DrawRectangle(bars, null, new Rect(x, bottom - tall, barWidth, tall));
                }
            }
        }

        DrawHeat(drawingContext, steps, plotLeft, slot, bottom, plotHeight);
        DrawLabels(drawingContext, steps, plotLeft, slot, height);
    }

    private void DrawHeat(DrawingContext context, IReadOnlyList<HistoryStep> steps, double left, double slot, double bottom, double plotHeight)
    {
        if (HeatBrush is not { } heat)
        {
            return;
        }

        Pen pen = new(heat, 1.6) { LineJoin = PenLineJoin.Round };
        pen.Freeze();

        StreamGeometry line = new();
        bool any = false;

        using (StreamGeometryContext draw = line.Open())
        {
            bool open = false;

            for (int index = 0; index < steps.Count; index++)
            {
                if (steps[index].Heat is not { } value)
                {
                    // A gap breaks the line rather than bridging time nobody measured.
                    open = false;
                    continue;
                }

                Point point = new(left + (slot * index) + (slot / 2), bottom - (plotHeight * Math.Clamp(value, 0, 1)));

                if (!open)
                {
                    draw.BeginFigure(point, isFilled: false, isClosed: false);
                    open = true;
                }
                else
                {
                    draw.LineTo(point, isStroked: true, isSmoothJoin: true);
                }

                any = true;
            }
        }

        if (!any)
        {
            return;
        }

        line.Freeze();
        context.DrawGeometry(null, pen, line);
    }

    private static double Tall(double fraction, double plotHeight) =>
        Math.Max(1, plotHeight * Math.Clamp(fraction, 0, 1));

    private void DrawGrid(DrawingContext context, double left, double top, double width, double height)
    {
        if (GridBrush is { } grid)
        {
            Pen pen = new(grid, 1);
            pen.Freeze();

            for (int i = 0; i <= 2; i++)
            {
                double y = Snap(top + (height * i / 2.0));
                context.DrawLine(pen, new Point(left, y), new Point(left + width, y));
            }
        }

        Label(context, TopLabel, left - 8, top, Alignment.Right, AxisBrush);
        Label(context, MiddleLabel, left - 8, top + (height / 2), Alignment.Right, AxisBrush);

        if (HasHeatScale)
        {
            Label(context, HeatTopLabel, left + width + 6, top, Alignment.Left, HeatBrush ?? AxisBrush);
            Label(context, HeatBottomLabel, left + width + 6, top + height, Alignment.Left, HeatBrush ?? AxisBrush);
        }
    }

    private void DrawLabels(DrawingContext context, IReadOnlyList<HistoryStep> steps, double left, double slot, double height)
    {
        if (AxisBrush is not { } brush)
        {
            return;
        }

        double y = height - BottomGutter + 12;
        double clearUntil = double.MinValue;

        for (int index = 0; index < steps.Count; index++)
        {
            string label = steps[index].Label;

            if (label.Length == 0)
            {
                continue;
            }

            FormattedText text = Text(label, brush);
            double x = left + (slot * index);

            // A label that would run into the last one written is left out.
            if (x - (text.Width / 2) < clearUntil)
            {
                continue;
            }

            context.DrawText(text, new Point(x - (text.Width / 2), y - (text.Height / 2)));
            clearUntil = x + (text.Width / 2) + 8;
        }
    }

    private FormattedText Text(string text, Brush brush) => new(
        text,
        CultureInfo.CurrentCulture,
        FlowDirection.LeftToRight,
        new Typeface(new FontFamily("Segoe UI Variable Text, Segoe UI"), FontStyles.Normal, FontWeights.Normal, FontStretches.Normal),
        LabelSize,
        brush,
        VisualTreeHelper.GetDpi(this).PixelsPerDip);

    private enum Alignment
    {
        Left,

        Right,
    }

    private void Label(DrawingContext context, string text, double x, double y, Alignment alignment, Brush? brush)
    {
        if (brush is null || text.Length == 0)
        {
            return;
        }

        FormattedText formatted = Text(text, brush);
        double offset = alignment == Alignment.Right ? formatted.Width : 0;
        context.DrawText(formatted, new Point(x - offset, y - (formatted.Height / 2)));
    }

    private static double Snap(double value) => Math.Round(value) + 0.5;
}
