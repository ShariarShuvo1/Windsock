using System.Collections.ObjectModel;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using Windsock.App.Controls;
using Windsock.App.Services.Network;
using Windsock.Core.Adapters;
using Windsock.Core.Connectivity;
using Windsock.Core.Formatting;
using Windsock.Core.Lookups;
using Windsock.Core.Qr;
using Windsock.Core.SpeedTests;
using Windsock.Core.Wifi;

namespace Windsock.App.ViewModels;

/// <summary>
/// Backs Network › Adapters and Wi-Fi: every adapter with its addresses,
/// router, DNS servers and metered state; the Wi-Fi link in detail, its signal
/// over the last minutes, its drops and, on request, the networks nearby; and
/// the network's details as text for a help request.
/// </summary>
/// <remarks>
/// <para>
/// Everything here is read from Windows and nothing is sent anywhere. The
/// adapters and the Wi-Fi are watched only while the page is on screen. The
/// Wi-Fi network's name needs Windows' location permission since 24H2, so it
/// is read straight away only when Windows' settings already allow it, and
/// otherwise only when the person presses for it - which may make Windows ask
/// them. A scan needs the same permission and runs only on a press.
/// </para>
/// <para>
/// The full edition adds the quick fixes and Share as QR code. The password a
/// QR code carries is held only while the code is on screen and dropped when it
/// is hidden or the page is left.
/// </para>
/// </remarks>
public sealed partial class AdaptersPanelViewModel : ObservableObject, IDisposable
{
    private static readonly TimeSpan SignalSpan = TimeSpan.FromMinutes(5);
    private static readonly TimeSpan DropsLookBack = TimeSpan.FromDays(30);

    /// <summary>How many drops show before somebody asks for them all.</summary>
    private const int LatestDrops = 8;

    private readonly AdapterMonitor _adapters;
    private readonly WifiMonitor _wifi;
    private readonly WifiScanner _scanner;
    private readonly WifiDropLog _drops;
    private readonly ConnectionMonitor _connection;
    private readonly PublicAddressLookup _publicAddress;
    private readonly SpeedTestService _speedTests;
    private readonly Dispatcher _dispatcher;
    private readonly ILogger<AdaptersPanelViewModel> _logger;
#if !STORE
    private readonly WifiKeyReader _keys;
    private readonly NetworkFixes _fixes;
#endif

    private IDisposable? _adapterWatch;
    private IDisposable? _wifiWatch;
    private bool _wantName;
    private bool _shown;
    private bool _disposed;
    private WifiState _lastWifiState = WifiState.Unknown;
    private AdapterTileViewModel? _restartPending;
    private int _wifiQueued;
    private int _adaptersQueued;

    // Every drop as far back as the page looks, newest first; Drops may hold only the latest.
    private List<DropRow> _allDrops = [];

    public AdaptersPanelViewModel(
        AdapterMonitor adapters,
        WifiMonitor wifi,
        WifiScanner scanner,
        WifiDropLog drops,
        ConnectionMonitor connection,
        PublicAddressLookup publicAddress,
        SpeedTestService speedTests,
#if !STORE
        WifiKeyReader keys,
        NetworkFixes fixes,
#endif
        Dispatcher dispatcher,
        ILogger<AdaptersPanelViewModel> logger)
    {
        _adapters = adapters;
        _wifi = wifi;
        _scanner = scanner;
        _drops = drops;
        _connection = connection;
        _publicAddress = publicAddress;
        _speedTests = speedTests;
#if !STORE
        _keys = keys;
        _fixes = fixes;
#endif
        _dispatcher = dispatcher;
        _logger = logger;
    }

    /// <summary>Whether this edition offers the quick fixes and the QR code.</summary>
#pragma warning disable CA1822 // An instance property, so XAML can bind to it like the rest.
#if STORE
    public bool IsFullEdition => false;
#else
    public bool IsFullEdition => true;
#endif
#pragma warning restore CA1822

    /// <summary>The line under the page's title.</summary>
    [ObservableProperty]
    public partial string Subtitle { get; private set; } = "Reading the adapters…";

    [ObservableProperty]
    public partial string CarrierValue { get; private set; } = "—";

