using System.Globalization;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Automation.Peers;
using System.Windows.Input;
using System.Windows.Media;
using Windsock.Core.Formatting;

namespace Windsock.App.Controls;

/// <summary>One bar of a <see cref="BarSeriesChart"/>.</summary>
/// <param name="Label">What the axis says under it, kept short: "5", "Mon", "14:00".</param>
/// <param name="Detail">The bar in a sentence, for the line under the chart and for screen readers.</param>
/// <param name="Primary">The bottom part, in bytes.</param>
/// <param name="Secondary">Stacked on the primary part, in its own colour.</param>
/// <param name="Faint">Stacked on top, in the primary colour, fainter: a figure somebody else counted.</param>
/// <param name="Paired">A second bar beside this one, for a comparison; null for none.</param>
/// <param name="IsFuture">Whether the bar is time still to come, drawn as an empty track.</param>
public sealed record ChartBar(
    string Label,
    string Detail,
    double Primary,
    double Secondary = 0,
    double Faint = 0,
    double? Paired = null,
    bool IsFuture = false)
{
    /// <summary>How tall the stacked bar is.</summary>
    public double Stacked => Primary + Secondary + Faint;
}

/// <summary>
/// A plain bar chart of byte figures: one bar a step, parts stacked, with an
/// optional bar beside each for a comparison and an optional line across for a
/// budget. Used by the data plan, reports and an app over time.
/// </summary>
/// <remarks>
/// <para>
/// Pointing at a bar, or moving along the bars with the arrow keys once the
/// chart has focus, chooses it; <see cref="SelectedDetail"/> says what it holds,
/// for a line under the chart and for screen readers (it is the chart's help
/// text too), so nothing is only on hover. Home and End go to the ends.
/// </para>
/// <para>
/// It paints no ground of its own, so it sits on the page or in a band alike.
/// </para>
/// </remarks>
public sealed class BarSeriesChart : FrameworkElement
{
    private const double LeftGutter = 58;
    private const double RightPadding = 8;
    private const double BottomGutter = 20;
    private const double TopPadding = 10;
    private const double LabelSize = 10.5;
    private const double FaintOpacity = 0.4;

    public static readonly DependencyProperty ItemsProperty = Register<IReadOnlyList<ChartBar>>(nameof(Items), [], OnItemsChanged);

    public static readonly DependencyProperty PrimaryBrushProperty = Register<Brush?>(nameof(PrimaryBrush), null);

    public static readonly DependencyProperty SecondaryBrushProperty = Register<Brush?>(nameof(SecondaryBrush), null);

    public static readonly DependencyProperty PairedBrushProperty = Register<Brush?>(nameof(PairedBrush), null);

    public static readonly DependencyProperty GridBrushProperty = Register<Brush?>(nameof(GridBrush), null);

    public static readonly DependencyProperty AxisBrushProperty = Register<Brush?>(nameof(AxisBrush), null);

    public static readonly DependencyProperty HighlightBrushProperty = Register<Brush?>(nameof(HighlightBrush), null);

    public static readonly DependencyProperty TrackBrushProperty = Register<Brush?>(nameof(TrackBrush), null);

    public static readonly DependencyProperty ReferenceValueProperty = Register<double?>(nameof(ReferenceValue), null);

    public static readonly DependencyProperty ReferenceBrushProperty = Register<Brush?>(nameof(ReferenceBrush), null);

    public static readonly DependencyProperty ReferenceLabelProperty = Register(nameof(ReferenceLabel), string.Empty);

    public static readonly DependencyProperty SelectedIndexProperty = DependencyProperty.Register(
        nameof(SelectedIndex),
        typeof(int),
        typeof(BarSeriesChart),
        new FrameworkPropertyMetadata(-1, FrameworkPropertyMetadataOptions.AffectsRender | FrameworkPropertyMetadataOptions.BindsTwoWayByDefault, OnSelectedChanged));

    private static readonly DependencyPropertyKey SelectedDetailKey = DependencyProperty.RegisterReadOnly(
        nameof(SelectedDetail),
        typeof(string),
        typeof(BarSeriesChart),
        new PropertyMetadata(string.Empty));

    public static readonly DependencyProperty SelectedDetailProperty = SelectedDetailKey.DependencyProperty;

    public BarSeriesChart()
    {
        Focusable = true;
        Cursor = Cursors.Arrow;
    }

    /// <summary>The bars, left to right.</summary>
    public IReadOnlyList<ChartBar> Items
    {
        get => (IReadOnlyList<ChartBar>)GetValue(ItemsProperty);
        set => SetValue(ItemsProperty, value);
    }

    public Brush? PrimaryBrush
    {
        get => (Brush?)GetValue(PrimaryBrushProperty);
        set => SetValue(PrimaryBrushProperty, value);
    }

    public Brush? SecondaryBrush
    {
        get => (Brush?)GetValue(SecondaryBrushProperty);
        set => SetValue(SecondaryBrushProperty, value);
    }

