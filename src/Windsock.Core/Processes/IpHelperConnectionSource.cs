using Windows.Win32;
using Windows.Win32.NetworkManagement.IpHelper;

namespace Windsock.Core.Processes;

/// <summary>
/// Counts the sockets each process holds open, through the IP Helper connection
/// tables.
/// </summary>
public sealed unsafe class IpHelperConnectionSource : IProcessConnectionSource
{
    private const uint AddressFamilyIpv4 = 2;   // AF_INET
    private const uint AddressFamilyIpv6 = 23;  // AF_INET6

    private const uint NoError = 0;
    private const uint InsufficientBuffer = 122;  // ERROR_INSUFFICIENT_BUFFER

    private const uint TcpStateListen = 2;
    private byte[] _buffer = new byte[32 * 1024];

    /// <inheritdoc />
    public void CopyTo(Dictionary<int, SocketCounts> destination)
    {
        destination.Clear();

        ReadTcp(destination, AddressFamilyIpv4);
        ReadTcp(destination, AddressFamilyIpv6);
        ReadUdp(destination, AddressFamilyIpv4);
        ReadUdp(destination, AddressFamilyIpv6);
    }

    private void ReadTcp(Dictionary<int, SocketCounts> destination, uint family)
    {
        if (!Fill(tcp: true, family))
        {
            return;
        }

        fixed (byte* buffer = _buffer)
        {
            if (family == AddressFamilyIpv6)
            {
                MIB_TCP6TABLE_OWNER_PID* table = (MIB_TCP6TABLE_OWNER_PID*)buffer;

                foreach (ref readonly MIB_TCP6ROW_OWNER_PID row in table->table.AsSpan((int)table->dwNumEntries))
                {
                    if ((uint)row.dwState != TcpStateListen)
                    {
                        Increment(destination, (int)row.dwOwningPid, tcp: true);
                    }
                }
            }
            else
            {
                MIB_TCPTABLE_OWNER_PID* table = (MIB_TCPTABLE_OWNER_PID*)buffer;

                foreach (ref readonly MIB_TCPROW_OWNER_PID row in table->table.AsSpan((int)table->dwNumEntries))
                {
                    if ((uint)row.dwState != TcpStateListen)
                    {
                        Increment(destination, (int)row.dwOwningPid, tcp: true);
                    }
                }
            }
        }
    }

    private void ReadUdp(Dictionary<int, SocketCounts> destination, uint family)
    {
        if (!Fill(tcp: false, family))
        {
            return;
        }

        fixed (byte* buffer = _buffer)
        {
            if (family == AddressFamilyIpv6)
            {
                MIB_UDP6TABLE_OWNER_PID* table = (MIB_UDP6TABLE_OWNER_PID*)buffer;

                foreach (ref readonly MIB_UDP6ROW_OWNER_PID row in table->table.AsSpan((int)table->dwNumEntries))
                {
                    Increment(destination, (int)row.dwOwningPid, tcp: false);
                }
            }
            else
            {
                MIB_UDPTABLE_OWNER_PID* table = (MIB_UDPTABLE_OWNER_PID*)buffer;

                foreach (ref readonly MIB_UDPROW_OWNER_PID row in table->table.AsSpan((int)table->dwNumEntries))
                {
                    Increment(destination, (int)row.dwOwningPid, tcp: false);
                }
            }
        }
    }

    private bool Fill(bool tcp, uint family)
    {
        for (int attempt = 0; attempt < 3; attempt++)
        {
            uint size = (uint)_buffer.Length;

            uint result = tcp
                ? PInvoke.GetExtendedTcpTable(
                    _buffer, ref size, false, family, TCP_TABLE_CLASS.TCP_TABLE_OWNER_PID_ALL, 0)
                : PInvoke.GetExtendedUdpTable(
                    _buffer, ref size, false, family, UDP_TABLE_CLASS.UDP_TABLE_OWNER_PID, 0);

            if (result == NoError)
            {
                return true;
            }

            if (result != InsufficientBuffer)
            {
                return false;
            }
            _buffer = new byte[Math.Max(size, (uint)_buffer.Length * 2)];
        }

        return false;
    }

    private static void Increment(Dictionary<int, SocketCounts> destination, int processId, bool tcp)
    {
        if (processId <= 0)
        {
            return;
        }

        destination.TryGetValue(processId, out SocketCounts counts);

        destination[processId] = tcp
            ? counts with { Tcp = counts.Tcp + 1 }
            : counts with { Udp = counts.Udp + 1 };
    }
}
