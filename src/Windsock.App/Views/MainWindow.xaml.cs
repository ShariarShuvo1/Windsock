using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using Windsock.App.Services;
using Windsock.App.ViewModels;
using Windsock.Core.Settings;

namespace Windsock.App.Views;

/// <summary>
/// The main application window. Composes the feature sections; the sections
/// themselves own their data and lifetime.
/// </summary>
public partial class MainWindow : Window, IDisposable
{
    private const double OverviewRoom = 230;
    private const double PanelRoom = 150;

    private GridLength _overviewShare = new(OverviewRoom, GridUnitType.Pixel);
    private GridLength _panelShare = new(1, GridUnitType.Star);

    private readonly WindsockSettings _settings;
    private readonly TrayIcon _tray;

    public MainWindow(
        ThroughputOverviewView throughputOverview,
        WorkspaceView workspace,
        WindsockSettings settings)
    {
        ArgumentNullException.ThrowIfNull(throughputOverview);
        ArgumentNullException.ThrowIfNull(settings);

        _settings = settings;

        InitializeComponent();
        OverviewHost.Content = throughputOverview;
        WorkspaceHost.Content = workspace;

        if (throughputOverview.DataContext is ThroughputOverviewViewModel overview)
        {
            overview.PropertyChanged += OnOverviewChanged;
        }

        _tray = new TrayIcon(this);
        _tray.Opened += (_, _) => Restore();
        _tray.MenuRequested += (_, _) => ShowTrayMenu();

        Closing += OnClosing;
        StateChanged += (_, _) => FollowVisibility();
        IsVisibleChanged += (_, _) => FollowTray();
    }

    /// <summary>Brings a hidden or minimised window back to the front.</summary>
    private void Restore()
    {
        Show();

        if (WindowState == WindowState.Minimized)
        {
            WindowState = WindowState.Normal;
        }

        _ = Activate();
        Topmost = true;
        Topmost = false;
    }

    /// <summary>
    /// The icon is there only while the window is not.
    /// </summary>
    private void FollowTray()
    {
        if (IsVisible)
        {
            _tray.Hide();
            return;
        }

        _tray.Show("Windsock is still running");
    }

    private void ShowTrayMenu()
    {
        ContextMenu menu = new()
        {
            Placement = System.Windows.Controls.Primitives.PlacementMode.MousePoint,
        };

        MenuItem open = new() { Header = "Open Windsock" };
        open.Click += (_, _) => Restore();

        MenuItem quit = new() { Header = "Quit" };
        quit.Click += (_, _) =>
        {
            _tray.Dispose();
            Application.Current.Shutdown();
        };

        menu.Items.Add(open);
        menu.Items.Add(new Separator());
        menu.Items.Add(quit);

        // Without the window in front the menu does not dismiss when the next
        // click lands somewhere else, which is a shell rule about tray menus
        // rather than anything WPF does.
        _ = Activate();
        menu.IsOpen = true;
    }

    private void FollowVisibility()
    {
        Visibility content = WindowState == WindowState.Minimized
            ? Visibility.Collapsed
            : Visibility.Visible;

        OverviewHost.Visibility = content;
        WorkspaceHost.Visibility = content;
    }

    private void OnClosing(object? sender, CancelEventArgs e)
    {
        ArgumentNullException.ThrowIfNull(e);

        if (_settings.OnClose != CloseAction.KeepRunning)
        {
            Dispose();
            return;
        }

        e.Cancel = true;
        Hide();
    }

    /// <summary>Takes the icon out of the notification area on the way out.</summary>
    public void Dispose()
    {
        _tray.Dispose();
        GC.SuppressFinalize(this);
    }

    private void OnOverviewChanged(object? sender, PropertyChangedEventArgs e)
    {
        ArgumentNullException.ThrowIfNull(e);

        if (e.PropertyName == nameof(ThroughputOverviewViewModel.IsExpanded)
            && sender is ThroughputOverviewViewModel overview)
        {
            ApplySplit(overview.IsExpanded);
        }
    }

    private void ApplySplit(bool expanded)
    {
        if (!OverviewRow.Height.IsAuto)
        {
            _overviewShare = OverviewRow.Height;
            _panelShare = PanelRow.Height;
        }

        if (expanded)
        {
            Share(OverviewRow, _overviewShare, OverviewRoom);
            Share(PanelRow, _panelShare, PanelRoom);
            return;
        }
        Share(OverviewRow, GridLength.Auto, 0);
        Share(PanelRow, new GridLength(1, GridUnitType.Star), PanelRoom);
    }

    private static void Share(RowDefinition row, GridLength height, double least)
    {
        row.MinHeight = least;
        row.Height = height;
    }
}
