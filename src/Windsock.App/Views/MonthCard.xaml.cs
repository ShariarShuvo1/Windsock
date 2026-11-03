using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Windsock.App.ViewModels;

namespace Windsock.App.Views;

/// <summary>A month's summary, laid out off screen and drawn to a picture.</summary>
public partial class MonthCard : UserControl
{
    private const double Scale = 2;

    public MonthCard(MonthCardModel model)
    {
        ArgumentNullException.ThrowIfNull(model);

        InitializeComponent();
        DataContext = model;
    }

    /// <summary>Lays the card out and draws it at twice the screen's density.</summary>
    public BitmapSource Render()
    {
        Measure(new Size(Width, double.PositiveInfinity));
        Arrange(new Rect(DesiredSize));
        UpdateLayout();

        int width = (int)Math.Ceiling(ActualWidth * Scale);
        int height = (int)Math.Ceiling(ActualHeight * Scale);
        RenderTargetBitmap bitmap = new(width, height, 96 * Scale, 96 * Scale, PixelFormats.Pbgra32);
        bitmap.Render(this);
        bitmap.Freeze();
        return bitmap;
    }

    /// <summary>Writes the picture as a PNG.</summary>
    public static void Save(BitmapSource picture, string path)
    {
        ArgumentNullException.ThrowIfNull(picture);

        PngBitmapEncoder encoder = new();
        encoder.Frames.Add(BitmapFrame.Create(picture));

        using FileStream stream = File.Create(path);
        encoder.Save(stream);
    }
}
