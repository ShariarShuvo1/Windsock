using System.Net;
using Windows.Win32;
using Windows.Win32.NetworkManagement.IpHelper;
using Windsock.Core.Processes;

namespace Windsock.Core.Insight;

/// <summary>Who could reach a socket that is waiting for others.</summary>
public enum ListenReach
{
    /// <summary>Bound to the loopback address: only programs on this PC.</summary>
    ThisPcOnly,

    /// <summary>Bound to every address: any network the PC is on, as far as the firewall lets them.</summary>
    AllNetworks,

    /// <summary>Bound to one of the PC's addresses: whoever can reach that address.</summary>
    OneAddress,
}

/// <summary>One socket a program holds open for others to reach.</summary>
/// <param name="ProcessId">The program's process.</param>
/// <param name="Protocol">TCP, which accepts connections, or UDP, which takes whatever arrives.</param>
/// <param name="IsIpv6">Whether it is an IPv6 socket.</param>
/// <param name="LocalAddress">The address it is bound to: 0.0.0.0 or :: for every address.</param>
/// <param name="Port">The port it waits on.</param>
public sealed record ListeningSocket(int ProcessId, TransportProtocol Protocol, bool IsIpv6, string LocalAddress, int Port)
{
    /// <summary>Who could reach it.</summary>
    public ListenReach Reach => Listeners.ReachOf(LocalAddress);

    /// <summary>The port with its usual service, such as "445 (smb)", or the number alone.</summary>
    public string PortText => PortNames.Describe(Port);

    /// <summary>
    /// A UDP socket on a port Windows hands out for outgoing traffic: almost
    /// always a program's own end of a conversation it started, such as a name
    /// lookup, rather than a service waiting for callers. Still listed, since
    /// UDP takes whatever arrives, but a page may fold these away.
    /// </summary>
    public bool IsLikelyClientEnd => Protocol == TransportProtocol.Udp && Port >= Listeners.DynamicPortsFrom;
}

/// <summary>
/// Programs on this PC accepting connections, and on which ports, from the
/// IP Helper's listener tables, which a standard user may read in full.
/// </summary>
/// <remarks>
/// Measured on 2026-10-05 as a standard user: 44 TCP listeners on IPv4 and 18
/// on IPv6, 41 UDP sockets on IPv4 and 21 on IPv6, every one with its owner -
/// System for SMB on 139 and 445. A UDP socket has no "listening" state: every
/// bound UDP socket is listed, since any of them takes what is sent to it.
/// </remarks>
public static unsafe class Listeners
{
    /// <summary>The start of Windows' default range of ports for outgoing traffic.</summary>
    public const int DynamicPortsFrom = 49152;

    private const uint AddressFamilyIpv4 = 2;   // AF_INET
    private const uint AddressFamilyIpv6 = 23;  // AF_INET6
    private const uint NoError = 0;
    private const uint InsufficientBuffer = 122;

    /// <summary>Who could reach a socket bound to an address.</summary>
    public static ListenReach ReachOf(string? localAddress)
    {
        if (!IPAddress.TryParse(localAddress, out IPAddress? address))
        {
            return ListenReach.OneAddress;
        }

        if (address.Equals(IPAddress.Any) || address.Equals(IPAddress.IPv6Any))
        {
            return ListenReach.AllNetworks;
        }

        return LoopbackEnds.IsLoopback(address) ? ListenReach.ThisPcOnly : ListenReach.OneAddress;
    }

    /// <summary>Every TCP listener and bound UDP socket, both families. Empty when the tables cannot be read.</summary>
    public static IReadOnlyList<ListeningSocket> Read()
    {
        List<ListeningSocket> sockets = [];
        byte[] buffer = new byte[32 * 1024];

        if (Fill(ref buffer, tcp: true, AddressFamilyIpv4))
        {
            fixed (byte* start = buffer)
            {
                MIB_TCPTABLE_OWNER_PID* table = (MIB_TCPTABLE_OWNER_PID*)start;

                foreach (ref readonly MIB_TCPROW_OWNER_PID row in table->table.AsSpan((int)table->dwNumEntries))
                {
                    sockets.Add(new ListeningSocket((int)row.dwOwningPid, TransportProtocol.Tcp, false, new IPAddress(row.dwLocalAddr).ToString(), Port(row.dwLocalPort)));
                }
            }
        }

        if (Fill(ref buffer, tcp: true, AddressFamilyIpv6))
        {
            fixed (byte* start = buffer)
            {
                MIB_TCP6TABLE_OWNER_PID* table = (MIB_TCP6TABLE_OWNER_PID*)start;

                foreach (ref readonly MIB_TCP6ROW_OWNER_PID row in table->table.AsSpan((int)table->dwNumEntries))
                {
                    sockets.Add(new ListeningSocket((int)row.dwOwningPid, TransportProtocol.Tcp, true, new IPAddress(row.ucLocalAddr.AsReadOnlySpan(), row.dwLocalScopeId).ToString(), Port(row.dwLocalPort)));
                }
            }
        }

        if (Fill(ref buffer, tcp: false, AddressFamilyIpv4))
        {
            fixed (byte* start = buffer)
            {
                MIB_UDPTABLE_OWNER_PID* table = (MIB_UDPTABLE_OWNER_PID*)start;

                foreach (ref readonly MIB_UDPROW_OWNER_PID row in table->table.AsSpan((int)table->dwNumEntries))
                {
                    sockets.Add(new ListeningSocket((int)row.dwOwningPid, TransportProtocol.Udp, false, new IPAddress(row.dwLocalAddr).ToString(), Port(row.dwLocalPort)));
                }
            }
        }

        if (Fill(ref buffer, tcp: false, AddressFamilyIpv6))
        {
            fixed (byte* start = buffer)
            {
                MIB_UDP6TABLE_OWNER_PID* table = (MIB_UDP6TABLE_OWNER_PID*)start;

                foreach (ref readonly MIB_UDP6ROW_OWNER_PID row in table->table.AsSpan((int)table->dwNumEntries))
                {
                    sockets.Add(new ListeningSocket((int)row.dwOwningPid, TransportProtocol.Udp, true, new IPAddress(row.ucLocalAddr.AsReadOnlySpan(), row.dwLocalScopeId).ToString(), Port(row.dwLocalPort)));
                }
            }
        }

        return sockets;
    }

    private static bool Fill(ref byte[] buffer, bool tcp, uint family)
    {
        for (int attempt = 0; attempt < 3; attempt++)
        {
            uint size = (uint)buffer.Length;

            uint result = tcp
                ? PInvoke.GetExtendedTcpTable(buffer, ref size, true, family, TCP_TABLE_CLASS.TCP_TABLE_OWNER_PID_LISTENER, 0)
                : PInvoke.GetExtendedUdpTable(buffer, ref size, true, family, UDP_TABLE_CLASS.UDP_TABLE_OWNER_PID, 0);

            if (result == NoError)
            {
                return true;
            }

            if (result != InsufficientBuffer)
            {
                return false;
            }

            buffer = new byte[Math.Max(size + 4096, (uint)buffer.Length * 2)];
        }

        return false;
    }

    /// <summary>A port from the table, which keeps it in network byte order in the low 16 bits.</summary>
    internal static int Port(uint value) => (int)(((value & 0xFF) << 8) | ((value >> 8) & 0xFF));
}
