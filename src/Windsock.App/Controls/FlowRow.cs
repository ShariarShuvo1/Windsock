using System.Windows;
using System.Windows.Controls;

namespace Windsock.App.Controls;

/// <summary>
/// A toolbar row that wraps onto further lines when it runs out of width, with
/// one gap between every control and every line.
/// </summary>
/// <remarks>
/// <para>
/// This replaces a <see cref="WrapPanel"/> whose gaps were margins on the
/// controls. A margin travels with its control, so whichever control wrapped
/// carried its left margin to the start of the new line and stood indented
/// under the one above it, and a row of controls each spaced by hand never
/// agreed on one gap. Here the gap belongs to the row: a control that starts a
/// line starts at the edge.
/// </para>
/// <para>
/// A child that is itself a <see cref="FlowRow"/> wraps as a group: it moves to
/// a line of its own before it breaks, and breaks only when a whole line is too
/// narrow for it. That is what keeps related controls together - the filters
/// with the filters, the actions with the actions.
/// </para>
/// <para>
/// Two attached settings. <see cref="TrailingProperty"/> sends a child, and
/// whatever follows it on its line, to the far end - but only on a line it
/// shares, where the space before it says "these are a separate set". A child
/// that wrapped starts its line at the left like everything else, rather than
/// hanging off the right edge beneath controls aligned to the left.
/// <see cref="FillProperty"/> lets a child take its line's spare width, up to its
/// own maximum, so a search box alone on a line runs the width of it.
/// </para>
/// </remarks>
public sealed class FlowRow : Panel
{
    private const double Tolerance = 0.5;

    public static readonly DependencyProperty SpacingProperty =
        DependencyProperty.Register(
            nameof(Spacing),
            typeof(double),
            typeof(FlowRow),
            new FrameworkPropertyMetadata(8d, FrameworkPropertyMetadataOptions.AffectsMeasure));

    public static readonly DependencyProperty LineSpacingProperty =
        DependencyProperty.Register(
            nameof(LineSpacing),
            typeof(double),
            typeof(FlowRow),
            new FrameworkPropertyMetadata(8d, FrameworkPropertyMetadataOptions.AffectsMeasure));

    public static readonly DependencyProperty GroupSpacingProperty =
        DependencyProperty.Register(
            nameof(GroupSpacing),
            typeof(double),
            typeof(FlowRow),
            new FrameworkPropertyMetadata(16d, FrameworkPropertyMetadataOptions.AffectsMeasure));

    public static readonly DependencyProperty TrailingProperty =
        DependencyProperty.RegisterAttached(
            "Trailing",
            typeof(bool),
            typeof(FlowRow),
            new FrameworkPropertyMetadata(false, FrameworkPropertyMetadataOptions.AffectsParentArrange));

    public static readonly DependencyProperty FillProperty =
        DependencyProperty.RegisterAttached(
            "Fill",
            typeof(bool),
            typeof(FlowRow),
            new FrameworkPropertyMetadata(false, FrameworkPropertyMetadataOptions.AffectsParentArrange));

    /// <summary>The gap between neighbouring controls on a line.</summary>
    public double Spacing
    {
        get => (double)GetValue(SpacingProperty);
        set => SetValue(SpacingProperty, value);
    }

    /// <summary>The gap between one line and the next.</summary>
    public double LineSpacing
    {
        get => (double)GetValue(LineSpacingProperty);
        set => SetValue(LineSpacingProperty, value);
    }

    /// <summary>
    /// The wider gap either side of a group, and before a trailing child, so
    /// that two sets sharing a line still read as two sets.
    /// </summary>
    public double GroupSpacing
    {
        get => (double)GetValue(GroupSpacingProperty);
        set => SetValue(GroupSpacingProperty, value);
    }

    /// <summary>Whether the last measure needed more than one line.</summary>
    private bool _wrapped;

    public static bool GetTrailing(DependencyObject element)
    {
        ArgumentNullException.ThrowIfNull(element);
        return (bool)element.GetValue(TrailingProperty);
    }

    public static void SetTrailing(DependencyObject element, bool value)
    {
        ArgumentNullException.ThrowIfNull(element);
        element.SetValue(TrailingProperty, value);
    }

    public static bool GetFill(DependencyObject element)
    {
        ArgumentNullException.ThrowIfNull(element);
        return (bool)element.GetValue(FillProperty);
    }

    public static void SetFill(DependencyObject element, bool value)
    {
        ArgumentNullException.ThrowIfNull(element);
        element.SetValue(FillProperty, value);
    }

