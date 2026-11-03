using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;

namespace Windsock.Core.Lookups;

/// <summary>What the router says its own outside address is.</summary>
/// <param name="Address">The router's outside address, when it said.</param>
/// <param name="ResultCode">NAT-PMP's result: 0 success, 1 unsupported version, 2 not authorised, 3 network failure, 4 out of resources, 5 unsupported opcode; -1 for no answer.</param>
public sealed record RouterOutside(IPAddress? Address, int ResultCode)
{
    /// <summary>Whether the router answered with an address.</summary>
    public bool Answered => Address is not null;
}

/// <summary>Asks the router for its outside address with NAT-PMP (RFC 6886). The packet stays on the local network.</summary>
/// <remarks>
/// The request is two bytes, version 0 and opcode 0, to the router's UDP port
/// 5351. The answer is twelve: version 0, opcode 128, a two-byte result, the
/// seconds since the router's mapping table started, and the IPv4 address.
/// No port mapping is created or asked for. Routers without NAT-PMP (many,
/// including those offering only UPnP) simply do not answer.
/// </remarks>
public static class NatPmp
{
    /// <summary>The port routers listen on.</summary>
    public const int Port = 5351;

    /// <summary>The request: version 0, opcode 0 (external address).</summary>
    public static ReadOnlySpan<byte> Request => [0, 0];

    /// <summary>Reads an answer, or nothing when it is not an external-address answer.</summary>
    public static RouterOutside? Read(ReadOnlySpan<byte> reply)
    {
        if (reply.Length < 8 || reply[0] != 0 || reply[1] != 128)
        {
            return null;
        }

        int result = BinaryPrimitives.ReadUInt16BigEndian(reply[2..]);

        if (result != 0 || reply.Length < 12)
        {
            return new RouterOutside(null, result);
        }

        return new RouterOutside(new IPAddress(reply.Slice(8, 4)), 0);
    }

    /// <summary>Asks the router, up to three times a quarter, half and whole second apart.</summary>
    public static async Task<RouterOutside> AskAsync(IPAddress router, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(router);

        if (router.AddressFamily != AddressFamily.InterNetwork)
        {
            return new RouterOutside(null, -1);
        }

        try
        {
            using UdpClient client = new(AddressFamily.InterNetwork);
            client.Connect(router, Port);
            TimeSpan wait = TimeSpan.FromMilliseconds(250);

            for (int attempt = 0; attempt < 3; attempt++, wait += wait)
            {
                await client.SendAsync(Request.ToArray(), cancellationToken).ConfigureAwait(false);

                using CancellationTokenSource patience = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                patience.CancelAfter(wait);

                try
                {
                    while (true)
                    {
                        UdpReceiveResult received = await client.ReceiveAsync(patience.Token).ConfigureAwait(false);

                        if (received.RemoteEndPoint.Address.Equals(router) && Read(received.Buffer) is { } answer)
                        {
                            return answer;
                        }
                    }
                }
                catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
                {
                }
            }
        }
        catch (SocketException)
        {
            // A router that refuses the port answers with an ICMP error, which surfaces here.
        }

        return new RouterOutside(null, -1);
    }
}
