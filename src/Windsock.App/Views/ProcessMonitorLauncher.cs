using System.Windows;
using System.Windows.Threading;
using Windsock.App.Services;
using Windsock.App.ViewModels;
using Windsock.Core.Geography;
using Windsock.Core.Networking;
using Windsock.Core.Processes;

namespace Windsock.App.Views;

/// <summary>
/// Opens and keeps track of the per-process monitor windows.
/// </summary>
public sealed class ProcessMonitorLauncher
{
    private const double Cascade = 28;
    private readonly ProcessUsageMonitor _monitor;
    private readonly IProcessConnectionDetailSource _details;
    private readonly IProcessEndpointSource _endpoints;
    private readonly NetworkRouteCache _routes;
    private readonly IProcessIdentityResolver _identities;
    private readonly HostNameCache _hosts;
    private readonly ProcessIconCache _icons;
    private readonly ThroughputOverviewViewModel _overview;
    private readonly Dispatcher _dispatcher;
    private readonly Dictionary<int, ProcessMonitorWindow> _open = [];
    private IpLocations? _addresses;
    private WorldMap? _world;
    private bool _looked;
    private int _opened;

    public ProcessMonitorLauncher(
        ProcessUsageMonitor monitor,
        IProcessConnectionDetailSource details,
        IProcessEndpointSource endpoints,
        NetworkRouteCache routes,
        IProcessIdentityResolver identities,
        HostNameCache hosts,
        ProcessIconCache icons,
        ThroughputOverviewViewModel overview,
        Dispatcher dispatcher)
    {
        _monitor = monitor;
        _details = details;
        _endpoints = endpoints;
        _routes = routes;
        _identities = identities;
        _hosts = hosts;
        _icons = icons;
        _overview = overview;
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
            _hosts,
            _icons,
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

        if (_open.TryGetValue(processId, out ProcessMonitorWindow? existing))
        {
            if (existing.WindowState == WindowState.Minimized)
            {
                existing.WindowState = WindowState.Normal;
            }

            existing.Activate();
            return;
        }

        Look();

        ProcessMonitorViewModel viewModel = new(
            processId,
            _identities.Resolve(processId),
            _monitor,
            _details,
            _endpoints,
            _routes,
            _hosts,
            _icons,
            _addresses,
            _world,
            _overview.SelectedFamily,
            _overview.SelectedScale,
            _dispatcher);

        ProcessMonitorWindow window = new(viewModel);
        int step = _opened++ % 6;
        window.Left += step * Cascade;
        window.Top += step * Cascade;

        window.Closed += (_, _) => _open.Remove(processId);
        _open[processId] = window;
        window.Show();
    }
}
