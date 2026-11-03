using System.IO;
using Microsoft.Win32;
using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.Graphics.Gdi;
using Windows.Win32.UI.Shell;
using Windows.Win32.UI.WindowsAndMessaging;
using Windsock.Core.Docking;

namespace Windsock.App.Services;

/// <summary>
/// The few things about the session the meter has to ask Windows: what is in
/// front, whether the shell thinks it is full screen, and whether this is a
/// remote session.
/// </summary>
internal static unsafe class ShellState
{
    private const string GlassKey = @"SYSTEM\CurrentControlSet\Control\Terminal Server";

    private static readonly uint OwnProcess = (uint)Environment.ProcessId;

    /// <summary>
    /// This process's session, read once: it never changes for a process, and
    /// asking through Process cost 5.3 ms and 3 KB a time on the UI thread.
    /// </summary>
    private static readonly uint OwnSession = PInvoke.ProcessIdToSessionId(OwnProcess, out uint session) ? session : uint.MaxValue;

    /// <summary>
    /// The window in front and the monitor it is on, or nothing when the
    /// front is empty or one of Windsock's own windows.
    /// </summary>
    public static (nint Window, nint Monitor)? Front()
    {
        HWND front = PInvoke.GetForegroundWindow();

        if (front == HWND.Null)
        {
            return null;
        }

        uint process;
        _ = PInvoke.GetWindowThreadProcessId(front, &process);

        if (process == OwnProcess)
        {
            return null;
        }

        HMONITOR monitor = PInvoke.MonitorFromWindow(front, MONITOR_FROM_FLAGS.MONITOR_DEFAULTTONULL);

        return monitor.IsNull ? null : ((nint)front, (nint)monitor);
    }

    /// <summary>What the full-screen check needs to know about a window.</summary>
    public static FrontWindow? Describe(nint window)
    {
        var handle = new HWND(window);

        if (window == 0 || !PInvoke.GetWindowRect(handle, out RECT rect))
        {
            return null;
        }

        Span<char> name = stackalloc char[64];
        int length = PInvoke.GetClassName(handle, name);
        uint style = unchecked((uint)PInvoke.GetWindowLong(handle, WINDOW_LONG_PTR_INDEX.GWL_STYLE));
        uint extended = unchecked((uint)PInvoke.GetWindowLong(handle, WINDOW_LONG_PTR_INDEX.GWL_EXSTYLE));

        return new FrontWindow(
            new PixelRect(rect.left, rect.top, rect.right, rect.bottom),
            length > 0 ? new string(name[..length]) : string.Empty,
            FullScreenCheck.IsFramed(style, extended));
    }

    /// <summary>
    /// Whether the window's rectangle covers a whole monitor: the cheap first
    /// question, asked before anything that allocates.
    /// </summary>
    public static bool Fills(nint window, PixelRect monitor) =>
        window != 0
        && PInvoke.GetWindowRect(new HWND(window), out RECT rect)
        && rect.left <= monitor.Left && rect.top <= monitor.Top
        && rect.right >= monitor.Right && rect.bottom >= monitor.Bottom;

    /// <summary>
    /// Whether the shell says something is full screen: an exclusive game, a
    /// presentation, or a borderless app it has noticed.
    /// </summary>
    public static bool ShellBusy() =>
        PInvoke.SHQueryUserNotificationState(out QUERY_USER_NOTIFICATION_STATE state).Succeeded
        && FullScreenCheck.IsBusy((int)state);

    /// <summary>
    /// Whether Windsock is running in a Remote Desktop session, so its readings
    /// are of a computer the person is not sitting at.
    /// </summary>
    /// <remarks>
    /// The documented test (Microsoft Learn, "Detecting the Remote Desktop
    /// Services environment"): SM_REMOTESESSION, and where RemoteFX vGPU
    /// leaves that unset, the console's GlassSessionId differing from this
    /// session's.
    /// </remarks>
    public static bool InRemoteSession()
    {
        if (PInvoke.GetSystemMetrics(SYSTEM_METRICS_INDEX.SM_REMOTESESSION) != 0)
        {
            return true;
        }

        try
        {
            using RegistryKey? key = Registry.LocalMachine.OpenSubKey(GlassKey);

            return key?.GetValue("GlassSessionId") is int glass
                && OwnSession != uint.MaxValue
                && unchecked((uint)glass) != OwnSession;
        }
        catch (Exception ex) when (ex is System.Security.SecurityException or IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }
}
