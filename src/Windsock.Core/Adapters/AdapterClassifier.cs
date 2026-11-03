using System.Globalization;
using System.Net.NetworkInformation;
using Windsock.Core.Connectivity;

namespace Windsock.Core.Adapters;

/// <summary>Decides what kind of link an adapter is from what Windows reports about it.</summary>
/// <remarks>
/// Windows has no "this is a VPN" or "this is a virtual machine's switch" flag.
/// The interface and tunnel types catch Windows' own tunnels and point-to-point
/// links; everything else - WireGuard, Wintun, TAP, Hyper-V, WSL - presents as
/// an ordinary Ethernet adapter and is known only by its name. The VPN signs are
/// <see cref="VpnDetector"/>'s, so the Connection page and this one agree.
/// </remarks>
public static class AdapterClassifier
{
    /// <summary>IANA ifType: Ethernet (CSMA/CD).</summary>
    public const uint Ethernet = 6;

    /// <summary>IANA ifType: PPP.</summary>
    public const uint Ppp = 23;

    /// <summary>IANA ifType: software loopback.</summary>
    public const uint Loopback = 24;

    /// <summary>IANA ifType: proprietary virtual.</summary>
    public const uint ProprietaryVirtual = 53;

    /// <summary>IANA ifType: IEEE 802.11 wireless.</summary>
    public const uint Wireless = 71;

    /// <summary>IANA ifType: Gigabit Ethernet.</summary>
    public const uint GigabitEthernet = 117;

    /// <summary>IANA ifType: tunnel encapsulation.</summary>
    public const uint Tunnel = 131;

    /// <summary>IANA ifType: mobile broadband (GSM).</summary>
    public const uint MobileGsm = 243;

    /// <summary>IANA ifType: mobile broadband (CDMA).</summary>
    public const uint MobileCdma = 244;

    // TUNNEL_TYPE values from ifdef.h that are Windows' own IPv6 transition tunnels.
    private const uint SixToFour = 11;
    private const uint Isatap = 13;
    private const uint Teredo = 14;
    private const uint IpHttps = 15;

    private static readonly string[] VirtualSigns =
    [
        "hyper-v",
        "vethernet",
        "vmware",
        "virtualbox",
        "wsl",
        "docker",
        "npcap loopback",
        "kernel debug",
        "parallels",

        // Windows' own virtual radios for casting and hotspots: two of these
        // sat down beside the one real Wi-Fi adapter on the development machine.
        "wi-fi direct virtual",
        "hosted network virtual",
    ];

    /// <summary>What kind of link an adapter is.</summary>
    /// <param name="interfaceType">The IANA interface type.</param>
    /// <param name="tunnelType">Windows' TUNNEL_TYPE, 0 for none.</param>
    /// <param name="description">The driver's description.</param>
    /// <param name="name">The name people see.</param>
    public static AdapterKind Classify(uint interfaceType, uint tunnelType, string? description, string? name)
    {
        if (interfaceType == Loopback)
        {
            return AdapterKind.Loopback;
        }

        if (tunnelType is SixToFour or Isatap or Teredo or IpHttps || Mentions(description, "teredo", "isatap", "6to4", "ip-https"))
        {
            return AdapterKind.Transition;
        }

        if (VpnDetector.IsVpn((NetworkInterfaceType)(int)interfaceType, description, name))
        {
            return AdapterKind.Vpn;
        }

        if (Mentions(description, VirtualSigns) || Mentions(name, VirtualSigns))
        {
            return AdapterKind.Virtual;
        }

        if (Mentions(description, "bluetooth") || Mentions(name, "bluetooth"))
        {
            return AdapterKind.Bluetooth;
        }

        return interfaceType switch
        {
            Wireless => AdapterKind.WiFi,
            MobileGsm or MobileCdma => AdapterKind.Cellular,
            Ethernet or GigabitEthernet or 62 or 26 or 69 => AdapterKind.Ethernet,
            Tunnel or Ppp => AdapterKind.Vpn,
            _ => AdapterKind.Other,
        };
    }

    /// <summary>Reads an IF_OPER_STATUS value.</summary>
    public static AdapterState State(uint operStatus) => operStatus switch
    {
        1 => AdapterState.Up,
        2 => AdapterState.Down,
        6 => AdapterState.NotPresent,
        3 or 5 or 7 => AdapterState.Waiting,
        _ => AdapterState.Unknown,
    };

    /// <summary>
    /// A MAC address as Windows writes it, "AA-BB-CC-DD-EE-FF", or nothing for
    /// an adapter without one.
    /// </summary>
    public static string? HardwareAddress(ReadOnlySpan<byte> bytes)
    {
        if (bytes.IsEmpty || !bytes.ContainsAnyExcept((byte)0))
        {
            return null;
        }

        return string.Join('-', bytes.ToArray().Select(each => each.ToString("X2", CultureInfo.InvariantCulture)));
    }

    /// <summary>A link speed Windows reports, or nothing for its "not known" value.</summary>
    public static ulong? Speed(ulong bitsPerSecond) => bitsPerSecond is 0 or ulong.MaxValue ? null : bitsPerSecond;

    /// <summary>An MTU Windows reports, or nothing for loopback's all-ones value.</summary>
    public static uint? Mtu(uint mtu) => mtu is 0 or uint.MaxValue ? null : mtu;

    private static bool Mentions(string? text, params string[] signs) =>
        text is { Length: > 0 } && signs.Any(sign => text.Contains(sign, StringComparison.OrdinalIgnoreCase));
}
