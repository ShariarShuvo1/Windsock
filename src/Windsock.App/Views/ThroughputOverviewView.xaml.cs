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
        Chart.VisibleSampleCount = viewModel.DefaultVisibleSamples;

        Chart.HoverChanged += OnChartHoverChanged;
        IsVisibleChanged += (_, args) =>
        {
            if (args.NewValue is true)
            {
                _viewModel.Start();
            }
            else
            {
                _viewModel.Stop();
            }
        };

        Unloaded += (_, _) => _viewModel.Stop();
    }

    private void ResetZoom()
    {
        Chart.BeginAnimation(ThroughputChart.VisibleSampleCountProperty, null);
        Chart.VisibleSampleCount = _viewModel.DefaultVisibleSamples;
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
