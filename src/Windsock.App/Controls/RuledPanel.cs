using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace Windsock.App.Controls;

/// <summary>
/// Stacks rows one above the other with a hairline between each pair that is
/// showing.
/// </summary>
/// <remarks>
/// A rule drawn by each row, above or below itself, doubles up or goes missing
/// the moment a row is collapsed: the Store edition takes rows out of the
/// settings, and some rows only appear on request. Drawn by the stack, a line
/// only ever falls between two rows that are both there.
/// </remarks>
public sealed class RuledPanel : Panel
{
    public static readonly DependencyProperty DividerBrushProperty = DependencyProperty.Register(
        nameof(DividerBrush),
        typeof(Brush),
        typeof(RuledPanel),
        new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty DividerInsetProperty = DependencyProperty.Register(
        nameof(DividerInset),
        typeof(double),
        typeof(RuledPanel),
        new FrameworkPropertyMetadata(0d, FrameworkPropertyMetadataOptions.AffectsRender));

    /// <summary>
    /// Whether a row is ruled off from the row above it. Off for a row that
    /// belongs to a heading just above it, so the heading and its first row
    /// read as one group.
    /// </summary>
    public static readonly DependencyProperty RuleAboveProperty = DependencyProperty.RegisterAttached(
        "RuleAbove",
        typeof(bool),
        typeof(RuledPanel),
        new FrameworkPropertyMetadata(true, FrameworkPropertyMetadataOptions.AffectsParentArrange));

    public static bool GetRuleAbove(DependencyObject element)
    {
        ArgumentNullException.ThrowIfNull(element);
        return (bool)element.GetValue(RuleAboveProperty);
    }

    public static void SetRuleAbove(DependencyObject element, bool value)
    {
        ArgumentNullException.ThrowIfNull(element);
        element.SetValue(RuleAboveProperty, value);
    }

    /// <summary>What the line between two rows is drawn in.</summary>
    public Brush? DividerBrush
    {
        get => (Brush?)GetValue(DividerBrushProperty);
        set => SetValue(DividerBrushProperty, value);
    }

    /// <summary>How far in from either side the line stops.</summary>
    public double DividerInset
    {
        get => (double)GetValue(DividerInsetProperty);
        set => SetValue(DividerInsetProperty, value);
    }

    protected override Size MeasureOverride(Size availableSize)
    {
        double width = 0;
        double height = 0;

        foreach (UIElement child in InternalChildren)
        {
            child.Measure(new Size(availableSize.Width, double.PositiveInfinity));

            if (child.Visibility == Visibility.Collapsed)
            {
                continue;
            }

            width = Math.Max(width, child.DesiredSize.Width);
            height += child.DesiredSize.Height;
        }

        return new Size(width, height);
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        double top = 0;

        foreach (UIElement child in InternalChildren)
        {
            if (child.Visibility == Visibility.Collapsed)
            {
                continue;
            }

            child.Arrange(new Rect(0, top, finalSize.Width, child.DesiredSize.Height));
            top += child.DesiredSize.Height;
        }

        // The lines depend on which rows are showing, which can change
        // without the stack's own size changing at all.
        InvalidateVisual();
        return finalSize;
    }

    protected override void OnRender(DrawingContext dc)
    {
        ArgumentNullException.ThrowIfNull(dc);
        base.OnRender(dc);

        if (DividerBrush is not { } brush)
        {
            return;
        }

        double scale = VisualTreeHelper.GetDpi(this).DpiScaleY;
        double thickness = 1 / scale;
        var pen = new Pen(brush, thickness);
        pen.Freeze();

        double top = 0;
        bool first = true;

        foreach (UIElement child in InternalChildren)
        {
            if (child.Visibility == Visibility.Collapsed)
            {
                continue;
            }

            if (!first && GetRuleAbove(child))
            {
                // On a device pixel, so the hairline is one crisp pixel rather
                // than two faint ones.
                double y = (Math.Round(top * scale) / scale) + (thickness / 2);
                dc.DrawLine(pen, new Point(DividerInset, y), new Point(ActualWidth - DividerInset, y));
            }

            first = false;
            top += child.RenderSize.Height;
        }
    }
}
