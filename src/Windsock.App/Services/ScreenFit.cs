using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.Graphics.Gdi;

namespace Windsock.App.Services;

internal static class ScreenFit
{
    /// <summary>
    /// The working area of the monitor a window is on, in the units window
    /// positions are written in.
    /// </summary>
    public static Rect Room(Window? beside)
    {
        if (beside is null)
        {
            return SystemParameters.WorkArea;
        }

        nint handle = new WindowInteropHelper(beside).Handle;

        if (handle == 0)
        {
            return SystemParameters.WorkArea;
        }

        if (Reading(handle) is not { } reading)
        {
            return SystemParameters.WorkArea;
        }

        DpiScale dpi = VisualTreeHelper.GetDpi(beside);

        return new Rect(
            reading.rcWork.left / dpi.DpiScaleX,
            reading.rcWork.top / dpi.DpiScaleY,
            (reading.rcWork.right - reading.rcWork.left) / dpi.DpiScaleX,
            (reading.rcWork.bottom - reading.rcWork.top) / dpi.DpiScaleY);
    }

    private static unsafe MONITORINFO? Reading(nint handle)
    {
        HMONITOR monitor = PInvoke.MonitorFromWindow(
            new HWND(handle),
            MONITOR_FROM_FLAGS.MONITOR_DEFAULTTONEAREST);

        if (monitor.IsNull)
        {
            return null;
        }

        MONITORINFO reading = new() { cbSize = (uint)sizeof(MONITORINFO) };

        return PInvoke.GetMonitorInfo(monitor, ref reading) ? reading : null;
    }
}
