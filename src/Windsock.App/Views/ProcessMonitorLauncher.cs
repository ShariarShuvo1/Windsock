using System.Windows;
using System.Windows.Media;
using System.Windows.Threading;
using Windsock.App.Services;
using Windsock.App.ViewModels;
using Windsock.Core.Apps;
using Windsock.Core.Geography;
using Windsock.Core.Networking;
using Windsock.Core.Processes;

namespace Windsock.App.Views;

/// <summary>
/// Opens and keeps track of the details windows - one per app or process - and
/// builds the network map for the Map page.
/// </summary>
public sealed class ProcessMonitorLauncher
{
    private const double Cascade = 28;
    private readonly ProcessUsageMonitor _monitor;
    private readonly IProcessConnectionDetailSource _details;
    private readonly IProcessEndpointSource _endpoints;
    private readonly NetworkRouteCache _routes;
    private readonly OutsideContacts _contacts;
    private readonly IProcessIdentityResolver _identities;
    private readonly HostNameCache _hosts;
    private readonly ProcessIconCache _icons;
    private readonly AppIconCache _appIcons;
    private readonly AppNames _names;
    private readonly RecentAppFigures _figures;
    private readonly ThroughputOverviewViewModel _overview;
    private readonly AppFacts _facts;
    private readonly Dispatcher _dispatcher;
    private readonly Dictionary<string, ProcessMonitorWindow> _open = new(RunningApps.KeyComparer);
    private IpLocations? _addresses;
    private WorldMap? _world;
    private bool _looked;
    private int _opened;

    public ProcessMonitorLauncher(
        ProcessUsageMonitor monitor,
        IProcessConnectionDetailSource details,
        IProcessEndpointSource endpoints,
        NetworkRouteCache routes,
        OutsideContacts contacts,
        IProcessIdentityResolver identities,
        HostNameCache hosts,
        ProcessIconCache icons,
        AppIconCache appIcons,
        AppNames names,
        RecentAppFigures figures,
        ThroughputOverviewViewModel overview,
        AppFacts facts,
        Dispatcher dispatcher)
    {
        _monitor = monitor;
        _details = details;
        _endpoints = endpoints;
        _routes = routes;
        _contacts = contacts;
        _identities = identities;
        _hosts = hosts;
        _icons = icons;
        _appIcons = appIcons;
        _names = names;
        _figures = figures;
        _overview = overview;
        _facts = facts;
        _dispatcher = dispatcher;
    }

    /// <summary>
    /// Builds a map of the whole machine for somebody else to show.
    /// </summary>
    public NetworkMapViewModel CreateMap()
    {
        Look();

        return new NetworkMapViewModel(
            _monitor,
            _details,
            _endpoints,
            _routes,
            _contacts,
            _hosts,
            _icons,
            _appIcons,
            _names,
            _figures,
            _addresses,
            _world,
            _dispatcher);
    }

    private void Look()
    {
        if (_looked)
        {
            return;
        }

        _looked = true;
        _addresses = IpLocations.Open();
        _world = WorldMap.Open();
    }

    /// <summary>Shows the window for a process, or brings its window forward.</summary>
    public void Show(int processId)
    {
        if (processId <= 0)
        {
            return;
        }

        ProcessIdentity identity = _identities.Resolve(processId);

        Open(
            "pid:" + processId.ToString(System.Globalization.CultureInfo.InvariantCulture),
            () => new ProcessMonitorViewModel(
                processId,
                null,
                identity,
                identity.Name,
                _icons.Find(identity.FilePath),
                _monitor,
                _details,
                _endpoints,
                _routes,
                _contacts,
                _hosts,
                _figures,
                _addresses,
                _world,
                _overview.SelectedFamily,
                _overview.SelectedScale,
                _dispatcher),
            window => _facts.Create(
                identity.Name,
                identity.FilePath,
                () => ProcessOf(processId, identity),
                window.Connections));
    }

