using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace Windsock.App.Controls;

/// <summary>
/// Lays its children out in as many equal columns as will fit.
/// </summary>
/// <remarks>
/// The gap between cells belongs to the grid rather than to each child. A
/// margin travels with its child, so the last cell in every row carried a
/// gutter it had nothing to be apart from and the grid had to be pulled back
/// by a negative margin to end flush; with the gap here, the outer cells touch
/// the grid's edges on their own.
/// </remarks>
public sealed class FlowGrid : Panel
{
    public static readonly DependencyProperty MinimumColumnWidthProperty = DependencyProperty.Register(
        nameof(MinimumColumnWidth),
        typeof(double),
        typeof(FlowGrid),
        new FrameworkPropertyMetadata(180d, FrameworkPropertyMetadataOptions.AffectsMeasure));

    public static readonly DependencyProperty IsBalancedProperty = DependencyProperty.Register(
        nameof(IsBalanced),
        typeof(bool),
        typeof(FlowGrid),
        new FrameworkPropertyMetadata(false, FrameworkPropertyMetadataOptions.AffectsMeasure));

    public static readonly DependencyProperty SpacingProperty = DependencyProperty.Register(
        nameof(Spacing),
        typeof(double),
        typeof(FlowGrid),
        new FrameworkPropertyMetadata(0d, FrameworkPropertyMetadataOptions.AffectsMeasure));

