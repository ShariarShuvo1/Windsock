using System.ComponentModel;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Data;
using FluentIcons.Common;
using Microsoft.Extensions.DependencyInjection;
using Windsock.App.Controls;
using Windsock.App.ViewModels;
using Windsock.Core.Settings;

namespace Windsock.App.Views;

/// <summary>
/// The main application window: the sidebar, and the page it has chosen.
/// </summary>
/// <remarks>
/// <para>
/// Built when it is asked for and thrown away when it closes, pages and all,
/// even when Windsock keeps running afterwards. <c>App</c> owns that, along
/// with the notification-area icon that stands in for it.
/// </para>
/// <para>
/// The sidebar is built from <see cref="Pages"/>, one line a page. A page's
/// view is made from the window's scope the first time it is chosen rather
/// than with the window, so a page nobody opens costs nothing; the machine's
/// parts share one view, made at once because the sidebar's figures come from
/// it.
/// </para>
/// </remarks>
public partial class MainWindow : Window
{
    /// <summary>
    /// Every entry in the sidebar, in order. A line with a parent is a page in
    /// that group; a line nothing names as its parent is a page of its own; a
    /// line that others name is a group, never a page. Adding a page is adding
    /// a line here and registering its view.
    /// </summary>
    /// <remarks>
    /// The key is what a notification's link names (lowercase letters and
    /// hyphens; see <c>NoticeLaunch</c>). The automation name is what UI
    /// Automation finds the entry by, kept from before the regrouping where an
    /// entry already had one.
    /// </remarks>
    private static readonly Page[] Pages =
    [
        new("taskbar", "Taskbar", Symbol.PanelBottom) { View = typeof(TaskbarPanelView) },

        new("network", "Network", Symbol.ArrowsBidirectional),
        new("overview", "Overview", Symbol.DataArea) { Parent = "network", Part = SystemPart.Network, Automation = "Network overview", Aliases = ["system"] },
        new("apps", "Apps", Symbol.DataUsage) { Parent = "network", View = typeof(AppsPanelView), Aliases = ["process"] },
        new("map", "Map", Symbol.Earth) { Parent = "network", View = typeof(MapPanelView) },
        new("adapters", "Adapters and Wi-Fi", Symbol.WiFi1) { Parent = "network", View = typeof(AdaptersPanelView), Aliases = ["wifi"] },
        new("devices", "Home devices", Symbol.Router) { Parent = "network", View = typeof(DevicesPanelView), Aliases = ["home"] },
        new("activity", "Activity", Symbol.Pulse) { Parent = "network", View = typeof(ActivityPanelView) },

        new("connection", "Connection", Symbol.PlugConnected),
        new("status", "Status", Symbol.HeartPulse) { Parent = "connection", View = typeof(ConnectionPanelView), Automation = "Connection status" },
        new("outages", "Outages", Symbol.PlugDisconnected) { Parent = "connection", View = typeof(OutagesPanelView) },
        new("speedtest", "Speed test", Symbol.Gauge) { Parent = "connection", View = typeof(SpeedTestPanelView) },

        new("usage", "Usage", Symbol.DataHistogram),
        new("history", "History", Symbol.History) { Parent = "usage", View = typeof(HistoryPanelView) },
        new("plan", "Data plan", Symbol.DataPie) { Parent = "usage", View = typeof(DataPlanPanelView), Aliases = ["dataplan"] },
        new("reports", "Reports", Symbol.DataTrending) { Parent = "usage", View = typeof(ReportsPanelView) },

        new("machine", "System", Symbol.Desktop) { Automation = "System" },
        new("processor", "Processor", Symbol.DeveloperBoard) { Parent = "machine", Part = SystemPart.Processor },
        new("graphics", "Graphics", Symbol.Games) { Parent = "machine", Part = SystemPart.Graphics },
        new("memory", "Memory", Symbol.Ram) { Parent = "machine", Part = SystemPart.Memory },
        new("storage", "Storage", Symbol.HardDrive) { Parent = "machine", Part = SystemPart.Storage },
        new("battery", "Battery", Symbol.Battery10) { Parent = "machine", Part = SystemPart.Battery, NeedsBattery = true },

        new("alerts", "Alerts", Symbol.Alert) { View = typeof(AlertsPanelView) },

        new("settings", "Settings", Symbol.Settings) { View = typeof(SettingsPanelView), Docked = true },
    ];

