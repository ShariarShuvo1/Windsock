using System.Collections.ObjectModel;
using System.Globalization;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using Windsock.App.Services;
using Windsock.Core.Insight;
using Windsock.Core.Processes;

namespace Windsock.App.ViewModels;

/// <summary>
/// Backs Network › Activity: what on this PC is accepting connections, and,
/// in the full edition, where apps connected, the names they looked up, each
/// day's places and Windsock's own firewall rules.
/// </summary>
/// <remarks>
/// The listening ports need no privilege, so the Store edition shows those and
/// nothing else. Everything else here reads the kernel trace or the firewall,
/// which the full edition does as administrator; the rest of the class is in
/// <c>ActivityPanelViewModel.Full.cs</c>, built only into that edition.
/// Reads happen off the window's thread, every few seconds while the page is
/// on screen and not at all otherwise.
/// </remarks>
public sealed partial class ActivityPanelViewModel : ObservableObject, IDisposable
{
    private static readonly TimeSpan Step = TimeSpan.FromSeconds(5);

    private readonly ServiceHosts _services;
    private readonly IProcessIdentityResolver _identities;
    private readonly ProcessIconCache _icons;
    private readonly Dispatcher _dispatcher;
    private readonly DispatcherTimer _timer;
    private IReadOnlyList<ListeningSocket> _sockets = [];
    private int _reading;
    private bool _visible;

    public ActivityPanelViewModel(
        ServiceHosts services,
        IProcessIdentityResolver identities,
        ProcessIconCache icons,
        Dispatcher dispatcher,
        IServiceProvider provider)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(identities);
        ArgumentNullException.ThrowIfNull(icons);
        ArgumentNullException.ThrowIfNull(dispatcher);

        _services = services;
        _identities = identities;
        _icons = icons;
        _dispatcher = dispatcher;

        _timer = new DispatcherTimer(DispatcherPriority.Background, dispatcher) { Interval = Step };
        _timer.Tick += (_, _) => Read();

        BuildFull(provider);
    }

    /// <summary>The line under the page's title.</summary>
#if STORE
    public static string Subtitle => "Programs on this PC waiting for others to connect";
#else
    public static string Subtitle => "What waits for connections, and where apps connect";
