using Windsock.Core.Docking;
using Windsock.Core.Settings;
using Xunit;

namespace Windsock.Core.Tests;

/// <summary>
/// Where a floating meter goes once someone has dragged it.
/// </summary>
public sealed class FloatingPlacementTests
{
    private static readonly PixelRect Main = new(0, 0, 1920, 1080);
    private static readonly PixelRect Left = new(-2560, 0, 0, 1440);

    [Fact]
    public void ASpotOnAMonitor_IsKeptAsItIs()
    {
        MeterSpot spot = new() { Monitor = @"\\.\DISPLAY1", X = 800, Y = 40 };

        Assert.Equal(new PixelRect(800, 40, 888, 82), FloatingPlacement.Restore(spot, 88, 42, [Main, Left]));
    }

    /// <summary>
    /// The meter grew or the monitor shrank since: kept whole on the monitor
    /// holding its middle, moved as little as it takes.
    /// </summary>
    [Fact]
    public void ASpotHangingOffTheEdge_IsBroughtBackOn()
    {
        MeterSpot spot = new() { Monitor = @"\\.\DISPLAY1", X = 1870, Y = 1050 };

        Assert.Equal(new PixelRect(1832, 1038, 1920, 1080), FloatingPlacement.Restore(spot, 88, 42, [Main]));
    }

    [Fact]
    public void ASpotOnAMonitorLeftOfTheMainOne_IsKept()
    {
        MeterSpot spot = new() { Monitor = @"\\.\DISPLAY2", X = -400, Y = 1380 };

        Assert.Equal(new PixelRect(-400, 1380, -312, 1422), FloatingPlacement.Restore(spot, 88, 42, [Main, Left]));
    }

    /// <summary>
    /// The display it was left on has gone: nothing, and the meter goes back
    /// to its taskbar rather than somewhere nobody can see.
    /// </summary>
    [Fact]
    public void ASpotOnAMonitorThatHasGone_IsNotUsed()
    {
        MeterSpot spot = new() { Monitor = @"\\.\DISPLAY2", X = -400, Y = 1380 };

        Assert.Null(FloatingPlacement.Restore(spot, 88, 42, [Main]));
    }

    [Theory]
    [InlineData(1619, 1035, 96, true)]
    [InlineData(1630, 1045, 96, true)]
    [InlineData(1640, 1035, 96, false)]
    [InlineData(1619, 1012, 96, false)]
    [InlineData(1641, 1035, 144, true)]
    [InlineData(1645, 1035, 144, false)]
    public void DroppedNearItsOwnSpot_TheMoveIsForgotten(int left, int top, int dpi, bool home)
    {
        PixelRect spot = new(1619, 1035, 1707, 1077);

        Assert.Equal(home, FloatingPlacement.IsHome(PixelRect.FromSize(left, top, 88, 42), spot, dpi));
    }

    /// <summary>
    /// Over an auto-hiding bar: on screen while the bar is, off it once the
    /// bar has slid far enough to take the spot past the monitor's edge.
    /// </summary>
    [Theory]
    [InlineData(1035, true)]
    [InlineData(1038, true)]
    [InlineData(1039, false)]
    [InlineData(1081, false)]
    public void OverAnAutoHidingBar_TheMeterShowsOnlyWhileItsSpotIsOnScreen(int top, bool shown)
    {
        Assert.Equal(shown, FloatingPlacement.IsShown(PixelRect.FromSize(1619, top, 88, 42), Main));
    }
}
