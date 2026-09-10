using System.Windows;
using System.Windows.Controls;

namespace Windsock.App.Controls;

/// <summary>
/// Lays the parts of one reading out in a row, some against the left edge of
/// the place and the rest against the right.
/// </summary>
public sealed class SlotRow : Panel
{
    /// <summary>How many parts from the front sit against the left edge.</summary>
    public static readonly DependencyProperty LeftCountProperty = DependencyProperty.Register(
        nameof(LeftCount),
        typeof(int),
        typeof(SlotRow),
        new FrameworkPropertyMetadata(0, FrameworkPropertyMetadataOptions.AffectsArrange));

    public int LeftCount
    {
        get => (int)GetValue(LeftCountProperty);
        set => SetValue(LeftCountProperty, value);
    }

    protected override Size MeasureOverride(Size availableSize)
    {
        double wanted = 0;
        double tallest = 0;

        foreach (UIElement child in InternalChildren)
        {
            child.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));

            wanted += child.DesiredSize.Width;
            tallest = Math.Max(tallest, child.DesiredSize.Height);
        }

        if (double.IsFinite(availableSize.Width) && wanted > availableSize.Width)
        {
            Squeeze(availableSize.Width, wanted);

            wanted = 0;

            foreach (UIElement child in InternalChildren)
            {
                wanted += child.DesiredSize.Width;
            }
        }

        return new Size(wanted, tallest);
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        int left = Math.Clamp(LeftCount, 0, InternalChildren.Count);
        double at = 0;

        for (int each = 0; each < left; each++)
        {
            UIElement child = InternalChildren[each];

            child.Arrange(new Rect(at, 0, child.DesiredSize.Width, finalSize.Height));
            at += child.DesiredSize.Width;
        }

        // From the far edge back, so the last part ends where the place does.
        double end = finalSize.Width;

        for (int each = InternalChildren.Count - 1; each >= left; each--)
        {
            UIElement child = InternalChildren[each];
            double wide = child.DesiredSize.Width;

            child.Arrange(new Rect(Math.Max(at, end - wide), 0, wide, finalSize.Height));
            end -= wide;
        }

        return finalSize;
    }

    private void Squeeze(double room, double wanted)
    {
        UIElement? widest = null;

        foreach (UIElement child in InternalChildren)
        {
            if (widest is null || child.DesiredSize.Width > widest.DesiredSize.Width)
            {
                widest = child;
            }
        }

        if (widest is null)
        {
            return;
        }

        double spare = Math.Max(0, widest.DesiredSize.Width - (wanted - room));

        widest.Measure(new Size(spare, double.PositiveInfinity));
    }
}
