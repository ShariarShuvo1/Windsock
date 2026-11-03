using System.Collections.ObjectModel;
using System.Globalization;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using FluentIcons.Common;
using Windsock.Core.Home;
using Windsock.Core.Settings;

namespace Windsock.App.ViewModels;

/// <summary>One device on the home network, as its tile shows it.</summary>
public sealed partial class DeviceRowViewModel : ObservableObject
{
    private readonly DevicesPanelViewModel _owner;

    internal DeviceRowViewModel(DevicesPanelViewModel owner, HomeDevice device, DateTimeOffset now)
    {
        _owner = owner;
        Device = device;
        Name = device.Name ?? (device.IsRouter ? "Router" : device.Maker ?? "Unnamed device");
        bool namedForMaker = device.Name is null && !device.IsRouter && device.Maker is not null;
        Symbol = device.IsRouter ? Symbol.Router : device.HasPrivateAddress ? Symbol.Phone : Symbol.PhoneLaptop;
        IsHereNow = device.SeenWithin(now, TimeSpan.FromMinutes(10));

        List<string> first = [];

        if (device.Address is { } address)
        {
            first.Add(address.ToString());
        }

        first.Add(device.Mac.ToString());
        Addresses = string.Join(" · ", first);

        Maker = namedForMaker
            ? "No name found: named for its maker"
            : device.Maker ?? (device.HasPrivateAddress ? "Private address: the device hides its maker" : "Maker not known");

        List<string> second = [string.Concat("Last seen ", HomeText.Ago(device.LastSeen, now)), string.Concat("first seen ", HomeText.Ago(device.FirstSeen, now))];

        if (device.Nickname is null && device.FoundName is not null)
        {
            second.Add(device.NameSource switch
            {
                DeviceNameSource.Multicast => "named by the device",
                DeviceNameSource.NetBios => "named by the device",
                DeviceNameSource.Router => "named by the router",
                _ => string.Empty,
            });
        }

        Seen = string.Join(" · ", second.Where(each => each.Length > 0));
        Spoken = string.Concat(Name, ", ", Maker, ", ", Addresses, ", ", Seen);
        EditName = device.Name ?? string.Empty;
    }

    /// <summary>The device.</summary>
    public HomeDevice Device { get; }

    /// <summary>The name shown: the person's own, the one found, or what it is.</summary>
    public string Name { get; }

    /// <summary>The icon: a router, a phone-like device with a private address, or a computer.</summary>
    public Symbol Symbol { get; }

    /// <summary>Who made its adapter, or why that is not known.</summary>
    public string Maker { get; }

    /// <summary>Its address and hardware address.</summary>
    public string Addresses { get; }

    /// <summary>When it was last and first seen, and where its name came from.</summary>
    public string Seen { get; }

    /// <summary>Whether it was seen in the last ten minutes.</summary>
    public bool IsHereNow { get; }

    /// <summary>Whether it is the network's router.</summary>
    public bool IsRouter => Device.IsRouter;

    /// <summary>What a screen reader says for the tile.</summary>
    public string Spoken { get; }

    /// <summary>Whether it can be sent a wake-up packet: not the router, and looking is on.</summary>
    public bool CanWake => !Device.IsRouter && _owner.IsLooking;

    /// <summary>Whether the name is being edited.</summary>
    [ObservableProperty]
    public partial bool IsRenaming { get; set; }

    /// <summary>The name as typed.</summary>
    [ObservableProperty]
    public partial string EditName { get; set; }

    /// <summary>Whether forgetting it is being asked about.</summary>
    [ObservableProperty]
    public partial bool IsConfirmingForget { get; set; }

    [RelayCommand]
    private void StartRename()
    {
        EditName = Device.Name ?? string.Empty;
        IsRenaming = true;
    }

    [RelayCommand]
    private void SaveName()
    {
        IsRenaming = false;
        _owner.Rename(this, EditName);
    }

    [RelayCommand]
    private void CancelRename() => IsRenaming = false;

    [RelayCommand]
    private Task WakeAsync() => _owner.WakeAsync(this);

    [RelayCommand]
    private void AskForget() => IsConfirmingForget = true;

