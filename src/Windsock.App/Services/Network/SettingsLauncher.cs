using System.ComponentModel;
using System.Diagnostics;

namespace Windsock.App.Services.Network;

/// <summary>Opens a page of Windows Settings, or any address the shell knows how to open.</summary>
public static class SettingsLauncher
{
    /// <summary>Opens the address; false when Windows had nothing to open it with.</summary>
    public static bool Open(Uri address)
    {
        ArgumentNullException.ThrowIfNull(address);

        try
        {
            using Process? opened = Process.Start(new ProcessStartInfo(address.OriginalString) { UseShellExecute = true });
            return true;
        }
        catch (Win32Exception)
        {
            return false;
        }
    }

    /// <summary>Opens a file with the program Windows associates with it.</summary>
    public static bool OpenFile(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);

        try
        {
            using Process? opened = Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
            return true;
        }
        catch (Exception ex) when (ex is Win32Exception or InvalidOperationException)
        {
            return false;
        }
    }
}
