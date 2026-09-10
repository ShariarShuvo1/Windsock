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
        TableWheel.Attach(Table);
        Table.Sorting += OnTableSorting;
        Table.MouseMove += OnTableMouseMove;
        Table.MouseLeave += (_, _) => _viewModel.HighlightedStart = null;

        viewModel.PropertyChanged += OnViewModelChanged;
        viewModel.ViewReset += (_, _) => ClearSort();

        ShowRecordedColumn();
        Loaded += (_, _) =>
        {
            _viewModel.Start();
            WatchScrolling();
        };

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
        if (_scroll is not null || TableWheel.Scroller(Table) is not { } found)
        {
            return;
        }

        _scroll = found;
        _scroll.ScrollChanged += (_, _) => _viewModel.IsFollowing = _scroll.VerticalOffset < 4;
    }

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

        Table.Items.SortDescriptions.Clear();
        Table.Items.SortDescriptions.Add(new SortDescription(path, direction));

        foreach (DataGridColumn column in Table.Columns)
        {
            column.SortDirection = column == e.Column ? direction : null;
        }

        Table.Items.Refresh();
        _scroll?.ScrollToTop();
        _viewModel.IsSorted = true;
    }

    private static ListSortDirection FirstDirection(DataGridColumn column) =>
        column.Header is TableColumn model && model.DescendingFirst
            ? ListSortDirection.Descending
            : ListSortDirection.Ascending;

    private void ClearSort()
    {
        Table.Items.SortDescriptions.Clear();

        foreach (DataGridColumn column in Table.Columns)
        {
            column.SortDirection = null;
        }

        Table.Items.Refresh();
        _scroll?.ScrollToTop();
        _viewModel.IsSorted = false;
    }

    private void OnChartHoverChanged(object? sender, UsageHoverEventArgs e)
    {
        _viewModel.SetHover(e.HasValue, e.Bucket);

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
