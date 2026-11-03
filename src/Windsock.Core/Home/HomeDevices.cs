using System.Net;
using System.Net.Sockets;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Windsock.Core.History;
using Windsock.Core.Settings;

namespace Windsock.Core.Home;

/// <summary>Where looking for home devices stands.</summary>
public enum HomeLookState
{
    /// <summary>Switched off in settings.</summary>
    Off,

    /// <summary>Switched on, but this PC is not on a home network: see <see cref="HomeStatus.Absence"/>.</summary>
    Away,

    /// <summary>Looking on a home network.</summary>
    Looking,
}

/// <summary>Where looking for home devices stands, and on which network.</summary>
/// <param name="State">Off, away, or looking.</param>
/// <param name="Absence">Why it is away, when it is.</param>
/// <param name="NetworkKey">The home network being looked at, or nothing.</param>
/// <param name="NetworkName">What Windows calls it.</param>
/// <param name="LastLook">When the last look finished.</param>
/// <param name="Sweeping">Whether a sweep of the whole network is running.</param>
public sealed record HomeStatus(
    HomeLookState State,
    HomeAbsence Absence,
    string? NetworkKey,
    string? NetworkName,
    DateTimeOffset? LastLook,
    bool Sweeping)
{
    /// <summary>Before anything was looked at, or while switched off.</summary>
    public static HomeStatus Off { get; } = new(HomeLookState.Off, HomeAbsence.None, null, null, null, false);
}

/// <summary>
/// Keeps the list of devices on the home network: reads Windows' neighbour
/// table, names what it finds, and says when something new joins.
/// </summary>
/// <remarks>
/// <para>
/// Nothing happens until <see cref="HomeSettings.FindDevices"/> is on, and then
/// only on a network Windows classes as private. Reading the neighbour table
/// sends nothing, and is done every two minutes, or every fifteen seconds while
/// somebody watches. Naming sends a little, all of it to the home network: a
/// multicast DNS browse at most hourly (every fifteen minutes while watched), a
/// NetBIOS query to a device and a reverse lookup to the router for each device
/// without a name, at most every six hours each. A sweep - an ARP request to
/// every address on the link - happens only when asked for.
/// </para>
/// <para>
/// A device is new the first time it is seen on a network. Nothing is announced
/// for an hour after a network is first looked at, since everything is new
/// then, nor for devices a sweep turns up, which the person is looking at
/// already. A device using a private hardware address can come back as new
/// when it changes that address.
/// </para>
/// </remarks>
public sealed partial class HomeDevices : BackgroundService
{
    private static readonly TimeSpan Background = TimeSpan.FromMinutes(2);
    private static readonly TimeSpan Watched = TimeSpan.FromSeconds(15);
    private static readonly TimeSpan BrowseBackground = TimeSpan.FromHours(1);
    private static readonly TimeSpan BrowseWatched = TimeSpan.FromMinutes(15);
    private static readonly TimeSpan AskAgainAfter = TimeSpan.FromHours(6);
    private static readonly TimeSpan SettleTime = TimeSpan.FromHours(1);
    private const int MostNamedAPass = 16;
    private const int MostAskedAfterBrowse = 32;

    private readonly WindsockSettings _settings;
    private readonly HomeLinkReader _links;
    private readonly INeighbourSource _neighbours;
    private readonly IMulticastBrowser _multicast;
    private readonly IDeviceNamer _namer;
    private readonly IHomeStore _store;
    private readonly MakerNames _makers;
    private readonly TimeProvider _time;
    private readonly ILogger<HomeDevices> _logger;
    private readonly SemaphoreSlim _nudge = new(0, 1);
    private readonly Lock _gate = new();
    private readonly Dictionary<(string, MacAddress), DateTimeOffset> _asked = [];

    private int _watchers;
    private bool _lookAgain = true;
    private TaskCompletionSource<int>? _sweep;
    private DateTimeOffset _browsed = DateTimeOffset.MinValue;
    private IReadOnlyList<HomeDevice> _devices = [];
    private HomeReading _reading = new([], HomeAbsence.Unknown);

