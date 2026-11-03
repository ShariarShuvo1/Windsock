using System.Net.NetworkInformation;
using Microsoft.Extensions.Logging;
using Windows.Networking.Connectivity;
using Windsock.Core.Connectivity;
using Windsock.Core.Networking;
using WinRtNetworkInformation = Windows.Networking.Connectivity.NetworkInformation;

namespace Windsock.App.Services.Usage;

/// <summary>
/// Reads which network carries the internet from Windows' connection profiles:
/// its id, its name, its kind, what Windows says it costs, and any data limit
/// Windows holds for it.
/// </summary>
/// <remarks>
/// <para>
/// The id is the network list's own GUID for the network
/// (<c>NetworkAdapter.NetworkItem.NetworkId</c>), which stays the same each time
/// the PC joins that network. Measured on 2026-10-05: the wired network here
/// read <c>0d1170d5-…</c>, while every Wi-Fi profile not connected read the
/// same placeholder, <c>a0b6ad8e-a7e6-11f1-8d64-806e6f6e6963</c> (a time-based
/// GUID whose node spells "nonic"), so only the connected profile's id is
/// used, and a placeholder or empty id falls back to the kind and name.
/// </para>
/// <para>
/// Measured the same day: <c>GetDataPlanStatus</c> answered for every profile
/// with every field empty - no limit, no billing date, no usage - with no data
/// limit set under Settings, Data usage. Whether setting one there fills it in
/// was not tried (it changes a system setting); a limit a mobile operator
/// provisions does, by Microsoft's documentation. Either way an empty answer is
/// "no limit", not a failure.
/// </para>
/// <para>
/// With a VPN up, Windows' internet profile is the VPN's; the physical network
/// under it is the one the bytes are billed on, so that is the one reported, as
/// the connection checks do.
/// </para>
/// </remarks>
public sealed partial class WindowsNetworkSightingSource : INetworkSightingSource, IDisposable
{
    private const int IanaEthernet = 6;
    private const int IanaWiFi = 71;
    private const int IanaWwanPp = 243;
    private const int IanaWwanPp2 = 244;
    private const long Megabyte = 1024L * 1024;

    private readonly ILogger<WindowsNetworkSightingSource> _logger;
    private bool _hearsWindows;
    private bool _failed;

    public WindowsNetworkSightingSource(ILogger<WindowsNetworkSightingSource> logger)
    {
        _logger = logger;

        try
        {
            WinRtNetworkInformation.NetworkStatusChanged += OnStatusChanged;
            _hearsWindows = true;
        }
#pragma warning disable CA1031 // Without the event the network is still read once a minute.
        catch (Exception ex)
#pragma warning restore CA1031
        {
            LogNoEvents(ex);
        }
    }

    /// <inheritdoc />
    public event EventHandler? Changed;

    /// <inheritdoc />
    public NetworkSighting? Read()
    {
        try
        {
            NetworkSighting? seen = ReadWindows();
            _failed = false;
            return seen;
        }
#pragma warning disable CA1031 // Windows' networking API fails in more ways than it documents; unknown is a reading.
        catch (Exception ex)
#pragma warning restore CA1031
        {
            if (!_failed)
            {
                _failed = true;
                LogReadFailed(ex);
            }

            return null;
        }
    }

