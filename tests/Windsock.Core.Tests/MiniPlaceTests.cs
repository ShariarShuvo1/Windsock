using Windsock.Core.Docking;
using Windsock.Core.Meter;
using Windsock.Core.Settings;
using Windsock.Core.Windowing;
using Xunit;

namespace Windsock.Core.Tests;

/// <summary>
/// Where the compact window opens: where it was left while that is on a
/// monitor, whole, and otherwise above the main taskbar's right end.
/// </summary>
public sealed class MiniPlaceTests
{
    private static readonly ScreenArea Main = new(
        @"\\.\DISPLAY1",
        new PixelRect(0, 0, 1920, 1080),
        new PixelRect(0, 0, 1920, 1032),
        Primary: true,
        Dpi: 96);

    private static readonly ScreenArea Left = new(
        @"\\.\DISPLAY2",
        new PixelRect(-2560, 0, 0, 1440),
        new PixelRect(-2560, 0, 0, 1392),
        Primary: false,
        Dpi: 144);

    private static MeterSpot Spot(string monitor, int x, int y) => new() { Monitor = monitor, X = x, Y = y };

    [Fact]
    public void NothingKept_OpensAboveTheMainTaskbarsRightEnd()
    {
        PixelRect chosen = MiniPlace.Choose(null, [Left, Main], 240, 72);

        Assert.Equal(PixelRect.FromSize(1920 - 240 - 16, 1032 - 72 - 16, 240, 72), chosen);
    }

    [Fact]
    public void TheDefaultGap_GrowsWithTheScale()
    {
        ScreenArea large = Main with { Dpi = 192 };

        PixelRect chosen = MiniPlace.Choose(null, [large], 480, 144);

        Assert.Equal(1920 - 480 - 32, chosen.Left);
        Assert.Equal(1032 - 144 - 32, chosen.Top);
    }

    [Fact]
    public void AKeptPlace_IsWhereItOpens()
    {
        PixelRect chosen = MiniPlace.Choose(Spot(@"\\.\DISPLAY2", -900, 300), [Main, Left], 360, 108);

        Assert.Equal(PixelRect.FromSize(-900, 300, 360, 108), chosen);
    }

    [Fact]
    public void AKeptPlaceHalfOffTheEdge_IsPulledInWhole()
    {
        PixelRect chosen = MiniPlace.Choose(Spot(@"\\.\DISPLAY1", 1800, 1000), [Main], 240, 72);

        Assert.Equal(PixelRect.FromSize(1920 - 240, 1032 - 72, 240, 72), chosen);
    }

    [Fact]
    public void AMonitorRenamedInPlace_StillHoldsTheKeptPlace()
    {
        // Windows renumbers displays when they are plugged in again.
        PixelRect chosen = MiniPlace.Choose(Spot(@"\\.\DISPLAY7", 400, 200), [Main], 240, 72);

        Assert.Equal(PixelRect.FromSize(400, 200, 240, 72), chosen);
    }

    [Fact]
    public void AMonitorThatHasGone_SendsItHome()
    {
        PixelRect chosen = MiniPlace.Choose(Spot(@"\\.\DISPLAY2", -900, 300), [Main], 240, 72);

        Assert.Equal(PixelRect.FromSize(1920 - 240 - 16, 1032 - 72 - 16, 240, 72), chosen);
    }

    [Fact]
    public void BehindTheTaskbar_IsNotOnTheWorkingArea()
    {
        PixelRect chosen = MiniPlace.Choose(Spot(@"\\.\DISPLAY1", 100, 1050), [Main], 240, 72);

        Assert.Equal(1032 - 72 - 16, chosen.Top);
    }

    [Fact]
    public void NoMonitorsKnown_KeepsWhatItHas()
    {
        Assert.Equal(PixelRect.FromSize(10, 20, 240, 72), MiniPlace.Choose(Spot("x", 10, 20), [], 240, 72));
        Assert.Equal(PixelRect.FromSize(0, 0, 240, 72), MiniPlace.Choose(null, [], 240, 72));
    }

    [Fact]
    public void LargerThanTheMonitor_KeepsItsTopLeftOnIt()
    {
        PixelRect chosen = MiniPlace.Choose(null, [Main], 4000, 2000);

        Assert.Equal(0, chosen.Left);
        Assert.Equal(0, chosen.Top);
    }
}
