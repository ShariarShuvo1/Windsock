using Microsoft.Win32;

namespace Windsock.Core.Notifications;

/// <summary>
/// Tells Windows who is sending notices, for the edition installed without a
/// package.
/// </summary>
/// <remarks>
/// <para>
/// A packaged app is known to Windows by its package. An unpackaged one is not
/// known at all, and Windows silently drops notices from an app id it has no
/// record of. The record is a key under the current user's classes: a display
/// name and icon for the notice header, and the id of a COM class Windows calls
/// when a notice is clicked. The class is registered to launch Windsock with
/// <see cref="ActivatedSwitch"/>, so clicking a notice that outlived the app
/// still opens it.
/// </para>
/// <para>
/// Only the current user's hive is touched, so nothing here needs elevation, and
/// the installer's uninstall hook removes both keys again.
/// </para>
/// </remarks>
public static class NotificationRegistration
{
    /// <summary>The app id notices are sent under when there is no package.</summary>
    public const string AppId = "ShariarShuvo.Windsock";

    /// <summary>
    /// The COM class Windows calls when a notice is clicked. Fixed for ever: a
    /// notice already sitting in the notification centre names this id.
    /// </summary>
    public static readonly Guid Activator = new("7172F3B6-593A-40AA-AB74-D4710493330E");

    /// <summary>What Windows puts on the command line when a click has to start Windsock.</summary>
    public const string ActivatedSwitch = "-ToastActivated";

    private const string DisplayName = "Windsock";

    private static string AppKey => @"AppUserModelId\" + AppId;

    private static string ClassKey => @"CLSID\" + Activator.ToString("B").ToUpperInvariant();

    /// <summary>
    /// Writes, or rewrites, the record under a classes key - normally
    /// <c>HKCU\Software\Classes</c>. Rewritten on every start because the
    /// program may have moved since the last one.
    /// </summary>
    public static void Write(RegistryKey classes, string program, string? icon)
    {
        ArgumentNullException.ThrowIfNull(classes);
        ArgumentException.ThrowIfNullOrWhiteSpace(program);

        using (RegistryKey app = classes.CreateSubKey(AppKey, writable: true))
        {
            app.SetValue("DisplayName", DisplayName, RegistryValueKind.String);
            app.SetValue("CustomActivator", Activator.ToString("B").ToUpperInvariant(), RegistryValueKind.String);

            if (icon is { Length: > 0 })
            {
                app.SetValue("IconUri", icon, RegistryValueKind.String);
            }
            else
            {
                app.DeleteValue("IconUri", throwOnMissingValue: false);
            }
        }

        using RegistryKey server = classes.CreateSubKey(ClassKey + @"\LocalServer32", writable: true);
        server.SetValue(string.Empty, Command(program), RegistryValueKind.String);
    }

    /// <summary>Whether a record has been written under this classes key.</summary>
    public static bool Exists(RegistryKey classes)
    {
        ArgumentNullException.ThrowIfNull(classes);

        using RegistryKey? app = classes.OpenSubKey(AppKey);
        return app is not null;
    }

    /// <summary>Removes the record. Safe when there is nothing to remove.</summary>
    public static void Remove(RegistryKey classes)
    {
        ArgumentNullException.ThrowIfNull(classes);

        classes.DeleteSubKeyTree(AppKey, throwOnMissingSubKey: false);
        classes.DeleteSubKeyTree(ClassKey, throwOnMissingSubKey: false);
    }

    /// <summary>Whether this launch was Windows starting Windsock for a clicked notice.</summary>
    public static bool IsActivation(IEnumerable<string>? arguments) =>
        arguments?.Any(each => string.Equals(each, ActivatedSwitch, StringComparison.OrdinalIgnoreCase)) == true;

    /// <summary>The command line the COM class launches, with the path quoted.</summary>
    internal static string Command(string program) => string.Concat("\"", program, "\" ", ActivatedSwitch);
}
