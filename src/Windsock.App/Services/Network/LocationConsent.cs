using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using Microsoft.Win32;
using Windsock.Core;

namespace Windsock.App.Services.Network;

/// <summary>What Windows' location settings appear to say for Windsock.</summary>
public enum LocationAccess
{
    /// <summary>Could not be told; asking is the only way to know.</summary>
    Unknown,

    /// <summary>Every switch on the way appears to be on.</summary>
    Allowed,

    /// <summary>Location is off for the device, for this user, or for desktop apps (or this package).</summary>
    Denied,
}

/// <summary>
/// Reads, without changing anything, whether Windows' location permission
/// looks on for Windsock - which since Windows 11 24H2 decides whether Wi-Fi's
/// network name and nearby networks can be read - and opens the page to change it.
/// </summary>
/// <remarks>
/// <para>
/// A guess from the registry, never the deciding answer: the Wi-Fi call itself
/// says Allowed or Access Denied, and that is what the page believes. The guess
/// lets the page say "Windows will ask you" or "location is off" before the
/// person presses anything. Keys (all present on the development machine):
/// <c>CapabilityAccessManager\ConsentStore\location</c> under HKLM (the device)
/// and HKCU (this user), and under HKCU its <c>NonPackaged</c> key for desktop
/// apps, or a key named by the package family for the Store edition. Each holds
/// <c>Value</c>, "Allow" or "Deny". Windsock never writes them.
/// </para>
/// <para>
/// Nothing here runs at startup: the page reads it when the person opens Wi-Fi
/// details.
/// </para>
/// </remarks>
public static class LocationConsent
{
    private const string Store = @"SOFTWARE\Microsoft\Windows\CurrentVersion\CapabilityAccessManager\ConsentStore\location";

    /// <summary>The settings page where location is switched on.</summary>
    public static readonly Uri SettingsPage = new("ms-settings:privacy-location");

    /// <summary>The best guess at whether location is allowed for Windsock.</summary>
    public static LocationAccess Read()
    {
        try
        {
            string? device = Value(Registry.LocalMachine, Store);
            string? user = Value(Registry.CurrentUser, Store);
            string? app = PackageIdentity.Exists
                ? PackagedValue()
                : Value(Registry.CurrentUser, Store + @"\NonPackaged");

            if (IsDeny(device) || IsDeny(user) || IsDeny(app))
            {
                return LocationAccess.Denied;
            }

            return IsAllow(device) && IsAllow(user) && (app is null || IsAllow(app))
                ? LocationAccess.Allowed
                : LocationAccess.Unknown;
        }
        catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException or IOException)
        {
            return LocationAccess.Unknown;
        }
    }

    /// <summary>Opens Windows' location settings.</summary>
    public static bool OpenSettings()
    {
        try
        {
            using Process? opened = Process.Start(new ProcessStartInfo(SettingsPage.ToString()) { UseShellExecute = true });
            return true;
        }
        catch (Win32Exception)
        {
            return false;
        }
    }

    private static string? PackagedValue()
    {
        using RegistryKey? root = Registry.CurrentUser.OpenSubKey(Store);

        if (root is null)
        {
            return null;
        }

        foreach (string name in root.GetSubKeyNames())
        {
            if (name.StartsWith("ShariarShuvo.Windsock", StringComparison.OrdinalIgnoreCase))
            {
                return Value(Registry.CurrentUser, Store + @"\" + name);
            }
        }

        return null;
    }

    private static string? Value(RegistryKey hive, string path)
    {
        using RegistryKey? key = hive.OpenSubKey(path);
        return key?.GetValue("Value") as string;
    }

    private static bool IsDeny(string? value) => string.Equals(value, "Deny", StringComparison.OrdinalIgnoreCase);

    private static bool IsAllow(string? value) => string.Equals(value, "Allow", StringComparison.OrdinalIgnoreCase);
}
