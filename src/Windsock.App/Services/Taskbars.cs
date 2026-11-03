using System.IO;
using System.Runtime.InteropServices;
using Microsoft.Win32;
using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.Graphics.Gdi;
using Windows.Win32.UI.HiDpi;
using Windows.Win32.UI.WindowsAndMessaging;
using Windsock.Core.Docking;

namespace Windsock.App.Services;

/// <summary>One monitor, as Windows describes it.</summary>
/// <param name="Handle">The monitor's handle, good until the displays change.</param>
/// <param name="Name">The name Windows gives it, such as <c>\\.\DISPLAY2</c>.</param>
/// <param name="Bounds">The whole monitor, in screen pixels.</param>
/// <param name="Work">The part of it windows may use.</param>
/// <param name="Primary">Whether it is the main display.</param>
/// <param name="Dpi">Its dots per inch, 96 at 100%.</param>
internal readonly record struct FoundMonitor(
    nint Handle,
    string Name,
    PixelRect Bounds,
    PixelRect Work,
    bool Primary,
    int Dpi);

/// <summary>One taskbar window, and the windows inside it that mark its parts.</summary>
/// <param name="Handle">The taskbar window.</param>
/// <param name="Primary">Whether it is the main taskbar, the one with the notification area.</param>
/// <param name="Monitor">The monitor it belongs to.</param>
/// <param name="Tray">The window marking the notification area, or the clock on another monitor's bar.</param>
/// <param name="Band">The classic task band, whose width the task buttons lay themselves out in.</param>
/// <param name="Kind">Which taskbar Explorer is drawing.</param>
/// <param name="Mirrored">Whether a right-to-left Windows mirrors it, so its children's x counts from its right.</param>
internal sealed record FoundTaskbar(
    nint Handle,
    bool Primary,
    FoundMonitor Monitor,
    nint Tray,
    nint Band,
    TaskbarKind Kind,
    bool Mirrored);

/// <summary>
/// Finds every taskbar and every monitor, through the windows Explorer makes.
/// </summary>
/// <remarks>
/// The main taskbar is <c>Shell_TrayWnd</c>; each other monitor that shows
/// one has a <c>Shell_SecondaryTrayWnd</c> with no notification area. On the
/// Windows 11 taskbar the buttons are XAML inside a
/// <c>DesktopWindowContentBridge</c>, and the older windows Explorer still
/// keeps - <c>TrayNotifyWnd</c>, <c>MSTaskSwWClass</c> - are there for
/// compatibility: the notification area's window tracks the notification area
/// (measured: 1713 to 1920 on a 1920-wide bar, matching its buttons), but the
/// task band does not track the task buttons (it read 55 to 187 while six
/// buttons ran to 319). On a classic taskbar, ExplorerPatcher's or
/// StartAllBack's, the bridge is absent and the task band is the real thing.
/// </remarks>
internal static unsafe partial class Taskbars
{
    private const string MainClass = "Shell_TrayWnd";
    private const string OtherClass = "Shell_SecondaryTrayWnd";
    private const string NotifyClass = "TrayNotifyWnd";
    private const string ClockClass = "ClockButton";
    private const string RebarClass = "ReBarWindow32";
    private const string BandClass = "MSTaskSwWClass";
    private const string BridgeClass = "Windows.UI.Composition.DesktopWindowContentBridge";

    private const uint GetState = 0x00000004;
    private const uint LayoutRightToLeft = 0x00400000;
    private const uint AutoHideState = 0x0000001;

    /// <summary>Every monitor, the main one first.</summary>
    public static List<FoundMonitor> Monitors()
    {
        List<FoundMonitor> found = [];
        List<nint> handles = [];

        BOOL Each(HMONITOR monitor, HDC context, RECT* clip, LPARAM data)
        {
            handles.Add(monitor);
            return true;
        }

        _ = PInvoke.EnumDisplayMonitors(HDC.Null, (RECT?)null, Each, 0);

        foreach (nint handle in handles)
        {
            if (Describe(handle) is { } monitor)
            {
                found.Add(monitor);
            }
        }

        found.Sort((left, right) => right.Primary.CompareTo(left.Primary));
        return found;
    }

    /// <summary>Every taskbar Explorer has made, the main one first.</summary>
    public static List<FoundTaskbar> Find(IReadOnlyList<FoundMonitor> monitors)
    {
        ArgumentNullException.ThrowIfNull(monitors);

        List<FoundTaskbar> found = [];
        HWND main = PInvoke.FindWindow(MainClass, null);

        if (main != HWND.Null && Read(main, primary: true, monitors) is { } bar)
        {
            found.Add(bar);
        }

        HWND other = HWND.Null;

        for (int guard = 0; guard < 32; guard++)
        {
            other = PInvoke.FindWindowEx(HWND.Null, other, OtherClass, null);

            if (other == HWND.Null)
            {
                break;
            }

            if (Read(other, primary: false, monitors) is { } secondary)
            {
                found.Add(secondary);
            }
        }

        return found;
    }

    /// <summary>
    /// The main taskbar's window, how many taskbars there are, and whether the
    /// main one is drawing Windows 11's taskbar: cheap enough to ask every
    /// second and a half, and enough to notice a bar coming or going - or
    /// Explorer, just restarted, finishing its XAML only after the bar was
    /// first read as a classic one.
    /// </summary>
    public static (nint Main, int Count, bool Modern) Census()
    {
        HWND main = PInvoke.FindWindow(MainClass, null);
        int count = main == HWND.Null ? 0 : 1;
        bool modern = main != HWND.Null && IsModern(main);
        HWND other = HWND.Null;

        for (int guard = 0; guard < 32; guard++)
        {
            other = PInvoke.FindWindowEx(HWND.Null, other, OtherClass, null);

            if (other == HWND.Null)
            {
                break;
            }

            count++;
        }

        return (main, count, modern);
    }