    /// <summary>Settings' own pages, reachable by link as if they were entries.</summary>
    private static readonly Dictionary<string, SettingsPage> SettingsPages = new(StringComparer.Ordinal)
    {
        ["general"] = SettingsPage.General,
        ["appearance"] = SettingsPage.Appearance,
        ["privacy"] = SettingsPage.Privacy,
        ["advanced"] = SettingsPage.Advanced,
        ["about"] = SettingsPage.About,
    };

    private readonly WindsockSettings _settings;
    private readonly ISettingsStore _store;
    private readonly IServiceProvider _services;
    private readonly SystemPanelView _system;
    private readonly Dictionary<string, NavigationItem> _entries = new(StringComparer.Ordinal);
    private readonly Dictionary<NavigationItem, Page> _pages = [];

    public MainWindow(
        SystemPanelView system,
        SidebarViewModel sidebar,
        IServiceProvider services,
        WindsockSettings settings,
        ISettingsStore store)
    {
        ArgumentNullException.ThrowIfNull(system);
        ArgumentNullException.ThrowIfNull(sidebar);
        ArgumentNullException.ThrowIfNull(settings);

        _settings = settings;
        _store = store;
        _services = services;
        _system = system;

        InitializeComponent();

        SystemPanelViewModel machine = system.ViewModel;

        foreach (Page page in Pages)
        {
            NavigationItem entry = Build(page);
            _entries[page.Key] = entry;
            _pages[entry] = page;

            // A battery's page is listed once the machine says it has one.
            if (!page.NeedsBattery)
            {
                Sections.Items.Add(entry);
            }
        }

        // Each group's figure answers its question at a glance; each part of
        // the machine keeps its own, as it had when the parts were entries.
        Read("taskbar", sidebar, nameof(SidebarViewModel.Taskbar));
        Read("network", machine.Network, nameof(SystemTileViewModel.Readout));
        Read("connection", sidebar, nameof(SidebarViewModel.Connection));
        Read("usage", machine, nameof(SystemPanelViewModel.TodayFigure));
        Read("machine", sidebar, nameof(SidebarViewModel.Machine));
        Read("processor", machine.Processor, nameof(SystemTileViewModel.Readout));
        Read("graphics", machine.Graphics, nameof(SystemTileViewModel.Readout));
        Read("memory", machine.Memory, nameof(SystemTileViewModel.Readout));
        Read("storage", machine.Storage, nameof(SystemTileViewModel.Readout));

        // The alerts not yet read, as a count on the entry's icon.
        _entries["alerts"].SetBinding(NavigationPane.BadgeProperty, new Binding(nameof(SidebarViewModel.AlertsBadge)) { Source = sidebar });
        _entries["alerts"].SetBinding(AutomationProperties.ItemStatusProperty, new Binding(nameof(SidebarViewModel.AlertsStatus)) { Source = sidebar });
        Read("battery", machine.Battery, nameof(SystemTileViewModel.Readout));

        machine.PropertyChanged += OnMachineChanged;
        OfferBattery();

        // The window opens on the network's overview, whatever is listed first:
        // chosen through the selector itself, so a tab control left with nothing
        // selected cannot fall back to its first entry, the Taskbar.
        Sections.SelectedItem = _entries["overview"];

        Sections.SelectionChanged += OnSectionChanged;
        ShowChosen();

        // Set before listening, so opening the window is not taken for the
        // user changing their mind.
        Sections.IsPaneOpen = settings.SidebarOpen;
        Sections.PaneOpenChanged += OnSidebarChanged;

        StateChanged += (_, _) => FollowVisibility();
        IsVisibleChanged += (_, _) => FollowVisibility();

        // The search box, the page to open on and where to open: MainWindow.Navigation.cs.
        StartNavigation();
    }

    /// <summary>
    /// Turns to a page by its plain name - "history", "outages", "privacy" -
    /// as a clicked notification asks. A group's name turns to its first page.
    /// An unknown or absent name leaves the window where it was.
    /// </summary>
    public void ShowSection(string section)
    {
        ArgumentNullException.ThrowIfNull(section);

        if (SettingsPages.TryGetValue(section, out SettingsPage settingsPage))
        {
            NavigationItem settings = _entries["settings"];
            settings.IsSelected = true;

            if (Prepare(settings) is SettingsPanelView view)
            {
                view.ViewModel.SelectedPage = settingsPage;
            }

            return;
        }

        Page? page = Pages.FirstOrDefault(each =>
            string.Equals(each.Key, section, StringComparison.Ordinal)
            || each.Aliases.Contains(section, StringComparer.Ordinal));

        if (page is not null && _entries.TryGetValue(page.Key, out NavigationItem? entry) && Sections.Items.Contains(entry))
        {
            entry.IsSelected = true;
        }
    }

