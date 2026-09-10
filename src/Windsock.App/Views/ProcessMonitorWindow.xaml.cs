using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Threading;
using Windsock.App.Controls;
using Windsock.App.ViewModels;

namespace Windsock.App.Views;

/// <summary>One process, watched live, in a window of its own.</summary>
public partial class ProcessMonitorWindow : Window
{
    private const double GraphRoom = 330;
    private const double TableRoom = 150;
    private readonly ProcessMonitorViewModel _viewModel;
    private readonly ListCollectionView _view;
    private Spotlight? _filling;

    private GridLength _graphShare = new(1.25, GridUnitType.Star);
    private GridLength _tableShare = new(1.4, GridUnitType.Star);

    private bool _drawn;

    public ProcessMonitorWindow(ProcessMonitorViewModel viewModel)
    {
        ArgumentNullException.ThrowIfNull(viewModel);

        InitializeComponent();

        _viewModel = viewModel;
        DataContext = viewModel;
        HostColumn.Header = viewModel.HostColumn;
        RemotePortColumn.Header = viewModel.RemotePortColumn;
        StateColumn.Header = viewModel.StateColumn;
        ProtocolColumn.Header = viewModel.ProtocolColumn;
        VersionColumn.Header = viewModel.VersionColumn;
        LocalAddressColumn.Header = viewModel.LocalAddressColumn;
        LocalPortColumn.Header = viewModel.LocalPortColumn;
        OpenedColumn.Header = viewModel.OpenedColumn;
        AgeColumn.Header = viewModel.AgeColumn;
        _view = new ListCollectionView(viewModel.Connections)
        {
            Filter = row => row is ConnectionRowViewModel connection && viewModel.Matches(connection),
        };

        Table.ItemsSource = _view;
        Table.Sorting += OnSorting;
        Graph.HoverChanged += OnGraphHover;
        Graph.Activated += (_, chosen) => viewModel.Choose(chosen.Item);
        Viewport.Cleared += (_, _) => viewModel.Choose(null);
        FitButton.Click += (_, _) =>
        {
            Viewport.FitOrReturn();
            Keyboard.ClearFocus();
        };
        Viewport.ViewChanged += (_, _) => Graph.Scale = Viewport.Zoom;
        GraphHost.DetachedChanged += (_, _) =>
        {
            if (viewModel.IsFullScreen)
            {
                viewModel.IsFullScreen = false;
            }
        };

        viewModel.PropertyChanged += OnViewModelChanged;
        PreviewKeyDown += OnKeyDown;
        TableWheel.Attach(Table);
        Table.MouseMove += OnTableMouseMove;
        Table.PreviewMouseLeftButtonDown += OnTablePressed;
        Table.MouseLeave += (_, _) => viewModel.HighlightPeer(null);

        viewModel.FilterChanged += (_, _) => _view.Refresh();
        viewModel.ViewReset += (_, _) => ClearSort();

        Closed += (_, _) =>
        {
            GraphHost.Recall();
            viewModel.Dispose();
        };
    }

    private void OnViewModelChanged(object? sender, PropertyChangedEventArgs e)
    {
        ArgumentNullException.ThrowIfNull(e);

        if (e.PropertyName == nameof(ProcessMonitorViewModel.IsFullScreen))
        {
            ApplyFullScreen();
        }

        if (e.PropertyName is nameof(ProcessMonitorViewModel.IsFullScreen)
            or nameof(ProcessMonitorViewModel.IsExpanded))
        {
            ApplySplit();
        }

        if (e.PropertyName == nameof(ProcessMonitorViewModel.IsWorld))
        {
            ApplyWorld();
        }
        if (e.PropertyName == nameof(ProcessMonitorViewModel.Peers)
            && !_drawn
            && _viewModel.Peers.Count > 0)
        {
            _drawn = true;

            if (!Viewport.IsMoved)
            {
                Dispatcher.BeginInvoke(Viewport.Fit, DispatcherPriority.Loaded);
            }
        }
    }

    private void ApplyWorld()
    {
        Viewport.SmallestFit = _viewModel.IsWorld ? 0.2 : 0.5;
        Dispatcher.BeginInvoke(Viewport.Fit, DispatcherPriority.Loaded);
    }

    private void ApplySplit()
    {
        if (GraphRow.Height.IsStar && TableRow.Height.IsStar)
        {
            _graphShare = GraphRow.Height;
            _tableShare = TableRow.Height;
        }
        if (_viewModel.IsFullScreen)
        {
            return;
        }

        if (_viewModel.IsExpanded)
        {
            Share(GraphRow, _graphShare, GraphRoom);
            Share(TableRow, _tableShare, TableRoom);
            return;
        }
        Share(GraphRow, GridLength.Auto, 0);
        Share(TableRow, new GridLength(1, GridUnitType.Star), TableRoom);
    }

    private static void Share(RowDefinition row, GridLength height, double least)
    {
        row.MinHeight = least;
        row.Height = height;
    }

    private void OnKeyDown(object sender, KeyEventArgs e)
    {
        ArgumentNullException.ThrowIfNull(e);

        if (e.Key != Key.Escape)
        {
            return;
        }
        if (_viewModel.IsSettingsOpen)
        {
            _viewModel.IsSettingsOpen = false;
            e.Handled = true;
            return;
        }

        if (_viewModel.IsFullScreen)
        {
            _viewModel.IsFullScreen = false;
            e.Handled = true;
        }
    }

    private void ApplyFullScreen()
    {
        if (_viewModel.IsFullScreen)
        {
            _filling ??= Spotlight.Give(GraphBox);
            Viewport.Focus();
            return;
        }

        _filling?.Dispose();
        _filling = null;
    }

    private void OnGraphHover(object? sender, GraphHoverEventArgs e)
    {
        ArgumentNullException.ThrowIfNull(e);
        _viewModel.HighlightRows(e.Item);
    }

    private void OnTablePressed(object sender, MouseButtonEventArgs e)
    {
        ArgumentNullException.ThrowIfNull(e);

        if (e.ClickCount != 1
            || Ancestry.Within<ButtonBase, DataGridRow>(e.OriginalSource as DependencyObject)
            || Ancestry.Find<DataGridRow>(e.OriginalSource as DependencyObject)
                is not { Item: ConnectionRowViewModel row })
        {
            return;
        }

        _viewModel.ToggleConnectionCommand.Execute(row);
    }

    private void OnTableMouseMove(object sender, MouseEventArgs e)
    {
        ArgumentNullException.ThrowIfNull(e);

        _viewModel.HighlightPeer(
            Ancestry.Find<DataGridRow>(e.OriginalSource as DependencyObject) is
                { Item: ConnectionRowViewModel row }
                ? row.PeerKey
                : null);
    }

    private void OnSorting(object? sender, DataGridSortingEventArgs e)
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
            _ => e.Column.Header is TableColumn model && model.DescendingFirst
                ? ListSortDirection.Descending
                : ListSortDirection.Ascending,
        };

        _view.SortDescriptions.Clear();
        _view.SortDescriptions.Add(new SortDescription(path, direction));

        foreach (DataGridColumn column in Table.Columns)
        {
            column.SortDirection = column == e.Column ? direction : null;
        }

        _viewModel.IsSorted = true;
    }

    private void ClearSort()
    {
        _view.SortDescriptions.Clear();

        foreach (DataGridColumn column in Table.Columns)
        {
            column.SortDirection = null;
        }

        _view.Refresh();
        _viewModel.IsSorted = false;
    }
}
