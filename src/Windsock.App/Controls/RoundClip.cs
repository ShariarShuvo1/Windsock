using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace Windsock.App.Controls;

/// <summary>
/// Clips what it holds to a rectangle with rounded corners the size of itself.
/// </summary>
/// <remarks>
/// A border draws rounded corners but clips its content, if at all, to a
/// square. Anything that scrolls under a rounded surface then shows its own
/// square corners past the surface's round ones.
/// </remarks>
public sealed class RoundClip : Decorator
{
    public static readonly DependencyProperty RadiusProperty = DependencyProperty.Register(
        nameof(Radius),
        typeof(double),
        typeof(RoundClip),
        new FrameworkPropertyMetadata(8d, FrameworkPropertyMetadataOptions.AffectsArrange));

    /// <summary>How round the corners are.</summary>
    public double Radius
    {
        get => (double)GetValue(RadiusProperty);
        set => SetValue(RadiusProperty, value);
    }

    protected override Size ArrangeOverride(Size arrangeSize)
    {
        Size arranged = base.ArrangeOverride(arrangeSize);

        RectangleGeometry shape = new(new Rect(arranged), Radius, Radius);
        shape.Freeze();
        Clip = shape;

        return arranged;
    }
}
