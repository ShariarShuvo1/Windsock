using System.Globalization;

namespace Windsock.Core.Insight;

/// <summary>One running Windows service and the process it runs in.</summary>
/// <param name="Name">The service's key name, such as <c>DoSvc</c>.</param>
/// <param name="DisplayName">What Windows calls it on screen, such as "Delivery Optimization".</param>
/// <param name="ProcessId">The process it runs in.</param>
public sealed record HostedService(string Name, string DisplayName, int ProcessId);

/// <summary>What to call a process that runs services, and how sure that is.</summary>
/// <param name="Text">"Delivery Optimization", or "BFE, Windows Defender Firewall" for a shared host.</param>
/// <param name="Services">Every service the process runs, by display name, in the order shown.</param>
/// <param name="IsShared">
/// Whether the process runs more than one service. Its traffic is counted for
/// the process as a whole, so it cannot be split between them; each connection
/// can still be put to its service by the socket table's owner module.
/// </param>
public sealed record ServiceHostLabel(string Text, IReadOnlyList<string> Services, bool IsShared);

/// <summary>
/// Which services each process runs, from one reading of the service manager.
/// </summary>
/// <remarks>
/// Windows 11 gives most services a service host of their own, so a bare
/// <c>svchost.exe</c> row can nearly always be called by its one service's name.
/// Measured on 2026-10-05 as a standard user: 141 running services in 131
/// processes, five of them shared (one holds BrokerInfrastructure, DcomLaunch,
/// PlugPlay, Power and SystemEventsBroker), and every one of 97 svchost
/// processes named.
/// </remarks>
public sealed class ServiceHostMap
{
    private readonly Dictionary<int, List<HostedService>> _byProcess = [];

    /// <summary>An empty map, for when the service manager could not be read.</summary>
    public static ServiceHostMap Empty { get; } = new([]);

    /// <summary>Groups one reading's services by process.</summary>
    public ServiceHostMap(IEnumerable<HostedService> services)
    {
        ArgumentNullException.ThrowIfNull(services);

        foreach (HostedService service in services)
        {
            if (service.ProcessId <= 0)
            {
                continue;
            }

            if (!_byProcess.TryGetValue(service.ProcessId, out List<HostedService>? list))
            {
                list = [];
                _byProcess[service.ProcessId] = list;
            }

            list.Add(service);
        }

        foreach (List<HostedService> list in _byProcess.Values)
        {
            list.Sort(static (a, b) => string.Compare(a.DisplayName, b.DisplayName, StringComparison.OrdinalIgnoreCase));
        }
    }

    /// <summary>How many processes run at least one service.</summary>
    public int Hosts => _byProcess.Count;

    /// <summary>The services a process runs; empty for one that runs none.</summary>
    public IReadOnlyList<HostedService> In(int processId) =>
        _byProcess.TryGetValue(processId, out List<HostedService>? list) ? list : [];

    /// <summary>Whether a process runs a service of this key name.</summary>
    public bool Runs(int processId, string serviceName) =>
        In(processId).Any(each => string.Equals(each.Name, serviceName, StringComparison.OrdinalIgnoreCase));

    /// <summary>The process a service runs in, or null when it is not running.</summary>
    public int? ProcessOf(string serviceName)
    {
        foreach ((int processId, List<HostedService> list) in _byProcess)
        {
            if (list.Any(each => string.Equals(each.Name, serviceName, StringComparison.OrdinalIgnoreCase)))
            {
                return processId;
            }
        }

        return null;
    }

    /// <summary>What to call a process by its services, or null for one that runs none.</summary>
    public ServiceHostLabel? Describe(int processId)
    {
        IReadOnlyList<HostedService> services = In(processId);

        if (services.Count == 0)
        {
            return null;
        }

        List<string> names = [.. services.Select(static each => Shown(each))];
        return new ServiceHostLabel(Join(names), names, services.Count > 1);
    }

    /// <summary>
    /// A key two readings agree on for one process's services, so a host's
    /// traffic can be kept under it: the service's key name, or the key names
    /// of a shared host joined in order.
    /// </summary>
    public string? KeyOf(int processId)
    {
        IReadOnlyList<HostedService> services = In(processId);

        return services.Count == 0
            ? null
            : string.Join(
                ',',
                services.Select(static each => each.Name).Order(StringComparer.OrdinalIgnoreCase));
    }

    private static string Shown(HostedService service) =>
        string.IsNullOrWhiteSpace(service.DisplayName) ? service.Name : service.DisplayName;

    /// <summary>"A", "A and B", or "A, B and 3 more".</summary>
    internal static string Join(IReadOnlyList<string> names) => names.Count switch
    {
        0 => string.Empty,
        1 => names[0],
        2 => string.Format(CultureInfo.CurrentCulture, "{0} and {1}", names[0], names[1]),
        3 => string.Format(CultureInfo.CurrentCulture, "{0}, {1} and {2}", names[0], names[1], names[2]),
        _ => string.Format(CultureInfo.CurrentCulture, "{0}, {1} and {2} more", names[0], names[1], names.Count - 2),
    };
}

/// <summary>Reads which services are running and where.</summary>
public interface IServiceHostSource
{
    /// <summary>Every running service the caller may see; empty when the service manager cannot be read.</summary>
    IReadOnlyList<HostedService> Read();
}

/// <summary>
/// Keeps a recent reading of the service manager, read again when it is asked
/// for after going stale, since services start and stop.
/// </summary>
public sealed class ServiceHosts
{
    private static readonly TimeSpan Freshness = TimeSpan.FromSeconds(30);

    private readonly IServiceHostSource _source;
    private readonly TimeProvider _clock;
    private readonly Lock _gate = new();
    private ServiceHostMap _map = ServiceHostMap.Empty;
    private DateTimeOffset _readAt = DateTimeOffset.MinValue;

    public ServiceHosts(IServiceHostSource source)
        : this(source, TimeProvider.System)
    {
    }

    internal ServiceHosts(IServiceHostSource source, TimeProvider clock)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(clock);

        _source = source;
        _clock = clock;
    }

    /// <summary>
    /// The current map, read again when the last reading is more than half a
    /// minute old. Reading takes a few milliseconds; call it off the UI thread
    /// when it may be stale.
    /// </summary>
    public ServiceHostMap Current
    {
        get
        {
            DateTimeOffset now = _clock.GetUtcNow();

            lock (_gate)
            {
                if (now - _readAt < Freshness)
                {
                    return _map;
                }
            }

            ServiceHostMap fresh = new(_source.Read());

            lock (_gate)
            {
                _map = fresh;
                _readAt = now;
                return fresh;
            }
        }
    }

    /// <summary>Forgets the reading, so the next ask reads again.</summary>
    public void Invalidate()
    {
        lock (_gate)
        {
            _readAt = DateTimeOffset.MinValue;
        }
    }
}
