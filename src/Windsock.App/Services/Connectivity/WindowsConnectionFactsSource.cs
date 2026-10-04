using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using Microsoft.Extensions.Logging;
using Windows.Networking.Connectivity;
using Windsock.Core.Connectivity;
using WinRtNetworkInformation = Windows.Networking.Connectivity.NetworkInformation;

namespace Windsock.App.Services.Connectivity;

/// <summary>
/// Reads what Windows knows about the connection: its own verdict on reaching
/// the internet, which network is in use, the router's address, and whether a
/// VPN is up.
/// </summary>
/// <remarks>
/// <para>
/// The verdict is the same one behind the "No internet" mark on the taskbar's
/// network icon. Windows reaches it by fetching a small test page from Microsoft
/// now and then; Windsock sends nothing to get it, it only asks what Windows
/// already concluded.
/// </para>
/// <para>
/// With a VPN up, Windows' internet profile is usually the VPN's own adapter,
/// which has no router of its own worth pinging and no name anybody picked. So
/// the network reported is the physical one underneath, and the VPN is reported
/// separately.
/// </para>
/// </remarks>
public sealed partial class WindowsConnectionFactsSource : IConnectionFactsSource, IDisposable
{
    private readonly ILogger<WindowsConnectionFactsSource> _logger;
    private bool _hearsWindows;
    private bool _failed;

    public WindowsConnectionFactsSource(ILogger<WindowsConnectionFactsSource> logger)
    {
        _logger = logger;

        NetworkChange.NetworkAddressChanged += OnAddressChanged;
        NetworkChange.NetworkAvailabilityChanged += OnAvailabilityChanged;

        try
        {
            WinRtNetworkInformation.NetworkStatusChanged += OnStatusChanged;
            _hearsWindows = true;
        }
#pragma warning disable CA1031 // Without the event the checks still run on their timer.
        catch (Exception ex)
#pragma warning restore CA1031
        {
            LogNoStatusEvents(ex);
        }
    }

    /// <inheritdoc />
    public event EventHandler? Changed;

    /// <inheritdoc />
    public ConnectionFacts Read()
    {
        NetworkInterface[] adapters = Adapters();
        bool vpn = adapters.Any(adapter => adapter.OperationalStatus == OperationalStatus.Up && IsVpn(adapter));

        try
        {
            ConnectionFacts facts = ReadWindows(adapters, vpn);
            _failed = false;
            return facts;
        }
#pragma warning disable CA1031 // Windows' networking API can fail in more ways than it documents; unknown is a reading.
        catch (Exception ex)
#pragma warning restore CA1031
        {
            if (!_failed)
            {
                _failed = true;
                LogReadFailed(ex);
            }

            NetworkInterface? physical = adapters.FirstOrDefault(adapter => Usable(adapter) && Gateway(adapter) is not null);

            return new ConnectionFacts(
                WindowsReach.Unknown,
                physical is null ? null : new NetworkIdentity(physical.Name, KindOf(physical)),
                physical is null ? null : Gateway(physical),
                vpn);
        }
    }

    private static ConnectionFacts ReadWindows(NetworkInterface[] adapters, bool vpn)
    {
        ConnectionProfile? internet = WinRtNetworkInformation.GetInternetConnectionProfile();
        WindowsReach reach = internet is null ? WindowsReach.None : Reach(internet.GetNetworkConnectivityLevel());

        ConnectionProfile? chosen = internet;
        NetworkInterface? adapter = Match(internet, adapters);

        if (adapter is null || !Usable(adapter))
        {
            chosen = null;
            adapter = null;

            foreach (ConnectionProfile profile in WinRtNetworkInformation.GetConnectionProfiles())
            {
                NetworkConnectivityLevel level = profile.GetNetworkConnectivityLevel();

                if (level == NetworkConnectivityLevel.None
                    || Match(profile, adapters) is not { } candidate
                    || !Usable(candidate))
                {
                    continue;
                }

                // Prefer the one with a router; stop at the first that has one.
                if (adapter is null || (Gateway(adapter) is null && Gateway(candidate) is not null))
                {
                    chosen = profile;
                    adapter = candidate;
                }

                if (reach == WindowsReach.None && level == NetworkConnectivityLevel.LocalAccess)
                {
                    reach = WindowsReach.LocalOnly;
                }

                if (Gateway(adapter) is not null)
                {
                    break;
                }
            }
        }

        string? gateway = adapter is null ? null : Gateway(adapter);
        gateway ??= adapters.Where(Usable).Select(Gateway).FirstOrDefault(found => found is not null);

        NetworkIdentity? network = adapter is null
            ? null
            : new NetworkIdentity(NameOf(chosen, adapter), KindOf(chosen, adapter));

        return new ConnectionFacts(reach, network, gateway, vpn);
    }

