using System.Windows;
using System.Windows.Controls;

namespace Windsock.App.Controls;

/// <summary>
/// Lays two children out side by side while there is room, and stacks them
/// otherwise.
/// </summary>
public sealed class AdaptiveRow : Panel
{
    public static readonly DependencyProperty BreakpointProperty =
        DependencyProperty.Register(
            nameof(Breakpoint),
            typeof(double),
            typeof(AdaptiveRow),
            new FrameworkPropertyMetadata(640d, FrameworkPropertyMetadataOptions.AffectsMeasure));

    public static readonly DependencyProperty SpacingProperty =
        DependencyProperty.Register(
            nameof(Spacing),
            typeof(double),
            typeof(AdaptiveRow),
            new FrameworkPropertyMetadata(24d, FrameworkPropertyMetadataOptions.AffectsMeasure));

    public static readonly DependencyProperty LeadWidthProperty =
        DependencyProperty.Register(
            nameof(LeadWidth),
            typeof(double),
            typeof(AdaptiveRow),
            new FrameworkPropertyMetadata(double.NaN, FrameworkPropertyMetadataOptions.AffectsMeasure));

    public static readonly DependencyProperty LeadFillsProperty =
        DependencyProperty.Register(
            nameof(LeadFills),
            typeof(bool),
            typeof(AdaptiveRow),
            new FrameworkPropertyMetadata(false, FrameworkPropertyMetadataOptions.AffectsMeasure));

    private static readonly DependencyPropertyKey IsRowPropertyKey =
        DependencyProperty.RegisterReadOnly(
            nameof(IsRow),
            typeof(bool),
            typeof(AdaptiveRow),
            new PropertyMetadata(true));

    /// <summary>
    /// Whether the children are currently laid out as a row.
    /// </summary>
    public static readonly DependencyProperty IsRowProperty = IsRowPropertyKey.DependencyProperty;
    private bool _isRow = true;

    public bool IsRow
    {
        get => (bool)GetValue(IsRowProperty);
        private set => SetValue(IsRowPropertyKey, value);
    }

    /// <summary>Width at or above which the children sit in a row.</summary>
    public double Breakpoint
    {
        get => (double)GetValue(BreakpointProperty);
        set => SetValue(BreakpointProperty, value);
    }

    /// <summary>Gap between the two children, in either orientation.</summary>
    public double Spacing
    {
        get => (double)GetValue(SpacingProperty);
        set => SetValue(SpacingProperty, value);
    }

    /// <summary>
    /// Width given to the first child in row orientation. <see cref="double.NaN"/>
    /// uses that child's own desired width. Ignored when <see cref="LeadFills"/>
    /// is set.
    /// </summary>
    public double LeadWidth
    {
        get => (double)GetValue(LeadWidthProperty);
        set => SetValue(LeadWidthProperty, value);
    }

    /// <summary>
    /// Whether the lead takes whatever room the second child does not need.
    /// </summary>
    public bool LeadFills
    {
        get => (bool)GetValue(LeadFillsProperty);
        set => SetValue(LeadFillsProperty, value);
    }

    protected override Size MeasureOverride(Size availableSize)
    {
        if (InternalChildren.Count == 0)
        {
            return default;
        }

        UIElement lead = InternalChildren[0];

        if (InternalChildren.Count == 1)
        {
            lead.Measure(availableSize);
            return lead.DesiredSize;
        }

        UIElement fill = InternalChildren[1];
        double available = availableSize.Width;
        _isRow = double.IsInfinity(available) || available >= Breakpoint;
        if (IsRow != _isRow)
        {
            IsRow = _isRow;
        }

        if (_isRow && LeadFills && !double.IsInfinity(available))
        {
            // The second child asks first, and the first child has the rest.
            fill.Measure(new Size(available, availableSize.Height));

            double taken = Math.Min(fill.DesiredSize.Width, available);
            double room = Math.Max(0, available - taken - Spacing);

            lead.Measure(new Size(room, availableSize.Height));

            return new Size(available, Math.Max(lead.DesiredSize.Height, fill.DesiredSize.Height));
        }

        if (_isRow)
        {
            double leadBudget = double.IsNaN(LeadWidth) ? available : LeadWidth;
            lead.Measure(new Size(leadBudget, availableSize.Height));

            double leadWidth = double.IsNaN(LeadWidth) ? lead.DesiredSize.Width : LeadWidth;
            double remaining = double.IsInfinity(available)
                ? double.PositiveInfinity
                : Math.Max(0, available - leadWidth - Spacing);

            fill.Measure(new Size(remaining, availableSize.Height));

            double width = double.IsInfinity(available)
                ? leadWidth + Spacing + fill.DesiredSize.Width
                : available;

            return new Size(width, Math.Max(lead.DesiredSize.Height, fill.DesiredSize.Height));
        }

        lead.Measure(new Size(available, double.PositiveInfinity));

        double heightLeft = double.IsInfinity(availableSize.Height)
            ? double.PositiveInfinity
            : Math.Max(0, availableSize.Height - lead.DesiredSize.Height - Spacing);

        fill.Measure(new Size(available, heightLeft));

        return new Size(available, lead.DesiredSize.Height + Spacing + fill.DesiredSize.Height);
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        if (InternalChildren.Count == 0)
        {
            return finalSize;
        }

        UIElement lead = InternalChildren[0];

        if (InternalChildren.Count == 1)
        {
            lead.Arrange(new Rect(finalSize));
            return finalSize;
        }

        UIElement fill = InternalChildren[1];

        if (_isRow && LeadFills)
        {
            double taken = Math.Min(fill.DesiredSize.Width, finalSize.Width);
            double room = Math.Max(0, finalSize.Width - taken - Spacing);

            lead.Arrange(new Rect(0, 0, room, finalSize.Height));
            fill.Arrange(new Rect(finalSize.Width - taken, 0, taken, finalSize.Height));
            return finalSize;
        }

        if (_isRow)
        {
            double leadWidth = double.IsNaN(LeadWidth) ? lead.DesiredSize.Width : LeadWidth;
            leadWidth = Math.Min(leadWidth, finalSize.Width);
            double fillWidth = Math.Max(0, finalSize.Width - leadWidth - Spacing);

            lead.Arrange(new Rect(0, 0, leadWidth, finalSize.Height));
            fill.Arrange(new Rect(leadWidth + Spacing, 0, fillWidth, finalSize.Height));
            return finalSize;
        }

        double leadHeight = Math.Min(lead.DesiredSize.Height, finalSize.Height);
        double fillHeight = Math.Max(0, finalSize.Height - leadHeight - Spacing);

        lead.Arrange(new Rect(0, 0, finalSize.Width, leadHeight));
        fill.Arrange(new Rect(0, leadHeight + Spacing, finalSize.Width, fillHeight));
        return finalSize;
    }
}
