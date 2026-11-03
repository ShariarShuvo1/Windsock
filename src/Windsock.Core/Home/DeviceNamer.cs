using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;

namespace Windsock.Core.Home;

/// <summary>Asks for a device's name on the home network, without leaving it.</summary>
public interface IDeviceNamer
{
    /// <summary>
    /// The device's name and where it came from, or nothing when nobody
    /// answered. Asks the device itself over NetBIOS, then the router's DNS
    /// server; never anything beyond the link. Throws only when cancelled.
    /// </summary>
    Task<(string Name, DeviceNameSource Source)?> NameAsync(IPAddress device, HomeLink link, CancellationToken cancellationToken);
}

/// <summary>Asks a device for its NetBIOS name, then the router for its DNS name, each once with a short wait.</summary>
/// <remarks>
/// Both are single UDP datagrams to an address on the link: the device, then
/// the router. Microsoft is winding NetBIOS down, so expect it to answer less
/// and less; the router's DNS server knows the names devices asked for when
/// they took an address, where the router keeps them.
/// </remarks>
public sealed class DeviceNamer : IDeviceNamer
{
    private static readonly TimeSpan Wait = TimeSpan.FromMilliseconds(900);

    /// <inheritdoc />
    public async Task<(string Name, DeviceNameSource Source)?> NameAsync(IPAddress device, HomeLink link, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(device);
        ArgumentNullException.ThrowIfNull(link);

        if (device.AddressFamily != AddressFamily.InterNetwork || link.Address is null || !link.Contains(device))
        {
            return null;
        }

        ushort id = (ushort)RandomNumberGenerator.GetInt32(1, ushort.MaxValue);

        if (await AskAsync(link.Address, new IPEndPoint(device, NetBiosStatus.Port), NetBiosStatus.Request(id), cancellationToken).ConfigureAwait(false) is { } status
            && NetBiosStatus.Parse(status, id) is { } netBios)
        {
            return (netBios, DeviceNameSource.NetBios);
        }

        if (link.Gateway is { } router && !router.Equals(device))
        {
            id = (ushort)RandomNumberGenerator.GetInt32(1, ushort.MaxValue);

            if (await AskAsync(link.Address, new IPEndPoint(router, ReversePointer.Port), ReversePointer.Request(id, device), cancellationToken).ConfigureAwait(false) is { } pointer
                && ReversePointer.Parse(pointer, id) is { } dns)
            {
                return (dns, DeviceNameSource.Router);
            }
        }

        return null;
    }

    /// <summary>Sends one datagram from the link's own address and waits briefly for the answer from the same place.</summary>
    private static async Task<byte[]?> AskAsync(IPAddress from, IPEndPoint to, byte[] question, CancellationToken cancellationToken)
    {
        try
        {
            using UdpClient udp = new(new IPEndPoint(from, 0));
            using CancellationTokenSource wait = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            wait.CancelAfter(Wait);

            await udp.SendAsync(question, to, wait.Token).ConfigureAwait(false);

            while (true)
            {
                UdpReceiveResult answer = await udp.ReceiveAsync(wait.Token).ConfigureAwait(false);

                if (answer.RemoteEndPoint.Address.Equals(to.Address))
                {
                    return answer.Buffer;
                }
            }
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return null;
        }
        catch (SocketException)
        {
            return null;
        }
    }
}
