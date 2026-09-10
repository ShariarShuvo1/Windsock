using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using Windsock.App.ViewModels;

namespace Windsock.App.Views;

/// <summary>
/// The window that shows the live readings inside the Windows taskbar.
/// </summary>
public partial class TaskbarMeterWindow : Window
{
    public TaskbarMeterWindow(TaskbarMeterViewModel viewModel)
    {
        ArgumentNullException.ThrowIfNull(viewModel);

        InitializeComponent();
        DataContext = viewModel;

        Root.MouseLeftButtonUp += OnPressed;
    }

    /// <summary>Raised when the reader clicks the meter.</summary>
    public event EventHandler? Chosen;

    /// <summary>This window's handle, or zero before it has one.</summary>
    public nint Handle => new WindowInteropHelper(this).Handle;

    /// <summary>Creates the window's handle without showing the window.</summary>
    public nint Realise() => new WindowInteropHelper(this).EnsureHandle();

    /// <summary>
    /// How wide the readings would like to be, in device-independent pixels.
    /// </summary>
    public double DesiredWidth(double height)
    {
        Readings.Measure(new Size(double.PositiveInfinity, Math.Max(1, height)));
        return Readings.DesiredSize.Width;
    }

    private void OnPressed(object sender, MouseButtonEventArgs e)
    {
        ArgumentNullException.ThrowIfNull(e);

        Chosen?.Invoke(this, EventArgs.Empty);
        e.Handled = true;
    }
}
