using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.UI.WindowsAndMessaging;

namespace Windsock.App.Services;

internal readonly record struct DockPlacement(
    nint Host,
    int X,
    int Y,
    int Width,
    int Height,
    uint Dpi);

internal static class TaskbarDock
{
    private const string TaskbarClass = "Shell_TrayWnd";
    private const string NotifyClass = "TrayNotifyWnd";
    private const int Gap = 6;
    private const int Inset = 3;

    /// <summary>Where a taskbar could not be found.</summary>
    public static DockPlacement None => default;

    /// <summary>The taskbar on the primary display, or zero when there is none.</summary>
    public static nint Taskbar()
    {
        HWND taskbar = PInvoke.FindWindow(TaskbarClass, null);
        return taskbar;
    }

    /// <summary>
    /// Finds the taskbar and the edge the meter hangs from.
    /// </summary>
    public static DockPlacement Frame()
    {
        nint taskbar = Taskbar();

        if (taskbar == 0)
        {
            return None;
        }

        var host = new HWND(taskbar);

        if (!PInvoke.GetWindowRect(host, out RECT bar))
        {
            return None;
        }

        HWND notify = PInvoke.FindWindowEx(host, HWND.Null, NotifyClass, null);

        int right = notify != HWND.Null && PInvoke.GetWindowRect(notify, out RECT area)
            ? area.left
            : bar.right;

        return new DockPlacement(
            taskbar,
            right - bar.left - Gap,
            Inset,
            0,
            Math.Max(1, bar.bottom - bar.top - (Inset * 2)),
            Dpi(host));
    }

    /// <summary>Hangs a meter of a given width from a frame's right edge.</summary>
    public static DockPlacement Hang(DockPlacement frame, int width) =>
        frame with
        {
            X = frame.X - width,
            Width = Math.Max(1, width),
        };

    /// <summary>The same placement, in screen coordinates, for a floating meter.</summary>
    public static DockPlacement OnScreen(DockPlacement placement)
    {
        if (placement.Host == 0 || !PInvoke.GetWindowRect(new HWND(placement.Host), out RECT bar))
        {
            return placement;
        }

        return placement with
        {
            X = bar.left + placement.X,
            Y = bar.top + placement.Y,
        };
    }

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
    /// Puts the window where the placement says, and above the taskbar's own
    /// content.
    /// </summary>
    public static bool Place(nint window, DockPlacement placement, bool floating)
    {
        if (window == 0)
        {
            return false;
        }
        HWND order = floating ? new HWND(-1) : HWND.Null;

        return PInvoke.SetWindowPos(
            new HWND(window),
            order,
            placement.X,
            placement.Y,
            placement.Width,
            placement.Height,
            SET_WINDOW_POS_FLAGS.SWP_NOACTIVATE | SET_WINDOW_POS_FLAGS.SWP_SHOWWINDOW);
    }

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

    private static uint Dpi(HWND window)
    {
        if (!OperatingSystem.IsWindowsVersionAtLeast(10, 0, 14393))
        {
            return 96;
        }

        uint dpi = PInvoke.GetDpiForWindow(window);
        return dpi == 0 ? 96 : dpi;
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