    /// <summary>The colour of the bar beside each, in a comparison.</summary>
    public Brush? PairedBrush
    {
        get => (Brush?)GetValue(PairedBrushProperty);
        set => SetValue(PairedBrushProperty, value);
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

    /// <summary>The wash behind the chosen bar.</summary>
    public Brush? HighlightBrush
    {
        get => (Brush?)GetValue(HighlightBrushProperty);
        set => SetValue(HighlightBrushProperty, value);
    }

    /// <summary>The empty track a bar still to come is drawn as.</summary>
    public Brush? TrackBrush
    {
        get => (Brush?)GetValue(TrackBrushProperty);
        set => SetValue(TrackBrushProperty, value);
    }

    /// <summary>A value to draw a line across at, such as a daily budget; null for none.</summary>
    public double? ReferenceValue
    {
        get => (double?)GetValue(ReferenceValueProperty);
        set => SetValue(ReferenceValueProperty, value);
    }

    public Brush? ReferenceBrush
    {
        get => (Brush?)GetValue(ReferenceBrushProperty);
        set => SetValue(ReferenceBrushProperty, value);
    }

    /// <summary>What the line across is, written at its end.</summary>
    public string ReferenceLabel
    {
        get => (string)GetValue(ReferenceLabelProperty);
        set => SetValue(ReferenceLabelProperty, value);
    }

    /// <summary>The bar chosen by pointer or keyboard, or -1.</summary>
    public int SelectedIndex
    {
        get => (int)GetValue(SelectedIndexProperty);
        set => SetValue(SelectedIndexProperty, value);
    }

    /// <summary>The chosen bar in a sentence; empty with none chosen.</summary>
    public string SelectedDetail => (string)GetValue(SelectedDetailProperty);

    protected override AutomationPeer OnCreateAutomationPeer() => new ChartAutomationPeer(this, Summary);

    /// <summary>What a screen reader is told: the chosen bar, else how many there are and the highest.</summary>
    private string Summary()
    {
        if (SelectedDetail.Length > 0)
        {
            return SelectedDetail;
        }

        IReadOnlyList<ChartBar> items = Items;
        ChartBar? highest = items.Where(bar => !bar.IsFuture).MaxBy(bar => bar.Stacked);

        return highest is null || highest.Stacked <= 0
            ? "No bars."
            : string.Format(CultureInfo.CurrentCulture, "{0} bars. The highest: {1}.", items.Count, highest.Detail);
    }

    private static DependencyProperty Register<T>(string name, T fallback, PropertyChangedCallback? changed = null) =>
        DependencyProperty.Register(
            name,
            typeof(T),
            typeof(BarSeriesChart),
            new FrameworkPropertyMetadata(fallback, FrameworkPropertyMetadataOptions.AffectsRender, changed));

    private static void OnItemsChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is BarSeriesChart chart)
        {
            if (chart.SelectedIndex >= chart.Items.Count)
            {
                chart.SelectedIndex = -1;
            }

            chart.Describe();
        }
    }

    private static void OnSelectedChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is BarSeriesChart chart)
        {
            chart.Describe();
        }
    }

    private void Describe()
    {
        int index = SelectedIndex;
        string detail = index >= 0 && index < Items.Count ? Items[index].Detail : string.Empty;
        SetValue(SelectedDetailKey, detail);
        AutomationProperties.SetHelpText(this, detail);
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        ArgumentNullException.ThrowIfNull(e);

        int count = Items.Count;

        if (count == 0)
        {
            return;
        }

        int index = SelectedIndex;
        int moved = e.Key switch
        {
            Key.Left => index <= 0 ? 0 : index - 1,
            Key.Right => index < 0 ? 0 : Math.Min(count - 1, index + 1),
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

        if (SelectedIndex < 0 && Items.Count > 0)
        {
            SelectedIndex = Items.Count - 1;
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

    protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonDown(e);
        Focus();
    }

    private int IndexAt(double x)
    {
        int count = Items.Count;
        double width = ActualWidth - LeftGutter - RightPadding;

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

        if (width <= LeftGutter + RightPadding + 4 || height <= BottomGutter + TopPadding + 4)
        {
            return;
        }

        // Hit-testable across the whole area, so pointing between bars still chooses one.
        drawingContext.DrawRectangle(Brushes.Transparent, null, new Rect(0, 0, width, height));

        IReadOnlyList<ChartBar> items = Items;
        double plotLeft = LeftGutter;
        double plotWidth = width - LeftGutter - RightPadding;
        double plotTop = TopPadding;
        double plotHeight = height - TopPadding - BottomGutter;
        double bottom = plotTop + plotHeight;

        double ceiling = 0;

        foreach (ChartBar bar in items)
        {
            ceiling = Math.Max(ceiling, Math.Max(bar.Stacked, bar.Paired ?? 0));
        }

        if (ReferenceValue is { } reference && reference > 0)
        {
            ceiling = Math.Max(ceiling, reference * 1.08);
        }

        DrawGrid(drawingContext, plotLeft, plotTop, plotWidth, plotHeight, ceiling);

        if (items.Count == 0)
        {
            return;
        }

        double slot = plotWidth / items.Count;
        bool paired = items.Any(bar => bar.Paired is not null);
        double barWidth = Math.Max(1, paired ? slot * 0.38 : slot * (slot > 6 ? 0.72 : 0.9));
        int selected = SelectedIndex;

        if (selected >= 0 && selected < items.Count && HighlightBrush is { } highlight)
        {
            drawingContext.DrawRectangle(highlight, null, new Rect(plotLeft + (slot * selected), plotTop, slot, plotHeight));
        }

        for (int index = 0; index < items.Count; index++)
        {
            ChartBar bar = items[index];
            double slotLeft = plotLeft + (slot * index);
            double x = paired ? slotLeft + (slot / 2) - barWidth - 1 : slotLeft + ((slot - barWidth) / 2);

            // A step still to come beside one that has been: the earlier one is still drawn.
            if (bar.Paired is { } pair && pair > 0 && ceiling > 0)
            {
                double pairHeight = BarHeight(pair, plotHeight, ceiling);
                Fill(drawingContext, PairedBrush, x + barWidth + 2, bottom - pairHeight, barWidth, pairHeight);
            }

            if (bar.IsFuture)
            {
                if (TrackBrush is { } track)
                {
                    drawingContext.DrawRectangle(track, null, new Rect(x, bottom - 2, barWidth, 2));
                }

                continue;
            }

            if (ceiling <= 0)
            {
                continue;
            }

            double primary = BarHeight(bar.Primary, plotHeight, ceiling);
            double secondary = BarHeight(bar.Secondary, plotHeight, ceiling);
            double faint = BarHeight(bar.Faint, plotHeight, ceiling);

            Fill(drawingContext, PrimaryBrush, x, bottom - primary, barWidth, primary);
            Fill(drawingContext, SecondaryBrush, x, bottom - primary - secondary, barWidth, secondary);

            if (faint > 0 && PrimaryBrush is { } brush)
            {
                drawingContext.PushOpacity(FaintOpacity);
                Fill(drawingContext, brush, x, bottom - primary - secondary - faint, barWidth, faint);
                drawingContext.Pop();
            }
        }

        if (ReferenceValue is { } line && line > 0 && ceiling > 0 && ReferenceBrush is { } referenceBrush)
        {
            double y = Snap(bottom - (plotHeight * line / ceiling));
            Pen pen = new(referenceBrush, 1.2) { DashStyle = new DashStyle([4, 3], 0) };
            pen.Freeze();
            drawingContext.DrawLine(pen, new Point(plotLeft, y), new Point(plotLeft + plotWidth, y));

            if (ReferenceLabel.Length > 0)
            {
                Label(drawingContext, ReferenceLabel, plotLeft + plotWidth - 2, y - 8, right: true, referenceBrush);
            }
        }

        DrawLabels(drawingContext, items, plotLeft, slot, height);
    }

    private static double BarHeight(double value, double plotHeight, double ceiling)
    {
        if (value <= 0)
        {
            return 0;
        }

        // A figure too small to see still shows, a pixel tall.
        return Math.Max(1, plotHeight * value / ceiling);
    }

    private static void Fill(DrawingContext context, Brush? brush, double x, double y, double width, double height)
    {
        if (brush is not null && height > 0)
        {
            context.DrawRectangle(brush, null, new Rect(x, y, width, height));
        }
    }

    private void DrawGrid(DrawingContext context, double left, double top, double width, double height, double ceiling)
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
                Label(context, SizeFormatter.Format((long)(ceiling * (2 - i) / 2)), left - 8, y, right: true, AxisBrush);
            }
        }
    }

    private void DrawLabels(DrawingContext context, IReadOnlyList<ChartBar> items, double left, double slot, double height)
    {
        if (AxisBrush is null)
        {
            return;
        }

        double widest = 0;

        foreach (ChartBar bar in items)
        {
            widest = Math.Max(widest, Text(bar.Label, AxisBrush).Width);
        }

        // Every label that fits with a little room either side; the rest skipped evenly.
        int every = Math.Max(1, (int)Math.Ceiling((widest + 8) / Math.Max(1, slot)));
        double y = height - BottomGutter + 4;

        for (int index = 0; index < items.Count; index += every)
        {
            Label(context, items[index].Label, left + (slot * index) + (slot / 2), y, right: false, AxisBrush, centred: true);
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

    private void Label(DrawingContext context, string text, double x, double y, bool right, Brush? brush, bool centred = false)
    {
        if (brush is null || text.Length == 0)
        {
            return;
        }

        FormattedText formatted = Text(text, brush);
        double offset = right ? formatted.Width : centred ? formatted.Width / 2 : 0;
        context.DrawText(formatted, new Point(x - offset, y - (formatted.Height / 2)));
    }

    private static double Snap(double value) => Math.Round(value) + 0.5;
}
