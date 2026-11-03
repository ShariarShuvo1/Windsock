using Windsock.Core.Docking;
using Xunit;

namespace Windsock.Core.Tests;

/// <summary>
/// Where the meter sits on a taskbar of every shape.
/// </summary>
/// <remarks>
/// The first numbers are the development machine's, measured on 2026-10-05
/// (Windows 11 25H2, build 26200, 1920 × 1080 at 96 DPI, taskbar at the bottom,
/// left-aligned): Shell_TrayWnd 0,1032-1920,1080, TrayNotifyWnd 1713-1920, and
/// through UI Automation Start at 0-55, seven app buttons from 55 to 363, and
/// the notification area's buttons from 1713 to 1920. With the steady width the
/// live meter was 88 pixels wide and docked at 1619,1035-1707,1077: the first
/// test is that, worked out instead of measured. Nothing else could be set up
/// on that machine, so the other edges, sizes and DPIs are built from the same
/// 48-pixel bar scaled or turned, and say so.
/// </remarks>
public sealed class DockGeometryTests
{
    private static readonly PixelRect Screen = new(0, 0, 1920, 1080);

    private static TaskbarFrame Measured(params TaskbarObstacle[] obstacles) => new()
    {
        Bar = new PixelRect(0, 1032, 1920, 1080),
        Monitor = Screen,
        WorkArea = new PixelRect(0, 0, 1920, 1032),
        IsPrimary = true,
        Tray = new PixelRect(1713, 1032, 1920, 1080),
        Obstacles = obstacles,
    };

    private static TaskbarObstacle[] MeasuredButtons() =>
    [
        new(new PixelRect(0, 1032, 55, 1080), ObstacleKind.Fixed),
        .. Enumerable.Range(0, 7).Select(each =>
            new TaskbarObstacle(PixelRect.FromSize(55 + (44 * each), 1032, 44, 48), ObstacleKind.Task)),
        new(new PixelRect(1713, 1032, 1745, 1080), ObstacleKind.Tray),
        new(new PixelRect(1745, 1032, 1777, 1080), ObstacleKind.Tray),
        new(new PixelRect(1781, 1032, 1805, 1080), ObstacleKind.Tray),
        new(new PixelRect(1809, 1032, 1833, 1080), ObstacleKind.Tray),
        new(new PixelRect(1841, 1032, 1904, 1080), ObstacleKind.Tray),
        new(new PixelRect(1908, 1032, 1920, 1080), ObstacleKind.Tray),
    ];

    [Fact]
    public void OnTheMeasuredBar_TheMeterLandsWhereItDidLive()
    {
        DockSpot spot = DockGeometry.Place(Measured(), 88);

        Assert.Equal(new PixelRect(1619, 1035, 1707, 1077), spot.Screen);
        Assert.Equal(new PixelRect(1619, 3, 1707, 45), spot.On(new PixelRect(0, 1032, 1920, 1080)));
        Assert.False(spot.Crowded);
    }

    [Fact]
    public void TheButtonsUiAutomationFound_ChangeNothingOnAnUncrowdedBar()
    {
        DockSpot spot = DockGeometry.Place(Measured(MeasuredButtons()), 88);

        Assert.Equal(new PixelRect(1619, 1035, 1707, 1077), spot.Screen);
        Assert.False(spot.Crowded);
    }

    /// <summary>
    /// The Widgets button stands just left of the notification area on a
    /// left-aligned Windows 11 taskbar; the meter hangs from it instead of
    /// covering it.
    /// </summary>
    [Fact]
    public void AButtonBesideTheNotificationArea_CountsAsPartOfIt()
    {
        DockSpot spot = DockGeometry.Place(
            Measured(new TaskbarObstacle(new PixelRect(1560, 1032, 1708, 1080), ObstacleKind.Fixed)),
            88);

        Assert.Equal(1560 - 6, spot.Screen.Right);
        Assert.False(spot.Crowded);
    }

    [Fact]
    public void ButtonsBesideEachOther_AreAllTakenIn()
    {
        DockSpot spot = DockGeometry.Place(
            Measured(
                new TaskbarObstacle(new PixelRect(1660, 1032, 1705, 1080), ObstacleKind.Fixed),
                new TaskbarObstacle(new PixelRect(1610, 1032, 1655, 1080), ObstacleKind.Fixed)),
            88);

        Assert.Equal(1610 - 6, spot.Screen.Right);
    }

