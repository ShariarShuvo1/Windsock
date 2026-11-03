using System.Globalization;
using System.Windows;
using System.Windows.Automation.Peers;
using System.Windows.Input;
using System.Windows.Media;

namespace Windsock.App.Controls;

/// <summary>One bar of a <see cref="MetricBarsChart"/>.</summary>
/// <param name="Label">The axis label under it, kept short.</param>
/// <param name="Value">The bar's height, or nothing for a gap.</param>
/// <param name="Detail">The bar in a sentence, for the line under the chart and screen readers.</param>
/// <param name="Paired">A second, thinner bar beside it, or nothing.</param>
/// <param name="Highlight">Whether to draw it in the highlight colour.</param>
public sealed record MetricBar(string Label, double? Value, string Detail, double? Paired = null, bool Highlight = false);

/// <summary>
/// A plain bar chart of any figure - speeds, shares of a plan, how busy a
/// channel is - with an optional line across for a reference such as the plan.
/// </summary>
/// <remarks>
/// Pointing at a bar or moving along them with the arrow keys chooses it, and
/// <see cref="SelectedDetail"/> says what it holds, so nothing is on hover
/// alone; a screen reader is told the chosen bar's sentence, or the tallest
/// bar's when none is chosen. It paints no ground of its own.
/// </remarks>
public sealed class MetricBarsChart : FrameworkElement
{
    private const double LeftGutter = 54;
    private const double BottomGutter = 18;
    private const double TopPadding = 8;
    private const double LabelSize = 10;

    public static readonly DependencyProperty ItemsProperty = Register<IReadOnlyList<MetricBar>>(nameof(Items), [], OnItemsChanged);

    public static readonly DependencyProperty MaximumProperty = Register(nameof(Maximum), double.NaN);

    public static readonly DependencyProperty ReferenceValueProperty = Register<double?>(nameof(ReferenceValue), null);

    public static readonly DependencyProperty AxisFormatProperty = Register(nameof(AxisFormat), "0");

    public static readonly DependencyProperty PrimaryBrushProperty = Register<Brush?>(nameof(PrimaryBrush), null);

    public static readonly DependencyProperty PairedBrushProperty = Register<Brush?>(nameof(PairedBrush), null);

    public static readonly DependencyProperty HighlightBrushProperty = Register<Brush?>(nameof(HighlightBrush), null);

    public static readonly DependencyProperty ReferenceBrushProperty = Register<Brush?>(nameof(ReferenceBrush), null);

    public static readonly DependencyProperty GridBrushProperty = Register<Brush?>(nameof(GridBrush), null);

    public static readonly DependencyProperty AxisBrushProperty = Register<Brush?>(nameof(AxisBrush), null);

    public static readonly DependencyProperty SelectedIndexProperty = DependencyProperty.Register(
        nameof(SelectedIndex),
        typeof(int),
        typeof(MetricBarsChart),
        new FrameworkPropertyMetadata(-1, FrameworkPropertyMetadataOptions.AffectsRender, OnSelectedChanged));

    private static readonly DependencyPropertyKey SelectedDetailKey = DependencyProperty.RegisterReadOnly(
        nameof(SelectedDetail),
        typeof(string),
        typeof(MetricBarsChart),
        new PropertyMetadata(string.Empty));

    public static readonly DependencyProperty SelectedDetailProperty = SelectedDetailKey.DependencyProperty;

    private readonly Typeface _typeface = new(SystemFonts.MessageFontFamily, FontStyles.Normal, FontWeights.Normal, FontStretches.Normal);

    public MetricBarsChart()
    {
        Focusable = true;
        SetResourceReference(FocusVisualStyleProperty, "FocusRingStyle");
    }

    /// <summary>The bars, left to right.</summary>
    public IReadOnlyList<MetricBar> Items
    {
        get => (IReadOnlyList<MetricBar>)GetValue(ItemsProperty);
        set => SetValue(ItemsProperty, value);
    }

    /// <summary>The top of the scale, or NaN to fit the tallest bar and the reference.</summary>
    public double Maximum
    {
        get => (double)GetValue(MaximumProperty);
        set => SetValue(MaximumProperty, value);
    }

    /// <summary>A value to draw a line across at, such as the plan's speed.</summary>
    public double? ReferenceValue
    {
        get => (double?)GetValue(ReferenceValueProperty);
        set => SetValue(ReferenceValueProperty, value);
    }

