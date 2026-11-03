using System.Windows;
using System.Windows.Media;
using Windsock.Core.Qr;

namespace Windsock.App.Controls;

/// <summary>
/// Draws a <see cref="QrCode"/>: dark modules on a white field with the four
/// modules of quiet border scanners need, whatever the theme - a phone camera
/// reads dark on light, so the code never takes the page's colours.
/// </summary>
public sealed class QrView : FrameworkElement
{
    private const int QuietZone = 4;

    public static readonly DependencyProperty CodeProperty = DependencyProperty.Register(
        nameof(Code),
        typeof(QrCode),
        typeof(QrView),
        new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));

    public QrView()
    {
        // The control itself on whole pixels, and edges drawn without blending,
        // so every module lands exactly on the device's pixel grid at any scale.
        UseLayoutRounding = true;
        RenderOptions.SetEdgeMode(this, EdgeMode.Aliased);
    }

    /// <summary>The code to draw, or nothing for an empty field.</summary>
    public QrCode? Code
    {
        get => (QrCode?)GetValue(CodeProperty);
        set => SetValue(CodeProperty, value);
    }

    protected override void OnRender(DrawingContext drawingContext)
    {
        ArgumentNullException.ThrowIfNull(drawingContext);

        double side = Math.Min(ActualWidth, ActualHeight);

        if (side <= 0)
        {
            return;
        }

        double left = (ActualWidth - side) / 2;
        double top = (ActualHeight - side) / 2;

        drawingContext.DrawRectangle(Brushes.White, null, new Rect(left, top, side, side));

        if (Code is not { } code)
        {
            return;
        }

        int modules = code.Size + (2 * QuietZone);

        // Whole device pixels a module, so no module blurs into its neighbour.
        double dpi = VisualTreeHelper.GetDpi(this).PixelsPerDip;
        double unit = Math.Floor(side * dpi / modules) / dpi;

        if (unit <= 0)
        {
            return;
        }

        // The first module's corner on a whole device pixel too: at 150 % a
        // centring offset of half a pixel shifted every module off the grid.
        double offsetX = Snap(left + ((side - (unit * modules)) / 2) + (unit * QuietZone), dpi);
        double offsetY = Snap(top + ((side - (unit * modules)) / 2) + (unit * QuietZone), dpi);

        StreamGeometry geometry = new();

        using (StreamGeometryContext context = geometry.Open())
        {
            for (int y = 0; y < code.Size; y++)
            {
                for (int x = 0; x < code.Size; x++)
                {
                    if (!code[x, y])
                    {
                        continue;
                    }

                    double px = offsetX + (x * unit);
                    double py = offsetY + (y * unit);
                    context.BeginFigure(new Point(px, py), isFilled: true, isClosed: true);
                    context.LineTo(new Point(px + unit, py), false, false);
                    context.LineTo(new Point(px + unit, py + unit), false, false);
                    context.LineTo(new Point(px, py + unit), false, false);
                }
            }
        }

        geometry.Freeze();
        drawingContext.DrawGeometry(Brushes.Black, null, geometry);
    }

    private static double Snap(double value, double dpi) => Math.Round(value * dpi) / dpi;
}