    public HomeDevices(
        WindsockSettings settings,
        HomeLinkReader links,
        INeighbourSource neighbours,
        IMulticastBrowser multicast,
        IDeviceNamer namer,
        IHomeStore store,
        MakerNames makers,
        TimeProvider time,
        ILogger<HomeDevices> logger)
    {
        ArgumentNullException.ThrowIfNull(store);

        _settings = settings;
        _links = links;
        _neighbours = neighbours;
        _multicast = multicast;
        _namer = namer;
        _store = store;
        _makers = makers;
        _time = time;
        _logger = logger;
    }

    /// <summary>Raised, on a background thread, after the list or the status changes.</summary>
    public event EventHandler? Changed;

    /// <summary>Raised, on a background thread, for each device seen on the home network for the first time.</summary>
    public event EventHandler<DeviceJoined>? Joined;

    /// <summary>Where looking stands.</summary>
    public HomeStatus Status { get; private set; } = HomeStatus.Off;

    /// <summary>The devices on the home network being looked at, most recently seen first, each with its maker.</summary>
    public IReadOnlyList<HomeDevice> Devices
    {
        get
        {
            lock (_gate)
            {
                return _devices;
            }
        }
    }

    /// <summary>The home link being looked at, or nothing.</summary>
    public HomeLink? Link
    {
        get
        {
            lock (_gate)
            {
                return _reading.Primary;
            }
        }
    }

    /// <summary>Whether the setting is on.</summary>
    public bool IsEnabled => _settings.Home.FindDevices;

    /// <summary>Asks for looks every fifteen seconds rather than two minutes, until the token is let go.</summary>
    public IDisposable Watch()
    {
        lock (_gate)
        {
            _watchers++;
        }

        Volatile.Write(ref _lookAgain, true);
        Nudge();
        return new Watcher(this);
    }

    /// <summary>Looks again now, as after the setting changed.</summary>
    public void Refresh()
    {
        Volatile.Write(ref _lookAgain, true);
        Nudge();
    }

    /// <summary>
    /// Asks every address on the home link who holds it, browses for names and
    /// looks again. Returns how many devices the list then holds; 0 when not on
    /// a home network or switched off. Only ever done because somebody asked.
    /// </summary>
    public Task<int> SweepAsync()
    {
        if (!IsEnabled)
        {
            return Task.FromResult(0);
        }

        TaskCompletionSource<int> sweep;

        lock (_gate)
        {
            _sweep ??= new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
            sweep = _sweep;
        }

        Nudge();
        return sweep.Task;
    }