    /// <summary>
    /// An app button never joins the notification area's group, however near
    /// it stands: a crowded bar must not push the meter off along all of them.
    /// </summary>
    [Fact]
    public void AnAppButtonBesideTheNotificationArea_IsCoveredAndSaidSo()
    {
        DockSpot spot = DockGeometry.Place(
            Measured(new TaskbarObstacle(new PixelRect(1660, 1032, 1704, 1080), ObstacleKind.Task)),
            88);

        Assert.Equal(1707, spot.Screen.Right);
        Assert.True(spot.Crowded);
    }

    [Fact]
    public void AButtonFarFromTheNotificationArea_IsLeftAlone()
    {
        DockSpot spot = DockGeometry.Place(
            Measured(new TaskbarObstacle(new PixelRect(0, 1032, 55, 1080), ObstacleKind.Fixed)),
            88);

        Assert.Equal(1707, spot.Screen.Right);
        Assert.False(spot.Crowded);
    }

    [Fact]
    public void AMeterLongerThanTheBarHasRoomFor_StartsAtTheBarsEndAndSaysSo()
    {
        DockSpot spot = DockGeometry.Place(Measured(), 1800);

        Assert.Equal(0, spot.Screen.Left);
        Assert.True(spot.Crowded);
    }

    [Fact]
    public void AtTheTop_TheMeterHangsFromTheNotificationAreaTheSameWay()
    {
        TaskbarFrame frame = new()
        {
            Bar = new PixelRect(0, 0, 1920, 48),
            Monitor = Screen,
            IsPrimary = true,
            Tray = new PixelRect(1713, 0, 1920, 48),
        };

        Assert.Equal(new PixelRect(1619, 3, 1707, 45), DockGeometry.Place(frame, 88).Screen);
    }

    /// <summary>
    /// The Small taskbar: thinner, so the meter is thinner with it. Microsoft
    /// publishes no height; 32 stands in for it.
    /// </summary>
    [Fact]
    public void OnAThinBar_TheMeterIsAsThinAsTheBarLetsIt()
    {
        TaskbarFrame frame = new()
        {
            Bar = new PixelRect(0, 1048, 1920, 1080),
            Monitor = Screen,
            IsPrimary = true,
            Tray = new PixelRect(1740, 1048, 1920, 1080),
        };

        DockSpot spot = DockGeometry.Place(frame, 120);

        Assert.Equal(26, DockGeometry.Across(frame));
        Assert.Equal(new PixelRect(1614, 1051, 1734, 1077), spot.Screen);
    }

    /// <summary>
    /// Down a side, the notification area is at the bottom, so the meter sits
    /// on top of it, as wide as the bar less its insets.
    /// </summary>
    [Theory]
    [InlineData(0, 62)]
    [InlineData(1858, 1920)]
    public void DownASide_TheMeterSitsAboveTheNotificationArea(int left, int right)
    {
        TaskbarFrame frame = new()
        {
            Bar = new PixelRect(left, 0, right, 1080),
            Monitor = Screen,
            IsPrimary = true,
            Tray = new PixelRect(left, 880, right, 1080),
        };

        DockSpot spot = DockGeometry.Place(frame, 60);

        Assert.True(frame.IsVertical);
        Assert.Equal(new PixelRect(left + 3, 814, right - 3, 874), spot.Screen);
        Assert.Equal(56, DockGeometry.Across(frame));
    }

    [Fact]
    public void DownASide_AButtonAboveTheNotificationArea_CountsAsPartOfIt()
    {
        TaskbarFrame frame = new()
        {
            Bar = new PixelRect(0, 0, 62, 1080),
            Monitor = Screen,
            IsPrimary = true,
            Tray = new PixelRect(0, 880, 62, 1080),
            Obstacles = [new TaskbarObstacle(new PixelRect(0, 830, 62, 876), ObstacleKind.Fixed)],
        };

        Assert.Equal(830 - 6, DockGeometry.Place(frame, 60).Screen.Bottom);
    }

    /// <summary>
    /// At 150% every length scales: the gap from 6 to 9, the inset from 3 to 5,
    /// on a bar 72 pixels thick.
    /// </summary>
    [Fact]
    public void AtOneAndAHalfTimes_EveryLengthScales()
    {
        TaskbarFrame frame = new()
        {
            Bar = new PixelRect(0, 1008, 1920, 1080),
            Monitor = Screen,
            Dpi = 144,
            IsPrimary = true,
            Tray = new PixelRect(1610, 1008, 1920, 1080),
        };

        DockSpot spot = DockGeometry.Place(frame, 132);

        Assert.Equal(62, DockGeometry.Across(frame));
        Assert.Equal(new PixelRect(1610 - 9 - 132, 1013, 1601, 1075), spot.Screen);
    }