    [ObservableProperty]
    public partial string CarrierNote { get; private set; } = string.Empty;

    [ObservableProperty]
    public partial string AddressValue { get; private set; } = "—";

    [ObservableProperty]
    public partial string AddressNote { get; private set; } = string.Empty;

    [ObservableProperty]
    public partial string RouterValue { get; private set; } = "—";

    [ObservableProperty]
    public partial string RouterNote { get; private set; } = string.Empty;

    [ObservableProperty]
    public partial string DnsValue { get; private set; } = "—";

    [ObservableProperty]
    public partial string DnsNote { get; private set; } = string.Empty;

    [ObservableProperty]
    public partial string MeteredValue { get; private set; } = "—";

    [ObservableProperty]
    public partial string MeteredNote { get; private set; } = string.Empty;

    [ObservableProperty]
    public partial string VpnValue { get; private set; } = "—";

    [ObservableProperty]
    public partial string VpnNote { get; private set; } = string.Empty;

    /// <summary>Whether Windows treats the network carrying traffic as metered, for the link to Settings.</summary>
    [ObservableProperty]
    public partial bool CarrierIsMetered { get; private set; }

    public ObservableCollection<AdapterTileViewModel> Adapters { get; } = [];

    /// <summary>Whether adapters that are down or virtual are listed too.</summary>
    [ObservableProperty]
    public partial bool ShowEvery { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsEmpty))]
    public partial bool HasAdapters { get; private set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsEmpty))]
    public partial bool AdaptersRead { get; private set; }

    /// <summary>Whether the adapters were read and none is worth listing.</summary>
    public bool IsEmpty => AdaptersRead && !HasAdapters;

    /// <summary>A sentence after a copy, a fix, or a refusal.</summary>
    [ObservableProperty]
    public partial string Notice { get; private set; } = string.Empty;

    /// <summary>The question asked before restarting an adapter, or empty.</summary>
    [ObservableProperty]
    public partial string ConfirmText { get; private set; } = string.Empty;

    [ObservableProperty]
    public partial bool IsWorking { get; private set; }

    // ------------------------------------------------------------------ Wi-Fi

    /// <summary>Whether this PC has a Wi-Fi adapter at all.</summary>
    [ObservableProperty]
    public partial bool HasWifi { get; private set; }

    [ObservableProperty]
    public partial bool WifiConnected { get; private set; }

    [ObservableProperty]
    public partial string WifiTitle { get; private set; } = "Wi-Fi";

    [ObservableProperty]
    public partial string WifiNote { get; private set; } = string.Empty;

    [ObservableProperty]
    public partial string GenerationValue { get; private set; } = "—";

    [ObservableProperty]
    public partial string GenerationNote { get; private set; } = string.Empty;

    [ObservableProperty]
    public partial string ChannelValue { get; private set; } = "—";

    [ObservableProperty]
    public partial string ChannelNote { get; private set; } = string.Empty;

    [ObservableProperty]
    public partial string SignalValue { get; private set; } = "—";

    [ObservableProperty]
    public partial string SignalNote { get; private set; } = string.Empty;

    [ObservableProperty]
    public partial string RateValue { get; private set; } = "—";

    [ObservableProperty]
    public partial string RateNote { get; private set; } = string.Empty;

    [ObservableProperty]
    public partial string NameValue { get; private set; } = "—";

    [ObservableProperty]
    public partial string NameNote { get; private set; } = string.Empty;

    /// <summary>Whether the name is not shown and pressing for it is offered.</summary>
    [ObservableProperty]
    public partial bool CanAskForName { get; private set; }

    /// <summary>Whether Windows refused the name or a scan for want of location permission.</summary>
    [ObservableProperty]
    public partial bool LocationDenied { get; private set; }

    public ObservableCollection<WifiLinkRow> WifiLinks { get; } = [];

    [ObservableProperty]
    public partial bool IsMultiLink { get; private set; }

    [ObservableProperty]
    public partial IReadOnlyList<SignalPoint> SignalPoints { get; private set; } = [];

    [ObservableProperty]
    public partial int SignalRevision { get; private set; }

    /// <summary>The signal chart in a sentence, for screen readers.</summary>
    [ObservableProperty]
    public partial string SignalSummary { get; private set; } = string.Empty;

    // ------------------------------------------------------------------- Scan

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(ScanCommand))]
    public partial bool IsScanning { get; private set; }

    [ObservableProperty]
    public partial bool HasScan { get; private set; }

    [ObservableProperty]
    public partial string ScanNote { get; private set; } = "Nobody has scanned yet. A scan takes a few seconds, needs Windows' location permission, and briefly slows Wi-Fi while the adapter listens on other channels.";

    public ObservableCollection<BandCrowdingRow> Bands { get; } = [];

    public ObservableCollection<NearbyRow> Nearby { get; } = [];

    // ------------------------------------------------------------------ Drops

    public ObservableCollection<DropRow> Drops { get; } = [];

    [ObservableProperty]
    public partial bool HasDrops { get; private set; }

    /// <summary>Whether the list holds every drop rather than the latest few.</summary>
    [ObservableProperty]
    public partial bool ShowAllDrops { get; set; }

    /// <summary>Whether there are more drops than the latest few on show.</summary>
    [ObservableProperty]
    public partial bool HasMoreDrops { get; private set; }

    [ObservableProperty]
    public partial string MoreDropsText { get; private set; } = string.Empty;

    [ObservableProperty]
    public partial string DropsSummary { get; private set; } = string.Empty;

    // --------------------------------------------------------------------- QR

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasQr))]
    public partial QrCode? Qr { get; private set; }

    /// <summary>Whether a QR code is on screen.</summary>
    public bool HasQr => Qr is not null;

    [ObservableProperty]
    public partial string QrTitle { get; private set; } = string.Empty;

    [ObservableProperty]
    public partial string QrNote { get; private set; } = string.Empty;

    /// <summary>Starts watching, for as long as the page is on screen.</summary>
    public void Show()
    {
        if (_disposed || _shown)
        {
            return;
        }

        _shown = true;
        _adapters.Changed += OnAdaptersChanged;
        _wifi.Sampled += OnWifiSampled;
        _adapterWatch = _adapters.Watch();
        _wantName = LocationConsent.Read() == LocationAccess.Allowed;
        _wifiWatch = _wifi.Watch(_wantName);

        ApplyAdapters(_adapters.Latest);
        ApplyWifi(_wifi.Latest);
        _ = LoadDropsAsync();
    }

    /// <summary>Stops watching, and lets go of a QR code on screen.</summary>
    public void Hide()
    {
        if (!_shown)
        {
            return;
        }

        _shown = false;
        _adapters.Changed -= OnAdaptersChanged;
        _wifi.Sampled -= OnWifiSampled;
        _adapterWatch?.Dispose();
        _adapterWatch = null;
        _wifiWatch?.Dispose();
        _wifiWatch = null;
        HideQr();
        CancelConfirm();
    }

    partial void OnShowEveryChanged(bool value) => ApplyAdapters(_adapters.Latest);

    private void OnAdaptersChanged(object? sender, AdapterReading reading)
    {
        if (Interlocked.Exchange(ref _adaptersQueued, 1) == 0)
        {
            _dispatcher.BeginInvoke(() =>
            {
                Interlocked.Exchange(ref _adaptersQueued, 0);
                ApplyAdapters(_adapters.Latest);
            });
        }
    }

    private void OnWifiSampled(object? sender, WifiReading reading)
    {
        if (Interlocked.Exchange(ref _wifiQueued, 1) == 0)
        {
            _dispatcher.BeginInvoke(() =>
            {
                Interlocked.Exchange(ref _wifiQueued, 0);
                ApplyWifi(_wifi.Latest);
            });
        }
    }

    private void ApplyAdapters(AdapterReading reading)
    {
        AdapterSnapshot snapshot = reading.Snapshot;
        AdaptersRead = snapshot.At != DateTimeOffset.MinValue;
        AdapterDetails? carrier = snapshot.Carrier;
        AdapterDetails? physical = snapshot.Physical;

        if (!AdaptersRead)
        {
            return;
        }

        if (carrier is null)
        {
            Subtitle = "No adapter is carrying internet traffic";
            CarrierValue = "None";
            CarrierNote = "No adapter has a router";
            AddressValue = RouterValue = DnsValue = MeteredValue = "—";
            AddressNote = RouterNote = DnsNote = MeteredNote = string.Empty;
            CarrierIsMetered = false;
        }
        else
        {
            CarrierValue = carrier.Name;
            CarrierNote = string.Join(" · ", new[]
            {
                AdapterTileViewModel.KindOf(carrier.Kind),
                carrier.ReceiveBitsPerSecond is { } speed ? AdapterTileViewModel.Bits(speed) : string.Empty,
            }.Where(part => part.Length > 0));

            AdapterAddress? four = carrier.FourAddresses.FirstOrDefault(each => each.Scope != AddressScope.LinkLocal);
            AddressValue = four?.Address.ToString() ?? "—";
            AddressNote = four is null ? "No IPv4 address" : string.Concat("/", four.PrefixLength.ToString(CultureInfo.CurrentCulture), four.FromDhcp ? " · from DHCP" : " · set by hand");

            System.Net.IPAddress? gateway = carrier.Gateways.FirstOrDefault(each => each.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork)
                ?? (carrier.Gateways.Count > 0 ? carrier.Gateways[0] : null);
            RouterValue = gateway?.ToString() ?? "—";
            RouterNote = carrier.DhcpServer is { } dhcp && !dhcp.Equals(gateway) ? string.Concat("DHCP from ", dhcp.ToString()) : "Default gateway";

            DnsServer[] servers = [.. carrier.DnsServers];
            DnsValue = servers.Length == 0 ? "—" : servers[0].Address.ToString();
            DnsNote = servers.Length switch
            {
                0 => "No DNS server",
                1 => Encryption(servers[0].Encryption),
                _ => string.Create(CultureInfo.CurrentCulture, $"and {servers.Length - 1} more · {Encryption(servers[0].Encryption)}"),
            };

            AdapterDetails costed = physical ?? carrier;
            CarrierIsMetered = costed.Cost?.IsMetered == true;
            MeteredValue = costed.Cost is null ? "—" : CarrierIsMetered ? "Metered" : "Not metered";
            MeteredNote = costed.Cost is null
                ? "Windows did not say"
                : CarrierIsMetered ? "Updates held back" : "Treated as unlimited";

            Subtitle = string.Concat(carrier.Name, " carries your internet", snapshot.TrafficTunnelled ? " through a VPN" : string.Empty);
        }

        AdapterDetails[] vpns = [.. snapshot.Vpns];
        VpnValue = vpns.Length == 0 ? "Off" : snapshot.TrafficTunnelled ? "On" : "Connected";
        VpnNote = vpns.Length == 0
            ? "No VPN adapter is up"
            : snapshot.TrafficTunnelled
                ? string.Concat("Traffic goes through ", vpns[0].Name)
                : string.Concat(vpns[0].Name, " is up but not carrying traffic");

        IEnumerable<AdapterDetails> listed = ShowEvery
            ? snapshot.Adapters.Where(each => each.Kind != AdapterKind.Loopback)
            : snapshot.Shown;

        Adapters.Clear();

        foreach (AdapterDetails adapter in listed)
        {
            Adapters.Add(new AdapterTileViewModel(
                adapter,
                ReferenceEquals(adapter, carrier),
                IsFullEdition,
                RenewAsync,
                AskRestart));
        }

        HasAdapters = Adapters.Count > 0;
        HasWifi = HasWifi || snapshot.Adapters.Any(each => each.Kind == AdapterKind.WiFi);
    }

    private void ApplyWifi(WifiReading reading)
    {
        HasWifi = reading.State is not (WifiState.NoAdapter or WifiState.Unknown or WifiState.ServiceOff)
            || _adapters.Latest.Snapshot.Adapters.Any(each => each.Kind == AdapterKind.WiFi);
        WifiConnected = reading.IsConnected;

        if (reading.State != _lastWifiState)
        {
            bool reload = _lastWifiState != WifiState.Unknown;
            _lastWifiState = reading.State;

            if (reload)
            {
                _ = LoadDropsAsync();
            }
        }

        WifiTitle = reading.State switch
        {
            WifiState.Connected => "Wi-Fi",
            WifiState.Connecting => "Wi-Fi, connecting",
            WifiState.RadioOff => "Wi-Fi, switched off",
            WifiState.ServiceOff => "Wi-Fi, service not running",
            WifiState.Disconnected => "Wi-Fi, not connected",
            _ => "Wi-Fi",
        };

        if (!reading.IsConnected)
        {
            WifiNote = reading.State switch
            {
                WifiState.RadioOff => "The Wi-Fi radio is off. Turn it on from the network flyout on the taskbar.",
                WifiState.ServiceOff => "The WLAN AutoConfig service is not running, so Windows cannot use Wi-Fi.",
                WifiState.Connecting => "Joining a network.",
                _ => "This PC is not joined to a Wi-Fi network. Its details appear here when it is.",
            };
            GenerationValue = ChannelValue = SignalValue = RateValue = NameValue = "—";
            GenerationNote = ChannelNote = SignalNote = RateNote = NameNote = string.Empty;
            CanAskForName = false;
            WifiLinks.Clear();
            IsMultiLink = false;
            SignalSummary = "No Wi-Fi signal: not connected.";
        }
        else
        {
            WifiNote = reading.Fallback
                ? "Read the way older Windows gives it; some figures need Windows 11 24H2."
                : "Read from Windows' realtime connection quality, which needs no location permission.";
            GenerationValue = reading.Generation;
            GenerationNote = WifiBands.Standard(reading.Phy);
            ChannelValue = reading.Channel > 0 ? string.Create(CultureInfo.CurrentCulture, $"{reading.Channel}") : "—";
            ChannelNote = string.Join(" · ", new[]
            {
                WifiBands.Describe(reading.Band),
                reading.WidthMhz > 0 ? string.Create(CultureInfo.CurrentCulture, $"{reading.WidthMhz} MHz wide") : string.Empty,
            }.Where(part => part.Length > 0 && part != "—"));

            SignalValue = reading.Rssi is { } rssi ? string.Create(CultureInfo.CurrentCulture, $"{rssi} dBm") : "—";
            SignalNote = reading.Bars is { } bars
                ? string.Create(CultureInfo.CurrentCulture, $"{BarsWord(bars)} · {reading.SignalPercent}%")
                : string.Empty;

            RateValue = reading.ReceiveMbps is { } receive ? string.Create(CultureInfo.CurrentCulture, $"{receive:0} Mbit/s") : "—";
            RateNote = reading.TransmitMbps is { } transmit ? string.Create(CultureInfo.CurrentCulture, $"{transmit:0} Mbit/s sending") : "Receiving";

            (NameValue, NameNote, CanAskForName, LocationDenied) = reading.Name switch
            {
                NameAccess.Allowed => (reading.NetworkName is { Length: > 0 } name ? name : "(hidden network)", reading.Association?.SecurityName ?? string.Empty, false, false),
                NameAccess.Denied => ("Not allowed", "Windows' location permission is off for Windsock", true, true),
                NameAccess.Unavailable => ("—", "Windows did not give it", true, LocationDenied),
                _ => ("Not shown", "Showing it needs Windows' location permission", true, LocationDenied),
            };

            WifiLinks.Clear();
            IsMultiLink = reading.IsMultiLink;

            if (reading.IsMultiLink && reading.Quality is { } quality)
            {
                foreach (WifiLink link in quality.Links)
                {
                    WifiLinks.Add(new WifiLinkRow(string.Create(
                        CultureInfo.CurrentCulture,
                        $"Link {link.LinkId}: {WifiBands.Describe(link.Band)}, channel {link.Channel}{(link.WidthMhz > 0 ? $", {link.WidthMhz} MHz" : string.Empty)}, {link.Rssi} dBm")));
                }
            }

            SignalSummary = reading.Rssi is { } now
                ? string.Concat("Wi-Fi signal over the last five minutes; now ", NetworkWords.Signal(now), ".")
                : "Wi-Fi signal over the last five minutes.";
        }

        SignalPoints = _wifi.History.Since(DateTimeOffset.Now - SignalSpan);
        SignalRevision++;
    }

    private static string BarsWord(int bars) => bars switch
    {
        4 => "Excellent",
        3 => "Good",
        2 => "Fair",
        1 => "Weak",
        _ => "Very weak",
    };

    private static string Encryption(DnsEncryption encryption) => encryption switch
    {
        DnsEncryption.Configured => "encryption set in Windows",
        DnsEncryption.Available => "could be encrypted",
        DnsEncryption.NotAvailable => "plain",
        _ => string.Empty,
    };

    private async Task LoadDropsAsync()
    {
        bool withNames = _wifi.Latest.Name == NameAccess.Allowed || LocationConsent.Read() == LocationAccess.Allowed;
        IReadOnlyList<WifiDrop> drops = await Task.Run(() => _drops.Drops(DropsLookBack, withNames)).ConfigureAwait(true);
        DateTimeOffset now = DateTimeOffset.Now;

        List<DropRow> rows = [];

        foreach (WifiDrop drop in drops.Take(50))
        {
            string back = drop.Duration is { } gone
                ? string.Concat("Back after ", OutageWords.Brief(gone))
                : "Not back yet";

            string reason = drop.Reason ?? "Windows gave no reason";

            if (drop.Network is { Length: > 0 } network)
            {
                reason = string.Concat(network, ": ", reason);
            }

            rows.Add(new DropRow(NetworkWords.Ago(drop.At, now), reason, back, drop.Deliberate));
        }

        _allDrops = rows;
        FillDrops();
        int real = drops.Count(each => !each.Deliberate);

        DropsSummary = _drops.State switch
        {
            WifiLogState.Missing => "Windows' Wi-Fi log is switched off on this PC, so drops cannot be listed.",
            WifiLogState.Refused => "Windows did not let the Wi-Fi log be read.",
            _ when drops.Count == 0 => "No Wi-Fi disconnections in the last 30 days, or as far back as Windows' log reaches.",
            _ => string.Create(
                CultureInfo.CurrentCulture,
                $"{drops.Count} disconnection{(drops.Count == 1 ? string.Empty : "s")} in the last 30 days; {real} looked like a drop rather than something somebody did. The reasons are Windows' own."),
        };
    }

    partial void OnShowAllDropsChanged(bool value) => FillDrops();

    private void FillDrops()
    {
        Drops.Clear();

        foreach (DropRow row in ShowAllDrops ? _allDrops : _allDrops.Take(LatestDrops))
        {
            Drops.Add(row);
        }

        HasDrops = Drops.Count > 0;
        HasMoreDrops = _allDrops.Count > LatestDrops;
        MoreDropsText = ShowAllDrops
            ? string.Create(CultureInfo.CurrentCulture, $"Show the latest {LatestDrops}")
            : string.Create(CultureInfo.CurrentCulture, $"Show all {_allDrops.Count}");
    }

    [RelayCommand]
    private void AskForName()
    {
        if (_wantName)
        {
            return;
        }

        // May make Windows ask the person about location, which is why it
        // waits for this press.
        _wantName = true;
        _wifiWatch?.Dispose();
        _wifiWatch = _shown ? _wifi.Watch(withName: true) : null;
    }

    [RelayCommand]
    private static void OpenLocationSettings() => LocationConsent.OpenSettings();

    [RelayCommand]
    private static void OpenMeteredSettings() => SettingsLauncher.Open(new Uri("ms-settings:network-status"));

    [RelayCommand(CanExecute = nameof(CanScan))]
    private async Task ScanAsync()
    {
        IsScanning = true;
        ScanNote = "Listening for nearby networks…";

        try
        {
            WifiReading now = _wifi.Latest;
            string? own = now.NetworkName;
            Dictionary<WifiBand, int> channels = [];

            if (now.IsConnected && now.Channel > 0)
            {
                channels[now.Band] = now.Channel;
            }

            // A scan takes the adapter off its channel for moments at a time, so
            // it waits for a speed test, and a test refuses while a scan runs.
            WifiScan scan = await _scanner.ScanAsync(
                own is null ? null : each => string.Equals(each.Name, own, StringComparison.Ordinal),
                channels,
                now.WidthMhz > 0 ? now.WidthMhz : 20,
                () => _speedTests.IsRunning).ConfigureAwait(true);

            ShowScan(scan, own, now);
        }
        finally
        {
            IsScanning = false;
        }
    }

    private bool CanScan() => !IsScanning;

    private void ShowScan(WifiScan scan, string? own, WifiReading now)
    {
        Bands.Clear();
        Nearby.Clear();

        if (scan.Status != WlanStatus.Ok)
        {
            HasScan = false;
            LocationDenied = scan.Status == WlanStatus.Denied || LocationDenied;
            ScanNote = scan.Status switch
            {
                WlanStatus.Denied => "Windows did not allow the scan: nearby networks need its location permission. Turn on location for Windsock (and for desktop apps) in Windows Settings, then scan again.",
                WlanStatus.RadioOff => "The Wi-Fi radio is off, so there is nothing to listen with.",
                WlanStatus.NoService => "There is no Wi-Fi adapter to scan with, or its service is not running.",
                WlanStatus.Busy => "A speed test is running. Scan once it has finished: a scan would slow the test, and the test would crowd the scan.",
                _ => "The scan did not work. Try again in a moment.",
            };
            return;
        }

        HasScan = true;
        ScanNote = string.Create(
            CultureInfo.CurrentCulture,
            $"{scan.Networks.Count} access points heard at {ClockFormat.Time(scan.At)}{(scan.Fresh ? string.Empty : ", from the adapter's last scan (scans are at most twice a minute)")}. Your own network is left out of the crowding.");

        foreach ((WifiBand band, IReadOnlyList<ChannelLoad> loads) in scan.Loads.OrderBy(pair => pair.Key))
        {
            scan.Suggestions.TryGetValue(band, out ChannelSuggestion? suggestion);
            int heard = scan.Networks.Count(each => each.Band == band);

            string advice = suggestion switch
            {
                null => string.Empty,
                { CurrentChannel: { } current } when current == suggestion.Channel || !suggestion.IsBetter =>
                    string.Create(CultureInfo.CurrentCulture, $"Your channel {current} is as quiet as any. Nothing to change."),
                { CurrentChannel: { } current } =>
                    string.Create(CultureInfo.CurrentCulture, $"Channel {suggestion.Channel} is quieter than your {current}. Change it in your router's settings.{(ChannelCrowding.IsDfs(band, suggestion.Channel) ? " It needs radar detection, which some routers will not use." : string.Empty)}"),
                _ => string.Create(CultureInfo.CurrentCulture, $"The quietest channel here is {suggestion.Channel}."),
            };

            List<MetricBar> bars = [.. loads.Select(load => new MetricBar(
                load.Channel.ToString(CultureInfo.CurrentCulture),
                load.Load,
                string.Create(CultureInfo.CurrentCulture, $"Channel {load.Channel}: {load.Networks} network{(load.Networks == 1 ? string.Empty : "s")} overlap it, busyness {load.Load:0.0}{(load.Dfs ? ", needs radar detection" : string.Empty)}.{(suggestion?.Channel == load.Channel ? " The quietest." : string.Empty)}"),
                Highlight: suggestion?.Channel == load.Channel))];

            Bands.Add(new BandCrowdingRow(
                WifiBands.Describe(band),
                string.Create(CultureInfo.CurrentCulture, $"{heard} access point{(heard == 1 ? string.Empty : "s")} heard"),
                advice,
                bars));
        }

        foreach (NearbyNetwork network in scan.Networks.Take(40))
        {
            bool mine = own is not null && string.Equals(network.Name, own, StringComparison.Ordinal);

            Nearby.Add(new NearbyRow(
                network.Name.Length > 0 ? network.Name : "(hidden network)",
                string.Concat(
                    WifiBands.Describe(network.Band),
                    string.Create(CultureInfo.CurrentCulture, $" · channel {network.Channel}"),
                    network.WidthMhz > 20 ? string.Create(CultureInfo.CurrentCulture, $" · {network.WidthMhz} MHz") : string.Empty),
                NetworkWords.Signal(network.Rssi),
                WifiBands.Generation(network.Generation, network.Band),
                mine));
        }

        _ = now;
    }

    [RelayCommand]
    private void Copy()
    {
        SupportFacts facts = new(
            _adapters.Latest,
            _connection.Latest,
            _wifi.Latest,
            _publicAddress.Latest);

        string text = NetworkSupportText.Write(facts);

        try
        {
            Clipboard.SetText(text);
            Notice = "Copied the network's details. MAC addresses are left out; network names are in, so read it before you paste it anywhere.";
        }
        catch (COMException)
        {
            Notice = "Another program is holding the clipboard. Try again in a moment.";
        }
        catch (ExternalException)
        {
            Notice = "Another program is holding the clipboard. Try again in a moment.";
        }
    }

    [RelayCommand]
    private void DismissNotice() => Notice = string.Empty;

    [RelayCommand]
    private void CancelConfirm()
    {
        _restartPending = null;
        ConfirmText = string.Empty;
    }

    private Task AskRestart(AdapterTileViewModel tile)
    {
        _restartPending = tile;
        ConfirmText = string.Concat("Restart ", tile.Name, "? Its connection drops for a few seconds, and Windows asks for administrator permission.");
        return Task.CompletedTask;
    }

    [RelayCommand]
    private async Task ConfirmAsync()
    {
        AdapterTileViewModel? tile = _restartPending;
        CancelConfirm();

#if STORE
        _ = tile;
        await Task.CompletedTask.ConfigureAwait(true);
#else
        if (tile is not null)
        {
            await RunFixAsync(NetworkFix.ResetAdapter, tile.Adapter).ConfigureAwait(true);
        }
#endif
    }

#if STORE
    // The quick fixes are not in this edition; the tiles never offer them.
    private static Task RenewAsync(AdapterTileViewModel tile)
    {
        _ = tile;
        return Task.CompletedTask;
    }
#else
    private Task RenewAsync(AdapterTileViewModel tile) => RunFixAsync(NetworkFix.RenewAddress, tile.Adapter);

    [RelayCommand]
    private Task FlushDnsAsync() => RunFixAsync(NetworkFix.FlushDns, null);

    private async Task RunFixAsync(NetworkFix fix, AdapterDetails? adapter)
    {
        if (IsWorking)
        {
            return;
        }

        IsWorking = true;
        Notice = NetworkFixCommands.Explain(fix);

        try
        {
            FixResult result = await _fixes.RunAsync(fix, adapter).ConfigureAwait(true);
            Notice = result.Message;
        }
        finally
        {
            IsWorking = false;
            _ = _adapters.ReadNowAsync();
        }
    }
#endif

    [RelayCommand]
    private void ShareQr()
    {
#if STORE
        Notice = "Sharing Wi-Fi as a QR code is in the full edition only.";
#else
        WifiShareResult result = _keys.ShareCurrent();

        switch (result.Outcome)
        {
            case WifiShareOutcome.Ready when result.Payload is { } payload:
                Qr = QrCode.EncodeText(payload, QrErrorCorrection.Medium);
                QrTitle = result.Network ?? "This network";
                QrNote = "Point a phone's camera at the code to join. It holds the password, so show it only to people you would tell it to. Windsock keeps no copy; it is gone when you hide it or leave this page.";
                break;

            case WifiShareOutcome.LocationNeeded:
                LocationDenied = true;
                Notice = "Windows needs its location permission on before it will say which network this is. Turn it on in Windows Settings, then try again.";
                break;

            case WifiShareOutcome.KeyWithheld:
                Notice = "Windows kept this network's password encrypted. Restart Windsock as administrator (Settings › Advanced) and try again.";
                break;

            case WifiShareOutcome.Enterprise:
                Notice = "This network signs in with an account rather than a password, which a QR code cannot carry.";
                break;

            case WifiShareOutcome.NotConnected:
                Notice = "Wi-Fi is not connected, so there is no network to share.";
                break;

            default:
                Notice = "The network's saved settings could not be read.";
                break;
        }
#endif
    }

    [RelayCommand]
    private void HideQr()
    {
        Qr = null;
        QrTitle = string.Empty;
        QrNote = string.Empty;
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        Hide();
        _disposed = true;
        _ = _logger;
    }
}