    [RelayCommand]
    private void CancelForget() => IsConfirmingForget = false;

    [RelayCommand]
    private void Forget()
    {
        IsConfirmingForget = false;
        _owner.Forget(this);
    }
}

/// <summary>One port the router has opened to the internet, as its row shows it.</summary>
/// <param name="Port">The port and protocol, as "TCP 51413".</param>
/// <param name="Leads">Where it leads: the device's name and port, or its address.</param>
/// <param name="Asked">What the program that opened it called it, and how long it lasts.</param>
/// <param name="IsThisPc">Whether it leads to this PC.</param>
public sealed record PortMappingRowViewModel(string Port, string Leads, string Asked, bool IsThisPc)
{
    /// <summary>What a screen reader says for the row.</summary>
    public string Spoken => string.Concat(Port, ", to ", Leads, ", ", Asked);
}

/// <summary>
/// Backs Network › Home devices: what is on the home network, and the router's
/// own account of itself and of the ports it has opened.
/// </summary>
/// <remarks>
/// <para>
/// While the page is on screen it holds <see cref="HomeDevices.Watch"/>, which
/// looks every fifteen seconds instead of every two minutes - only when looking
/// is switched on, and only on a private network; the page says plainly which
/// of those is missing and offers the switch. Switched off, the devices already
/// kept for this network are still listed, and can be renamed or forgotten.
/// </para>
/// <para>
/// The router band reads the last kept report; a check runs only on the
/// button, or by itself once a week while the router check is on. Port
/// mappings are listed to review, not as an alarm: a game console or a call
/// opening a port is the ordinary case.
/// </para>
/// </remarks>
public sealed partial class DevicesPanelViewModel : ObservableObject, IDisposable
{
    private readonly HomeDevices _devices;
    private readonly RouterWatch _router;
    private readonly WindsockSettings _settings;
    private readonly ISettingsStore _store;
    private readonly Dispatcher _dispatcher;

    private IDisposable? _watch;
    private RouterReport? _report;
    private bool _shown;
    private bool _disposed;

    public DevicesPanelViewModel(
        HomeDevices devices,
        RouterWatch router,
        WindsockSettings settings,
        ISettingsStore store,
        Dispatcher dispatcher)
    {
        ArgumentNullException.ThrowIfNull(devices);
        ArgumentNullException.ThrowIfNull(router);

        _devices = devices;
        _router = router;
        _settings = settings;
        _store = store;
        _dispatcher = dispatcher;

        _devices.Changed += OnDevicesChanged;
        _router.Checked += OnRouterChecked;
        Read();
    }

    /// <summary>The device tiles, the router first, then the most recently seen.</summary>
    public ObservableCollection<DeviceRowViewModel> Rows { get; } = [];

    /// <summary>The router's opened ports.</summary>
    public ObservableCollection<PortMappingRowViewModel> Mappings { get; } = [];

    /// <summary>The line under the page's title.</summary>
    [ObservableProperty]
    public partial string PageSubtitle { get; private set; } = string.Empty;

