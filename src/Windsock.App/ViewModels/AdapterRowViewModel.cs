using System.Globalization;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using Windsock.Core.Formatting;

namespace Windsock.App.ViewModels;

/// <summary>
/// One network adapter that is up, as the System section lists it under the
/// network chart: what it is, how fast its link is, and its address.
/// </summary>
public sealed record AdapterRowViewModel(string Name, string Description, string Kind, string Speed, string Address)
{
    /// <summary>
    /// Reads the adapters that are carrying traffic, fastest link first.
    /// </summary>
    /// <remarks>
    /// The managed call rather than the IP Helper table the meter reads: this
    /// runs when the section opens and then every half minute, where the cost
    /// that ruled it out once a second does not matter, and it is the call that
    /// knows link speeds and addresses.
    ///
    /// Only adapters with an address anything could reach are listed. Windows
    /// reports its own plumbing as adapters that are up - a packet scheduler
    /// and two filters bound to every real card, and the WAN miniports behind
    /// them - and on the development machine that was thirteen rows for one
    /// Ethernet cable. None of that plumbing holds an address; the card, a VPN
    /// and a virtual switch all do.
    /// </remarks>
    public static IReadOnlyList<AdapterRowViewModel> Read()
    {
        NetworkInterface[] interfaces;

        try
        {
            interfaces = NetworkInterface.GetAllNetworkInterfaces();
        }
        catch (NetworkInformationException)
        {
            return [];
        }

        List<(long Speed, AdapterRowViewModel Row)> rows = [];

        foreach (NetworkInterface adapter in interfaces)
        {
            try
            {
                if (adapter.OperationalStatus != OperationalStatus.Up
                    || adapter.NetworkInterfaceType is NetworkInterfaceType.Loopback or NetworkInterfaceType.Tunnel
                    || AddressOf(adapter) is not { } address)
                {
                    continue;
                }

                rows.Add((adapter.Speed, new AdapterRowViewModel(
                    adapter.Name,
                    adapter.Description,
                    KindOf(adapter.NetworkInterfaceType),
                    SpeedOf(adapter.Speed),
                    address)));
            }
            catch (NetworkInformationException)
            {
                // An adapter that went away between the list and the question.
            }
        }

        return [.. rows.OrderByDescending(row => row.Speed).Select(row => row.Row)];
    }

    private static string KindOf(NetworkInterfaceType type) => type switch
    {
        NetworkInterfaceType.Wireless80211 => "Wi-Fi",
        NetworkInterfaceType.Ethernet or NetworkInterfaceType.GigabitEthernet or NetworkInterfaceType.FastEthernetT
            or NetworkInterfaceType.FastEthernetFx or NetworkInterfaceType.Ethernet3Megabit => "Ethernet",
        NetworkInterfaceType.Wwanpp or NetworkInterfaceType.Wwanpp2 => "Mobile",
        NetworkInterfaceType.Ppp => "Dial-up",
        _ => "Other",
    };

    /// <summary>A link speed the way a router's box states it.</summary>
    public static string SpeedOf(long bitsPerSecond)
    {
        if (bitsPerSecond <= 0)
        {
            return MeterReadout.Unknown;
        }

        return bitsPerSecond switch
        {
            >= 1_000_000_000 => string.Create(CultureInfo.InvariantCulture, $"{bitsPerSecond / 1_000_000_000d:0.#} Gbps"),
            >= 1_000_000 => string.Create(CultureInfo.InvariantCulture, $"{bitsPerSecond / 1_000_000d:0.#} Mbps"),
            _ => string.Create(CultureInfo.InvariantCulture, $"{bitsPerSecond / 1_000d:0.#} Kbps"),
        };
    }

    /// <summary>
    /// The adapter's IPv4 address, or its first IPv6 one that is not only for
    /// its own link, or nothing when it has neither. A self-assigned 169.254
    /// address is Windows saying no network answered, so it counts as none.
    /// </summary>
    private static string? AddressOf(NetworkInterface adapter)
    {
        UnicastIPAddressInformationCollection addresses = adapter.GetIPProperties().UnicastAddresses;

        foreach (UnicastIPAddressInformation address in addresses)
        {
            if (address.Address.AddressFamily == AddressFamily.InterNetwork
                && !address.Address.ToString().StartsWith("169.254.", StringComparison.Ordinal))
            {
                return address.Address.ToString();
            }
        }

        foreach (UnicastIPAddressInformation address in addresses)
        {
            if (address.Address.AddressFamily == AddressFamily.InterNetworkV6
                && !address.Address.IsIPv6LinkLocal
                && !address.Address.IsIPv6SiteLocal)
            {
                return address.Address.ToString();
            }
        }

        return null;
    }
}
