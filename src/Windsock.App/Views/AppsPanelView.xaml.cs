using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using Windsock.App.Controls;
using Windsock.App.ViewModels;

namespace Windsock.App.Views;

/// <summary>
/// The Apps page: every app on the network, live with the network map above
/// it, or totalled over a period of history.
/// </summary>
public partial class AppsPanelView : UserControl
{
    private const double MapRoom = 260;
    private const string DefaultTotalsSort = nameof(AppRowViewModel.BytesTotal);

    private readonly AppsPanelViewModel _viewModel;
    private readonly LiveAppsViewModel _live;
    private readonly ProcessMonitorLauncher _monitors;

    private GridLength _mapShare = new(420);

    public AppsPanelView(AppsPanelViewModel viewModel, ProcessMonitorLauncher monitors)
    {
        InitializeComponent();

#if STORE
        // The Store edition never asks for administrator rights and has no
        // process view, and is built without either. Taken out before the page
        // has a data context, so their bindings are never tried.
        if (ElevateButton.Parent is Panel holder)
        {
            holder.Children.Remove(ElevateButton);
        }

        if (ViewPicker.Parent is Panel actions)
        {
            actions.Children.Remove(ViewPicker);
        }
#endif

        _viewModel = viewModel;
        _live = viewModel.Live;
        _monitors = monitors;
        DataContext = viewModel;

        AttachLive();
        AttachTotals();

        // Visibility rather than Loaded: a minimised window stays loaded, and so
        // did this page's collection, every second, with nobody looking. The
        // map follows the same rule, since it opens with the page: it is built
        // when the live view is seen and let go when it is not, unless it has
        // been taken out into a window of its own, where it is still looked at.
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

            ApplyMap();
        };

        Unloaded += (_, _) =>
        {
            _viewModel.Stop();
            MapHost.Recall();
        };

        viewModel.PropertyChanged += (_, changed) =>
        {
            if (changed.PropertyName == nameof(AppsPanelViewModel.SelectedPeriod))
            {
                ApplyMap();
            }
        };

