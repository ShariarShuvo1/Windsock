using Windsock.Core.Docking;
using Xunit;

namespace Windsock.Core.Tests;

/// <summary>
/// Telling a full-screen app in front of a monitor from everything else that
/// covers it.
/// </summary>
/// <remarks>
/// Style values from the Windows SDK: WS_POPUP 0x80000000, WS_CAPTION
/// 0x00C00000 (WS_BORDER | WS_DLGFRAME), WS_THICKFRAME 0x00040000,
/// WS_OVERLAPPEDWINDOW 0x00CF0000, WS_EX_TOOLWINDOW 0x80, WS_EX_WINDOWEDGE
/// 0x100. The frame rule is Chromium's (ui/base/fullscreen_win.cc).
/// </remarks>
public sealed class FullScreenCheckTests
{
    private static readonly PixelRect Main = new(0, 0, 1920, 1080);
    private static readonly PixelRect Second = new(1920, 0, 4480, 1440);

    [Fact]
    public void ABorderlessGameFillingTheMonitor_IsFullScreen()
    {
        Assert.True(FullScreenCheck.Covers(new FrontWindow(Main, "UnrealWindow", false), Main, shellBusy: false));
    }

    [Fact]
    public void AFullScreenWindowOnAnotherMonitor_CoversThatMonitor()
    {
        FrontWindow game = new(Second, "UnityWndClass", false);

        Assert.True(FullScreenCheck.Covers(game, Second, shellBusy: false));
        Assert.False(FullScreenCheck.Covers(game, Main, shellBusy: false));
    }

    /// <summary>
    /// A window maximised over an auto-hiding taskbar overhangs the whole
    /// monitor by its frame. It is an ordinary window, and the meter stays.
    /// </summary>
    [Fact]
    public void AFramedWindowMaximisedOverEverything_IsNotFullScreen()
    {
        Assert.False(FullScreenCheck.Covers(
            new FrontWindow(new PixelRect(-8, -8, 1928, 1088), "Chrome_WidgetWin_1", true),
            Main,
            shellBusy: false));
    }

    [Fact]
    public void AWindowShortOfTheMonitor_IsNotFullScreen()
    {
        Assert.False(FullScreenCheck.Covers(
            new FrontWindow(new PixelRect(0, 0, 1920, 1032), "Notepad", false),
            Main,
            shellBusy: false));
    }

    [Theory]
    [InlineData("Progman")]
    [InlineData("WorkerW")]
    [InlineData("Shell_TrayWnd")]
    [InlineData("XamlExplorerHostIslandWindow")]
    [InlineData("Windows.UI.Core.CoreWindow")]
    public void TheShellsOwnWindows_AreNeverFullScreenApps(string className)
    {
        Assert.False(FullScreenCheck.Covers(new FrontWindow(Main, className, false), Main, shellBusy: true));
    }

    /// <summary>
    /// An exclusive game can leave its window a little short of the monitor;
    /// with the shell saying something is full screen, most of it is enough.
    /// </summary>
    [Fact]
    public void NearlyFilling_CountsOnlyWhenTheShellSaysSo()
    {
        FrontWindow game = new(new PixelRect(0, 0, 1920, 1040), "SDL_app", false);

        Assert.False(FullScreenCheck.Covers(game, Main, shellBusy: false));
        Assert.True(FullScreenCheck.Covers(game, Main, shellBusy: true));
        Assert.False(FullScreenCheck.Covers(new FrontWindow(new PixelRect(0, 0, 1000, 1080), "SDL_app", false), Main, shellBusy: true));
    }

    /// <summary>
    /// QUNS_BUSY is left out on purpose: overlays such as NVIDIA's set it with
    /// nothing full screen at all, which is why PowerToys made it opt-in.
    /// </summary>
    [Theory]
    [InlineData(1, false)]
    [InlineData(2, false)]
    [InlineData(3, true)]
    [InlineData(4, true)]
    [InlineData(5, false)]
    [InlineData(7, false)]
    public void TheShellsAnswer_MeansFullScreenOnlyForGamesAndPresentations(int state, bool busy)
    {
        Assert.Equal(busy, FullScreenCheck.IsBusy(state));
    }

    [Theory]
    [InlineData(0x80000000u, 0u, false)]
    [InlineData(0x80000000u | 0x10000000u, 0u, false)]
    [InlineData(0x00CF0000u, 0u, true)]
    [InlineData(0x00C00000u, 0u, true)]
    [InlineData(0x00040000u, 0u, true)]
    [InlineData(0x80000000u, 0x80u, true)]
    [InlineData(0x80000000u, 0x100u, true)]
    public void AFrame_IsReadFromTheStyles(uint style, uint extended, bool framed)
    {
        Assert.Equal(framed, FullScreenCheck.IsFramed(style, extended));
    }
}
