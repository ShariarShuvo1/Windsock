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
    private readonly ChartMarkersFeed _markers;
    private readonly SignalStripViewModel _signal;
    private readonly LanSplitViewModel _split;

    public ThroughputOverviewView(ThroughputOverviewViewModel viewModel, ChartMarkersFeed markers, SignalStripViewModel signal, LanSplitViewModel split)
    {
        ArgumentNullException.ThrowIfNull(markers);
        ArgumentNullException.ThrowIfNull(signal);
        ArgumentNullException.ThrowIfNull(split);

        InitializeComponent();

        // The local-network and internet split needs the kernel trace, which
        // the Store edition is built without.
        _split = split;
#if STORE
        ((Panel)LanSplit.Parent).Children.Remove(LanSplit);
#else
        LanSplit.DataContext = split;
#endif

        // The Wi-Fi signal strip under the plot has its own view model.
        _signal = signal;
        Signal.DataContext = signal;

        _viewModel = viewModel;
        _markers = markers;
        DataContext = viewModel;

        // Outages, network changes and alerts marked on the plot, placed against
        // the newest sample's time, which moves only while the chart is live.
        // Followed while loaded: the view model outlives the window, and must
        // not keep it.
        Loaded += (_, _) =>
        {
            _markers.PropertyChanged -= OnMarkersChanged;
            _markers.PropertyChanged += OnMarkersChanged;
            _viewModel.PropertyChanged -= OnChartChanged;
            _viewModel.PropertyChanged += OnChartChanged;
            Chart.Markers = _markers.Markers;
        };

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
            _markers.Stop();
            _signal.Follow(false);
            _split.Stop();
            _markers.PropertyChanged -= OnMarkersChanged;
            _viewModel.PropertyChanged -= OnChartChanged;
        };
    }

    private void Follow()
    {
        if (IsVisible || ChartHost.IsDetached)
        {
            _viewModel.Start();
            _markers.Start();
            _signal.Follow(true);
            _split.Start();
            Chart.LatestSampleTime = _markers.Latest;
            return;
        }

        _viewModel.Stop();
        _markers.Stop();
        _signal.Follow(false);
        _split.Stop();
    }

    private void OnMarkersChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(ChartMarkersFeed.Markers))
        {
            Chart.Markers = _markers.Markers;
        }
    }

    private void OnChartChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        // A new sample on the chart moves "now", and every mark with it.
        if (e.PropertyName == nameof(ThroughputOverviewViewModel.SeriesRevision))
        {
            Chart.LatestSampleTime = _markers.Latest;
        }
    }

    private void OnChartHoverChanged(object? sender, ChartHoverEventArgs e)
    {
        _viewModel.SetHover(e.HasValue, e.DownloadBytesPerSecond, e.UploadBytesPerSecond, e.SecondsAgo);

        HoverEvent.Text = e.Marker is { } marker
            ? (marker.Detail.Length > 0 ? string.Concat(marker.Title, " - ", marker.Detail) : marker.Title)
            : string.Empty;
        HoverEvent.Visibility = e.Marker is null ? Visibility.Collapsed : Visibility.Visible;

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
