using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.Graphics.Gdi;
using Windows.Win32.UI.WindowsAndMessaging;
using Windsock.Core.Docking;
using Windsock.Core.Settings;
using Windsock.Core.Windowing;

namespace Windsock.App.Services;

/// <summary>
/// Keeps where the main window was on each monitor, and puts it back there,
/// never anywhere it cannot be seen.
/// </summary>
/// <remarks>
/// <para>
/// Places are kept in each monitor's physical pixels, with that monitor's DPI,
/// and put back with <c>SetWindowPos</c> rather than WPF's Left and Top, which
/// are device-independent units read at whatever DPI the window had before it
/// moved. The choosing and fitting is <see cref="WindowFit"/> in Core, tested.
/// </para>
/// <para>
/// Put back in two steps: moved onto its monitor first, which sends the window
/// WM_DPICHANGED when that monitor's scale differs and lets WPF resize it as
/// Windows suggests, then given the exact size kept for it there.
/// </para>
/// </remarks>
internal static class WindowPlacement
{
    /// <summary>
    /// The place to put the window back at, or nothing when no kept place
    /// belongs to a monitor that is there now.
    /// </summary>
    public static (PixelRect Bounds, bool Maximised)? Planned(WindowSettings window, double minimumWidth, double minimumHeight)
    {
        ArgumentNullException.ThrowIfNull(window);

        try
        {
            List<ScreenArea> monitors = [.. Taskbars.Monitors().Select(Area)];

            if (WindowFit.Choose(window, monitors) is not { } chosen)
            {
                return null;
            }

            return (WindowFit.Fit(chosen.Place, chosen.Monitor, minimumWidth, minimumHeight), chosen.Place.Maximised);
        }
#pragma warning disable CA1031 // Monitors that cannot be read mean opening where Windows puts new windows, as before.
        catch (Exception)
#pragma warning restore CA1031
        {
            return null;
        }
    }

    /// <summary>Puts a window whose handle exists at a planned place, before it is shown.</summary>
    public static void Restore(Window window, PixelRect bounds)
    {
        ArgumentNullException.ThrowIfNull(window);

        HWND handle = new(new WindowInteropHelper(window).Handle);

        if (handle.IsNull)
        {
            return;
        }

        const SET_WINDOW_POS_FLAGS Quiet = SET_WINDOW_POS_FLAGS.SWP_NOZORDER | SET_WINDOW_POS_FLAGS.SWP_NOACTIVATE;

        _ = PInvoke.SetWindowPos(handle, HWND.Null, bounds.Left, bounds.Top, 0, 0, Quiet | SET_WINDOW_POS_FLAGS.SWP_NOSIZE);
        _ = PInvoke.SetWindowPos(handle, HWND.Null, bounds.Left, bounds.Top, bounds.Width, bounds.Height, Quiet);
    }

    /// <summary>
    /// Keeps where a window is, on the monitor it is on, as its restored size
    /// and place when it is maximised. Nothing is kept for a minimised window.
    /// </summary>
    public static void Remember(Window window, WindowSettings settings)
    {
        ArgumentNullException.ThrowIfNull(window);
        ArgumentNullException.ThrowIfNull(settings);

        HWND handle = new(new WindowInteropHelper(window).Handle);

        if (handle.IsNull || window.WindowState == WindowState.Minimized)
        {
            return;
        }

        try
        {
            HMONITOR monitor = PInvoke.MonitorFromWindow(handle, MONITOR_FROM_FLAGS.MONITOR_DEFAULTTONEAREST);
            FoundMonitor? found = Taskbars.Monitors().FirstOrDefault(each => each.Handle == (nint)monitor);

            if (found is not { } on)
            {
                return;
            }

            PixelRect bounds;
            bool maximised = window.WindowState == WindowState.Maximized;

            if (maximised)
            {
                // A maximised window's own rectangle is the monitor; the place to
                // keep is where it goes back to, which WPF holds in its units.
                DpiScale dpi = VisualTreeHelper.GetDpi(window);
                Rect restore = window.RestoreBounds;

                if (restore.IsEmpty)
                {
                    return;
                }

                bounds = PixelRect.FromSize(
                    (int)Math.Round(restore.Left * dpi.DpiScaleX),
                    (int)Math.Round(restore.Top * dpi.DpiScaleY),
                    (int)Math.Round(restore.Width * dpi.DpiScaleX),
                    (int)Math.Round(restore.Height * dpi.DpiScaleY));
            }
            else if (PInvoke.GetWindowRect(handle, out RECT rect))
            {
                bounds = new PixelRect(rect.left, rect.top, rect.right, rect.bottom);
            }
            else
            {
                return;
            }

            if (bounds.IsEmpty)
            {
                return;
            }

            settings.Remember(new WindowPlace
            {
                Monitor = on.Name,
                Left = bounds.Left,
                Top = bounds.Top,
                Width = bounds.Width,
                Height = bounds.Height,
                Dpi = on.Dpi,
                Maximised = maximised,
            });
        }
#pragma warning disable CA1031 // Not keeping a place is no reason to stop a window closing.
        catch (Exception)
#pragma warning restore CA1031
        {
        }
    }

    private static ScreenArea Area(FoundMonitor monitor) =>
        new(monitor.Name, monitor.Bounds, monitor.Work, monitor.Primary, monitor.Dpi);
}