        ApplyMap();
    }

    // ------------------------------------------------------------------ live

    private void AttachLive()
    {
        Attach(LiveNameColumn, _live.NameColumn);
        Attach(LiveDescriptionColumn, _live.DescriptionColumn);
        Attach(LiveProcessIdColumn, _live.ProcessIdColumn);
        Attach(LiveSendColumn, _live.SendColumn);
        Attach(LiveReceiveColumn, _live.ReceiveColumn);
        Attach(LiveTotalColumn, _live.TotalColumn);
        Attach(LiveConnectionColumn, _live.ConnectionColumn);
        Attach(LiveTcpColumn, _live.TcpColumn);
        Attach(LiveUdpColumn, _live.UdpColumn);
        Attach(LivePacketColumn, _live.PacketColumn);
        Attach(LiveSentColumn, _live.SentColumn);
        Attach(LiveReceivedColumn, _live.ReceivedColumn);
        Attach(LiveTransferredColumn, _live.TransferredColumn);
        Attach(LiveUptimeColumn, _live.UptimeColumn);
        Attach(LivePathColumn, _live.PathColumn);

        ShowLiveSort();

        TableWheel.Attach(LiveTable);
        LiveTable.Sorting += OnLiveSorting;
        LiveTable.MouseDoubleClick += OnLiveDoubleClick;
        LiveTable.PreviewMouseLeftButtonDown += OnLivePressed;
        LiveTable.PreviewMouseRightButtonDown += OnLiveRightPressed;

        _live.WatchRequested += (_, processId) => _monitors.Show(processId);
        _live.WatchAppRequested += (_, key) => _monitors.ShowApp(key);
        _live.ViewReset += (_, _) => ShowLiveSort();
        _live.PropertyChanged += (_, changed) =>
        {
            if (changed.PropertyName == nameof(LiveAppsViewModel.IsMapOpen))
            {
                ApplyMap();
            }
        };

        MapHost.DetachedChanged += (_, _) => ApplyMap();
    }

    private void ApplyMap()
    {
        // A period of history has no map. One out in a window of its own comes
        // home and goes with the rest, so nothing goes on tracing routes for a
        // view nobody chose.
        if (!_viewModel.IsLivePeriod && MapHost.IsDetached)
        {
            MapHost.Recall();
        }

        bool shown = _viewModel.IsLivePeriod && (IsVisible || MapHost.IsDetached);

        if (_live.IsMapOpen)
        {
            if (shown)
            {
                _live.Map ??= _monitors.CreateMap();
            }
            else if (!MapHost.IsDetached)
            {
                LetGoOfMap();
            }

            if (MapHost.IsDetached)
            {
                Remember();

                MapRow.MinHeight = 0;
                MapRow.Height = GridLength.Auto;
            }
            else
            {
                MapRow.MinHeight = MapRoom;
                MapRow.Height = _mapShare;
            }

            TableRow.Height = new GridLength(1, GridUnitType.Star);

            return;
        }

        Remember();
        MapHost.Recall();

        MapRow.MinHeight = 0;
        MapRow.Height = GridLength.Auto;
        TableRow.Height = new GridLength(1, GridUnitType.Star);

        LetGoOfMap();
    }

    private void LetGoOfMap()
    {
        NetworkMapViewModel? was = _live.Map;
        _live.Map = null;
        was?.Dispose();
    }

    private void Remember()
    {
        if (MapRow.Height.IsAbsolute && MapRow.Height.Value > 0)
        {
            _mapShare = MapRow.Height;
        }
    }

    private void OnLiveDoubleClick(object sender, MouseButtonEventArgs e)
    {
        ArgumentNullException.ThrowIfNull(e);

        DependencyObject? under = LiveTable.InputHitTest(e.GetPosition(LiveTable)) as DependencyObject;

        if (Ancestry.Within<ButtonBase, DataGridRow>(under)
            || Ancestry.Find<DataGridRow>(under) is not { Item: ProcessRowViewModel { CanOpenDetails: true } row })
        {
            return;
        }

        _live.WatchCommand.Execute(row);
    }

    private void OnLiveSorting(object sender, DataGridSortingEventArgs e)
    {
        e.Handled = true;

        if (!string.IsNullOrEmpty(e.Column.SortMemberPath))
        {
            _live.SortBy(e.Column.SortMemberPath);
            ShowLiveSort();
        }
    }

    private void ShowLiveSort()
    {
        ListSortDirection direction = _live.SortDescending
            ? ListSortDirection.Descending
            : ListSortDirection.Ascending;

        foreach (DataGridColumn column in LiveTable.Columns)
        {
            column.SortDirection = column.SortMemberPath == _live.SortKey ? direction : null;
        }
    }

    private void OnLivePressed(object sender, MouseButtonEventArgs e)
    {
        ArgumentNullException.ThrowIfNull(e);

        if (e.ClickCount != 1
            || Ancestry.Within<ButtonBase, DataGridRow>(e.OriginalSource as DependencyObject)
            || Ancestry.Find<DataGridRow>(e.OriginalSource as DependencyObject)
                is not { Item: ProcessRowViewModel { IsExpandable: true } row })
        {
            return;
        }

        _live.ToggleGroupCommand.Execute(row);
    }

    private void OnLiveRightPressed(object sender, MouseButtonEventArgs e)
    {
        ArgumentNullException.ThrowIfNull(e);

        if (Ancestry.Within<ButtonBase, DataGridRow>(e.OriginalSource as DependencyObject)
            || Ancestry.Find<DataGridRow>(e.OriginalSource as DependencyObject)
                is not { Item: ProcessRowViewModel row })
        {
            return;
        }

        _live.TogglePinCommand.Execute(row);
        e.Handled = true;
    }

    // ---------------------------------------------------------------- totals

    private void AttachTotals()
    {
        AppTotalsViewModel totals = _viewModel.Totals;

        Attach(TotalsNameColumn, totals.NameColumn);
        Attach(TotalsKindColumn, totals.KindColumn);
        Attach(TotalsDownloadColumn, totals.DownloadColumn);
        Attach(TotalsUploadColumn, totals.UploadColumn);
        Attach(TotalsTotalColumn, totals.TotalColumn);
        Attach(TotalsShareColumn, totals.ShareColumn);
        Attach(TotalsRecentColumn, totals.RecentColumn);
        Attach(TotalsPathColumn, totals.PathColumn);

        TableWheel.Attach(TotalsTable);
        TotalsTable.Sorting += OnTotalsSorting;
        totals.ViewReset += (_, _) => SortTotals(DefaultTotalsSort, ListSortDirection.Descending, isDefault: true);

        SortTotals(DefaultTotalsSort, ListSortDirection.Descending, isDefault: true);
    }

    private void OnTotalsSorting(object? sender, DataGridSortingEventArgs e)
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
            _ => e.Column.Header is TableColumn { DescendingFirst: true }
                ? ListSortDirection.Descending
                : ListSortDirection.Ascending,
        };

        SortTotals(path, direction, isDefault: path == DefaultTotalsSort && direction == ListSortDirection.Descending);
    }

    private void SortTotals(string path, ListSortDirection direction, bool isDefault)
    {
        TotalsTable.Items.SortDescriptions.Clear();
        TotalsTable.Items.SortDescriptions.Add(new SortDescription(path, direction));

        foreach (DataGridColumn column in TotalsTable.Columns)
        {
            column.SortDirection = column.SortMemberPath == path ? direction : null;
        }

        // Live, so a row whose figures change each minute moves to where it now
        // belongs without the whole table being rebuilt under the reader.
        if (TotalsTable.Items is ICollectionViewLiveShaping { CanChangeLiveSorting: true } live)
        {
            live.LiveSortingProperties.Clear();
            live.LiveSortingProperties.Add(path);
            live.IsLiveSorting = true;
        }

        _viewModel.Totals.IsSorted = !isDefault;
    }

    // ---------------------------------------------------------------- shared

    /// <summary>
    /// Ties a grid column to its model: its header, and whether it is drawn,
    /// which is whether it is switched on and belongs to the view on screen.
    /// </summary>
    private static void Attach(DataGridColumn column, TableColumn model)
    {
        column.Header = model;
        column.Visibility = model.IsShown ? Visibility.Visible : Visibility.Collapsed;

        model.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(TableColumn.IsShown))
            {
                column.Visibility = model.IsShown ? Visibility.Visible : Visibility.Collapsed;
            }
        };
    }
}
