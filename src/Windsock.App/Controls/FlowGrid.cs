using System.Windows;
using System.Windows.Controls;

namespace Windsock.App.Controls;

/// <summary>
/// Lays its children out in as many equal columns as will fit.
/// </summary>
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

    /// <summary>How narrow a column may become before there is one fewer.</summary>
    public double MinimumColumnWidth
    {
        get => (double)GetValue(MinimumColumnWidthProperty);
        set => SetValue(MinimumColumnWidthProperty, value);
    }

    /// <summary>
    /// Whether every row must be full.
    /// </summary>
    public bool IsBalanced
    {
        get => (bool)GetValue(IsBalancedProperty);
        set => SetValue(IsBalancedProperty, value);
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

        if (count == 0)
        {
            return default;
        }

        int columns = Columns(availableSize.Width, count);
        double width = ColumnWidth(availableSize.Width, columns);
        double tallest = 0;
        foreach (UIElement child in InternalChildren)
        {
            child.Measure(new Size(width, double.PositiveInfinity));
        }

        foreach (UIElement child in shown)
        {
            tallest = Math.Max(tallest, child.DesiredSize.Height);
        }

        int rows = (count + columns - 1) / columns;

        return new Size(
            double.IsInfinity(availableSize.Width) ? width * columns : availableSize.Width,
            tallest * rows);
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

        for (int index = 0; index < count; index++)
        {
            int column = index % columns;
            int row = index / columns;

            shown[index].Arrange(
                new Rect(column * width, row * tallest, width, tallest));
        }

        return finalSize;
    }

    private int Columns(double available, int count)
    {
        if (double.IsInfinity(available) || available <= 0 || MinimumColumnWidth <= 0)
        {
            return 1;
        }

        int fits = Math.Clamp((int)(available / MinimumColumnWidth), 1, count);

        return IsBalanced ? Even(fits, count) : fits;
    }

    private static int Even(int fits, int count)
    {
        for (int columns = fits; columns > 1; columns--)
        {
            if (count % columns == 0)
            {
                return columns;
            }
        }

        return 1;
    }

    private double ColumnWidth(double available, int columns) =>
        double.IsInfinity(available) || available <= 0
            ? Math.Max(MinimumColumnWidth, 1)
            : available / columns;
}