#endif

    /// <summary>Whether Windsock is running as administrator, which everything on the page but the listeners needs.</summary>
    public static bool IsElevated => Environment.IsPrivilegedProcess;

    /// <summary>Programs accepting connections, those reachable from the network first.</summary>
    public ObservableCollection<ListeningProgramViewModel> Listening { get; } = [];

    /// <summary>
    /// Whether to leave out UDP sockets on the ports Windows hands out for
    /// outgoing traffic: a program's own end of a conversation it started, such
    /// as a name lookup, rather than something waiting for callers.
    /// </summary>
    [ObservableProperty]
    public partial bool HideClientEnds { get; set; } = true;

    /// <summary>
    /// Whether to list only what another machine could reach: on at first,
    /// since that is the part worth a look; a socket on this PC alone is a
    /// program talking to itself.
    /// </summary>
    [ObservableProperty]
    public partial bool NetworkOnly { get; set; } = true;

    /// <summary>How many programs wait for connections, for the plate.</summary>
    [ObservableProperty]
    public partial string ProgramsText { get; private set; } = "—";

    /// <summary>How many ports another machine could reach, for the plate.</summary>
    [ObservableProperty]
    public partial string OpenPortsText { get; private set; } = "—";

    /// <summary>Under the open ports plate: what that number means.</summary>
    [ObservableProperty]
    public partial string OpenPortsNote { get; private set; } = string.Empty;

    /// <summary>Whether the list has nothing to show.</summary>
    [ObservableProperty]
    public partial bool IsListeningEmpty { get; private set; }

    /// <summary>The empty list's title: why there is nothing.</summary>
    [ObservableProperty]
    public partial string ListeningEmptyTitle { get; private set; } = string.Empty;

    /// <summary>Begins reading, while the page is on screen.</summary>
    public void Show()
    {
        if (_visible)
        {
            return;
        }

        _visible = true;
        _timer.Start();
        Read();
        ShowFull();
    }

    /// <summary>Stops reading, while the page is out of sight.</summary>
    public void Hide()
    {
        if (!_visible)
        {
            return;
        }

        _visible = false;
        _timer.Stop();
        HideFull();
    }

    public void Dispose()
    {
        Hide();
        DisposeFull();
    }

    partial void OnHideClientEndsChanged(bool value) => Present();

    partial void OnNetworkOnlyChanged(bool value) => Present();

    /// <summary>Reads the listener tables in the background, then the page's other parts.</summary>
    private void Read()
    {
        if (!_visible || Interlocked.CompareExchange(ref _reading, 1, 0) != 0)
        {
            return;
        }

        _ = Task.Run(() =>
        {
            try
            {
                IReadOnlyList<ListeningSocket> sockets = Listeners.Read();
                ServiceHostMap map = _services.Current;
                Dictionary<int, (string Name, string? Path)> names = [];

                foreach (int processId in sockets.Select(each => each.ProcessId).Distinct())
                {
                    names[processId] = NameOf(processId, map);
                }

                _dispatcher.BeginInvoke(() =>
                {
                    _sockets = sockets;
                    _names = names;
                    Present();
                });
            }
            finally
            {
                Interlocked.Exchange(ref _reading, 0);
            }
        });

        ReadFull();
    }

    private Dictionary<int, (string Name, string? Path)> _names = [];

    /// <summary>What to call a process: a service host by its services, a program by its own description.</summary>
    private (string Name, string? Path) NameOf(int processId, ServiceHostMap map)
    {
        if (processId == 4)
        {
            return ("System", null);
        }

        ProcessIdentity identity = _identities.Resolve(processId);

        if (map.Describe(processId) is { } services
            && (string.Equals(identity.Name, "svchost", StringComparison.OrdinalIgnoreCase) || identity.FilePath is null))
        {
            return (services.Text, identity.FilePath);
        }

        return (string.IsNullOrWhiteSpace(identity.Description) ? identity.Name : identity.Description, identity.FilePath);
    }

    private void Present()
    {
        List<ListeningSocket> shown = [.. _sockets.Where(each =>
            (!HideClientEnds || !each.IsLikelyClientEnd)
            && (!NetworkOnly || each.Reach != ListenReach.ThisPcOnly))];

        List<ListeningProgramViewModel> programs = [.. shown
            .GroupBy(each => each.ProcessId)
            .Select(group =>
            {
                (string name, string? path) = _names.TryGetValue(group.Key, out var known) ? known : (string.Create(CultureInfo.InvariantCulture, $"PID {group.Key}"), null);
                return new ListeningProgramViewModel(group.Key, name, path, _icons.Find(path), [.. group]);
            })
            .OrderByDescending(each => each.IsOpenToNetwork)
            .ThenBy(each => each.Name, StringComparer.CurrentCultureIgnoreCase)];

        Listening.Clear();

        foreach (ListeningProgramViewModel program in programs)
        {
            Listening.Add(program);
        }

        int open = _sockets
            .Where(each => each.Reach != ListenReach.ThisPcOnly && !each.IsLikelyClientEnd)
            .Select(each => (each.Protocol, each.Port))
            .Distinct()
            .Count();

        ProgramsText = _sockets.Select(each => each.ProcessId).Distinct().Count().ToString("N0", CultureInfo.CurrentCulture);
        OpenPortsText = open.ToString("N0", CultureInfo.CurrentCulture);
        OpenPortsNote = open == 0
            ? "None open"
            : "Open to the network";

        IsListeningEmpty = programs.Count == 0;
        ListeningEmptyTitle = _sockets.Count == 0
            ? "The listener tables could not be read"
            : NetworkOnly ? "Nothing on this PC waits for other machines" : "Nothing matches";
    }

    /// <summary>A country's name in English from its two-letter code, or the code when Windows does not know it.</summary>
    internal static string CountryName(string code)
    {
        try
        {
            return new RegionInfo(code).EnglishName;
        }
        catch (ArgumentException)
        {
            return code;
        }
    }

    // The full edition's parts; empty in the Store edition, which has none of them.
    partial void BuildFull(IServiceProvider provider);

    partial void ShowFull();

    partial void HideFull();

    partial void ReadFull();

    partial void DisposeFull();
}