    /// <summary>Whether devices are being looked for, on a home network.</summary>
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(SweepCommand))]
    public partial bool IsLooking { get; private set; }

    /// <summary>Whether looking is switched off.</summary>
    [ObservableProperty]
    public partial bool IsOff { get; private set; }

    /// <summary>Why nothing is being looked for, or what is going on; empty while all is ordinary.</summary>
    [ObservableProperty]
    public partial string StatusNotice { get; private set; } = string.Empty;

    /// <summary>What the last action did, such as a wake-up packet sent.</summary>
    [ObservableProperty]
    public partial string ActionNotice { get; private set; } = string.Empty;

    /// <summary>Whether a sweep is running.</summary>
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(SweepCommand))]
    public partial bool IsSweeping { get; private set; }

    /// <summary>Whether there are tiles.</summary>
    [ObservableProperty]
    public partial bool HasRows { get; private set; }

    /// <summary>Whether there are none.</summary>
    [ObservableProperty]
    public partial bool IsEmpty { get; private set; } = true;

    /// <summary>The empty list's title.</summary>
    [ObservableProperty]
    public partial string EmptyTitle { get; private set; } = string.Empty;

    /// <summary>The empty list's note: why, and what to do.</summary>
    [ObservableProperty]
    public partial string EmptyNote { get; private set; } = string.Empty;

    /// <summary>The list band's note: how many, and when it last looked.</summary>
    [ObservableProperty]
    public partial string ListNote { get; private set; } = string.Empty;

    // ------------------------------------------------------------------ router

    /// <summary>Whether the router check is switched on.</summary>
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(CheckRouterCommand))]
    public partial bool IsRouterCheckOn { get; private set; }

    /// <summary>Whether a check is running.</summary>
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(CheckRouterCommand))]
    public partial bool IsChecking { get; private set; }

    /// <summary>Whether there is a report to show.</summary>
    [ObservableProperty]
    public partial bool HasReport { get; private set; }

    /// <summary>The router's maker and model, or what the check found.</summary>
    [ObservableProperty]
    public partial string RouterName { get; private set; } = string.Empty;

    /// <summary>When it was checked: "Checked 3 days ago".</summary>
    [ObservableProperty]
    public partial string RouterChecked { get; private set; } = string.Empty;

    /// <summary>The router's internet side, in words.</summary>
    [ObservableProperty]
    public partial string RouterOutside { get; private set; } = string.Empty;

    /// <summary>The router band's note, or why there is no report.</summary>
    [ObservableProperty]
    public partial string RouterNote { get; private set; } = string.Empty;

    /// <summary>Whether ports are listed.</summary>
    [ObservableProperty]
    public partial bool HasMappings { get; private set; }

    /// <summary>A word under the ports: none opened, or the list may be longer.</summary>
    [ObservableProperty]
    public partial string MappingsNote { get; private set; } = string.Empty;

    /// <summary>Starts following the network while the page is on screen.</summary>
    public void Show()
    {
        if (_disposed || _shown)
        {
            return;
        }

        _shown = true;
        _watch = _devices.Watch();
        _ = LoadReportAsync();
        Read();
    }

    /// <summary>Stops, once the page is no longer on screen.</summary>
    public void Hide()
    {
        if (!_shown)
        {
            return;
        }

        _shown = false;
        _watch?.Dispose();
        _watch = null;
    }

    [RelayCommand]
    private void TurnOn()
    {
        _settings.Home.FindDevices = true;
        _store.Save(_settings);
        _devices.Refresh();
        Read();
    }

    [RelayCommand]
    private void TurnOnRouterCheck()
    {
        _settings.Home.CheckRouter = true;
        _store.Save(_settings);
        Read();
        _ = CheckRouterAsync();
    }

    private bool CanSweep => IsLooking && !IsSweeping;

    [RelayCommand(CanExecute = nameof(CanSweep))]
    private async Task SweepAsync()
    {
        IsSweeping = true;
        ActionNotice = "Asking every address on this network who holds it. This takes about half a minute.";

        try
        {
            int count = await _devices.SweepAsync().ConfigureAwait(true);
            ActionNotice = string.Create(CultureInfo.CurrentCulture, $"Looked at every address: {count} {(count == 1 ? "device" : "devices")} on the list.");
        }
        finally
        {
            IsSweeping = false;
        }
    }

    private bool CanCheck => IsRouterCheckOn && !IsChecking;

    [RelayCommand(CanExecute = nameof(CanCheck))]
    private async Task CheckRouterAsync()
    {
        IsChecking = true;
        RouterNote = "Asking your router…";

        try
        {
            RouterReport? report = await Task.Run(() => _router.CheckAsync(CancellationToken.None)).ConfigureAwait(true);

            if (report is not null)
            {
                ShowReport(report);
            }
        }
        finally
        {
            IsChecking = false;
        }
    }

    internal void Rename(DeviceRowViewModel row, string name)
    {
        _devices.Rename(row.Device.Mac, string.Equals(name.Trim(), row.Device.FoundName, StringComparison.Ordinal) ? null : name);
    }

    internal async Task WakeAsync(DeviceRowViewModel row)
    {
        bool sent = await _devices.WakeAsync(row.Device.Mac, CancellationToken.None).ConfigureAwait(true);
        ActionNotice = sent
            ? string.Concat("Sent a wake-up packet to ", row.Name, ". Whether it wakes is up to the device and its settings.")
            : string.Concat("The wake-up packet to ", row.Name, " could not be sent.");
    }

    internal void Forget(DeviceRowViewModel row)
    {
        if (_devices.Forget(row.Device.Mac))
        {
            ActionNotice = string.Concat("Forgot ", row.Name, ". If it is seen again it counts as new.");
        }
    }

    private async Task LoadReportAsync()
    {
        RouterReport? report = await Task.Run(_router.Latest).ConfigureAwait(true);

        if (!_disposed)
        {
            _report = report;
            ShowReport(report);
        }
    }

    private void OnDevicesChanged(object? sender, EventArgs e) => _dispatcher.BeginInvoke(new Action(Read));

    private void OnRouterChecked(object? sender, RouterReport report) => _dispatcher.BeginInvoke(new Action(() => ShowReport(report)));

    private void Read()
    {
        if (_disposed)
        {
            return;
        }

        HomeStatus status = _devices.Status;
        DateTimeOffset now = DateTimeOffset.Now;

        IsOff = !_settings.Home.FindDevices;
        IsLooking = status.State == HomeLookState.Looking;
        IsRouterCheckOn = _settings.Home.CheckRouter;

        string network = status.NetworkName is { Length: > 0 } name ? name : "this network";

        PageSubtitle = status.State switch
        {
            HomeLookState.Looking => string.Concat("What is connected to ", network),
            _ when status.NetworkName is not null => string.Concat("What has been seen on ", network),
            _ => "What is connected at home",
        };

        StatusNotice = IsOff
            ? "Looking for devices is off. When it is on, Windsock reads the list of neighbours Windows already keeps, and asks devices on this network for their names. It only ever looks on a network Windows calls private, through a wired or Wi-Fi connection with its own router and never through a VPN, and everything it sends goes to a device on your network or to your router."
            : status.State == HomeLookState.Away
                ? HomeScope.Describe(status.Absence)
                : string.Empty;

        IReadOnlyList<HomeDevice> devices = _devices.Devices;
        List<HomeDevice> ordered = [.. devices.OrderByDescending(each => each.IsRouter).ThenByDescending(each => each.LastSeen)];

        Rows.Clear();

        foreach (HomeDevice device in ordered)
        {
            Rows.Add(new DeviceRowViewModel(this, device, now));
        }

        HasRows = Rows.Count > 0;
        IsEmpty = !HasRows;

        (EmptyTitle, EmptyNote) = (IsOff, status.State) switch
        {
            (true, _) => ("Nothing looked for yet", "Turn looking on to list the devices on your home network: names, makers, and when each was first and last seen."),
            (false, HomeLookState.Away) => ("Not on a home network", HomeScope.Describe(status.Absence)),
            _ => ("No devices yet", "Only the devices this PC has talked to lately show up by themselves. Look for every device asks each address on the network, once."),
        };

        string count = string.Create(CultureInfo.CurrentCulture, $"{Rows.Count} {(Rows.Count == 1 ? "device" : "devices")}");
        int here = Rows.Count(each => each.IsHereNow);
        ListNote = status.LastLook is { } last && IsLooking
            ? string.Create(CultureInfo.CurrentCulture, $"{count}, {here} seen in the last ten minutes · looked {HomeText.Ago(last, now)}")
            : count;

        if (_report is { } report)
        {
            ShowReport(report);
        }
        else if (!IsRouterCheckOn)
        {
            RouterNote = RouterOffNote;
        }
    }

    private const string RouterOffNote = "The router check is off. When it is on, Windsock asks your router over UPnP for its maker, its model and the ports it has opened to the internet - only ever asking, never changing anything. Nothing it sends leaves your network.";

    private void ShowReport(RouterReport? report)
    {
        if (_disposed)
        {
            return;
        }

        _report = report;
        Mappings.Clear();
        HasReport = report is { Answer: RouterAnswer.Answered or RouterAnswer.NotAGateway };

        if (report is null)
        {
            RouterName = string.Empty;
            RouterChecked = string.Empty;
            RouterOutside = string.Empty;
            RouterNote = IsRouterCheckOn ? "Not checked yet on this network. Check asks your router once." : RouterOffNote;
            HasMappings = false;
            MappingsNote = string.Empty;
            return;
        }

        DateTimeOffset now = DateTimeOffset.Now;
        RouterChecked = string.Concat("Checked ", HomeText.Ago(report.CheckedAt, now));
        RouterName = report.MakeAndModel ?? "Your router";

        RouterNote = report.Answer switch
        {
            RouterAnswer.NoUpnp => "Your router did not answer UPnP. That is often on purpose, and is the safer setting: no program on the network can open a port by itself.",
            RouterAnswer.NotAGateway => "Your router answered UPnP but offers no internet gateway service, so it says nothing about its ports.",
            RouterAnswer.NotHome => "Not on a home network with a router.",
            _ when !IsRouterCheckOn => RouterOffNote,
            _ => "What your router says about itself. Every figure is the router's own word.",
        };

        RouterOutside = report.ExternalKind switch
        {
            ExternalAddressKind.Public => string.Concat("Internet address ", report.ExternalAddress, " · ", report.ConnectionStatus ?? "status not given"),
            ExternalAddressKind.Private => string.Concat("Its own outside address is private (", report.ExternalAddress, "): there is another router between it and the internet, so a port it opens may still be closed further out"),
            ExternalAddressKind.Shared => string.Concat("Its outside address is shared by your provider (", report.ExternalAddress, ", carrier-grade NAT): ports it opens do not reach the internet"),
            _ => report.ConnectionStatus is { } state ? string.Concat("Connection ", state.ToLowerInvariant()) : string.Empty,
        };

        Dictionary<string, string> names = new(StringComparer.Ordinal);

        foreach (HomeDevice device in _devices.Devices)
        {
            if (device.Address is { } address)
            {
                names[address.ToString()] = device.Name ?? device.Maker ?? address.ToString();
            }
        }

        HashSet<string> mine = [.. System.Net.NetworkInformation.NetworkInterface.GetAllNetworkInterfaces()
            .SelectMany(each => each.GetIPProperties().UnicastAddresses)
            .Select(each => each.Address.ToString())];

        foreach (PortMapping mapping in report.PortMappings.OrderBy(each => each.ExternalPort))
        {
            bool thisPc = mine.Contains(mapping.InternalClient);
            string device = thisPc ? "this PC" : names.GetValueOrDefault(mapping.InternalClient) ?? mapping.InternalClient;
            string lasts = mapping.LeaseSeconds > 0
                ? string.Concat("for ", Core.Formatting.DurationLabel.Format(mapping.LeaseSeconds))
                : "until removed";
            string asked = string.Concat(
                mapping.Description.Length > 0 ? string.Concat("“", mapping.Description, "”") : "No description",
                ", ",
                lasts,
                mapping.Enabled ? string.Empty : ", switched off",
                mapping.RemoteHost.Length > 0 ? string.Concat(", only from ", mapping.RemoteHost) : string.Empty);

            Mappings.Add(new PortMappingRowViewModel(
                string.Create(CultureInfo.InvariantCulture, $"{mapping.Protocol} {mapping.ExternalPort}"),
                string.Create(CultureInfo.CurrentCulture, $"{device}, port {mapping.InternalPort}"),
                asked,
                thisPc));
        }

        HasMappings = Mappings.Count > 0;
        MappingsNote = report.Answer != RouterAnswer.Answered
            ? string.Empty
            : !report.MappingsComplete
                ? "The router stopped answering part way, or would not list them, so more ports may be open than are shown."
                : Mappings.Count == 0
                    ? "No ports opened through UPnP."
                    : "Opened by programs through UPnP. A game console, a call or file sharing doing so is ordinary; one you do not recognise is worth a look.";
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        Hide();
        _devices.Changed -= OnDevicesChanged;
        _router.Checked -= OnRouterChecked;
    }
}
