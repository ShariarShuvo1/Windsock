using System.Globalization;
using System.Net.Sockets;
using CommunityToolkit.Mvvm.Input;
using FluentIcons.Common;
using Windsock.App.Controls;
using Windsock.Core.Adapters;
using Windsock.Core.Asn;
using Windsock.Core.Formatting;
using Windsock.Core.Wifi;

namespace Windsock.App.ViewModels;

/// <summary>A label and a value, as a tile's facts show them.</summary>
/// <param name="Label">What it is.</param>
/// <param name="Value">What it says.</param>
public sealed record FactRow(string Label, string Value)
{
    /// <summary>What a screen reader names the fact by: "IPv4, 192.168.0.103 (DHCP)".</summary>
    public override string ToString() => string.Concat(Label, ", ", Value);
}

/// <summary>One DNS server an adapter uses, in words.</summary>
/// <param name="Address">Its address.</param>
/// <param name="Origin">Where it came from.</param>
/// <param name="Encryption">What Windows could do about encrypting lookups to it.</param>
/// <param name="IsConfigured">Whether Windows is set to encrypt to it.</param>
public sealed record DnsRow(string Address, string Origin, string Encryption, bool IsConfigured)
{
    /// <summary>What a screen reader names the server by: "DNS 192.168.0.1, Automatic, Plain: ...".</summary>
    public override string ToString() => string.Concat("DNS ", Address, ", ", Origin, ", ", Encryption);
}

/// <summary>One adapter, as a tile on the Adapters and Wi-Fi page.</summary>
public sealed partial class AdapterTileViewModel
{
    private readonly Func<AdapterTileViewModel, Task>? _renew;
    private readonly Func<AdapterTileViewModel, Task>? _restart;

    public AdapterTileViewModel(
        AdapterDetails adapter,
        bool carriesTraffic,
        bool offersFixes,
        Func<AdapterTileViewModel, Task>? renew,
        Func<AdapterTileViewModel, Task>? restart)
    {
        ArgumentNullException.ThrowIfNull(adapter);

        Adapter = adapter;
        _renew = renew;
        _restart = restart;

        Name = adapter.Name;
        Description = adapter.Description;
        Icon = IconOf(adapter.Kind);
        KindText = KindOf(adapter.Kind);
        StateText = StateOf(adapter.State);
        IsUp = adapter.IsUp;
        CarriesTraffic = carriesTraffic;
        IsVpn = adapter.IsVpn;
        IsMetered = adapter.Cost?.IsMetered == true;
        OffersFixes = offersFixes && adapter.IsUp && adapter.Kind is not (AdapterKind.Loopback or AdapterKind.Transition);
        SettingsUri = Services.Network.WindowsAdapterProfiles.SettingsPage(adapter.Kind);

        List<string> badges = [];

        if (carriesTraffic)
        {
            badges.Add("Carries internet traffic");
        }

        if (adapter.IsVpn)
        {
            badges.Add(adapter.IsUp ? "VPN on" : "VPN off");
        }

        if (IsMetered)
        {
            badges.Add("Metered");
        }

        Badges = badges;
        Facts = Describe(adapter);
        DnsServers = [.. adapter.DnsServers.Select(server => new DnsRow(
            server.Address.ToString(),
            server.Origin switch
            {
                DnsServerOrigin.Manual => "Set by hand",
                DnsServerOrigin.Automatic => "Automatic",
                _ => "Origin not known",
            },
            server.Encryption switch
            {
                DnsEncryption.Configured => "Encryption set in Windows",
                DnsEncryption.Available => "Could be encrypted; not set",
                DnsEncryption.NotAvailable => "Plain: Windows cannot encrypt to it",
                _ => "Encryption not known",
            },
            server.Encryption == DnsEncryption.Configured))];

        Summary = string.Join(" · ", new[] { KindText, StateText, Speed(adapter) }.Where(part => part.Length > 0));
    }

    /// <summary>The adapter itself.</summary>
    public AdapterDetails Adapter { get; }

    public string Name { get; }

    public string Description { get; }

    public Symbol Icon { get; }

    public string KindText { get; }

    public string StateText { get; }

    /// <summary>Kind, state and speed on one line.</summary>
    public string Summary { get; }

    public bool IsUp { get; }

    public bool CarriesTraffic { get; }

    public bool IsVpn { get; }

    public bool IsMetered { get; }

    public IReadOnlyList<string> Badges { get; }

    public bool HasBadges => Badges.Count > 0;

    public IReadOnlyList<FactRow> Facts { get; }

    public IReadOnlyList<DnsRow> DnsServers { get; }

    public bool HasDnsServers => DnsServers.Count > 0;

    /// <summary>Whether the full edition's quick fixes are offered for it.</summary>
    public bool OffersFixes { get; }

    /// <summary>Windows Settings' page for this kind of connection, where metered is set.</summary>
    public Uri SettingsUri { get; }

    /// <summary>The adapter's name for screen readers on its buttons.</summary>
    public string RenewName => string.Concat("Renew the address of ", Name);

