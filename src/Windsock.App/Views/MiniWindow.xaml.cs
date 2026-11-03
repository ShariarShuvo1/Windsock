using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.UI.WindowsAndMessaging;
using Windsock.App.Services;
using Windsock.App.ViewModels;
using Windsock.Core.Docking;
using Windsock.Core.Meter;
using Windsock.Core.Settings;
using Windsock.Core.Windowing;

namespace Windsock.App.Views;

/// <summary>
/// The compact window: the rates and their last minute, always on top.
/// </summary>
/// <remarks>
/// Its place is kept in screen pixels with the monitor's name, like the
/// floating meter's, and put back with <c>SetWindowPos</c> before it is first
/// drawn - WPF's Left and Top are device-independent units read at whatever
/// DPI the window had, which is wrong on a monitor of another scale.
/// </remarks>
public partial class MiniWindow : Window
{
    private readonly MiniViewModel _viewModel;
    private readonly WindsockSettings _settings;
    private readonly ISettingsStore _store;

    public MiniWindow(MiniViewModel viewModel, WindsockSettings settings, ISettingsStore store)
    {
        ArgumentNullException.ThrowIfNull(viewModel);

        InitializeComponent();

        _viewModel = viewModel;
        _settings = settings;
        _store = store;
        DataContext = viewModel;

        SourceInitialized += (_, _) => Place();
        Frame.MouseLeftButtonDown += OnPressing;
        Frame.MouseRightButtonUp += OnMenu;
        Frame.Invoked += (_, _) => OpenRequested?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Raised when the reader double-clicks it, asking for the main window.</summary>
    public event EventHandler? OpenRequested;

    /// <summary>Raised when the reader right-clicks it, asking for the menu.</summary>
    public event EventHandler? MenuRequested;

    /// <summary>
    /// Whether the menu now asked for was asked for from the keyboard, so it
    /// opens against the window rather than wherever the pointer happens to be.
    /// </summary>
    public bool MenuFromKeyboard { get; private set; }

    /// <inheritdoc />
    /// <remarks>
    /// The keys do what the mouse does: Enter is the double-click, Shift+F10
    /// and the menu key the right-click, and Escape closes it.
    /// </remarks>
    protected override void OnKeyDown(KeyEventArgs e)
    {
        ArgumentNullException.ThrowIfNull(e);

        Key key = e.Key == Key.System ? e.SystemKey : e.Key;

        switch (key)
        {
            case Key.Escape:
                e.Handled = true;
                Close();
                return;

            case Key.Enter:
                e.Handled = true;
                OpenRequested?.Invoke(this, EventArgs.Empty);
                return;

            case Key.Apps:
            case Key.F10 when (Keyboard.Modifiers & ModifierKeys.Shift) != 0:
                e.Handled = true;
                AskForMenu(fromKeyboard: true);
                return;
        }

        base.OnKeyDown(e);
    }

    /// <inheritdoc />
    /// <remarks>
    /// Its place is kept here rather than once it has closed: by then the
    /// window has no handle to ask where it is, as WindowPlacement found.
    /// </remarks>
    protected override void OnClosing(System.ComponentModel.CancelEventArgs e)
    {
        Remember();
        base.OnClosing(e);
    }

    /// <inheritdoc />
    protected override void OnClosed(EventArgs e)
    {
        _viewModel.Dispose();
        base.OnClosed(e);
    }

    private void OnPressing(object sender, MouseButtonEventArgs e)
    {
        e.Handled = true;

        if (e.ClickCount >= 2)
        {
            OpenRequested?.Invoke(this, EventArgs.Empty);
            return;
        }

        try
        {
            DragMove();
        }
        catch (InvalidOperationException)
        {
            // The button was already up: a click, which does nothing here.
            return;
        }

        Remember();
    }

    private void OnMenu(object sender, MouseButtonEventArgs e)
    {
        e.Handled = true;
        AskForMenu(fromKeyboard: false);
    }

    private void AskForMenu(bool fromKeyboard)
    {
        MenuFromKeyboard = fromKeyboard;

        try
        {
            MenuRequested?.Invoke(this, EventArgs.Empty);
        }
        finally
        {
            MenuFromKeyboard = false;
        }
    }

    /// <summary>Puts the window where it was left, or in its default corner, before it is drawn.</summary>
    private void Place()
    {
        HWND handle = new(new WindowInteropHelper(this).Handle);

        if (handle.IsNull)
        {
            return;
        }

        try
        {
            List<FoundMonitor> found = Taskbars.Monitors();
            List<ScreenArea> monitors = [.. found.Select(each => new ScreenArea(each.Name, each.Bounds, each.Work, each.Primary, each.Dpi))];

            // Measured here, before the first layout, at the scale of the
            // monitor it is about to land on; WPF then resizes it to match
            // as the move tells it the DPI.
            Frame.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
            Size wanted = Frame.DesiredSize;
            MeterSpot? spot = _settings.Taskbar.MiniSpot;
            int dpi = found.Find(each => spot is not null && each.Work.Contains(spot.X, spot.Y)).Dpi is > 0 and var on
                ? on
                : found.Find(each => each.Primary).Dpi is > 0 and var main ? main : 96;

            int width = (int)Math.Ceiling(wanted.Width * dpi / 96.0);
            int height = (int)Math.Ceiling(wanted.Height * dpi / 96.0);
            PixelRect chosen = MiniPlace.Choose(spot, monitors, width, height);

            _ = PInvoke.SetWindowPos(
                handle,
                HWND.Null,
                chosen.Left,
                chosen.Top,
                0,
                0,
                SET_WINDOW_POS_FLAGS.SWP_NOSIZE | SET_WINDOW_POS_FLAGS.SWP_NOZORDER | SET_WINDOW_POS_FLAGS.SWP_NOACTIVATE);
        }
#pragma warning disable CA1031 // A monitor that cannot be read leaves the window where Windows put it.
        catch (Exception)
#pragma warning restore CA1031
        {
        }
    }

    /// <summary>Keeps where the window is, in pixels, with its monitor's name.</summary>
    private void Remember()
    {
        nint handle = new WindowInteropHelper(this).Handle;

        if (handle == 0 || Taskbars.Rect(handle) is not { } rect)
        {
            return;
        }

        try
        {
            FoundMonitor monitor = Taskbars.Monitors().Find(each => each.Bounds.Contains(rect.Left, rect.Top));
            MeterSpot? before = _settings.Taskbar.MiniSpot;

            if (before is not null && before.X == rect.Left && before.Y == rect.Top && before.Monitor == (monitor.Name ?? string.Empty))
            {
                return;
            }

            _settings.Taskbar.MiniSpot = new MeterSpot { Monitor = monitor.Name ?? string.Empty, X = rect.Left, Y = rect.Top };
            _store.Save(_settings);
        }
#pragma warning disable CA1031 // A place that cannot be kept is found again next time; the window still closes.
        catch (Exception)
#pragma warning restore CA1031
        {
        }
    }
}