    private static NetworkSighting? ReadWindows()
    {
        NetworkInterface[] adapters = Adapters();
        ConnectionProfile? profile = WinRtNetworkInformation.GetInternetConnectionProfile();

        if (profile is null || IsVpn(profile, adapters))
        {
            profile = WinRtNetworkInformation.GetConnectionProfiles()
                .Where(each => each.GetNetworkConnectivityLevel() != NetworkConnectivityLevel.None && !IsVpn(each, adapters))
                .OrderByDescending(each => each.GetNetworkConnectivityLevel())
                .FirstOrDefault() ?? (profile is not null && !IsVpn(profile, adapters) ? profile : null);
        }

        if (profile is null)
        {
            return null;
        }

        NetworkKind kind = KindOf(profile);
        string name = NameOf(profile);
        Guid id = NetworkIdOf(profile);
        string key = id == Guid.Empty || IsPlaceholder(id)
            ? NetworkTag.KeyFromName(kind, name)
            : id.ToString("D").ToLowerInvariant();

        (NetworkCost cost, bool roaming, bool approaching, bool over) = CostOf(profile);
        bool? metered = cost switch
        {
            NetworkCost.Unrestricted => false,
            NetworkCost.Fixed or NetworkCost.Variable => true,
            _ => null,
        };

        return new NetworkSighting(new NetworkTag(key, name, kind, metered), cost, roaming, approaching, over, PlanOf(profile));
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

    private static bool IsVpn(ConnectionProfile profile, NetworkInterface[] adapters)
    {
        Guid? adapterId = profile.NetworkAdapter?.NetworkAdapterId;

        if (adapterId is null)
        {
            return false;
        }

        NetworkInterface? adapter = adapters.FirstOrDefault(each => Guid.TryParse(each.Id, out Guid parsed) && parsed == adapterId);
        return adapter is not null && VpnDetector.IsVpn(adapter.NetworkInterfaceType, adapter.Description, adapter.Name);
    }

    private static Guid NetworkIdOf(ConnectionProfile profile)
    {
        try
        {
            return profile.NetworkAdapter?.NetworkItem?.NetworkId ?? Guid.Empty;
        }
#pragma warning disable CA1031 // A profile without a network item is named by its name instead.
        catch (Exception)
#pragma warning restore CA1031
        {
            return Guid.Empty;
        }
    }

    /// <summary>
    /// The id Windows hands every Wi-Fi profile that is not connected: a
    /// time-based GUID with the node "\x80nonic" ("no NIC").
    /// </summary>
    private static bool IsPlaceholder(Guid id) =>
        id.ToString("D").EndsWith("-806e6f6e6963", StringComparison.OrdinalIgnoreCase);

    private static string NameOf(ConnectionProfile profile)
    {
        string? name = profile.ProfileName;

        if (string.IsNullOrWhiteSpace(name))
        {
            IReadOnlyList<string> names = profile.GetNetworkNames();
            name = names.Count > 0 ? names[0] : null;
        }

        return string.IsNullOrWhiteSpace(name) ? "Network" : name.Trim();
    }

    private static NetworkKind KindOf(ConnectionProfile profile)
    {
        if (profile.IsWlanConnectionProfile)
        {
            return NetworkKind.WiFi;
        }

        if (profile.IsWwanConnectionProfile)
        {
            return NetworkKind.Cellular;
        }

        return (int)(profile.NetworkAdapter?.IanaInterfaceType ?? 0) switch
        {
            IanaEthernet => NetworkKind.Ethernet,
            IanaWiFi => NetworkKind.WiFi,
            IanaWwanPp or IanaWwanPp2 => NetworkKind.Cellular,
            _ => NetworkKind.Other,
        };
    }

    private static (NetworkCost Cost, bool Roaming, bool Approaching, bool Over) CostOf(ConnectionProfile profile)
    {
        ConnectionCost? cost = profile.GetConnectionCost();

        if (cost is null)
        {
            return (NetworkCost.Unknown, false, false, false);
        }

        NetworkCost type = cost.NetworkCostType switch
        {
            NetworkCostType.Unrestricted => NetworkCost.Unrestricted,
            NetworkCostType.Fixed => NetworkCost.Fixed,
            NetworkCostType.Variable => NetworkCost.Variable,
            _ => NetworkCost.Unknown,
        };

        return (type, cost.Roaming, cost.ApproachingDataLimit, cost.OverDataLimit);
    }

    private static WindowsDataPlan? PlanOf(ConnectionProfile profile)
    {
        DataPlanStatus? status;

        try
        {
            status = profile.GetDataPlanStatus();
        }
#pragma warning disable CA1031 // No plan status is no plan.
        catch (Exception)
#pragma warning restore CA1031
        {
            return null;
        }

        if (status is null)
        {
            return null;
        }

        DataPlanUsage? usage = status.DataPlanUsage;
        WindowsDataPlan plan = new(
            status.DataLimitInMegabytes is { } limit ? limit * Megabyte : null,
            status.NextBillingCycle,
            usage is null ? null : usage.MegabytesUsed * Megabyte,
            usage?.LastSyncTime);

        return plan.HasAnything ? plan : null;
    }

    private void OnStatusChanged(object? sender) => Changed?.Invoke(this, EventArgs.Empty);

    public void Dispose()
    {
        if (!_hearsWindows)
        {
            return;
        }

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

    [LoggerMessage(Level = LogLevel.Warning, Message = "Windows will not report network changes; the network in use is read once a minute")]
    private partial void LogNoEvents(Exception error);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Could not read which network is in use from Windows")]
    private partial void LogReadFailed(Exception error);
}