    public static readonly DependencyProperty DividerBrushProperty = DependencyProperty.Register(
        nameof(DividerBrush),
        typeof(Brush),
        typeof(FlowGrid),
        new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty MaximumColumnsProperty = DependencyProperty.Register(
        nameof(MaximumColumns),
        typeof(int),
        typeof(FlowGrid),
        new FrameworkPropertyMetadata(int.MaxValue, FrameworkPropertyMetadataOptions.AffectsMeasure));

    /// <summary>How narrow a column may become before there is one fewer.</summary>
    public double MinimumColumnWidth
    {
        get => (double)GetValue(MinimumColumnWidthProperty);
        set => SetValue(MinimumColumnWidthProperty, value);
    }

    /// <summary>
    /// Whether no cell may be left alone on a row of its own. When the count
    /// does not divide evenly, the last row's cells share the whole width
    /// between them, so the grid still ends flush with the rows above it.
    /// </summary>
    public bool IsBalanced
    {
        get => (bool)GetValue(IsBalancedProperty);
        set => SetValue(IsBalancedProperty, value);
    }

    /// <summary>The gap between cells, across and down.</summary>
    public double Spacing
    {
        get => (double)GetValue(SpacingProperty);
        set => SetValue(SpacingProperty, value);
    }

    /// <summary>
    /// When set, a hairline between neighbouring cells and between rows, so a
    /// row of figures reads as one strip divided into parts rather than as a
    /// row of separate boxes. Meant for a grid with no spacing.
    /// </summary>
    public Brush? DividerBrush
    {
        get => (Brush?)GetValue(DividerBrushProperty);
        set => SetValue(DividerBrushProperty, value);
    }

    /// <summary>The most columns there may be, however wide the grid is.</summary>
    public int MaximumColumns
    {
        get => (int)GetValue(MaximumColumnsProperty);
        set => SetValue(MaximumColumnsProperty, value);
    }

    private List<UIElement> Shown()
    {
        List<UIElement> shown = new(InternalChildren.Count);

        foreach (UIElement child in InternalChildren)
        {
            if (child.Visibility != Visibility.Collapsed)
            {
                shown.Add(child);
            }
        }

        return shown;
    }

    protected override Size MeasureOverride(Size availableSize)
    {
        List<UIElement> shown = Shown();
        int count = shown.Count;

        foreach (UIElement child in InternalChildren)
        {
            if (child.Visibility == Visibility.Collapsed)
            {
                child.Measure(default);
            }
        }

        if (count == 0)
        {
            return default;
        }

        int columns = Columns(availableSize.Width, count);
        double width = ColumnWidth(availableSize.Width, columns);
        double tallest = 0;

        for (int index = 0; index < count; index++)
        {
            shown[index].Measure(new Size(CellWidth(availableSize.Width, width, columns, count, index), double.PositiveInfinity));
            tallest = Math.Max(tallest, shown[index].DesiredSize.Height);
        }

        int rows = (count + columns - 1) / columns;

        return new Size(
            double.IsInfinity(availableSize.Width) ? (width * columns) + (Spacing * (columns - 1)) : availableSize.Width,
            (tallest * rows) + (Spacing * (rows - 1)));
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        List<UIElement> shown = Shown();
        int count = shown.Count;

        if (count == 0)
        {
            return finalSize;
        }

        int columns = Columns(finalSize.Width, count);
        double width = ColumnWidth(finalSize.Width, columns);
        double tallest = 0;

        foreach (UIElement child in shown)
        {
            tallest = Math.Max(tallest, child.DesiredSize.Height);
        }

        int lastRowStart = (count - 1) / columns * columns;
        _rules.Clear();

        for (int index = 0; index < count; index++)
        {
            int column = index % columns;
            int row = index / columns;
            double cell = CellWidth(finalSize.Width, width, columns, count, index);
            double left = index >= lastRowStart && IsBalanced
                ? column * (cell + Spacing)
                : column * (width + Spacing);
            double top = row * (tallest + Spacing);

            shown[index].Arrange(new Rect(left, top, cell, tallest));

            if (column > 0)
            {
                _rules.Add((new Point(left, top), new Point(left, top + tallest)));
            }

            if (row > 0 && column == 0)
            {
                _rules.Add((new Point(0, top), new Point(finalSize.Width, top)));
            }
        }

        // Which cells are showing can change the lines without changing the
        // grid's own size.
        InvalidateVisual();
        return finalSize;
    }

    private readonly List<(Point From, Point To)> _rules = [];

    protected override void OnRender(DrawingContext dc)
    {
        ArgumentNullException.ThrowIfNull(dc);
        base.OnRender(dc);

        if (DividerBrush is not { } brush || _rules.Count == 0)
        {
            return;
        }

        DpiScale dpi = VisualTreeHelper.GetDpi(this);
        var pen = new Pen(brush, 1 / dpi.DpiScaleX);
        pen.Freeze();

        foreach ((Point from, Point to) in _rules)
        {
            // Snapped to a device pixel, so each rule is one crisp line.
            if (from.X == to.X)
            {
                double x = (Math.Round(from.X * dpi.DpiScaleX) / dpi.DpiScaleX) + (0.5 / dpi.DpiScaleX);
                dc.DrawLine(pen, new Point(x, from.Y), new Point(x, to.Y));
            }
            else
            {
                double y = (Math.Round(from.Y * dpi.DpiScaleY) / dpi.DpiScaleY) + (0.5 / dpi.DpiScaleY);
                dc.DrawLine(pen, new Point(from.X, y), new Point(to.X, y));
            }
        }
    }

    /// <summary>
    /// How wide one cell is. Only a balanced grid's short last row differs:
    /// its cells share the row between them.
    /// </summary>
    private double CellWidth(double available, double width, int columns, int count, int index)
    {
        int lastRowStart = (count - 1) / columns * columns;
        int lastRowCount = count - lastRowStart;

        if (!IsBalanced || index < lastRowStart || lastRowCount == columns || double.IsInfinity(available) || available <= 0)
        {
            return width;
        }

        return Math.Max(0, (available - (Spacing * (lastRowCount - 1))) / lastRowCount);
    }

    private int Columns(double available, int count)
    {
        if (double.IsInfinity(available) || available <= 0 || MinimumColumnWidth <= 0)
        {
            return 1;
        }

        int fits = Math.Clamp(
            (int)((available + Spacing) / (MinimumColumnWidth + Spacing)),
            1,
            Math.Max(1, Math.Min(count, MaximumColumns)));

        return IsBalanced ? Balanced(fits, count) : fits;
    }

    /// <summary>
    /// Full rows first: six figures with room for four or five go three over
    /// three rather than four over two, since a short row stretched under full
    /// ones puts its hairlines where the row above has none. Only a count that
    /// divides by no more than half the columns that fit gives that up.
    ///
    /// Otherwise the most columns that leave no cell alone on its row: five
    /// cards go five across, then three over two, rather than four with one left
    /// over. When the only way to avoid a lone cell is a single column, the lone
    /// cell is the lesser evil - it takes its row's whole width - because five
    /// cards one above the other is a screen and a half of scrolling for a glance.
    /// </summary>
    private static int Balanced(int fits, int count)
    {
        for (int columns = fits; columns > 1 && columns * 2 >= fits; columns--)
        {
            if (count % columns == 0)
            {
                return columns;
            }
        }

        for (int columns = fits; columns > 1; columns--)
        {
            if (count % columns != 1)
            {
                return columns;
            }
        }

        return fits;
    }

    private double ColumnWidth(double available, int columns) =>
        double.IsInfinity(available) || available <= 0
            ? Math.Max(MinimumColumnWidth, 1)
            : Math.Max(0, (available - (Spacing * (columns - 1))) / columns);
}
