using System.IO;
using Microsoft.Extensions.Logging;
using Microsoft.Win32;
using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.UI.WindowsAndMessaging;
using Windsock.Core;
using Windsock.Core.Feed;

namespace Windsock.App.Services;

/// <summary>
/// Puts the folder Windsock runs from on the user's own PATH, and takes it off
/// again, so that typing <c>windsock</c> in a new console runs the command line.
/// </summary>
/// <remarks>
/// <para>
/// Only ever on the person's word, and reversibly: the user's PATH is in
/// <c>HKCU\Environment</c>, read and written as REG_EXPAND_SZ with its variables
/// left unexpanded, and nothing but Windsock's own entry is ever added or taken
/// out (<see cref="UserPath"/>). Windows is then told the environment changed,
/// so consoles started afterwards see it; ones already open do not.
/// </para>
/// <para>
/// Not offered in a packaged Windsock. Inside the MSIX container a write to
/// HKCU is redirected into the package's private copy of the registry - the
/// trap the Run key fell into - and the Store package puts <c>windsock</c> on
/// the PATH itself, through its app execution alias.
/// </para>
/// <para>
/// The entry is the program folder itself, appended last, and the uninstaller
/// takes it out (<see cref="ForgetOnUninstall"/>). A folder holding only the
/// command was weighed and refused: a <c>.cmd</c> shim turns Ctrl+C in
/// <c>windsock watch</c> into "Terminate batch job (Y/N)?", a symbolic link
/// needs Developer Mode or an administrator, App Paths are not consulted by a
/// console, and a copy of the command needs the runtime beside it. The folder
/// is the user's own, writable by them anyway, so being last on their PATH
/// gives nobody anything they did not have.
/// </para>
/// </remarks>
public sealed partial class CommandLinePath(ILogger<CommandLinePath> logger)
{
    private const string EnvironmentKey = "Environment";
    private const string PathName = "Path";

    // cmd.exe stops reading an environment variable at 8,191 characters; a PATH
    // longer than that breaks consoles, so Windsock will not be what makes it so.
    private const int LongestPath = 8191;

    // HWND_BROADCAST and WM_SETTINGCHANGE (winuser.h).
    private const nint Broadcast = 0xFFFF;
    private const uint SettingChange = 0x001A;

    private readonly ILogger<CommandLinePath> _logger = logger;

    /// <summary>The folder the command line lives in.</summary>
    public static string Folder { get; } = AppContext.BaseDirectory.TrimEnd('\\', '/');

    /// <summary>Whether this copy can offer it: unpackaged, with the command line beside it.</summary>
    public static bool IsOffered => !PackageIdentity.Exists && File.Exists(Path.Combine(Folder, "Windsock.com"));

    /// <summary>Whether the folder is on the user's PATH now. False when it cannot be read.</summary>
    public bool IsOnPath()
    {
        try
        {
            return UserPath.Contains(Read(), Folder);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException)
        {
            LogFailed(ex);
            return false;
        }
    }

    /// <summary>Adds the folder. The problem in a sentence when it could not, or nothing.</summary>
    public string? Add() => Change(path => UserPath.Add(path, Folder), adding: true);

    /// <summary>Takes the folder out. The problem in a sentence when it could not, or nothing.</summary>
    public string? Remove() => Change(path => UserPath.Remove(path, Folder), adding: false);

    /// <summary>
    /// Takes the folder out of the user's PATH as Windsock is uninstalled, so
    /// no entry is left pointing at a folder that is about to go. Runs in
    /// Velopack's uninstall hook, before any host or logger exists, so it says
    /// nothing and gives up quietly; it waits at most a few seconds for running
    /// programs to hear of the change, because the hook's time is limited.
    /// </summary>
    public static void ForgetOnUninstall()
    {
        try
        {
            if (Edit(path => UserPath.Remove(path, Folder)) is Edited.Changed)
            {
                _ = Task.Run(Announce).Wait(TimeSpan.FromSeconds(3));
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException)
        {
        }
    }

    private string? Change(Func<string, string> edit, bool adding)
    {
        if (!IsOffered)
        {
            return "This copy of Windsock cannot change the PATH.";
        }

        try
        {
            switch (Edit(edit))
            {
                case Edited.TooLong:
                    return "Your PATH is already too long to add another folder to.";

                case Edited.Changed:
                    // A broadcast waits on every top-level window in turn, up to
                    // two seconds for each that does not answer, so it is never
                    // made from the window's own thread.
                    _ = Task.Run(Announce);
                    LogChanged(adding);
                    return null;

                default:
                    return null;
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException)
        {
            LogFailed(ex);
            return "Windows would not let Windsock change your PATH.";
        }
    }

    private enum Edited
    {
        Unchanged,
        Changed,
        TooLong,
    }

    /// <summary>Applies an edit to the user's PATH as stored, keeping its kind. Throws what the registry throws.</summary>
    private static Edited Edit(Func<string, string> edit)
    {
        using RegistryKey key = Registry.CurrentUser.CreateSubKey(EnvironmentKey, writable: true);
        string current = key.GetValue(PathName, string.Empty, RegistryValueOptions.DoNotExpandEnvironmentNames) as string ?? string.Empty;
        RegistryValueKind kind = key.GetValueNames().Contains(PathName, StringComparer.OrdinalIgnoreCase)
            ? key.GetValueKind(PathName)
            : RegistryValueKind.ExpandString;

        string changed = edit(current);

        if (string.Equals(changed, current, StringComparison.Ordinal))
        {
            return Edited.Unchanged;
        }

        if (changed.Length > LongestPath && changed.Length > current.Length)
        {
            return Edited.TooLong;
        }

        key.SetValue(PathName, changed, kind == RegistryValueKind.String ? RegistryValueKind.String : RegistryValueKind.ExpandString);
        return Edited.Changed;
    }

    private static string Read()
    {
        using RegistryKey? key = Registry.CurrentUser.OpenSubKey(EnvironmentKey);
        return key?.GetValue(PathName, string.Empty, RegistryValueOptions.DoNotExpandEnvironmentNames) as string ?? string.Empty;
    }

    /// <summary>Tells running programs - Explorer, chiefly, which starts new consoles - that the environment changed.</summary>
    private static unsafe void Announce()
    {
        fixed (char* area = "Environment")
        {
            _ = PInvoke.SendMessageTimeout(
                new HWND(Broadcast),
                SettingChange,
                default,
                (LPARAM)(nint)area,
                SEND_MESSAGE_TIMEOUT_FLAGS.SMTO_ABORTIFHUNG,
                2000,
                null);
        }
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Changed the user's PATH (added: {Added})")]
    private partial void LogChanged(bool added);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Could not read or change the user's PATH")]
    private partial void LogFailed(Exception exception);
}