    protected override Size MeasureOverride(Size availableSize)
    {
        List<UIElement> shown = Shown();

        foreach (UIElement child in shown)
        {
            child.Measure(new Size(availableSize.Width, double.PositiveInfinity));
        }

        double widest = 0;
        double height = 0;
        List<Line> lines = Break(shown, availableSize.Width);

        foreach (Line line in lines)
        {
            widest = Math.Max(widest, line.Width);
            height += line.Height;
        }

        height += Math.Max(0, lines.Count - 1) * LineSpacing;
        _wrapped = lines.Count > 1;

        return new Size(
            double.IsInfinity(availableSize.Width) ? widest : Math.Min(widest, availableSize.Width),
            height);
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        List<UIElement> shown = Shown();
        double top = 0;

        foreach (Line line in Break(shown, finalSize.Width))
        {
            double spare = Math.Max(0, finalSize.Width - line.Width);
            double[] widths = new double[line.Count];

            for (int each = 0; each < line.Count; each++)
            {
                widths[each] = Math.Min(shown[line.Start + each].DesiredSize.Width, finalSize.Width);
            }

            spare = Grow(shown, line, widths, spare);

            double left = 0;
            bool pushed = false;

            for (int each = 0; each < line.Count; each++)
            {
                UIElement child = shown[line.Start + each];

                if (each > 0)
                {
                    left += Gap(shown[line.Start + each - 1], child);

                    // Only where something came before it on this line.
                    if (!pushed && GetTrailing(child))
                    {
                        left += spare;
                        pushed = true;
                    }
                }

                double tall = child.DesiredSize.Height;
                child.Arrange(new Rect(left, top + ((line.Height - tall) / 2), widths[each], tall));
                left += widths[each];
            }

            top += line.Height + LineSpacing;
        }

        return finalSize;
    }

    /// <summary>
    /// Shares a line's spare width among the children that asked for it, each
    /// up to its own maximum, and returns what is still left.
    /// </summary>
    private static double Grow(List<UIElement> shown, Line line, double[] widths, double spare)
    {
        int growing = 0;

        for (int each = 0; each < line.Count; each++)
        {
            if (GetFill(shown[line.Start + each]))
            {
                growing++;
            }
        }

        if (growing == 0 || spare <= 0)
        {
            return spare;
        }

        double share = spare / growing;

        for (int each = 0; each < line.Count; each++)
        {
            UIElement child = shown[line.Start + each];

            if (!GetFill(child))
            {
                continue;
            }

            double most = child is FrameworkElement element && !double.IsInfinity(element.MaxWidth)
                ? element.MaxWidth + element.Margin.Left + element.Margin.Right
                : double.PositiveInfinity;

            double grown = Math.Min(widths[each] + share, Math.Max(widths[each], most));
            spare -= grown - widths[each];
            widths[each] = grown;
        }

        return Math.Max(0, spare);
    }

    /// <summary>The gap between two neighbours on a line.</summary>
    private double Gap(UIElement before, UIElement after) =>
        before is FlowRow || after is FlowRow || GetTrailing(after) ? GroupSpacing : Spacing;

    /// <summary>
    /// Cuts the children into lines, each as full as it will go.
    /// </summary>
    /// <remarks>
    /// A group that could not fit on one line gets its lines to itself. Left to
    /// share, its first line sat level with the middle of a neighbour, which is
    /// a control floating beside a block rather than a row of controls.
    /// </remarks>
    private List<Line> Break(List<UIElement> shown, double limit)
    {
        List<Line> lines = [];

        int start = 0;
        int count = 0;
        double width = 0;
        double height = 0;
        bool closeAfter = false;

        for (int index = 0; index < shown.Count; index++)
        {
            UIElement child = shown[index];
            Size size = child.DesiredSize;
            bool alone = child is FlowRow { _wrapped: true };
            double gap = count > 0 ? Gap(shown[index - 1], child) : 0;

            if (count > 0 && (alone || closeAfter || width + gap + size.Width > limit + Tolerance))
            {
                lines.Add(new Line(start, count, width, height));
                start = index;
                count = 0;
                width = 0;
                height = 0;
                gap = 0;
            }

            width += gap + size.Width;
            height = Math.Max(height, size.Height);
            count++;
            closeAfter = alone;
        }

        if (count > 0)
        {
            lines.Add(new Line(start, count, width, height));
        }

        return lines;
    }

    /// <summary>
    /// The children taking part. A collapsed child asked for no room and gets
    /// none, gap included; one that is only hidden keeps its place.
    /// </summary>
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

    private readonly record struct Line(int Start, int Count, double Width, double Height);
}
