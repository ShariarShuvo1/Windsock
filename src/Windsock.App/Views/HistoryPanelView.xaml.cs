using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using Windsock.App.Controls;
using Windsock.App.ViewModels;
using Windsock.Core.History;

namespace Windsock.App.Views;

/// <summary>The History panel: totals for a period, and the rows behind them.</summary>
public partial class HistoryPanelView : UserControl
{
    private const double PopoverGap = 14;
    private readonly HistoryPanelViewModel _viewModel;
    private ScrollViewer? _scroll;

    public HistoryPanelView(HistoryPanelViewModel viewModel)
    {
        InitializeComponent();

        _viewModel = viewModel;
        DataContext = viewModel;
        FromColumn.Header = viewModel.FromColumn;
        ToColumn.Header = viewModel.ToColumn;
        DownloadColumn.Header = viewModel.DownloadColumn;
        UploadColumn.Header = viewModel.UploadColumn;
        TotalColumn.Header = viewModel.TotalColumn;
        PeakDownColumn.Header = viewModel.PeakDownColumn;
        PeakUpColumn.Header = viewModel.PeakUpColumn;
        RecordedColumn.Header = viewModel.RecordedColumn;

        Chart.HoverChanged += OnChartHoverChanged;

        // The lanes keep to the usage chart's view, so a moment is in the same
        // place in both however far it is zoomed.
        Chart.ViewChanged += (_, _) => Lanes.Follow(Chart.View);
        Lanes.HoverChanged += OnLanesHoverChanged;

        // Clicking a bar goes to its row, on whatever page it is.
        Chart.BucketClicked += (_, e) => _viewModel.Reveal(e.Bucket.Start);
        viewModel.RowRevealed += OnRowRevealed;
        viewModel.PageTurned += (_, _) => ToTableTop();
        Table.PreviewKeyDown += (_, e) => e.Handled = PagerBar.TakeKey(_viewModel.Pager, e);
        TableWheel.Attach(Table);
        Table.Sorting += OnTableSorting;
        Table.MouseMove += OnTableMouseMove;
        Table.MouseLeave += (_, _) => _viewModel.HighlightedStart = null;

        viewModel.PropertyChanged += OnViewModelChanged;
        viewModel.ViewReset += (_, _) => ClearSort();

        ShowRecordedColumn();
        // Visibility rather than Loaded, which a minimised window never loses.
        IsVisibleChanged += (_, e) =>
        {
            if (e.NewValue is true)
            {
                _viewModel.Start();
            }
            else
            {
                _viewModel.Stop();
            }
        };

        Loaded += (_, _) => WatchScrolling();
        Unloaded += (_, _) => _viewModel.Stop();
    }

    private void OnTableMouseMove(object sender, MouseEventArgs e)
    {
        _viewModel.HighlightedStart =
            Ancestry.Find<DataGridRow>(e.OriginalSource as DependencyObject) is
                { Item: HistoryRowViewModel row }
                ? row.Start
                : null;
    }

    private void WatchScrolling()
    {
        if (_scroll is not null)
        {
            return;
        }

        // The page scrolls in Body; the grid's own scroller never moves, since
        // its scrolling is off. Following means the table's first rows have
        // not been scrolled past - the chart above them in view counts - so a
        // row arriving at the top never pushes down rows somebody is reading.
        _scroll = Body;
        _scroll.ScrollChanged += (_, _) => _viewModel.IsFollowing = TableTopNotPassed();
    }

    private bool TableTopNotPassed() =>
        !Table.IsVisible || Table.TranslatePoint(new Point(0, 0), Body).Y > -48;

    private void OnViewModelChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(HistoryPanelViewModel.SelectedResolution))
        {
            ShowRecordedColumn();
        }
    }

    private void ShowRecordedColumn() =>
        RecordedColumn.Visibility = _viewModel.SelectedResolution == UsageGranularity.Minute
            ? Visibility.Collapsed
            : Visibility.Visible;

    private void OnTableSorting(object? sender, DataGridSortingEventArgs e)
    {
        ArgumentNullException.ThrowIfNull(e);
        e.Handled = true;

        string path = e.Column.SortMemberPath;

        if (string.IsNullOrEmpty(path))
        {
            return;
        }

        ListSortDirection direction = e.Column.SortDirection switch
        {
            ListSortDirection.Ascending => ListSortDirection.Descending,
            ListSortDirection.Descending => ListSortDirection.Ascending,
            _ => FirstDirection(e.Column),
        };

        foreach (DataGridColumn column in Table.Columns)
        {
            column.SortDirection = column == e.Column ? direction : null;
        }

        // The whole period is sorted, not the page on screen; see PagedView.
        _viewModel.SortBy(path, direction);
        ToTableTop();
    }

    private void OnRowRevealed(object? sender, HistoryRowViewModel row) =>
        Dispatcher.BeginInvoke(
            System.Windows.Threading.DispatcherPriority.Loaded,
            new Action(() => (Table.ItemContainerGenerator.ContainerFromItem(row) as FrameworkElement)?.BringIntoView()));

    /// <summary>Scrolls up to the table's heading when a new page replaces the rows under the reader.</summary>
    private void ToTableTop() =>
        Dispatcher.BeginInvoke(
            System.Windows.Threading.DispatcherPriority.Loaded,
            new Action(() => Table.BringIntoView(new Rect(0, 0, Math.Max(1, Table.ActualWidth), 40))));

    private static ListSortDirection FirstDirection(DataGridColumn column) =>
        column.Header is TableColumn model && model.DescendingFirst
            ? ListSortDirection.Descending
            : ListSortDirection.Ascending;

    private void ClearSort()
    {
        foreach (DataGridColumn column in Table.Columns)
        {
            column.SortDirection = null;
        }

        _viewModel.SortBy(null, ListSortDirection.Ascending);
    }

    private void OnChartHoverChanged(object? sender, UsageHoverEventArgs e)
    {
        _viewModel.SetHover(e.HasValue, e.Bucket);

        if (!e.HasValue)
        {
            Popover.Visibility = Visibility.Collapsed;
            return;
        }

        Place(Popover, PopoverLayer, e.Position);
    }

    private void OnLanesHoverChanged(object? sender, LaneHoverEventArgs e)
    {
        _viewModel.SetMachineHover(e.HasValue, e.Start);

        if (!e.HasValue)
        {
            LanesPopover.Visibility = Visibility.Collapsed;
            return;
        }

        Place(LanesPopover, LanesPopoverLayer, e.Position);
    }

    private static void Place(FrameworkElement popover, FrameworkElement layer, Point position)
    {
        popover.Visibility = Visibility.Visible;
        popover.UpdateLayout();

        double layerWidth = layer.ActualWidth;
        double layerHeight = layer.ActualHeight;
        double width = popover.ActualWidth;
        double height = popover.ActualHeight;

        // Prefer the right of the pointer, flip to the left near the far edge.
        double left = position.X + PopoverGap;

        if (left + width > layerWidth)
        {
            left = position.X - PopoverGap - width;
        }

        // Prefer above the pointer, flip below when there is no room.
        double top = position.Y - height - PopoverGap;

        if (top < 0)
        {
            top = position.Y + PopoverGap;
        }

        // A popover taller than its chart - one listing apps - rises over what
        // is above the chart in the same card rather than being cut off below.
        double lowest = Math.Min(0, layerHeight - height);

        Canvas.SetLeft(popover, Math.Round(Math.Clamp(left, 0, Math.Max(0, layerWidth - width))));
        Canvas.SetTop(popover, Math.Round(Math.Clamp(top, lowest, Math.Max(0, layerHeight - height))));
    }

}