    /// <summary>A window's rectangle in screen pixels, or nothing for a window that has gone.</summary>
    public static PixelRect? Rect(nint window)
    {
        if (window == 0 || !PInvoke.GetWindowRect(new HWND(window), out RECT rect))
        {
            return null;
        }

        return new PixelRect(rect.left, rect.top, rect.right, rect.bottom);
    }

    /// <summary>A window's dots per inch, 96 when it cannot say.</summary>
    public static int Dpi(nint window)
    {
        uint dpi = window == 0 ? 0 : PInvoke.GetDpiForWindow(new HWND(window));
        return dpi == 0 ? 96 : (int)dpi;
    }

    /// <summary>Whether Windows hides the taskbar until the pointer reaches it.</summary>
    public static bool AutoHide()
    {
        AppBarData data = new() { Size = (uint)sizeof(AppBarData) };
        nuint state = SHAppBarMessage(GetState, &data);

        return (state & AutoHideState) != 0;
    }

    /// <summary>
    /// The product that put a classic taskbar back on Windows 11, when it can
    /// be told from the settings it keeps.
    /// </summary>
    public static string? Replacement()
    {
        try
        {
            using RegistryKey? startAllBack = Registry.CurrentUser.OpenSubKey(@"Software\StartIsBack");

            if (startAllBack is not null)
            {
                return "StartAllBack";
            }

            using RegistryKey? patcher = Registry.CurrentUser.OpenSubKey(@"Software\ExplorerPatcher");

            return patcher is not null ? "ExplorerPatcher" : null;
        }
        catch (Exception ex) when (ex is System.Security.SecurityException or IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <summary>The thread Explorer draws a taskbar on, for watching it move.</summary>
    public static uint ThreadOf(nint window)
    {
        if (window == 0)
        {
            return 0;
        }

        uint process;
        return PInvoke.GetWindowThreadProcessId(new HWND(window), &process);
    }

    /// <summary>
    /// APPBARDATA, declared by hand: its padding differs between 32 and 64
    /// bits, so CsWin32 will not generate it for an AnyCPU build. Natural
    /// alignment gives the header's layout on both - 48 bytes on x64, 36 on
    /// x86.
    /// </summary>
    [StructLayout(LayoutKind.Sequential)]
    private struct AppBarData
    {
        public uint Size;
        public nint Window;
        public uint CallbackMessage;
        public uint Edge;
        public RECT Rect;
        public nint Param;
    }

    [LibraryImport("shell32.dll")]
    private static partial nuint SHAppBarMessage(uint message, AppBarData* data);

    private static FoundTaskbar? Read(HWND bar, bool primary, IReadOnlyList<FoundMonitor> monitors)
    {
        HMONITOR at = PInvoke.MonitorFromWindow(bar, MONITOR_FROM_FLAGS.MONITOR_DEFAULTTONEAREST);
        FoundMonitor? monitor = null;

        foreach (FoundMonitor each in monitors)
        {
            if (each.Handle == (nint)at)
            {
                monitor = each;
                break;
            }
        }

        monitor ??= Describe(at);

        if (monitor is not { } home)
        {
            return null;
        }

        TaskbarKind kind = IsModern(bar) ? TaskbarKind.Modern : TaskbarKind.Classic;
        bool mirrored = (unchecked((uint)PInvoke.GetWindowLong(bar, WINDOW_LONG_PTR_INDEX.GWL_EXSTYLE)) & LayoutRightToLeft) != 0;

        HWND tray = PInvoke.FindWindowEx(bar, HWND.Null, primary ? NotifyClass : ClockClass, null);
        HWND band = HWND.Null;

        if (primary && kind == TaskbarKind.Classic)
        {
            HWND rebar = PInvoke.FindWindowEx(bar, HWND.Null, RebarClass, null);
            band = rebar == HWND.Null ? HWND.Null : PInvoke.FindWindowEx(rebar, HWND.Null, BandClass, null);
        }

        return new FoundTaskbar(
            bar,
            primary,
            home,
            tray != HWND.Null && PInvoke.IsWindowVisible(tray) ? tray : 0,
            band,
            kind,
            mirrored);
    }

    private static bool IsModern(HWND bar)
    {
        HWND bridge = PInvoke.FindWindowEx(bar, HWND.Null, BridgeClass, null);
        return bridge != HWND.Null && PInvoke.IsWindowVisible(bridge);
    }

    private static FoundMonitor? Describe(nint handle)
    {
        if (handle == 0)
        {
            return null;
        }

        MONITORINFOEXW info = default;
        info.monitorInfo.cbSize = (uint)sizeof(MONITORINFOEXW);

        if (!PInvoke.GetMonitorInfo(new HMONITOR(handle), (MONITORINFO*)&info))
        {
            return null;
        }

        RECT bounds = info.monitorInfo.rcMonitor;
        RECT work = info.monitorInfo.rcWork;
        int dpi = 96;

        if (PInvoke.GetDpiForMonitor(new HMONITOR(handle), MONITOR_DPI_TYPE.MDT_EFFECTIVE_DPI, out uint x, out _).Succeeded
            && x > 0)
        {
            dpi = (int)x;
        }

        return new FoundMonitor(
            handle,
            info.szDevice.ToString(),
            new PixelRect(bounds.left, bounds.top, bounds.right, bounds.bottom),
            new PixelRect(work.left, work.top, work.right, work.bottom),
            (info.monitorInfo.dwFlags & 1) != 0,
            dpi);
    }
}
