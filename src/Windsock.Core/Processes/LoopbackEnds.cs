using System.Net;

namespace Windsock.Core.Processes;

/// <summary>
/// Who is at the other end of a conversation that never leaves the machine.
/// </summary>
public sealed class LoopbackEnds
{
    private readonly Dictionary<Port, int> _talking = [];
    private readonly Dictionary<Port, int> _listening = [];

    /// <summary>Reads one table of sockets.</summary>
    public LoopbackEnds(IReadOnlyList<ProcessConnection> connections)
    {
        ArgumentNullException.ThrowIfNull(connections);

        foreach (ProcessConnection socket in connections)
        {
            Port port = new(socket.Protocol, socket.IsIpv6, socket.LocalPort);

            if (socket.IsListening)
            {
                if (IsLoopback(socket.LocalAddress) || IsAnyone(socket.LocalAddress))
                {
                    _listening.TryAdd(port, socket.ProcessId);
                }

                continue;
            }
            if (IsLoopback(socket.LocalAddress))
            {
                _talking.TryAdd(port, socket.ProcessId);
            }
        }
    }

    /// <summary>Whether an address is this machine talking to itself.</summary>
    public static bool IsLoopback(string? address)
    {
        if (address is null)
        {
            return false;
        }

        if (string.Equals(address, "127.0.0.1", StringComparison.Ordinal)
            || string.Equals(address, "::1", StringComparison.Ordinal))
        {
            return true;
        }

        return IPAddress.TryParse(address, out IPAddress? parsed) && IPAddress.IsLoopback(parsed);
    }

    private static bool IsAnyone(string? address) =>
        string.Equals(address, "0.0.0.0", StringComparison.Ordinal)
        || string.Equals(address, "::", StringComparison.Ordinal);

    /// <summary>
    /// The process a loopback connection actually reaches.
    /// </summary>
    public int? Owner(TransportProtocol protocol, bool ipv6, int port)
    {
        Port key = new(protocol, ipv6, port);

        if (_talking.TryGetValue(key, out int talking))
        {
            return talking;
        }

        if (_listening.TryGetValue(key, out int listening))
        {
            return listening;
        }
        Port other = new(protocol, !ipv6, port);

        return _listening.TryGetValue(other, out int either) ? either : null;
    }

    /// <summary>
    /// The process a loopback endpoint reaches, where the protocol is unknown.
    /// </summary>
    public int? Owner(bool ipv6, int port) =>
        Owner(TransportProtocol.Tcp, ipv6, port) ?? Owner(TransportProtocol.Udp, ipv6, port);

    private readonly record struct Port(TransportProtocol Protocol, bool IsIpv6, int Number);
}
