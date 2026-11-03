using System.Windows.Controls;
using Windsock.App.ViewModels;

namespace Windsock.App.Views;

/// <summary>
/// One part of the machine - whichever the sidebar has chosen - and what it is
/// doing.
/// </summary>
/// <remarks>
/// One view for all five parts, handed to the page by <c>MainWindow</c> whenever
/// one of them is chosen. It does not start or stop the readings itself: the
/// sidebar shows a live figure beside each part on every page, so the window
/// decides that.
/// </remarks>
public partial class SystemPanelView : UserControl
{
    public SystemPanelView(SystemPanelViewModel viewModel, ThroughputOverviewView networkChart)
    {
        ArgumentNullException.ThrowIfNull(viewModel);

        InitializeComponent();

        DataContext = viewModel;

        // The live throughput chart is a view of its own, shared in its view
        // model with the windows for single processes, and it follows its own
        // visibility: shown only while the network is the part chosen.
        NetworkChart.Content = networkChart;

        // The readings run whenever the window is up, for the sidebar; the
        // busiest processes and a part's history are only for the page itself.
        IsVisibleChanged += (_, _) => viewModel.SetOnScreen(IsVisible);
    }

    private void OnPowerSettings(object sender, System.Windows.RoutedEventArgs e)
    {
        try
        {
            using System.Diagnostics.Process? settings = System.Diagnostics.Process.Start(
                new System.Diagnostics.ProcessStartInfo("ms-settings:powersleep") { UseShellExecute = true });
        }
        catch (System.ComponentModel.Win32Exception)
        {
            // Settings is missing or refused: nothing to do but stay on the page.
        }
    }

    /// <summary>The readings behind the view, for the sidebar beside it.</summary>
    public SystemPanelViewModel ViewModel => (SystemPanelViewModel)DataContext;
}