    /// <summary>
    /// A 2560 × 1440 monitor at 150% to the right of the main one, its bar at
    /// the bottom. Its own clock was found through UI Automation, so the
    /// meter hangs from that.
    /// </summary>
    [Fact]
    public void OnAnotherMonitor_TheMeterHangsFromThatBarsClock()
    {
        TaskbarFrame frame = new()
        {
            Bar = new PixelRect(1920, 1368, 4480, 1440),
            Monitor = new PixelRect(1920, 0, 4480, 1440),
            Dpi = 144,
            Obstacles =
            [
                new TaskbarObstacle(new PixelRect(4350, 1368, 4462, 1440), ObstacleKind.Tray),
                new TaskbarObstacle(new PixelRect(4462, 1368, 4480, 1440), ObstacleKind.Tray),
            ],
        };

        DockSpot spot = DockGeometry.Place(frame, 132);

        Assert.Equal(4350 - 9, spot.Screen.Right);
        Assert.Equal(1368 + 5, spot.Screen.Top);
    }

    /// <summary>
    /// Another monitor's Windows 11 bar draws its clock in XAML and marks it
    /// with no window. Without UI Automation's answer, room is kept for it.
    /// </summary>
    [Fact]
    public void OnAnotherMonitorWithNothingSeen_RoomIsKeptForTheClock()
    {
        TaskbarFrame frame = new()
        {
            Bar = new PixelRect(1920, 1032, 3840, 1080),
            Monitor = new PixelRect(1920, 0, 3840, 1080),
            Kind = TaskbarKind.Modern,
        };

        Assert.Equal(3840 - 96 - 6, DockGeometry.Place(frame, 88).Screen.Right);
    }

    /// <summary>
    /// A classic bar on another monitor marks its clock with a window, which
    /// is passed in as the tray; no room is guessed beyond it.
    /// </summary>
    [Fact]
    public void OnAnotherClassicMonitor_TheClockWindowIsTheAnchor()
    {
        TaskbarFrame frame = new()
        {
            Bar = new PixelRect(1920, 1040, 3840, 1080),
            Monitor = new PixelRect(1920, 0, 3840, 1080),
            Kind = TaskbarKind.Classic,
            Tray = new PixelRect(3760, 1040, 3840, 1080),
        };

        Assert.Equal(3760 - 6, DockGeometry.Place(frame, 88).Screen.Right);
    }

    /// <summary>
    /// A right-to-left Windows mirrors the bar: the notification area is at
    /// the left, and the meter hangs to its right.
    /// </summary>
    [Fact]
    public void OnAMirroredBar_TheMeterHangsToTheRightOfTheNotificationArea()
    {
        TaskbarFrame frame = new()
        {
            Bar = new PixelRect(0, 1032, 1920, 1080),
            Monitor = Screen,
            IsPrimary = true,
            Tray = new PixelRect(0, 1032, 207, 1080),
        };

        Assert.Equal(new PixelRect(213, 1035, 301, 1077), DockGeometry.Place(frame, 88).Screen);
    }

    /// <summary>
    /// An auto-hiding bar mid-slide carries the meter with it: the spot is
    /// the same distance into the bar wherever the bar is.
    /// </summary>
    [Fact]
    public void OnASlidingBar_TheSpotMovesWithTheBar()
    {
        TaskbarFrame shown = Measured() with { AutoHide = true };
        TaskbarFrame sliding = shown with
        {
            Bar = new PixelRect(0, 1055, 1920, 1103),
            Tray = new PixelRect(1713, 1055, 1920, 1103),
        };

        Assert.Equal(DockGeometry.Place(shown, 88).Screen.Offset(0, 23), DockGeometry.Place(sliding, 88).Screen);
    }

    [Fact]
    public void AClassicTaskBand_IsNarrowedToEndBeforeTheMeter()
    {
        TaskbarFrame frame = Measured() with
        {
            Kind = TaskbarKind.Classic,
            TaskBand = new PixelRect(55, 1032, 1713, 1080),
        };
        PixelRect meter = DockGeometry.Place(frame, 88).Screen;

        Assert.Equal(new PixelRect(55, 1032, 1619 - 2, 1080), DockGeometry.Narrowed(frame, meter));
    }