    private static NetworkInterface[] Adapters()
    {
        try
        {
            return NetworkInterface.GetAllNetworkInterfaces();
        }
        catch (NetworkInformationException)
        {
            return [];
        }
    }

    private static WindowsReach Reach(NetworkConnectivityLevel level) => level switch
    {
        NetworkConnectivityLevel.InternetAccess => WindowsReach.Internet,
        NetworkConnectivityLevel.ConstrainedInternetAccess => WindowsReach.SignInRequired,
        NetworkConnectivityLevel.LocalAccess => WindowsReach.LocalOnly,
        _ => WindowsReach.None,
    };

    private static NetworkInterface? Match(ConnectionProfile? profile, NetworkInterface[] adapters)
    {
        if (profile?.NetworkAdapter is not { } networkAdapter)
        {
            return null;
        }

        Guid id = networkAdapter.NetworkAdapterId;

        return adapters.FirstOrDefault(adapter => Guid.TryParse(adapter.Id, out Guid each) && each == id);
    }

    /// <summary>An adapter that is up, is a real link rather than loopback or a tunnel, and is not a VPN.</summary>
    private static bool Usable(NetworkInterface adapter) =>
        adapter.OperationalStatus == OperationalStatus.Up
        && adapter.NetworkInterfaceType is not (NetworkInterfaceType.Loopback or NetworkInterfaceType.Tunnel)
        && !IsVpn(adapter);

    private static bool IsVpn(NetworkInterface adapter) =>
        VpnDetector.IsVpn(adapter.NetworkInterfaceType, adapter.Description, adapter.Name);

    private static string? Gateway(NetworkInterface adapter)
    {
        try
        {
            foreach (GatewayIPAddressInformation gateway in adapter.GetIPProperties().GatewayAddresses)
            {
                IPAddress address = gateway.Address;

                if (address.AddressFamily == AddressFamily.InterNetwork && !address.Equals(IPAddress.Any))
                {
                    return address.ToString();
                }
            }
        }
        catch (NetworkInformationException)
        {
        }

        return null;
    }

    private static string NameOf(ConnectionProfile? profile, NetworkInterface adapter)
    {
        string? name = profile?.ProfileName;
        return string.IsNullOrWhiteSpace(name) ? adapter.Name : name.Trim();
    }

    private static NetworkKind KindOf(ConnectionProfile? profile, NetworkInterface adapter)
    {
        if (profile is not null)
        {
            if (profile.IsWlanConnectionProfile)
            {
                return NetworkKind.WiFi;
            }

            if (profile.IsWwanConnectionProfile)
            {
                return NetworkKind.Cellular;
            }
        }

        return KindOf(adapter);
    }

    private static NetworkKind KindOf(NetworkInterface adapter) => adapter.NetworkInterfaceType switch
    {
        NetworkInterfaceType.Wireless80211 => NetworkKind.WiFi,
        NetworkInterfaceType.Ethernet or NetworkInterfaceType.GigabitEthernet or NetworkInterfaceType.FastEthernetT
            or NetworkInterfaceType.FastEthernetFx or NetworkInterfaceType.Ethernet3Megabit => NetworkKind.Ethernet,
        NetworkInterfaceType.Wwanpp or NetworkInterfaceType.Wwanpp2 => NetworkKind.Cellular,
        _ => NetworkKind.Other,
    };

    private void OnStatusChanged(object? sender) => Changed?.Invoke(this, EventArgs.Empty);

    private void OnAddressChanged(object? sender, EventArgs e) => Changed?.Invoke(this, EventArgs.Empty);

    private void OnAvailabilityChanged(object? sender, NetworkAvailabilityEventArgs e) => Changed?.Invoke(this, EventArgs.Empty);

    public void Dispose()
    {
        NetworkChange.NetworkAddressChanged -= OnAddressChanged;
        NetworkChange.NetworkAvailabilityChanged -= OnAvailabilityChanged;

        if (_hearsWindows)
        {
            _hearsWindows = false;

            try
            {
                WinRtNetworkInformation.NetworkStatusChanged -= OnStatusChanged;
            }
#pragma warning disable CA1031 // Nothing to be done about an event that will not let go at shutdown.
            catch (Exception)
#pragma warning restore CA1031
            {
            }
        }
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "Windows will not report network changes; connection checks run on their timer only")]
    private partial void LogNoStatusEvents(Exception error);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Windows' connection profile could not be read")]
    private partial void LogReadFailed(Exception error);
}