    public string RestartName => string.Concat("Restart ", Name);

    public string SettingsName => string.Concat("Open Windows Settings for ", Name);

    [RelayCommand]
    private Task RenewAsync() => _renew?.Invoke(this) ?? Task.CompletedTask;

    [RelayCommand]
    private Task RestartAsync() => _restart?.Invoke(this) ?? Task.CompletedTask;

    [RelayCommand]
    private void OpenSettings() => Services.Network.SettingsLauncher.Open(SettingsUri);

    /// <summary>What a screen reader names the tile by: "Ethernet, Ethernet · Connected · 100 Mbit/s, Carries internet traffic".</summary>
    public override string ToString() => HasBadges
        ? string.Concat(Name, ", ", Summary, ", ", string.Join(", ", Badges))
        : string.Concat(Name, ", ", Summary);

    private static List<FactRow> Describe(AdapterDetails adapter)
    {
        List<FactRow> facts = [];

        foreach (AdapterAddress address in adapter.FourAddresses.Where(each => each.Scope != AddressScope.LinkLocal || !adapter.IsUp))
        {
            facts.Add(new FactRow("IPv4", string.Concat(address.ToString(), address.FromDhcp ? " (DHCP)" : string.Empty, ScopeNote(address.Scope))));
        }

        foreach (AdapterAddress address in adapter.SixAddresses)
        {
            facts.Add(new FactRow(address.Scope == AddressScope.LinkLocal ? "IPv6 (link)" : "IPv6", string.Concat(
                address.ToString(),
                address.Temporary ? " (temporary)" : string.Empty,
                address.Preferred ? string.Empty : " (not ready)")));
        }

        foreach (System.Net.IPAddress gateway in adapter.Gateways)
        {
            facts.Add(new FactRow(gateway.AddressFamily == AddressFamily.InterNetwork ? "Router" : "IPv6 router", gateway.ToString()));
        }

        if (adapter.DhcpServer is { } dhcp)
        {
            facts.Add(new FactRow("DHCP server", dhcp.ToString()));
        }

        if (adapter.NetworkName is { Length: > 0 } network)
        {
            facts.Add(new FactRow("Windows network", network));
        }

        if (adapter.Cost is { } cost)
        {
            facts.Add(new FactRow("Metered", cost.IsMetered ? Metered(cost) : "No"));
        }

        if (adapter.ReceiveBitsPerSecond is { } receive)
        {
            facts.Add(new FactRow("Link speed", adapter.TransmitBitsPerSecond is { } send && send != receive
                ? string.Concat(Bits(receive), " in, ", Bits(send), " out")
                : Bits(receive)));
        }

        if (adapter.Mtu is { } mtu)
        {
            facts.Add(new FactRow("MTU", string.Create(CultureInfo.CurrentCulture, $"{mtu:N0} bytes")));
        }

        if (adapter.HardwareAddress is { } mac)
        {
            facts.Add(new FactRow("MAC address", mac));
        }

        if (adapter.DnsSuffix.Length > 0)
        {
            facts.Add(new FactRow("DNS suffix", adapter.DnsSuffix));
        }

        return facts;
    }

    private static string Metered(AdapterCost cost)
    {
        List<string> said = [cost.Kind == Core.Networking.NetworkCost.Variable ? "Yes, charged by use" : "Yes"];

        if (cost.Roaming)
        {
            said.Add("roaming");
        }

        if (cost.OverDataLimit)
        {
            said.Add("over its limit");
        }
        else if (cost.ApproachingDataLimit)
        {
            said.Add("near its limit");
        }

        return string.Join(", ", said);
    }

    private static string ScopeNote(AddressScope scope) => scope switch
    {
        AddressScope.SharedCarrier => " (provider's shared range)",
        AddressScope.Public => " (public)",
        AddressScope.LinkLocal => " (no DHCP answer)",
        _ => string.Empty,
    };

    private static string Speed(AdapterDetails adapter) =>
        adapter.ReceiveBitsPerSecond is { } receive ? Bits(receive) : string.Empty;

    /// <summary>A link speed: "1 Gbit/s", "866.7 Mbit/s".</summary>
    public static string Bits(ulong bitsPerSecond) => bitsPerSecond >= 1_000_000_000
        ? string.Create(CultureInfo.CurrentCulture, $"{bitsPerSecond / 1e9:0.##} Gbit/s")
        : string.Create(CultureInfo.CurrentCulture, $"{bitsPerSecond / 1e6:0.#} Mbit/s");

    /// <summary>The icon for a kind of link.</summary>
    public static Symbol IconOf(AdapterKind kind) => kind switch
    {
        AdapterKind.WiFi => Symbol.WiFi1,
        AdapterKind.Ethernet => Symbol.PlugConnected,
        AdapterKind.Cellular => Symbol.CellularData1,
        AdapterKind.Vpn => Symbol.ShieldLock,
        AdapterKind.Virtual or AdapterKind.Transition => Symbol.VirtualNetwork,
        AdapterKind.Bluetooth => Symbol.Bluetooth,
        _ => Symbol.Desktop,
    };