    [Fact]
    public void AClassicTaskBandDownASide_IsShortenedAboveTheMeter()
    {
        TaskbarFrame frame = new()
        {
            Bar = new PixelRect(0, 0, 62, 1080),
            Monitor = Screen,
            IsPrimary = true,
            Kind = TaskbarKind.Classic,
            Tray = new PixelRect(0, 880, 62, 1080),
            TaskBand = new PixelRect(0, 60, 62, 880),
        };
        PixelRect meter = DockGeometry.Place(frame, 60).Screen;

        Assert.Equal(new PixelRect(0, 60, 62, 814 - 2), DockGeometry.Narrowed(frame, meter));
    }

    [Fact]
    public void ATaskBandClearOfTheMeter_IsLeftAlone()
    {
        TaskbarFrame frame = Measured() with
        {
            Kind = TaskbarKind.Classic,
            TaskBand = new PixelRect(55, 1032, 1500, 1080),
        };

        Assert.Null(DockGeometry.Narrowed(frame, DockGeometry.Place(frame, 88).Screen));
    }

    [Fact]
    public void ATaskBandThatWouldBeLeftTooSmall_IsLeftAlone()
    {
        TaskbarFrame frame = Measured() with
        {
            Kind = TaskbarKind.Classic,
            TaskBand = new PixelRect(1560, 1032, 1713, 1080),
        };

        Assert.Null(DockGeometry.Narrowed(frame, DockGeometry.Place(frame, 88).Screen));
    }

    /// <summary>
    /// The Windows 11 taskbar has a task band window too, but its buttons do
    /// not live in it: narrowing it would move nothing.
    /// </summary>
    [Fact]
    public void AWindows11TaskBand_IsNeverNarrowed()
    {
        TaskbarFrame frame = Measured() with { TaskBand = new PixelRect(55, 1032, 1713, 1080) };

        Assert.Null(DockGeometry.Narrowed(frame, DockGeometry.Place(frame, 88).Screen));
    }

    /// <summary>
    /// Under Arabic or Hebrew Windows the taskbar carries WS_EX_LAYOUTRTL, and
    /// a child's x counts from the bar's right edge. The meter at 1619-1707 on
    /// a 1920-wide bar is then 213 from the right. (Found by review.)
    /// </summary>
    [Fact]
    public void InAMirroredBar_TheChildsXCountsFromTheRight()
    {
        PixelRect bar = new(0, 1032, 1920, 1080);
        PixelRect meter = new(1619, 1035, 1707, 1077);

        Assert.Equal(new PixelRect(1619, 3, 1707, 45), DockGeometry.InParent(meter, bar, mirrored: false));
        Assert.Equal(new PixelRect(213, 3, 301, 45), DockGeometry.InParent(meter, bar, mirrored: true));
        Assert.Equal(new PixelRect(213, 3, 301, 45), new DockSpot(meter, false).On(bar, mirrored: true));
    }

    [Fact]
    public void InAMirroredBarOnAnotherMonitor_TheParentsOwnEdgeCounts()
    {
        PixelRect bar = new(1920, 1032, 3840, 1080);
        PixelRect meter = new(1950, 1035, 2038, 1077);

        Assert.Equal(new PixelRect(1802, 3, 1890, 45), DockGeometry.InParent(meter, bar, mirrored: true));
    }

    /// <summary>
    /// The band as Explorer laid it out is kept apart from the frame, which
    /// holds the narrowed one once it is narrowed; given apart, the same answer
    /// comes back without making a new frame each tick.
    /// </summary>
    [Fact]
    public void ATaskBandGivenApart_IsNarrowedTheSame()
    {
        TaskbarFrame frame = Measured() with
        {
            Kind = TaskbarKind.Classic,
            TaskBand = new PixelRect(55, 1032, 1617, 1080),
        };
        PixelRect meter = DockGeometry.Place(frame, 88).Screen;

        Assert.Equal(
            new PixelRect(55, 1032, 1617, 1080),
            DockGeometry.Narrowed(frame, new PixelRect(55, 1032, 1713, 1080), meter));
    }

    [Fact]
    public void OnAMonitorWithoutATaskbar_AFloatingMeterTakesTheBottomCorner()
    {
        Assert.Equal(
            new PixelRect(1824, 982, 1912, 1024),
            DockGeometry.Corner(new PixelRect(0, 0, 1920, 1032), 88, 42, 96));
        Assert.Equal(
            new PixelRect(4480 - 12 - 132, 1440 - 12 - 63, 4480 - 12, 1440 - 12),
            DockGeometry.Corner(new PixelRect(1920, 0, 4480, 1440), 132, 63, 144));
    }
}
