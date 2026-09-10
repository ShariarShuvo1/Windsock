using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using Windsock.App.Controls;
using Windsock.App.ViewModels;

namespace Windsock.App.Views;

/// <summary>
/// The Process panel: a live table of the processes using the network.
/// </summary>
public partial class ProcessPanelView : UserControl
{
    private const double MapRoom = 260;
    private readonly ProcessPanelViewModel _viewModel;
    private readonly ProcessMonitorLauncher _monitors;

    private GridLength _mapShare = new(420);

    public ProcessPanelView(ProcessPanelViewModel viewModel, ProcessMonitorLauncher monitors)
    {
        InitializeComponent();

        _viewModel = viewModel;
        _monitors = monitors;
        DataContext = viewModel;
        Attach(NameColumn, viewModel.NameColumn);
        Attach(DescriptionColumn, viewModel.DescriptionColumn);
        Attach(ProcessIdColumn, viewModel.ProcessIdColumn);
        Attach(SendColumn, viewModel.SendColumn);
        Attach(ReceiveColumn, viewModel.ReceiveColumn);
        Attach(TotalColumn, viewModel.TotalColumn);
        Attach(ConnectionColumn, viewModel.ConnectionColumn);
        Attach(TcpColumn, viewModel.TcpColumn);
        Attach(UdpColumn, viewModel.UdpColumn);
        Attach(PacketColumn, viewModel.PacketColumn);
        Attach(SentColumn, viewModel.SentColumn);
        Attach(ReceivedColumn, viewModel.ReceivedColumn);
        Attach(TransferredColumn, viewModel.TransferredColumn);
        Attach(UptimeColumn, viewModel.UptimeColumn);
        Attach(PathColumn, viewModel.PathColumn);

        ShowSort();

        TableWheel.Attach(Table);
        Table.Sorting += OnTableSorting;
        Table.MouseDoubleClick += OnTableDoubleClick;
        Table.PreviewMouseLeftButtonDown += OnTablePressed;
        Table.PreviewMouseRightButtonDown += OnTableRightPressed;
        viewModel.WatchRequested += (_, processId) => _monitors.Show(processId);
        viewModel.PropertyChanged += (_, changed) =>
        {
            if (changed.PropertyName == nameof(ProcessPanelViewModel.IsMapOpen))
            {
                ApplyMap();
            }
        };
        MapHost.DetachedChanged += (_, _) => ApplyMap();
        viewModel.ViewReset += (_, _) => ShowSort();

        Loaded += (_, _) => _viewModel.Start();

        Unloaded += (_, _) =>
        {
            _viewModel.Stop();
            MapHost.Recall();
        };
    }

    private void ApplyMap()
    {
        if (_viewModel.IsMapOpen)
        {
            _viewModel.Map ??= _monitors.CreateMap();

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

        NetworkMapViewModel? was = _viewModel.Map;
        _viewModel.Map = null;
        was?.Dispose();
    }

    private void Remember()
    {
        if (MapRow.Height.IsAbsolute && MapRow.Height.Value > 0)
        {
            _mapShare = MapRow.Height;
        }
    }

    private void OnTableDoubleClick(object sender, MouseButtonEventArgs e)
    {
        ArgumentNullException.ThrowIfNull(e);

        DependencyObject? under = Table.InputHitTest(e.GetPosition(Table)) as DependencyObject;

        if (Ancestry.Within<ButtonBase, DataGridRow>(under)
            || Ancestry.Find<DataGridRow>(under)
                is not { Item: ProcessRowViewModel { Kind: not RowKind.Group } row })
        {
            return;
        }

        _monitors.Show(row.ProcessId);
    }

    private void OnTableSorting(object sender, DataGridSortingEventArgs e)
    {
        e.Handled = true;

        if (!string.IsNullOrEmpty(e.Column.SortMemberPath))
        {
            _viewModel.SortBy(e.Column.SortMemberPath);
            ShowSort();
        }
    }

    private void ShowSort()
    {
        ListSortDirection direction = _viewModel.SortDescending
            ? ListSortDirection.Descending
            : ListSortDirection.Ascending;

        foreach (DataGridColumn column in Table.Columns)
        {
            column.SortDirection = column.SortMemberPath == _viewModel.SortKey ? direction : null;
        }
    }

    private void OnTablePressed(object sender, MouseButtonEventArgs e)
    {
        ArgumentNullException.ThrowIfNull(e);

        if (e.ClickCount != 1
            || Ancestry.Within<ButtonBase, DataGridRow>(e.OriginalSource as DependencyObject)
            || Ancestry.Find<DataGridRow>(e.OriginalSource as DependencyObject)
                is not { Item: ProcessRowViewModel { Kind: RowKind.Group } row })
        {
            return;
        }

        _viewModel.ToggleGroupCommand.Execute(row);
    }

    private void OnTableRightPressed(object sender, MouseButtonEventArgs e)
    {
        ArgumentNullException.ThrowIfNull(e);

        if (Ancestry.Within<ButtonBase, DataGridRow>(e.OriginalSource as DependencyObject)
            || Ancestry.Find<DataGridRow>(e.OriginalSource as DependencyObject)
                is not { Item: ProcessRowViewModel row })
        {
            return;
        }

        _viewModel.TogglePinCommand.Execute(row);
        e.Handled = true;
    }

    private static void Attach(DataGridColumn column, TableColumn model)
    {
        column.Header = model;
        column.Visibility = Show(model.IsVisible);

        model.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(TableColumn.IsVisible))
            {
                column.Visibility = Show(model.IsVisible);
            }
        };
    }

    private static Visibility Show(bool visible) => visible ? Visibility.Visible : Visibility.Collapsed;
}
