using Windsock.Core.Docking;
using Xunit;

namespace Windsock.Core.Tests;

/// <summary>
/// Reading a taskbar's rectangle against its monitor.
/// </summary>
/// <remarks>
/// The bottom bar's numbers were measured on the development machine on
/// 2026-10-05 (Windows 11 25H2, build 26200, one 1920 × 1080 monitor at 96 DPI):
/// Shell_TrayWnd at 0,1032-1920,1080. Microsoft publishes no thickness for the
/// moved or Small bars of 26H2, so the other edges reuse that 48-pixel
/// thickness, and the auto-hidden ones the 2-pixel sliver an auto-hiding bar
/// leaves on screen.
/// </remarks>
public sealed class TaskbarGeometryTests
{
    private static readonly PixelRect Screen = new(0, 0, 1920, 1080);

    [Theory]
    [InlineData(0, 1032, 1920, 1080, TaskbarEdge.Bottom)]
    [InlineData(0, 0, 1920, 48, TaskbarEdge.Top)]
    [InlineData(0, 0, 48, 1080, TaskbarEdge.Left)]
    [InlineData(1872, 0, 1920, 1080, TaskbarEdge.Right)]
    [InlineData(0, 1048, 1920, 1080, TaskbarEdge.Bottom)]
    [InlineData(0, 0, 1920, 32, TaskbarEdge.Top)]
    public void TheEdge_ComesFromWhereTheBarSits(int left, int top, int right, int bottom, TaskbarEdge expected)
    {
        Assert.Equal(expected, TaskbarGeometry.EdgeOf(new PixelRect(left, top, right, bottom), Screen));
    }

    /// <summary>
    /// A second monitor's bar is read against that monitor, wherever the
    /// monitor sits on the desktop - to the left at negative coordinates,
    /// above, or to the right at 150%.
    /// </summary>
    [Theory]
    [InlineData(-1920, 0, 0, 1080, -1920, 1032, 0, 1080, TaskbarEdge.Bottom)]
    [InlineData(0, -1080, 1920, 0, 0, -48, 1920, 0, TaskbarEdge.Bottom)]
    [InlineData(0, -1080, 1920, 0, 0, -1080, 1920, -1032, TaskbarEdge.Top)]
    [InlineData(1920, 0, 4480, 1440, 1920, 1368, 4480, 1440, TaskbarEdge.Bottom)]
    [InlineData(1920, 0, 4480, 1440, 4408, 0, 4480, 1440, TaskbarEdge.Right)]
    public void AnotherMonitorsBar_IsReadAgainstItsOwnMonitor(
        int monitorLeft, int monitorTop, int monitorRight, int monitorBottom,
        int left, int top, int right, int bottom,
        TaskbarEdge expected)
    {
        PixelRect monitor = new(monitorLeft, monitorTop, monitorRight, monitorBottom);

        Assert.Equal(expected, TaskbarGeometry.EdgeOf(new PixelRect(left, top, right, bottom), monitor));
    }

    /// <summary>
    /// Slid away, an auto-hiding bar leaves a sliver on its monitor and the
    /// rest past the edge. The edge must still read right, or the meter would
    /// be laid out for the wrong side as the bar comes back.
    /// </summary>
    [Theory]
    [InlineData(0, 1078, 1920, 1126, TaskbarEdge.Bottom)]
    [InlineData(0, -46, 1920, 2, TaskbarEdge.Top)]
    [InlineData(-46, 0, 2, 1080, TaskbarEdge.Left)]
    [InlineData(1918, 0, 1966, 1080, TaskbarEdge.Right)]
    public void AHiddenBar_StillReadsItsEdge(int left, int top, int right, int bottom, TaskbarEdge expected)
    {
        Assert.Equal(expected, TaskbarGeometry.EdgeOf(new PixelRect(left, top, right, bottom), Screen));
    }

    [Theory]
    [InlineData(0, 1032, 1920, 1080, true)]
    [InlineData(0, 0, 48, 1080, true)]
    [InlineData(0, 1048, 1920, 1080, true)]
    [InlineData(0, 1078, 1920, 1126, true)]
    [InlineData(100, 100, 300, 300, false)]
    [InlineData(0, 0, 1920, 1080, false)]
    [InlineData(0, 1076, 1920, 1080, false)]
    [InlineData(0, 1032, 600, 1080, false)]
    [InlineData(0, 0, 0, 0, false)]
    public void OnlyABarShapedRectangle_IsDockedInto(int left, int top, int right, int bottom, bool recognised)
    {
        Assert.Equal(recognised, TaskbarGeometry.IsRecognised(new PixelRect(left, top, right, bottom), Screen));
    }

    [Fact]
    public void ThicknessAndScale_FollowTheBar()
    {
        TaskbarFrame bottom = new() { Bar = new PixelRect(0, 1008, 1920, 1080), Monitor = Screen, Dpi = 144 };
        TaskbarFrame left = new() { Bar = new PixelRect(0, 0, 62, 1080), Monitor = Screen };

        Assert.Equal(72, bottom.Thickness);
        Assert.Equal(1.5, bottom.Scale);
        Assert.Equal(5, bottom.Pixels(3));
        Assert.Equal(9, bottom.Pixels(6));
        Assert.Equal(62, left.Thickness);
        Assert.True(left.IsVertical);
    }
}
