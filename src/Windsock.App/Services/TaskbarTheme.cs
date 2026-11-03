using System.IO;
using Microsoft.Win32;

namespace Windsock.App.Services;

/// <summary>Whether the taskbar is drawn dark, which decides the meter's and the tray figures' ink.</summary>
internal static class TaskbarTheme
{
    /// <summary>
    /// Whether the taskbar is dark: Windows' "system" theme, which the taskbar
    /// follows whatever the apps' theme is. Dark when it cannot be read.
    /// </summary>
    public static bool IsDark()
    {
        try
        {
            object? value = Registry.GetValue(
                @"HKEY_CURRENT_USER\Software\Microsoft\Windows\CurrentVersion\Themes\Personalize",
                "SystemUsesLightTheme",
                null);

            return value is not int light || light == 0;
        }
        catch (Exception ex) when (ex is System.Security.SecurityException or IOException)
        {
            return true;
        }
    }
}
