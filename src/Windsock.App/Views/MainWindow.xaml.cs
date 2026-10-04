using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using Windsock.App.Controls;
using Windsock.App.ViewModels;
using Windsock.Core.Settings;

namespace Windsock.App.Views;

/// <summary>
/// The main application window. Composes the feature sections; the sections
/// themselves own their data and lifetime.
/// </summary>
/// <remarks>
/// Built when it is asked for and thrown away when it closes, sections and all,
/// even when Windsock keeps running afterwards. <c>App</c> owns that,
/// along with the notification-area icon that stands in for it.
/// </remarks>
public partial class MainWindow : Window
{
    private readonly WindsockSettings _settings;
    private readonly ISettingsStore _store;
    private readonly SystemPanelView _system;
    private readonly Dictionary<TabItem, SystemTileViewModel> _parts;

    public MainWindow(
        SystemPanelView system,
        ConnectionPanelView connection,
        HistoryPanelView history,
        AppsPanelView apps,
        TaskbarPanelView taskbar,
        SettingsPanelView settingsPanel,
        WindsockSettings settings,
        ISettingsStore store)
    {
        ArgumentNullException.ThrowIfNull(system);
        ArgumentNullException.ThrowIfNull(settings);

        _settings = settings;
        _store = store;
        _system = system;

        InitializeComponent();

        ConnectionTab.Content = connection;
        HistoryTab.Content = history;
        AppsTab.Content = apps;
        TaskbarTab.Content = taskbar;
        SettingsTab.Content = settingsPanel;

        SystemPanelViewModel machine = system.ViewModel;

        // The five parts of the machine share one page.
        _parts = new Dictionary<TabItem, SystemTileViewModel>
        {
            [NetworkOverviewTab] = machine.Network,
            [ProcessorItem] = machine.Processor,
            [GraphicsTab] = machine.Graphics,
            [MemoryTab] = machine.Memory,
            [StorageTab] = machine.Storage,
        };

        // Each part's entry carries its live figure beside its name: on the
        // group, where a part has pages, so it shows whether or not it is open.
        (TabItem Entry, SystemTileViewModel Part)[] figures =
        [
            (NetworkItem, machine.Network),
            (ProcessorItem, machine.Processor),
            (GraphicsTab, machine.Graphics),
            (MemoryTab, machine.Memory),
            (StorageTab, machine.Storage),
        ];

        foreach ((TabItem entry, SystemTileViewModel part) in figures)
        {
            entry.SetBinding(NavigationPane.ReadingProperty, new Binding(nameof(SystemTileViewModel.Readout)) { Source = part });
        }

        Sections.SelectionChanged += OnSectionChanged;
        ShowChosen();

        // Set before listening, so opening the window is not taken for the
        // user changing their mind.
        Sections.IsPaneOpen = settings.SidebarOpen;
        Sections.PaneOpenChanged += OnSidebarChanged;

        StateChanged += (_, _) => FollowVisibility();
        IsVisibleChanged += (_, _) => FollowVisibility();
    }

    /// <summary>
    /// Turns to a section by its plain name - "system", "history", "settings" -
    /// as a clicked notification asks. An unknown or absent section leaves the
    /// window where it was.
    /// </summary>
    public void ShowSection(string section)
    {
        TabItem? wanted = section switch
        {
            "system" => NetworkOverviewTab,
            "process" or "apps" => AppsTab,
            "connection" => ConnectionTab,
            "history" => HistoryTab,
            "taskbar" => TaskbarTab,
            "settings" => SettingsTab,
            _ => null,
        };

        if (wanted is not null && Sections.Items.Contains(wanted))
        {
            wanted.IsSelected = true;
        }
    }

    protected override void OnClosed(EventArgs e)
    {
        Sections.PaneOpenChanged -= OnSidebarChanged;
        Sections.SelectionChanged -= OnSectionChanged;
        _system.ViewModel.Hide();

        base.OnClosed(e);
    }

    private void OnSectionChanged(object sender, SelectionChangedEventArgs e)
    {
        // Selection changes inside the pages - a picker, a table - bubble up
        // through the sidebar too, and are none of its business.
        if (e.OriginalSource == Sections)
        {
            ShowChosen();
        }
    }

    /// <summary>Puts the chosen section's panel on the page.</summary>
    private void ShowChosen()
    {
        if (Sections.SelectedItem is not TabItem tab)
        {
            return;
        }

        if (_parts.TryGetValue(tab, out SystemTileViewModel? part))
        {
            part.IsSelected = true;
            WorkspaceHost.Content = _system;
            return;
        }

        WorkspaceHost.Content = tab.Content;
    }

    private void OnSidebarChanged(object? sender, EventArgs e)
    {
        if (_settings.SidebarOpen == Sections.IsPaneOpen)
        {
            return;
        }

        _settings.SidebarOpen = Sections.IsPaneOpen;
        _store.Save(_settings);
    }

    /// <summary>
    /// Takes the page out of the tree's idea of what is visible while the
    /// window is minimised, which WPF otherwise leaves loaded and visible, so
    /// every section that collects only while it is seen stops collecting.
    /// </summary>
    /// <remarks>
    /// The machine's readings follow the window rather than a page: the sidebar
    /// shows them beside the five parts whichever page is open, so they are
    /// wanted whenever the window can be seen, and not otherwise.
    /// </remarks>
    private void FollowVisibility()
    {
        bool seen = IsVisible && WindowState != WindowState.Minimized;

        WorkspaceHost.Visibility = seen ? Visibility.Visible : Visibility.Collapsed;

        if (seen)
        {
            _system.ViewModel.Show();
            return;
        }

        _system.ViewModel.Hide();
    }
}
