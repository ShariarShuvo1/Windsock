using Windsock.Core.Docking;
using Xunit;

namespace Windsock.Core.Tests;

/// <summary>
/// Sorting the Windows 11 taskbar's buttons, by the automation ids and class
/// names UI Automation gave for them on build 26200 on 2026-10-05.
/// </summary>
public sealed class TaskbarButtonsTests
{
    [Theory]
    [InlineData("StartButton", "ToggleButton", ObstacleKind.Fixed)]
    [InlineData("WidgetsButton", "ToggleButton", ObstacleKind.Fixed)]
    [InlineData("TaskViewButton", "ToggleButton", ObstacleKind.Fixed)]
    [InlineData("Appid: Chrome", "Taskbar.TaskListButtonAutomationPeer", ObstacleKind.Task)]
    [InlineData("Appid: Microsoft.WindowsCalculator_8wekyb3d8bbwe!App", "Taskbar.TaskListButtonAutomationPeer", ObstacleKind.Task)]
    [InlineData("Appid: Microsoft.Windows.Explorer", null, ObstacleKind.Task)]
    [InlineData("SystemTrayIcon", "SystemTray.NormalButton", ObstacleKind.Tray)]
    [InlineData("NotifyItemIcon", "SystemTray.NormalButton", ObstacleKind.Tray)]
    [InlineData("SystemTrayIcon", "SystemTray.OmniButton", ObstacleKind.Tray)]
    [InlineData("SystemTrayIcon", "SystemTray.ShowDesktopButton", ObstacleKind.Tray)]
    [InlineData("", "", ObstacleKind.Fixed)]
    [InlineData(null, null, ObstacleKind.Fixed)]
    public void EachButton_IsWhatItsNamesSay(string? id, string? className, ObstacleKind expected)
    {
        Assert.Equal(expected, TaskbarButtons.Classify(id, className));
    }
}
