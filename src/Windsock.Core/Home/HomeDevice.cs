using System.Net;

namespace Windsock.Core.Home;

/// <summary>Where a device's name came from.</summary>
public enum DeviceNameSource
{
    /// <summary>No name was found.</summary>
    None,

    /// <summary>The device announced it over multicast DNS (Bonjour, AirPlay, Chromecast, printers).</summary>
    Multicast,

    /// <summary>The device answered a NetBIOS name query (Windows PCs, Samba).</summary>
    NetBios,

    /// <summary>The router's own DNS server knew it, usually from the address it handed out.</summary>
    Router,
}

/// <summary>A device seen on a home network once, as one look found it.</summary>
/// <param name="Mac">Its hardware address.</param>
/// <param name="Address">Its IPv4 address, when seen.</param>
/// <param name="Address6">An IPv6 address of its, when seen.</param>
/// <param name="IsRouter">Whether it is the network's router.</param>
public readonly record struct DeviceSighting(MacAddress Mac, IPAddress? Address, IPAddress? Address6, bool IsRouter);

/// <summary>A device Windsock has seen on a home network.</summary>
/// <param name="NetworkKey">The network it is on (see <see cref="ConnectedNetwork.Key"/>).</param>
/// <param name="Mac">Its hardware address, which is what identifies it.</param>
/// <param name="FirstSeen">When it was first seen on this network.</param>
/// <param name="LastSeen">When it was last seen.</param>
/// <param name="Address">Its last IPv4 address, or nothing.</param>
/// <param name="Address6">Its last IPv6 address, or nothing.</param>
/// <param name="FoundName">The name it was found to have, or nothing.</param>
/// <param name="NameSource">Where <paramref name="FoundName"/> came from.</param>
/// <param name="Nickname">The name the person gave it, or nothing.</param>
/// <param name="IsRouter">Whether it is the network's router.</param>
public sealed record HomeDevice(
    string NetworkKey,
    MacAddress Mac,
    DateTimeOffset FirstSeen,
    DateTimeOffset LastSeen,
    IPAddress? Address,
    IPAddress? Address6,
    string? FoundName,
    DeviceNameSource NameSource,
    string? Nickname,
    bool IsRouter)
{
    /// <summary>Who made its network adapter, from the IEEE table, or nothing when not known.</summary>
    public string? Maker { get; init; }

    /// <summary>The name to show: the person's own, else the one found, else nothing.</summary>
    public string? Name => Nickname is { Length: > 0 } ? Nickname : FoundName;

    /// <summary>
    /// Whether it uses a private, randomised hardware address, as phones and
    /// Windows do per network. Such a device has no maker to name, and may come
    /// back as a "new" device if it changes its address.
    /// </summary>
    public bool HasPrivateAddress => Mac.IsLocallyAdministered;

    /// <summary>Whether it was seen within the given span of a moment.</summary>
    public bool SeenWithin(DateTimeOffset now, TimeSpan span) => now - LastSeen <= span;
}

/// <summary>A device seen on a home network for the first time, after that network's first look.</summary>
/// <param name="Device">The device.</param>
/// <param name="NetworkName">What Windows calls the network.</param>
public sealed record DeviceJoined(HomeDevice Device, string NetworkName);
