using System.Security;
using Microsoft.Win32;

namespace Windsock.Core.Startup;

/// <summary>
/// Whether Windows starts Windsock when the user signs in, through the HKCU
/// Run key.
/// </summary>
/// <remarks>
/// The unpackaged edition only. A packaged Windsock gets
/// <c>PackagedStartup</c> instead, because the container redirects this key
/// into a private copy: the write succeeds, the read agrees with it, and
/// Windows starts nothing. See <see cref="IStartupSwitch"/>.
/// </remarks>
public sealed class WindowsStartup : IStartupSwitch
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

    /// <summary>
    /// The registry is cheap enough to read every time, so this is never stale
    /// and <see cref="RefreshAsync"/> has nothing to do.
    /// </summary>
    public StartupState State => IsEnabled ? StartupState.On : StartupState.Off;

    /// <summary>Nothing to explain: this route is always available.</summary>
    public string Detail => string.Empty;

    /// <inheritdoc />
    public Task<StartupState> RefreshAsync() => Task.FromResult(State);

    /// <inheritdoc />
    public Task<StartupState> SetAsync(bool wanted)
    {
        Set(wanted);
        return Task.FromResult(State);
    }

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