    /// <summary>The kind of link in words.</summary>
    public static string KindOf(AdapterKind kind) => kind switch
    {
        AdapterKind.WiFi => "Wi-Fi",
        AdapterKind.Ethernet => "Ethernet",
        AdapterKind.Cellular => "Mobile broadband",
        AdapterKind.Vpn => "VPN",
        AdapterKind.Transition => "IPv6 tunnel",
        AdapterKind.Virtual => "Virtual",
        AdapterKind.Bluetooth => "Bluetooth",
        AdapterKind.Loopback => "Loopback",
        _ => "Other",
    };

    private static string StateOf(AdapterState state) => state switch
    {
        AdapterState.Up => "Connected",
        AdapterState.Down => "Not connected",
        AdapterState.NotPresent => "Not present",
        AdapterState.Waiting => "Waiting",
        _ => "State not known",
    };
}

/// <summary>One radio link of a Wi-Fi 7 multi-link connection.</summary>
/// <param name="Text">The link in a line: "Link 1: 6 GHz, channel 37, 320 MHz, -61 dBm".</param>
public sealed record WifiLinkRow(string Text)
{
    /// <summary>The link in a line, which is also what a screen reader says.</summary>
    public override string ToString() => Text;
}

/// <summary>One access point a scan heard.</summary>
/// <param name="Name">Its network's name, or "(hidden)".</param>
/// <param name="Where">Band, channel and width.</param>
/// <param name="Signal">How strongly it was heard.</param>
/// <param name="Generation">The newest Wi-Fi generation it advertises.</param>
/// <param name="IsOwn">Whether it is the person's own network.</param>
public sealed record NearbyRow(string Name, string Where, string Signal, string Generation, bool IsOwn)
{
    /// <summary>What a screen reader names the network by: its name, where, how strong and which generation.</summary>
    public override string ToString() =>
        string.Join(", ", new[] { Name, IsOwn ? "your network" : string.Empty, Where, Signal, Generation }.Where(part => part.Length > 0));
}

/// <summary>One band's crowding after a scan.</summary>
/// <param name="Band">The band in words.</param>
/// <param name="Summary">How many networks were heard, and how busy.</param>
/// <param name="Suggestion">The quieter channel, or that the current one is fine.</param>
/// <param name="Bars">How busy each candidate channel is, for the chart.</param>
public sealed record BandCrowdingRow(string Band, string Summary, string Suggestion, IReadOnlyList<MetricBar> Bars)
{
    /// <summary>What a screen reader names the band by: the band, how crowded, and the suggestion.</summary>
    public override string ToString() => string.Concat(Band, ", ", Summary, ", ", Suggestion);
}

/// <summary>One Wi-Fi drop.</summary>
/// <param name="When">When it went.</param>
/// <param name="Reason">Windows' words for why.</param>
/// <param name="Back">How long until Wi-Fi came back, or that it has not.</param>
/// <param name="Deliberate">Whether it looks like something somebody did.</param>
public sealed record DropRow(string When, string Reason, string Back, bool Deliberate)
{
    /// <summary>What a screen reader names the drop by: "Wi-Fi dropped yesterday at 3:19 PM, The driver disconnected, Not back yet".</summary>
    public override string ToString() => string.Concat("Wi-Fi dropped ", When, ", ", Reason, ", ", Back);
}

/// <summary>Helpers shared by the connection pages' words.</summary>
internal static class NetworkWords
{
    /// <summary>A network's name for a person, or the registry name when no organisation can be told.</summary>
    public static string Network(AutonomousSystem? system) => system is null
        ? "—"
        : string.Concat(system.Organisation, " (", system.Label, system.Country.Length > 0 ? string.Concat(", ", system.Country) : string.Empty, ")");

    /// <summary>"3 minutes ago", "yesterday at 21:04".</summary>
    public static string Ago(DateTimeOffset at, DateTimeOffset now)
    {
        TimeSpan gone = now - at;

        if (gone < TimeSpan.FromMinutes(1))
        {
            return "just now";
        }

        if (gone < TimeSpan.FromHours(1))
        {
            return string.Create(CultureInfo.CurrentCulture, $"{(int)gone.TotalMinutes} min ago");
        }

        if (at.Date == now.Date)
        {
            return string.Concat("today at ", ClockFormat.Time(at));
        }

        if (at.Date == now.Date.AddDays(-1))
        {
            return string.Concat("yesterday at ", ClockFormat.Time(at));
        }

        string day = at.ToString(at.Year == now.Year ? "d MMM" : "d MMM yyyy", ClockFormat.Current.Format);
        return string.Concat(day, ", ", ClockFormat.Time(at));
    }

    /// <summary>A signal in dBm and bars, "-58 dBm, 3 of 4 bars".</summary>
    public static string Signal(int rssi) =>
        string.Create(CultureInfo.CurrentCulture, $"{rssi} dBm, {WifiBands.Bars(rssi)} of 4 bars");
}
