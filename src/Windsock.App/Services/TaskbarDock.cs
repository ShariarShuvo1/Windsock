using System.Drawing;
using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.UI.WindowsAndMessaging;
using Windsock.Core.Docking;

namespace Windsock.App.Services;

/// <summary>
/// The Win32 calls that put a meter window into a taskbar, move it, and take
/// it out again. Every one of them is allowed to fail: a call that does means
/// no meter on that taskbar, never a crash.
/// </summary>
internal static class TaskbarDock
{
    /// <summary>
    /// Sent to a child window after its top-level parent changed DPI. WPF
    /// answers it by reading the window's DPI again, which is what a meter
    /// parented into a taskbar on another monitor needs.
    /// </summary>
    private const uint DpiChangedAfterParent = 0x02E3;

    /// <summary>
    /// Makes <paramref name="window"/> a child of the taskbar.
    /// </summary>
    public static bool Attach(nint window, nint host)
    {
        if (window == 0 || host == 0)
        {
            return false;
        }

        var child = new HWND(window);

        if (PInvoke.SetParent(child, new HWND(host)) == HWND.Null)
        {
            return false;
        }

        Style(child, style => (style & ~(uint)WINDOW_STYLE.WS_POPUP) | (uint)WINDOW_STYLE.WS_CHILD);

        return true;
    }

    /// <summary>
    /// Takes the window back out of the taskbar.
    /// </summary>
    public static void Detach(nint window)
    {
        if (window == 0 || !PInvoke.IsWindow(new HWND(window)))
        {
            return;
        }

        var child = new HWND(window);

        PInvoke.SetParent(child, HWND.Null);
        Style(child, style => (style & ~(uint)WINDOW_STYLE.WS_CHILD) | (uint)WINDOW_STYLE.WS_POPUP);
    }

    /// <summary>
    /// Tells WPF to read the window's DPI again, now that it has a parent
    /// whose DPI decides its own.
    /// </summary>
    /// <remarks>
    /// A child window gets no WM_DPICHANGED of its own. Its DPI is its
    /// top-level parent's, and the meter's top-level parent is Explorer's
    /// taskbar, which on another monitor can be at a different scale from
    /// wherever WPF first made the window. WPF handles the message Windows
    /// sends children after a parent's DPI changes by reading the DPI again,
    /// so the same message, sent after parenting, brings it up to date.
    /// </remarks>
    public static void SyncDpi(nint window)
    {
        if (window != 0)
        {
            _ = PInvoke.SendMessage(new HWND(window), DpiChangedAfterParent, 0, 0);
        }
    }

    /// <summary>Marks a window as one that never takes focus and never appears in Alt+Tab.</summary>
    public static void Aside(nint window, bool topmost)
    {
        if (window == 0)
        {
            return;
        }

        uint wanted = (uint)WINDOW_EX_STYLE.WS_EX_TOOLWINDOW | (uint)WINDOW_EX_STYLE.WS_EX_NOACTIVATE;
        uint floating = (uint)WINDOW_EX_STYLE.WS_EX_TOPMOST;

        ExStyle(
            new HWND(window),
            style => topmost ? style | wanted | floating : (style | wanted) & ~floating);
    }

    /// <summary>
    /// Puts the window where it belongs - in its parent's coordinates when it
    /// is docked, in screen coordinates when it floats - and shows it.
    /// </summary>
    public static bool Place(nint window, PixelRect rect, bool floating)
    {
        if (window == 0)
        {
            return false;
        }

        HWND order = floating ? new HWND(-1) : HWND.Null;

        return PInvoke.SetWindowPos(
            new HWND(window),
            order,
            rect.Left,
            rect.Top,
            Math.Max(1, rect.Width),
            Math.Max(1, rect.Height),
            SET_WINDOW_POS_FLAGS.SWP_NOACTIVATE | SET_WINDOW_POS_FLAGS.SWP_SHOWWINDOW);
    }

    /// <summary>Hides the window without destroying it.</summary>
    public static void Hide(nint window)
    {
        if (window != 0)
        {
            _ = PInvoke.ShowWindow(new HWND(window), SHOW_WINDOW_CMD.SW_HIDE);
        }
    }

    /// <summary>Whether the window is on screen.</summary>
    public static bool IsShown(nint window) => window != 0 && PInvoke.IsWindowVisible(new HWND(window));

    /// <summary>
    /// Whether the window is still the frontmost child of its taskbar.
    /// </summary>
    public static bool IsInFront(nint window, nint host)
    {
        if (window == 0 || host == 0)
        {
            return false;
        }

        HWND top = PInvoke.GetWindow(new HWND(host), GET_WINDOW_CMD.GW_CHILD);
        return top == new HWND(window);
    }

    /// <summary>Whether a window handle still refers to a live window.</summary>
    public static bool Alive(nint window) => window != 0 && PInvoke.IsWindow(new HWND(window));

    /// <summary>Brings a window to the front, focus and all.</summary>
    public static void Raise(nint window)
    {
        if (window != 0)
        {
            PInvoke.SetForegroundWindow(new HWND(window));
        }
    }

    /// <summary>
    /// Moves a classic taskbar's task band to <paramref name="screen"/>, which
    /// is how its buttons are made to lay themselves out clear of the meter.
    /// </summary>
    public static bool MoveBand(nint band, PixelRect screen)
    {
        if (band == 0 || screen.IsEmpty || !PInvoke.IsWindow(new HWND(band)))
        {
            return false;
        }

        HWND parent = PInvoke.GetParent(new HWND(band));
        Point near = new(screen.Left, screen.Top);
        Point far = new(screen.Right, screen.Bottom);

        // Both corners, and the nearer of the two: in a mirrored parent - a
        // right-to-left Windows - the screen's left edge is the client's right.
        if (parent != HWND.Null && (!PInvoke.ScreenToClient(parent, ref near) || !PInvoke.ScreenToClient(parent, ref far)))
        {
            return false;
        }

        return PInvoke.SetWindowPos(
            new HWND(band),
            HWND.Null,
            Math.Min(near.X, far.X),
            Math.Min(near.Y, far.Y),
            screen.Width,
            screen.Height,
            SET_WINDOW_POS_FLAGS.SWP_NOACTIVATE | SET_WINDOW_POS_FLAGS.SWP_NOZORDER | SET_WINDOW_POS_FLAGS.SWP_NOOWNERZORDER);
    }

    private static void Style(HWND window, Func<uint, uint> change) =>
        Apply(window, WINDOW_LONG_PTR_INDEX.GWL_STYLE, change);

    private static void ExStyle(HWND window, Func<uint, uint> change) =>
        Apply(window, WINDOW_LONG_PTR_INDEX.GWL_EXSTYLE, change);

    private static void Apply(HWND window, WINDOW_LONG_PTR_INDEX index, Func<uint, uint> change)
    {
        uint current = unchecked((uint)PInvoke.GetWindowLong(window, index));
        uint wanted = change(current);

        if (wanted != current)
        {
            _ = PInvoke.SetWindowLong(window, index, unchecked((int)wanted));
        }
    }
}
