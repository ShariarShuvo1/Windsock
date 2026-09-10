using System.Windows;
using System.Windows.Controls;

namespace Windsock.App.Controls;

/// <summary>
/// Fills the space it is given without ever asking for more.
/// </summary>
public sealed class FillPanel : Decorator
{
    public static readonly DependencyProperty MinimumHeightProperty =
        DependencyProperty.Register(
            nameof(MinimumHeight),
            typeof(double),
            typeof(FillPanel),
            new FrameworkPropertyMetadata(120d, FrameworkPropertyMetadataOptions.AffectsMeasure));

    private const double Tolerance = 0.5;
    private double _available;

    /// <summary>
    /// The only height this panel ever asks for.
    /// </summary>
    public double MinimumHeight
    {
        get => (double)GetValue(MinimumHeightProperty);
        set => SetValue(MinimumHeightProperty, value);
    }

    protected override Size MeasureOverride(Size constraint)
    {
        double floor = Math.Min(MinimumHeight, constraint.Height);
        double available = _available > 0 ? _available : floor;
        Child?.Measure(new Size(constraint.Width, available));

        double width = double.IsInfinity(constraint.Width)
            ? Child?.DesiredSize.Width ?? 0
            : constraint.Width;
        return new Size(width, floor);
    }

    protected override Size ArrangeOverride(Size arrangeSize)
    {
        if (Math.Abs(arrangeSize.Height - _available) > Tolerance)
        {
            _available = arrangeSize.Height;
            InvalidateMeasure();
        }

        Child?.Arrange(new Rect(arrangeSize));
        return arrangeSize;
    }
}