    /// <summary>A .NET number format for the axis labels: "0", "0%", "0' Mbit/s'".</summary>
    public string AxisFormat
    {
        get => (string)GetValue(AxisFormatProperty);
        set => SetValue(AxisFormatProperty, value);
    }

    public Brush? PrimaryBrush
    {
        get => (Brush?)GetValue(PrimaryBrushProperty);
        set => SetValue(PrimaryBrushProperty, value);
    }

    public Brush? PairedBrush
    {
        get => (Brush?)GetValue(PairedBrushProperty);
        set => SetValue(PairedBrushProperty, value);
    }

    public Brush? HighlightBrush
    {
        get => (Brush?)GetValue(HighlightBrushProperty);
        set => SetValue(HighlightBrushProperty, value);
    }

    public Brush? ReferenceBrush
    {
        get => (Brush?)GetValue(ReferenceBrushProperty);
        set => SetValue(ReferenceBrushProperty, value);
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

    /// <summary>The chosen bar, or -1.</summary>
    public int SelectedIndex
    {
        get => (int)GetValue(SelectedIndexProperty);
        set => SetValue(SelectedIndexProperty, value);
    }

    /// <summary>The chosen bar's sentence.</summary>
    public string SelectedDetail => (string)GetValue(SelectedDetailProperty);

    protected override void OnMouseMove(MouseEventArgs e)
    {
        ArgumentNullException.ThrowIfNull(e);
        base.OnMouseMove(e);

        int index = IndexAt(e.GetPosition(this).X);

        if (index >= 0)
        {
            SelectedIndex = index;
        }
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        ArgumentNullException.ThrowIfNull(e);

        int count = Items.Count;

        if (count == 0)
        {
            base.OnKeyDown(e);
            return;
        }

        int next = e.Key switch
        {
            Key.Left => Math.Max(0, SelectedIndex - 1),
            Key.Right => Math.Min(count - 1, SelectedIndex < 0 ? 0 : SelectedIndex + 1),
            Key.Home => 0,
            Key.End => count - 1,
            _ => -2,
        };

        if (next == -2)
        {
            base.OnKeyDown(e);
            return;
        }

        SelectedIndex = next;
        e.Handled = true;
    }

    protected override void OnRender(DrawingContext drawingContext)
    {
        ArgumentNullException.ThrowIfNull(drawingContext);

        IReadOnlyList<MetricBar> items = Items;
        double width = ActualWidth;
        double height = ActualHeight;
        double plotWidth = width - LeftGutter;
        double plotHeight = height - BottomGutter - TopPadding;

        if (plotWidth <= 10 || plotHeight <= 10 || items.Count == 0)
        {
            return;
        }

        double top = Top(items);
        double dpi = VisualTreeHelper.GetDpi(this).PixelsPerDip;

        if (GridBrush is { } grid)
        {
            Pen pen = new(grid, 1);
            pen.Freeze();

            for (int step = 0; step <= 2; step++)
            {
                double value = top * step / 2;
                double y = Math.Round(TopPadding + plotHeight - (value / top * plotHeight)) + 0.5;
                drawingContext.DrawLine(pen, new Point(LeftGutter, y), new Point(width, y));

                if (AxisBrush is { } axis)
                {
                    FormattedText label = Text(Format(value), axis, dpi);
                    drawingContext.DrawText(label, new Point(LeftGutter - label.Width - 8, y - (label.Height / 2)));
                }
            }
        }

        double slot = plotWidth / items.Count;
        double barWidth = Math.Max(2, Math.Min(36, slot * 0.62));
        int labelEvery = Math.Max(1, (int)Math.Ceiling(34 / slot));

        for (int each = 0; each < items.Count; each++)
        {
            MetricBar bar = items[each];
            double centre = LeftGutter + (slot * (each + 0.5));
            bool chosen = each == SelectedIndex;

            if (bar.Value is { } value && double.IsFinite(value) && value > 0)
            {
                Brush? fill = bar.Highlight || chosen ? HighlightBrush ?? PrimaryBrush : PrimaryBrush;
                double h = Math.Min(value, top) / top * plotHeight;
                double w = bar.Paired is null ? barWidth : barWidth * 0.6;
                double x = bar.Paired is null ? centre - (w / 2) : centre - w;
                drawingContext.DrawRoundedRectangle(fill, null, new Rect(x, TopPadding + plotHeight - h, w, h), 2, 2);
            }

            if (bar.Paired is { } paired && double.IsFinite(paired) && paired > 0 && PairedBrush is { } pairedBrush)
            {
                double w = barWidth * 0.4;
                double h = Math.Min(paired, top) / top * plotHeight;
                drawingContext.DrawRoundedRectangle(pairedBrush, null, new Rect(centre + 1, TopPadding + plotHeight - h, w, h), 2, 2);
            }

            if (each % labelEvery == 0 && AxisBrush is { } axis && bar.Label.Length > 0)
            {
                FormattedText label = Text(bar.Label, axis, dpi);
                drawingContext.DrawText(label, new Point(centre - (label.Width / 2), TopPadding + plotHeight + 3));
            }
        }

        if (ReferenceValue is { } reference && reference > 0 && ReferenceBrush is { } line)
        {
            Pen pen = new(line, 1.5) { DashStyle = DashStyles.Dash };
            pen.Freeze();
            double y = Math.Round(TopPadding + plotHeight - (Math.Min(reference, top) / top * plotHeight)) + 0.5;
            drawingContext.DrawLine(pen, new Point(LeftGutter, y), new Point(width, y));
        }

        // Pointer-hit area, so the whole plot answers the mouse.
        drawingContext.DrawRectangle(Brushes.Transparent, null, new Rect(LeftGutter, 0, plotWidth, height));
    }

    private double Top(IReadOnlyList<MetricBar> items)
    {
        if (double.IsFinite(Maximum) && Maximum > 0)
        {
            return Maximum;
        }

        double tallest = items.Max(each => Math.Max(each.Value ?? 0, each.Paired ?? 0));
        tallest = Math.Max(tallest, ReferenceValue ?? 0);
        return tallest <= 0 ? 1 : NiceCeiling(tallest * 1.08);
    }

    private static double NiceCeiling(double value)
    {
        double magnitude = Math.Pow(10, Math.Floor(Math.Log10(value)));

        foreach (double step in new[] { 1d, 2d, 2.5, 5d, 10d })
        {
            if (value <= step * magnitude)
            {
                return step * magnitude;
            }
        }

        return 10 * magnitude;
    }

    private int IndexAt(double x)
    {
        int count = Items.Count;
        double plotWidth = ActualWidth - LeftGutter;

        if (count == 0 || plotWidth <= 0 || x < LeftGutter)
        {
            return -1;
        }

        return Math.Clamp((int)((x - LeftGutter) / (plotWidth / count)), 0, count - 1);
    }

    private string Format(double value)
    {
        try
        {
            return value.ToString(AxisFormat, CultureInfo.CurrentCulture);
        }
        catch (FormatException)
        {
            return value.ToString("0", CultureInfo.CurrentCulture);
        }
    }

    private FormattedText Text(string text, Brush brush, double dpi) =>
        new(text, CultureInfo.CurrentCulture, FlowDirection.LeftToRight, _typeface, LabelSize, brush, dpi);

    private static void OnItemsChanged(DependencyObject owner, DependencyPropertyChangedEventArgs e)
    {
        MetricBarsChart chart = (MetricBarsChart)owner;
        int count = chart.Items.Count;

        if (chart.SelectedIndex >= count)
        {
            chart.SelectedIndex = count - 1;
        }

        chart.Describe();
    }

    private static void OnSelectedChanged(DependencyObject owner, DependencyPropertyChangedEventArgs e) =>
        ((MetricBarsChart)owner).Describe();

    private void Describe()
    {
        IReadOnlyList<MetricBar> items = Items;
        int index = SelectedIndex;
        string detail = index >= 0 && index < items.Count ? items[index].Detail : string.Empty;
        SetValue(SelectedDetailKey, detail);
    }

    protected override AutomationPeer OnCreateAutomationPeer() => new ChartAutomationPeer(this, Summary);

    /// <summary>The chosen bar in a sentence, or how many bars and the tallest.</summary>
    private string Summary()
    {
        if (SelectedDetail.Length > 0)
        {
            return SelectedDetail;
        }

        IReadOnlyList<MetricBar> items = Items;
        MetricBar? tallest = items.Where(each => each.Value is not null).MaxBy(each => each.Value);

        return tallest is null
            ? "Nothing is drawn yet."
            : string.Create(CultureInfo.CurrentCulture, $"{items.Count} bars. The tallest: {tallest.Detail} Use the arrow keys to read each.");
    }

    private static DependencyProperty Register<T>(string name, T fallback, PropertyChangedCallback? changed = null) =>
        DependencyProperty.Register(
            name,
            typeof(T),
            typeof(MetricBarsChart),
            new FrameworkPropertyMetadata(fallback, FrameworkPropertyMetadataOptions.AffectsRender, changed));
}