    /// <summary>
    /// Shows the window for an app - every process running it - or brings its
    /// window forward. Does nothing when none of its processes is running.
    /// </summary>
    public void ShowApp(string appKey)
    {
        ArgumentNullException.ThrowIfNull(appKey);

        ProcessIdentity? identity = null;
        IReadOnlyList<ProcessUsage> latest = _monitor.Latest;
        Dictionary<int, string> keys = RunningApps.KeysFor(latest);

        foreach (ProcessUsage usage in latest)
        {
            if (RunningApps.KeyComparer.Equals(keys[usage.ProcessId], appKey)
                && (identity is null || (identity.FilePath is null && usage.Identity.FilePath is not null)))
            {
                identity = usage.Identity;
            }
        }

        if (identity is null)
        {
            return;
        }

        string name = string.IsNullOrWhiteSpace(identity.Description) ? identity.Name : identity.Description;
        ImageSource? icon = _icons.Find(identity.FilePath);

        if (identity.PackageFamily is { } family && _names.Describe(family) is { Name.Length: > 0 } package)
        {
            name = package.Name;
            icon = _appIcons.Find(package) ?? icon;
        }

        ProcessIdentity found = identity;

        Open(
            "app:" + appKey,
            () => new ProcessMonitorViewModel(
                0,
                appKey,
                found,
                name,
                icon,
                _monitor,
                _details,
                _endpoints,
                _routes,
                _contacts,
                _hosts,
                _figures,
                _addresses,
                _world,
                _overview.SelectedFamily,
                _overview.SelectedScale,
                _dispatcher),
            window => _facts.Create(
                name,
                found.FilePath,
                () => AppProcesses(appKey),
                window.Connections));
    }

    /// <summary>
    /// One process as the monitor last read it, or as it was when its window
    /// opened: a process holding no socket is not in the monitor's reading,
    /// and its window still names who started it and what it runs.
    /// </summary>
    private List<ProcessUsage> ProcessOf(int processId, ProcessIdentity identity)
    {
        // Filtered by the start time the window opened with: a process that
        // now holds the same id is someone else. Without a reading, the
        // process as it was when the window opened, start time and all, so
        // ending it checks that start time against whoever holds the id.
        List<ProcessUsage> found = [.. _monitor.Latest.Where(usage => usage.ProcessId == processId
            && ProcessMonitorViewModel.IsSameProcess(usage.Identity.StartedAt, identity.StartedAt))];

        return found.Count > 0
            ? [.. found.Select(usage => usage with { Identity = usage.Identity with { StartedAt = identity.StartedAt ?? usage.Identity.StartedAt } })]
            : [new ProcessUsage(processId, identity, 0, 0, 0, 0, 0, default)];
    }

    /// <summary>The processes of an app in the monitor's latest reading.</summary>
    private List<ProcessUsage> AppProcesses(string appKey)
    {
        IReadOnlyList<ProcessUsage> latest = _monitor.Latest;
        Dictionary<int, string> keys = RunningApps.KeysFor(latest);
        return [.. latest.Where(usage => RunningApps.KeyComparer.Equals(keys[usage.ProcessId], appKey))];
    }

    private void Open(string key, Func<ProcessMonitorViewModel> build, Func<ProcessMonitorViewModel, AppFactsViewModel> facts)
    {
        if (_open.TryGetValue(key, out ProcessMonitorWindow? existing))
        {
            if (existing.WindowState == WindowState.Minimized)
            {
                existing.WindowState = WindowState.Normal;
            }

            existing.Activate();
            return;
        }

        Look();

        ProcessMonitorViewModel model = build();
        ProcessMonitorWindow window = new(model, facts(model));
        int step = _opened++ % 6;
        window.Left += step * Cascade;
        window.Top += step * Cascade;

        window.Closed += (_, _) => _open.Remove(key);
        _open[key] = window;
        window.Show();
    }
}