    private void OnMachineChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(SystemPanelViewModel.HasBattery))
        {
            OfferBattery();
        }
    }

    /// <summary>
    /// Lists the Battery page under System once the machine is known to have a
    /// battery, in its place after Storage. Never taken away again while the
    /// window is open, so the page cannot vanish from under a reader.
    /// </summary>
    private void OfferBattery()
    {
        if (!_system.ViewModel.HasBattery)
        {
            return;
        }

        foreach (Page page in Pages.Where(each => each.NeedsBattery))
        {
            NavigationItem entry = _entries[page.Key];

            if (Sections.Items.Contains(entry))
            {
                continue;
            }

            // After the last entry listed before it, so the order is the list's.
            int at = 0;

            foreach (Page earlier in Pages.TakeWhile(each => each != page))
            {
                int index = Sections.Items.IndexOf(_entries[earlier.Key]);

                if (index >= 0)
                {
                    at = index + 1;
                }
            }

            Sections.Items.Insert(at, entry);
        }
    }

    protected override void OnClosed(EventArgs e)
    {
        Sections.PaneOpenChanged -= OnSidebarChanged;
        Sections.SelectionChanged -= OnSectionChanged;
        _system.ViewModel.PropertyChanged -= OnMachineChanged;
        _system.ViewModel.Hide();

        base.OnClosed(e);
    }

    private static NavigationItem Build(Page page)
    {
        NavigationItem entry = new()
        {
            Name = page.Key,
            Header = page.Header,
            ParentName = page.Parent ?? string.Empty,
        };

        AutomationProperties.SetName(entry, page.Automation ?? page.Header);
        NavigationPane.SetIcon(entry, page.Icon);

        if (page.Docked)
        {
            // Docked to the foot of the sidebar, apart from the rest: where a
            // settings entry is expected to be.
            DockPanel.SetDock(entry, Dock.Bottom);
            entry.Margin = new Thickness(0);
        }

        return entry;
    }

    private void Read(string key, INotifyPropertyChanged source, string property) =>
        _entries[key].SetBinding(NavigationPane.ReadingProperty, new Binding(property) { Source = source });

    private void OnSectionChanged(object sender, SelectionChangedEventArgs e)
    {
        // Selection changes inside the pages - a picker, a table - bubble up
        // through the sidebar too, and are none of its business.
        if (e.OriginalSource == Sections)
        {
            ShowChosen();
        }
    }

    /// <summary>Puts the chosen page on screen, making its view the first time.</summary>
    private void ShowChosen()
    {
        if (Sections.SelectedItem is not NavigationItem entry || !_pages.TryGetValue(entry, out Page? page))
        {
            return;
        }

        if (page.Part is { } part)
        {
            _system.ViewModel.Choose(part);
            WorkspaceHost.Content = _system;
            return;
        }

        WorkspaceHost.Content = Prepare(entry);
    }

    /// <summary>The view for an entry's page, made from the window's scope the first time it is asked for.</summary>
    private object? Prepare(NavigationItem entry)
    {
        if (entry.Content is null && _pages[entry].View is { } type)
        {
            entry.Content = _services.GetRequiredService(type);
        }

        return entry.Content;
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
    /// every page that collects only while it is seen stops collecting.
    /// </summary>
    /// <remarks>
    /// The machine's readings follow the window rather than a page: the sidebar
    /// shows them beside its groups whichever page is open, so they are wanted
    /// whenever the window can be seen, and not otherwise.
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

    /// <summary>One entry in the sidebar.</summary>
    /// <param name="Key">Its name, for links and for the entries that name it as their group.</param>
    /// <param name="Header">What the sidebar calls it.</param>
    /// <param name="Icon">The icon beside the name, and all that shows once the sidebar is folded.</param>
    private sealed record Page(string Key, string Header, Symbol Icon)
    {
        /// <summary>The group it is a page of, if any.</summary>
        public string? Parent { get; init; }

        /// <summary>The view that is the page, made from the window's scope when first chosen.</summary>
        public Type? View { get; init; }

        /// <summary>The part of the machine it shows on the shared machine page, if it is one.</summary>
        public SystemPart? Part { get; init; }

        /// <summary>What UI Automation calls it, when that is not its header.</summary>
        public string? Automation { get; init; }

        /// <summary>Older names a link may still use for it.</summary>
        public IReadOnlyList<string> Aliases { get; init; } = [];

        /// <summary>Whether it sits apart at the foot of the sidebar.</summary>
        public bool Docked { get; init; }

        /// <summary>Whether it is listed only on a machine with a battery.</summary>
        public bool NeedsBattery { get; init; }
    }
}
