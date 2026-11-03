using System.Net;
using System.Net.Sockets;

namespace Windsock.Core.Home;

/// <summary>
/// Wake-on-LAN: the "magic packet" AMD described in 1995, which a sleeping
/// adapter set to wake on it watches for.
/// </summary>
/// <remarks>
/// The packet is six bytes of 0xFF and then the target's hardware address
/// sixteen times: 102 bytes, anywhere in a frame's payload. It is sent as a UDP
/// datagram to port 9 (discard) at the link's broadcast address, so it never
/// leaves the home network. Whether the device wakes is up to it - its adapter's
/// "Wake on Magic Packet" setting, its firmware, and for Wi-Fi whether it can
/// at all - and nothing comes back to say it did.
/// </remarks>
public static class WakeOnLan
{
    /// <summary>The port the packet goes to: discard, which nothing answers.</summary>
    public const int Port = 9;

    /// <summary>The packet's length.</summary>
    public const int PacketLength = 6 + (16 * MacAddress.Length);

    /// <summary>The magic packet for a device.</summary>
    public static byte[] Packet(MacAddress device)
    {
        byte[] packet = new byte[PacketLength];
        packet.AsSpan(0, 6).Fill(0xFF);

        Span<byte> octets = stackalloc byte[MacAddress.Length];
        device.WriteTo(octets);

        for (int copy = 0; copy < 16; copy++)
        {
            octets.CopyTo(packet.AsSpan(6 + (copy * MacAddress.Length)));
        }

        return packet;
    }

    /// <summary>
    /// Where to send it on a link: its own broadcast address, and the limited
    /// broadcast, which stays on whichever link it is sent from.
    /// </summary>
    public static IReadOnlyList<IPEndPoint> Targets(HomeLink link)
    {
        ArgumentNullException.ThrowIfNull(link);

        List<IPEndPoint> targets = [];

        if (link.Broadcast is { } subnet)
        {
            targets.Add(new IPEndPoint(subnet, Port));
        }

        targets.Add(new IPEndPoint(IPAddress.Broadcast, Port));
        return targets;
    }

    /// <summary>
    /// Sends the packet for a device on a home link, from the link's own
    /// address so it goes out of the right adapter. False when it could not be
    /// sent at all; true says nothing about whether the device woke.
    /// </summary>
    public static async Task<bool> SendAsync(MacAddress device, HomeLink link, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(link);

        if (!device.IsUnicast || link.Address is null)
        {
            return false;
        }

        byte[] packet = Packet(device);
        bool sent = false;

        try
        {
            using UdpClient udp = new(new IPEndPoint(link.Address, 0)) { EnableBroadcast = true };

            foreach (IPEndPoint target in Targets(link))
            {
                try
                {
                    await udp.SendAsync(packet, target, cancellationToken).ConfigureAwait(false);
                    sent = true;
                }
                catch (SocketException)
                {
                }
            }
        }
        catch (SocketException)
        {
            return false;
        }

        return sent;
    }
}
