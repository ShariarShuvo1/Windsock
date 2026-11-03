using Windsock.Core.Connectivity;

namespace Windsock.Core.Networking;

/// <summary>What Windows says using a network costs, as its connection profile reports it.</summary>
/// <remarks>
/// Mirrors <c>Windows.Networking.Connectivity.NetworkCostType</c>. A Wi-Fi
/// network Windows reports as anything but <see cref="Unrestricted"/> is metered:
/// either somebody set it so in Settings, or the network said so itself, as an
/// Android phone's hotspot does.
/// </remarks>
public enum NetworkCost
{
    /// <summary>Windows could not say.</summary>
    Unknown,

    /// <summary>No limit and no charge for using more.</summary>
    Unrestricted,

    /// <summary>A fixed allowance: metered.</summary>
    Fixed,

    /// <summary>Charged by the byte: metered.</summary>
    Variable,
}

/// <summary>
/// The network a minute of history was measured on, as history keeps it.
/// </summary>
/// <param name="Key">
/// A stable id for the network: Windows' own id for it where Windows gives one
/// (the network list's GUID, lower case), otherwise its kind and name.
/// </param>
/// <param name="Name">What Windows calls it: a Wi-Fi name, or a wired network's profile name.</param>
/// <param name="Kind">The kind of link.</param>
/// <param name="Metered">Whether Windows treats it as metered, or null when that is not known.</param>
public sealed record NetworkTag(string Key, string Name, NetworkKind Kind, bool? Metered = null)
{
    /// <summary>The key for a network Windows gives no id for: its kind and its name.</summary>
    public static string KeyFromName(NetworkKind kind, string name)
    {
        ArgumentNullException.ThrowIfNull(name);
        return string.Concat("name:", KindName(kind), ":", name.Trim());
    }

    /// <summary>The kind as history stores it: a word, so reordering the enum can never change a file.</summary>
    public static string KindName(NetworkKind kind) => kind switch
    {
        NetworkKind.Ethernet => "ethernet",
        NetworkKind.WiFi => "wifi",
        NetworkKind.Cellular => "cellular",
        _ => "other",
    };

    /// <summary>The kind from the word history stores; anything unknown is <see cref="NetworkKind.Other"/>.</summary>
    public static NetworkKind KindFrom(string? word) => word switch
    {
        "ethernet" => NetworkKind.Ethernet,
        "wifi" => NetworkKind.WiFi,
        "cellular" => NetworkKind.Cellular,
        _ => NetworkKind.Other,
    };
}

/// <summary>
/// A data limit Windows itself holds for a network: one set under Settings,
/// Data usage, or one a mobile operator provisioned.
/// </summary>
/// <param name="LimitBytes">The limit, or null when Windows has none.</param>
/// <param name="NextBillingCycle">When Windows says the allowance resets, or null.</param>
/// <param name="UsedBytes">What Windows says has been used against it, or null.</param>
/// <param name="UsedAsOf">When Windows last worked that out, or null.</param>
public sealed record WindowsDataPlan(long? LimitBytes, DateTimeOffset? NextBillingCycle, long? UsedBytes, DateTimeOffset? UsedAsOf)
{
    /// <summary>Whether Windows gave anything at all.</summary>
    public bool HasAnything => LimitBytes is not null || NextBillingCycle is not null || UsedBytes is not null;
}

/// <summary>Everything about the network in use that can be read without sending a packet.</summary>
/// <param name="Network">Which network it is.</param>
/// <param name="Cost">What Windows says it costs.</param>
/// <param name="Roaming">Whether Windows says it is roaming outside its home network.</param>
/// <param name="ApproachingDataLimit">Whether Windows says its data limit is close.</param>
/// <param name="OverDataLimit">Whether Windows says its data limit is passed.</param>
/// <param name="WindowsPlan">The data limit Windows holds for it, or null when it holds none.</param>
public sealed record NetworkSighting(
    NetworkTag Network,
    NetworkCost Cost,
    bool Roaming = false,
    bool ApproachingDataLimit = false,
    bool OverDataLimit = false,
    WindowsDataPlan? WindowsPlan = null)
{
    /// <summary>Whether Windows treats the network as metered.</summary>
    public bool IsMetered => Cost is NetworkCost.Fixed or NetworkCost.Variable;
}

/// <summary>
/// Reads which network this PC is using from Windows.
/// </summary>
/// <remarks>
/// The network meant is the one carrying the internet. With a VPN up that is the
/// physical network underneath it, as the connection checks report too.
/// </remarks>
public interface INetworkSightingSource
{
    /// <summary>The network in use now, or null when there is none. Never throws.</summary>
    NetworkSighting? Read();

    /// <summary>Raised, on any thread, when Windows says something about the network changed.</summary>
    event EventHandler? Changed;
}

/// <summary>The network history should tag the minute being recorded with.</summary>
public interface INetworkTagSource
{
    /// <summary>The network in use, or null when it is not known.</summary>
    NetworkTag? Current { get; }
}
