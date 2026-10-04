using System.Windows;
using System.Windows.Controls;
using Windsock.App.Controls;
using Windsock.App.ViewModels;

namespace Windsock.App.Views;

/// <summary>
/// The throughput section: live download and upload readouts beside a rolling
/// chart, with a hover readout and wheel zoom over the plot.
/// </summary>
public partial class ThroughputOverviewView : UserControl
{
    private const double PopoverGap = 14;
    private readonly ThroughputOverviewViewModel _viewModel;

    public ThroughputOverviewView(ThroughputOverviewViewModel viewModel)
    {
        InitializeComponent();

        _viewModel = viewModel;
        DataContext = viewModel;

        // The view model outlives the window; a pause does not.
        viewModel.Resume();
        Chart.VisibleSampleCount = viewModel.DefaultVisibleSamples;

        Chart.HoverChanged += OnChartHoverChanged;

        // Live while it can be seen: on the page, or out in a window of its own.
        // Following the page alone froze a lifted chart the moment another tile
        // or section was chosen, because the page it came from was no longer
        // showing.
        IsVisibleChanged += (_, _) => Follow();
        ChartHost.DetachedChanged += (_, _) => Follow();

        Unloaded += (_, _) =>
        {
            ChartHost.Recall();
            _viewModel.Stop();
        };
    }

    private void Follow()
    {
        if (IsVisible || ChartHost.IsDetached)
        {
            _viewModel.Start();
            return;
        }

        _viewModel.Stop();
    }

    private void OnChartHoverChanged(object? sender, ChartHoverEventArgs e)
    {
        _viewModel.SetHover(e.HasValue, e.DownloadBytesPerSecond, e.UploadBytesPerSecond, e.SecondsAgo);

        if (!e.HasValue)
        {
            Popover.Visibility = Visibility.Collapsed;
            return;
        }

        Popover.Visibility = Visibility.Visible;
        Popover.UpdateLayout();

        double layerWidth = PopoverLayer.ActualWidth;
        double layerHeight = PopoverLayer.ActualHeight;
        double width = Popover.ActualWidth;
        double height = Popover.ActualHeight;

        // Prefer the right of the pointer, flip to the left near the far edge.
        double left = e.Position.X + PopoverGap;
        if (left + width > layerWidth)
        {
            left = e.Position.X - PopoverGap - width;
        }

        // Prefer above the pointer, flip below when there is no room.
        double top = e.Position.Y - height - PopoverGap;
        if (top < 0)
        {
            top = e.Position.Y + PopoverGap;
        }

        Canvas.SetLeft(Popover, Math.Round(Math.Clamp(left, 0, Math.Max(0, layerWidth - width))));
        Canvas.SetTop(Popover, Math.Round(Math.Clamp(top, 0, Math.Max(0, layerHeight - height))));
    }
}