    /// <summary>
    /// Sends a Wake-on-LAN packet to a device on the home link. False when it
    /// could not be sent: switched off, not on a home network, no longer on the
    /// network whose devices are shown, or the address is not one a device can
    /// have. True says only that it was sent.
    /// </summary>
    public async Task<bool> WakeAsync(MacAddress device, CancellationToken cancellationToken)
    {
        HomeLink? shown = Link;

        if (!IsEnabled || shown is null)
        {
            return false;
        }

        // The network as it is now, not as it was at the last look: the packet
        // is a broadcast, and after a move it would go out on whatever network
        // the PC has joined since, where the device is not.
        HomeLink? now = await Task.Run(() => _links.Read().Primary, cancellationToken).ConfigureAwait(false);

        return now is not null
            && string.Equals(now.NetworkKey, shown.NetworkKey, StringComparison.Ordinal)
            && await WakeOnLan.SendAsync(device, now, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Gives a device a name of the person's own, or takes it away with nothing.</summary>
    public void Rename(MacAddress device, string? nickname)
    {
        if (Status.NetworkKey is not { } network)
        {
            return;
        }

        try
        {
            _store.Nickname(network, device, nickname);
        }
        catch (Exception ex) when (HistoryFailure.IsStorage(ex))
        {
            LogStoreFailed(ex);
        }

        Publish(network);
    }

    /// <summary>Forgets a device; it counts as new if it comes back.</summary>
    public bool Forget(MacAddress device)
    {
        if (Status.NetworkKey is not { } network)
        {
            return false;
        }

        bool gone = false;

        try
        {
            gone = _store.Forget(network, device);
        }
        catch (Exception ex) when (HistoryFailure.IsStorage(ex))
        {
            LogStoreFailed(ex);
        }

        Publish(network);
        return gone;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            TaskCompletionSource<int>? sweep;

            lock (_gate)
            {
                sweep = _sweep;
            }

            try
            {
                int count = IsEnabled
                    ? await LookAsync(sweep is not null, stoppingToken).ConfigureAwait(false)
                    : SwitchOff();

                Complete(sweep, count);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                Complete(sweep, 0);
                return;
            }
            catch (Exception ex) when (ex is SocketException or IOException or System.Runtime.InteropServices.COMException or System.ComponentModel.Win32Exception)
            {
                // A network that went away mid-look: the next look tries again.
                LogLookFailed(ex);
                Complete(sweep, Devices.Count);
            }

            try
            {
                _ = await _nudge.WaitAsync(IsWatched ? Watched : Background, stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
        }
    }

    private bool IsWatched
    {
        get
        {
            lock (_gate)
            {
                return _watchers > 0;
            }
        }
    }

    private void Complete(TaskCompletionSource<int>? sweep, int count)
    {
        if (sweep is null)
        {
            return;
        }

        lock (_gate)
        {
            if (ReferenceEquals(_sweep, sweep))
            {
                _sweep = null;
            }
        }

        sweep.TrySetResult(count);
    }

    /// <summary>
    /// Switched off: nothing is looked for or asked, but the devices already
    /// kept for the home network this PC is on are still shown, and can still
    /// be renamed or forgotten. Reading which network that is and the kept list
    /// sends nothing, and is done only when the setting changed or somebody
    /// asked, not on every pass.
    /// </summary>
    private int SwitchOff()
    {
        if (Status.State == HomeLookState.Off && !Interlocked.Exchange(ref _lookAgain, false))
        {
            return Devices.Count;
        }

        Interlocked.Exchange(ref _lookAgain, false);
        HomeReading reading = _links.Read();
        HomeLink? primary = reading.Primary;

        lock (_gate)
        {
            _reading = reading;
            Status = HomeStatus.Off with { NetworkKey = primary?.NetworkKey, NetworkName = primary?.NetworkName, Absence = reading.Absence };
        }

        if (primary is null)
        {
            lock (_gate)
            {
                _devices = [];
            }

            Changed?.Invoke(this, EventArgs.Empty);
            return 0;
        }

        return Publish(primary.NetworkKey);
    }

    private async Task<int> LookAsync(bool sweep, CancellationToken cancellationToken)
    {
        HomeReading reading = _links.Read();

        lock (_gate)
        {
            _reading = reading;
        }

        if (reading.Primary is not { } primary)
        {
            lock (_gate)
            {
                _devices = [];
                Status = new HomeStatus(HomeLookState.Away, reading.Absence, null, null, _time.GetLocalNow(), false);
            }

            Changed?.Invoke(this, EventArgs.Empty);
            return 0;
        }

        if (sweep)
        {
            Status = new HomeStatus(HomeLookState.Looking, HomeAbsence.None, primary.NetworkKey, primary.NetworkName, Status.LastLook, Sweeping: true);
            Changed?.Invoke(this, EventArgs.Empty);

            foreach (HomeLink link in reading.Links)
            {
                IReadOnlyList<(IPAddress Address, MacAddress Mac)> answered =
                    await ArpSweep.SweepAsync(_neighbours, link, cancellationToken).ConfigureAwait(false);
                LogSwept(link.NetworkName, answered.Count);
            }
        }

        DateTimeOffset now = _time.GetLocalNow();
        IReadOnlyList<Neighbour> rows = _neighbours.Read();
        List<DeviceJoined> joined = [];

        foreach (IGrouping<string, HomeLink> network in reading.Links.GroupBy(each => each.NetworkKey, StringComparer.Ordinal))
        {
            List<DeviceSighting> sightings = Sightings(rows, [.. network]);
            joined.AddRange(Record(network.First(), sightings, now, announce: !sweep));
        }

        bool browse = sweep || now - _browsed >= (IsWatched ? BrowseWatched : BrowseBackground);

        if (browse)
        {
            _browsed = now;

            foreach (HomeLink link in reading.Links)
            {
                await NameFromMulticastAsync(link, rows, now, cancellationToken).ConfigureAwait(false);
            }
        }

        foreach (HomeLink link in reading.Links.DistinctBy(each => each.NetworkKey))
        {
            await AskForNamesAsync(link, now, cancellationToken).ConfigureAwait(false);
        }

        lock (_gate)
        {
            Status = new HomeStatus(HomeLookState.Looking, HomeAbsence.None, primary.NetworkKey, primary.NetworkName, now, Sweeping: false);
        }

        int count = Publish(primary.NetworkKey);

        foreach (DeviceJoined each in joined)
        {
            Joined?.Invoke(this, each);
        }

        return count;
    }

    /// <summary>The devices in the neighbour table on a network's links, one sighting per hardware address.</summary>
    internal static List<DeviceSighting> Sightings(IReadOnlyList<Neighbour> rows, IReadOnlyList<HomeLink> links)
    {
        HashSet<uint> interfaces = [.. links.Select(each => each.InterfaceIndex)];
        HashSet<IPAddress> gateways = [.. links.Select(each => each.Gateway).OfType<IPAddress>()];
        Dictionary<MacAddress, (IPAddress? Four, IPAddress? Six, bool Router)> found = [];

        foreach (Neighbour row in rows)
        {
            if (!interfaces.Contains(row.InterfaceIndex) || !NeighbourFilter.IsDevice(row))
            {
                continue;
            }

            (IPAddress? four, IPAddress? six, bool router) = found.GetValueOrDefault(row.Mac);

            if (row.Address.AddressFamily == AddressFamily.InterNetwork)
            {
                if (links.Any(each => each.Contains(row.Address)) || four is null)
                {
                    four = row.Address;
                }
            }
            else if (six is null || (six.IsIPv6LinkLocal && !row.Address.IsIPv6LinkLocal))
            {
                six = row.Address;
            }

            found[row.Mac] = (four, six, router || row.IsRouter || gateways.Contains(row.Address));
        }

        return [.. found.Select(each => new DeviceSighting(each.Key, each.Value.Four, each.Value.Six, each.Value.Router))];
    }

    private List<DeviceJoined> Record(HomeLink link, List<DeviceSighting> sightings, DateTimeOffset now, bool announce)
    {
        try
        {
            IReadOnlyList<HomeDevice> fresh = _store.Record(link.NetworkKey, link.NetworkName, sightings, now);
            DateTimeOffset? baseline = _store.Baseline(link.NetworkKey);

            if (baseline is null)
            {
                _store.SetBaseline(link.NetworkKey, now);
                return [];
            }

            return announce && now - baseline.Value >= SettleTime
                ? [.. fresh.Select(each => new DeviceJoined(each with { Maker = _makers.Find(each.Mac) }, link.NetworkName))]
                : [];
        }
        catch (Exception ex) when (HistoryFailure.IsStorage(ex))
        {
            LogStoreFailed(ex);
            return [];
        }
    }

    private async Task NameFromMulticastAsync(HomeLink link, IReadOnlyList<Neighbour> rows, DateTimeOffset now, CancellationToken cancellationToken)
    {
        IReadOnlyList<MulticastService> services = await _multicast.BrowseAsync(link.InterfaceIndex, cancellationToken).ConfigureAwait(false);
        Dictionary<IPAddress, MacAddress> byAddress = [];

        foreach (Neighbour row in rows)
        {
            if (row.InterfaceIndex == link.InterfaceIndex && NeighbourFilter.IsDevice(row))
            {
                byAddress[row.Address] = row.Mac;
            }
        }

        List<DeviceSighting> learned = [];
        List<(MacAddress Mac, string Name)> names = [];
        int asked = 0;

        foreach (MulticastService service in services)
        {
            if (service.DeviceName is not { } name)
            {
                continue;
            }

            foreach (IPAddress address in service.Addresses)
            {
                if (!byAddress.TryGetValue(address, out MacAddress mac)
                    && address.AddressFamily == AddressFamily.InterNetwork
                    && link.Contains(address)
                    && link.Address is { } own
                    && asked++ < MostAskedAfterBrowse
                    && _neighbours.Ask(address, own) is { } answered
                    && answered.IsUnicast)
                {
                    mac = answered;
                    byAddress[address] = mac;
                    learned.Add(new DeviceSighting(mac, address, null, link.Gateway?.Equals(address) == true));
                }

                if (byAddress.TryGetValue(address, out MacAddress known))
                {
                    names.Add((known, name));
                    break;
                }
            }
        }

        try
        {
            if (learned.Count > 0)
            {
                _ = _store.Record(link.NetworkKey, link.NetworkName, learned, now);
            }

            foreach ((MacAddress mac, string name) in names.DistinctBy(each => each.Mac))
            {
                _store.Name(link.NetworkKey, mac, name, DeviceNameSource.Multicast, now);
            }
        }
        catch (Exception ex) when (HistoryFailure.IsStorage(ex))
        {
            LogStoreFailed(ex);
        }

        LogBrowsed(link.NetworkName, services.Count, names.Count);
    }

    private async Task AskForNamesAsync(HomeLink link, DateTimeOffset now, CancellationToken cancellationToken)
    {
        IReadOnlyList<HomeDevice> devices;

        try
        {
            devices = _store.Read(link.NetworkKey);
        }
        catch (Exception ex) when (HistoryFailure.IsStorage(ex))
        {
            LogStoreFailed(ex);
            return;
        }

        int asked = 0;

        foreach (HomeDevice device in devices)
        {
            if (asked >= MostNamedAPass)
            {
                break;
            }

            if (device.FoundName is not null || device.Address is not { } address || !link.Contains(address))
            {
                continue;
            }

            (string, MacAddress) key = (link.NetworkKey, device.Mac);

            if (_asked.TryGetValue(key, out DateTimeOffset last) && now - last < AskAgainAfter)
            {
                continue;
            }

            _asked[key] = now;
            asked++;

            if (await _namer.NameAsync(address, link, cancellationToken).ConfigureAwait(false) is { } found)
            {
                try
                {
                    _store.Name(link.NetworkKey, device.Mac, found.Name, found.Source, now);
                }
                catch (Exception ex) when (HistoryFailure.IsStorage(ex))
                {
                    LogStoreFailed(ex);
                    return;
                }
            }
        }
    }

    private int Publish(string network)
    {
        IReadOnlyList<HomeDevice> devices;

        try
        {
            devices = [.. _store.Read(network).Select(each => each with { Maker = _makers.Find(each.Mac) })];
        }
        catch (Exception ex) when (HistoryFailure.IsStorage(ex))
        {
            LogStoreFailed(ex);
            return Devices.Count;
        }

        lock (_gate)
        {
            _devices = devices;
        }

        Changed?.Invoke(this, EventArgs.Empty);
        return devices.Count;
    }

    private void Nudge()
    {
        _nudge.Signal();
    }

    public override void Dispose()
    {
        base.Dispose();
        _nudge.Dispose();
    }

    private sealed class Watcher(HomeDevices owner) : IDisposable
    {
        private HomeDevices? _owner = owner;

        public void Dispose()
        {
            HomeDevices? owner = Interlocked.Exchange(ref _owner, null);

            if (owner is null)
            {
                return;
            }

            lock (owner._gate)
            {
                owner._watchers--;
            }
        }
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "The home device list could not be read or written")]
    private partial void LogStoreFailed(Exception exception);

    [LoggerMessage(Level = LogLevel.Warning, Message = "A look for home devices failed; the next one will try again")]
    private partial void LogLookFailed(Exception exception);

    [LoggerMessage(Level = LogLevel.Information, Message = "Swept {Network}: {Count} devices answered")]
    private partial void LogSwept(string network, int count);

    [LoggerMessage(Level = LogLevel.Debug, Message = "Browsed {Network}: {Services} services, {Named} devices named")]
    private partial void LogBrowsed(string network, int services, int named);
}
