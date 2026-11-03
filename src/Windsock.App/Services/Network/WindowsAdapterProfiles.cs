using Windows.Networking.Connectivity;
using Windsock.Core.Adapters;
using Windsock.Core.Networking;
using WinRtNetworkInformation = Windows.Networking.Connectivity.NetworkInformation;

namespace Windsock.App.Services.Network;

/// <summary>
/// Reads each connected adapter's Windows network profile: its name and what
/// Windows says the network costs, which is where "metered" lives.
/// </summary>
/// <remarks>
/// Reading needs no permission and sends nothing. Changing the metered setting
/// needs Settings, so the page links to it rather than writing it. Saved Wi-Fi
/// profiles Windows lists with no connectivity are skipped: they are networks
/// remembered, not networks in use. Whether a data limit typed into Settings
/// shows up here as "approaching" or "over" the limit was not verified - no
/// limit was set on the development machine.
/// </remarks>
public sealed class WindowsAdapterProfiles : IAdapterProfileSource
{
    /// <inheritdoc />
    public IReadOnlyDictionary<Guid, AdapterProfile> Read()
    {
        Dictionary<Guid, AdapterProfile> profiles = [];

        foreach (ConnectionProfile profile in WinRtNetworkInformation.GetConnectionProfiles())
        {
            if (profile.NetworkAdapter is not { } adapter
                || profile.GetNetworkConnectivityLevel() == NetworkConnectivityLevel.None)
            {
                continue;
            }

            AdapterCost? cost = null;

            if (profile.GetConnectionCost() is { } windows)
            {
                cost = new AdapterCost(
                    windows.NetworkCostType switch
                    {
                        NetworkCostType.Unrestricted => NetworkCost.Unrestricted,
                        NetworkCostType.Fixed => NetworkCost.Fixed,
                        NetworkCostType.Variable => NetworkCost.Variable,
                        _ => NetworkCost.Unknown,
                    },
                    windows.Roaming,
                    windows.OverDataLimit,
                    windows.ApproachingDataLimit,
                    windows.BackgroundDataUsageRestricted);
            }

            string? name = string.IsNullOrWhiteSpace(profile.ProfileName) ? null : profile.ProfileName.Trim();
            profiles[adapter.NetworkAdapterId] = new AdapterProfile(name, cost);
        }

        return profiles;
    }

    /// <summary>Settings' page for the metered and data limit choices.</summary>
    public static Uri SettingsPage(AdapterKind kind) => kind switch
    {
        AdapterKind.WiFi => new Uri("ms-settings:network-wifi"),
        AdapterKind.Ethernet => new Uri("ms-settings:network-ethernet"),
        AdapterKind.Cellular => new Uri("ms-settings:network-cellular"),
        AdapterKind.Vpn => new Uri("ms-settings:network-vpn"),
        _ => new Uri("ms-settings:network-status"),
    };
}
