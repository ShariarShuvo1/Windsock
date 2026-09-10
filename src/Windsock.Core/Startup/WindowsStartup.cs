using System.Security;
using Microsoft.Win32;

namespace Windsock.Core.Startup;

/// <summary>
/// Whether Windows starts Windsock when the user signs in.
/// </summary>
public sealed class WindowsStartup
{
    /// <summary>Where Windows keeps the per-user list of things to start.</summary>
    public const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";

    /// <summary>The name Windsock's entry is filed under.</summary>
    public const string EntryName = "Windsock";

    /// <summary>Told to Windsock when Windows started it rather than a person.</summary>
    public const string BackgroundSwitch = "--background";
    private readonly string _key;

    public WindowsStartup()
        : this(RunKey)
    {
    }

    /// <summary>Uses a key of its own, which is what the tests are for.</summary>
    public WindowsStartup(string key)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
        _key = key;
    }

    /// <summary>Whether Windsock is on the list.</summary>
    public bool IsEnabled => Read() is not null;

    /// <summary>The command line Windows is set to run, or null for none.</summary>
    public string? Read()
    {
        try
        {
            using RegistryKey? key = Registry.CurrentUser.OpenSubKey(_key, writable: false);
            return key?.GetValue(EntryName) as string;
        }
        catch (Exception ex) when (ex is SecurityException or UnauthorizedAccessException or IOException)
        {
            return null;
        }
    }

    /// <summary>
    /// Puts Windsock on the list, or takes it off.
    /// </summary>
    public bool Set(bool wanted, string? program = null)
    {
        try
        {
            using RegistryKey key = Registry.CurrentUser.CreateSubKey(_key, writable: true);

            if (!wanted)
            {
                key.DeleteValue(EntryName, throwOnMissingValue: false);
                return true;
            }

            if (Where(program) is not { Length: > 0 } found)
            {
                return false;
            }

            key.SetValue(EntryName, Command(found), RegistryValueKind.String);
            return true;
        }
        catch (Exception ex) when (ex is SecurityException or UnauthorizedAccessException or IOException)
        {
            return false;
        }
    }

    /// <summary>What Windows is told to run.</summary>
    public static string Command(string program) =>
        string.Concat("\"", program, "\" ", BackgroundSwitch);

    /// <summary>Whether a command line asks for a quiet start.</summary>
    public static bool IsQuiet(IEnumerable<string>? arguments) =>
        CommandLine.Has(arguments, BackgroundSwitch);

    private static string? Where(string? program) =>
        string.IsNullOrWhiteSpace(program) ? Environment.ProcessPath : program;
}
