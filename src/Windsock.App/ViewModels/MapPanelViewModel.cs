using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Windsock.App.Views;
using Windsock.Core.Processes;
using Windsock.Core.Settings;

namespace Windsock.App.ViewModels;

/// <summary>
/// Backs the Map page: everything this PC is talking to, and the way the
/// traffic goes, with the page's whole height to itself.
/// </summary>
/// <remarks>
/// <para>
/// The map used to sit above the Apps table behind a splitter, sharing its
/// height and following its search. Reading a table and reading a map are
/// different tasks, so it has its own page now; it shows the whole machine,
/// in the unit the Apps page shows - apps, or processes while each process is
/// measured.
/// </para>
/// <para>
/// The map is built only while it can be seen - on the page, or lifted out
/// into a window of its own - and disposed otherwise, because a live map
/// watches every process's endpoints and, with Routes on, traces the way to
/// every place it draws.
/// </para>
/// </remarks>
public sealed partial class MapPanelViewModel : ObservableObject, IDisposable
{
    private readonly ProcessMonitorLauncher _launcher;
    private readonly ProcessUsageMonitor _monitor;
    private readonly WindsockSettings _settings;
    private readonly Dispatcher _dispatcher;
    private bool _following;
    private bool _disposed;

    public MapPanelViewModel(
        ProcessMonitorLauncher launcher,
        ProcessUsageMonitor monitor,
        WindsockSettings settings,
        Dispatcher dispatcher)
    {
        ArgumentNullException.ThrowIfNull(launcher);
        ArgumentNullException.ThrowIfNull(monitor);
        ArgumentNullException.ThrowIfNull(settings);

        _launcher = launcher;
        _monitor = monitor;
        _settings = settings;
        _dispatcher = dispatcher;
    }

    /// <summary>The map, while it can be seen.</summary>
    [ObservableProperty]
    public partial NetworkMapViewModel? Map { get; private set; }

    /// <summary>Whether the map keeps up with the machine or holds still to be read.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(LiveLabel))]
    public partial bool IsLive { get; set; } = true;

    /// <summary>What the Pause button offers to do, which is the opposite of what is happening.</summary>
    public string LiveLabel => IsLive ? "Pause" : "Resume";

    [RelayCommand]
    private void ToggleLive() => IsLive = !IsLive;

    partial void OnIsLiveChanged(bool value)
    {
        if (Map is { } map)
        {
            map.IsLive = value;
        }
    }

    /// <summary>Builds the map, if it is not built already, because it can now be seen.</summary>
    public void Show()
    {
        if (_disposed)
        {
            return;
        }

        if (Map is null)
        {
            NetworkMapViewModel map = _launcher.CreateMap();
            map.IsLive = IsLive;
            map.GroupByApp = !ShowsProcesses;
            Map = map;
        }

        if (!_following)
        {
            _following = true;
            _monitor.RateStateChanged += OnRateStateChanged;
        }
    }

    /// <summary>Lets the map go, because nothing can see it.</summary>
    public void Hide()
    {
        if (_following)
        {
            _following = false;
            _monitor.RateStateChanged -= OnRateStateChanged;
        }

        NetworkMapViewModel? was = Map;
        Map = null;
        was?.Dispose();
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        Hide();
    }

    /// <summary>
    /// Whether a node stands for one process rather than a whole app: chosen on
    /// the Apps page, and honoured only while each process is measured.
    /// </summary>
    private bool ShowsProcesses =>
        _settings.AppsShowProcesses && _monitor.RateState == TrafficSourceState.Running;

    private void OnRateStateChanged(object? sender, EventArgs e) =>
        _dispatcher.BeginInvoke(() =>
        {
            if (Map is { } map)
            {
                map.GroupByApp = !ShowsProcesses;
            }
        });
}
