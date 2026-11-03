using Windsock.Core.Docking;
using Windsock.Core.Settings;
using Windsock.Core.Windowing;
using Xunit;

namespace Windsock.Core.Tests;

public sealed class WindowFitTests
{
    private static readonly ScreenArea Main = new(
        @"\\.\DISPLAY1",
        new PixelRect(0, 0, 1920, 1080),
        new PixelRect(0, 0, 1920, 1032),
        Primary: true,
        Dpi: 96);

    private static readonly ScreenArea Side = new(
        @"\\.\DISPLAY2",
        new PixelRect(1920, 0, 4480, 1440),
        new PixelRect(1920, 0, 4480, 1392),
        Primary: false,
        Dpi: 144);

    private static WindowPlace Place(string monitor, int left, int top, int width, int height, int dpi = 96) =>
        new() { Monitor = monitor, Left = left, Top = top, Width = width, Height = height, Dpi = dpi };

    [Fact]
    public void TheLastMonitor_WinsWhenItIsThere()
    {
        WindowSettings window = new();
        window.Remember(Place(Main.Name, 100, 100, 1100, 760));
        window.Remember(Place(Side.Name, 2100, 200, 1650, 1140, 144));

        (WindowPlace place, ScreenArea monitor) = Assert.NotNull(WindowFit.Choose(window, [Main, Side]));

        Assert.Equal(Side, monitor);
        Assert.Equal(2100, place.Left);
    }

    [Fact]
    public void AMonitorThatHasGone_FallsBackToTheMainOnesOwnPlace()
    {
        WindowSettings window = new();
        window.Remember(Place(Main.Name, 100, 100, 1100, 760));
        window.Remember(Place(Side.Name, 2100, 200, 1650, 1140, 144));

        (WindowPlace place, ScreenArea monitor) = Assert.NotNull(WindowFit.Choose(window, [Main]));

        Assert.Equal(Main, monitor);
        Assert.Equal(100, place.Left);
    }

    [Fact]
    public void NoKeptPlaceOnAnyMonitorThere_ChoosesNothing()
    {
        WindowSettings window = new();
        window.Remember(Place(@"\\.\DISPLAY9", 100, 100, 1100, 760));

        Assert.Null(WindowFit.Choose(window, [Main]));
        Assert.Null(WindowFit.Choose(new WindowSettings(), [Main]));
    }

    [Fact]
    public void Fit_KeepsAPlaceThatFits() =>
        Assert.Equal(PixelRect.FromSize(100, 120, 1100, 760), WindowFit.Fit(Place(Main.Name, 100, 120, 1100, 760), Main, 460, 470));

    [Fact]
    public void Fit_NeverLeavesTheWindowOffTheWorkArea()
    {
        PixelRect fitted = WindowFit.Fit(Place(Main.Name, 1500, 900, 1100, 760), Main, 460, 470);

        Assert.True(Main.Work.Contains(fitted));
        Assert.Equal(1100, fitted.Width);
    }

    [Fact]
    public void Fit_ShrinksAWindowLargerThanTheMonitorNowIs()
    {
        PixelRect fitted = WindowFit.Fit(Place(Main.Name, -50, -50, 2600, 1600), Main, 460, 470);

        Assert.Equal(Main.Work, fitted);
    }

    [Fact]
    public void Fit_ScalesByAChangeInDpi()
    {
        // Kept at 96 DPI on what is now a 144 DPI monitor: the same size in
        // inches, so half as many pixels again.
        PixelRect fitted = WindowFit.Fit(Place(Side.Name, 2000, 100, 1000, 700, dpi: 96), Side, 460, 470);

        Assert.Equal(1500, fitted.Width);
        Assert.Equal(1050, fitted.Height);
    }

    [Fact]
    public void Fit_NeverGoesBelowTheWindowsMinimum()
    {
        PixelRect fitted = WindowFit.Fit(Place(Side.Name, 2000, 100, 300, 300, dpi: 144), Side, 460, 470);

        Assert.Equal(690, fitted.Width);
        Assert.Equal(705, fitted.Height);
    }

    [Fact]
    public void Fit_CentresAPlaceLeftOffAMonitorThatMoved()
    {
        PixelRect fitted = WindowFit.Fit(Place(Main.Name, 5000, 3000, 1000, 600), Main, 460, 470);

        Assert.Equal(PixelRect.FromSize(460, 216, 1000, 600), fitted);
    }

    [Fact]
    public void Remember_KeepsOnePlaceAMonitor_NewestFirst()
    {
        WindowSettings window = new();

        for (int each = 0; each < 12; each++)
        {
            window.Remember(Place(@"\\.\DISPLAY" + each.ToString(System.Globalization.CultureInfo.InvariantCulture), each, 0, 800, 600));
        }

        window.Remember(Place(@"\\.\DISPLAY11", 5, 5, 900, 700));

        Assert.Equal(WindowSettings.MostPlaces, window.Places.Count);
        Assert.Equal(900, window.Places[0].Width);
        Assert.Single(window.Places, each => each.Monitor == @"\\.\DISPLAY11");
        Assert.Equal(@"\\.\DISPLAY11", window.LastMonitor);
    }

    [Theory]
    [InlineData("", "history", "history")]
    [InlineData("overview", "history", "overview")]
    [InlineData("", "", null)]
    [InlineData("", "Not a page", null)]
    public void PageToOpen_IsTheChoiceOrTheLastPage(string openOn, string last, string? expected) =>
        Assert.Equal(expected, new WindowSettings { OpenOn = openOn, LastPage = last }.PageToOpen());
}
