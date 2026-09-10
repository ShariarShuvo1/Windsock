using System.Net;
using Windows.Win32;
using Windows.Win32.NetworkManagement.IpHelper;

namespace Windsock.Core.Processes;

/// <summary>
/// Reads a process's sockets in full, through the IP Helper connection tables.
/// </summary>
public sealed unsafe class IpHelperConnectionDetails : IProcessConnectionDetailSource
{
    private const int Anyone = 0;

    private const uint AddressFamilyIpv4 = 2;   // AF_INET
    private const uint AddressFamilyIpv6 = 23;  // AF_INET6

    private const uint NoError = 0;
    private const uint InsufficientBuffer = 122;  // ERROR_INSUFFICIENT_BUFFER

    private byte[] _buffer = new byte[64 * 1024];

    /// <inheritdoc />
    public IReadOnlyList<ProcessConnection> ReadAll()
    {
        List<ProcessConnection> connections = [];

        ReadTcp(connections, Anyone, AddressFamilyIpv4);
        ReadTcp(connections, Anyone, AddressFamilyIpv6);
        ReadUdp(connections, Anyone, AddressFamilyIpv4);
        ReadUdp(connections, Anyone, AddressFamilyIpv6);

        return connections;
    }

    public IReadOnlyList<ProcessConnection> Read(int processId)
    {
        List<ProcessConnection> connections = [];

        if (processId <= 0)
        {
            return connections;
        }

        ReadTcp(connections, processId, AddressFamilyIpv4);
        ReadTcp(connections, processId, AddressFamilyIpv6);
        ReadUdp(connections, processId, AddressFamilyIpv4);
        ReadUdp(connections, processId, AddressFamilyIpv6);

        return connections;
    }

    private void ReadTcp(List<ProcessConnection> into, int processId, uint family)
    {
        if (!Fill(tcp: true, family))
        {
            return;
        }

        fixed (byte* buffer = _buffer)
        {
            if (family == AddressFamilyIpv6)
            {
                MIB_TCP6TABLE_OWNER_MODULE* table = (MIB_TCP6TABLE_OWNER_MODULE*)buffer;

                foreach (ref readonly MIB_TCP6ROW_OWNER_MODULE row in table->table.AsSpan((int)table->dwNumEntries))
                {
                    if (processId != Anyone && (int)row.dwOwningPid != processId)
                    {
                        continue;
                    }

                    into.Add(new ProcessConnection(
                        (int)row.dwOwningPid,
                        TransportProtocol.Tcp,
                        IsIpv6: true,
                        Address(row.ucLocalAddr.AsReadOnlySpan(), row.dwLocalScopeId),
                        Port(row.dwLocalPort),
                        Address(row.ucRemoteAddr.AsReadOnlySpan(), row.dwRemoteScopeId),
                        Port(row.dwRemotePort),
                        (TcpConnectionState)(uint)row.dwState,
                        Created(row.liCreateTimestamp)));
                }
            }
            else
            {
                MIB_TCPTABLE_OWNER_MODULE* table = (MIB_TCPTABLE_OWNER_MODULE*)buffer;

                foreach (ref readonly MIB_TCPROW_OWNER_MODULE row in table->table.AsSpan((int)table->dwNumEntries))
                {
                    if (processId != Anyone && (int)row.dwOwningPid != processId)
                    {
                        continue;
                    }

                    into.Add(new ProcessConnection(
                        (int)row.dwOwningPid,
                        TransportProtocol.Tcp,
                        IsIpv6: false,
                        Address(row.dwLocalAddr),
                        Port(row.dwLocalPort),
                        Address(row.dwRemoteAddr),
                        Port(row.dwRemotePort),
                        (TcpConnectionState)(uint)row.dwState,
                        Created(row.liCreateTimestamp)));
                }
            }
        }
    }

    private void ReadUdp(List<ProcessConnection> into, int processId, uint family)
    {
        if (!Fill(tcp: false, family))
        {
            return;
        }

        fixed (byte* buffer = _buffer)
        {
            if (family == AddressFamilyIpv6)
            {
                MIB_UDP6TABLE_OWNER_MODULE* table = (MIB_UDP6TABLE_OWNER_MODULE*)buffer;

                foreach (ref readonly MIB_UDP6ROW_OWNER_MODULE row in table->table.AsSpan((int)table->dwNumEntries))
                {
                    if (processId != Anyone && (int)row.dwOwningPid != processId)
                    {
                        continue;
                    }
                    into.Add(new ProcessConnection(
                        (int)row.dwOwningPid,
                        TransportProtocol.Udp,
                        IsIpv6: true,
                        Address(row.ucLocalAddr.AsReadOnlySpan(), row.dwLocalScopeId),
                        Port(row.dwLocalPort),
                        RemoteAddress: null,
                        RemotePort: 0,
                        TcpConnectionState.Unknown,
                        Created(row.liCreateTimestamp)));
                }
            }
            else
            {
                MIB_UDPTABLE_OWNER_MODULE* table = (MIB_UDPTABLE_OWNER_MODULE*)buffer;

                foreach (ref readonly MIB_UDPROW_OWNER_MODULE row in table->table.AsSpan((int)table->dwNumEntries))
                {
                    if (processId != Anyone && (int)row.dwOwningPid != processId)
                    {
                        continue;
                    }

                    into.Add(new ProcessConnection(
                        (int)row.dwOwningPid,
                        TransportProtocol.Udp,
                        IsIpv6: false,
                        Address(row.dwLocalAddr),
                        Port(row.dwLocalPort),
                        RemoteAddress: null,
                        RemotePort: 0,
                        TcpConnectionState.Unknown,
                        Created(row.liCreateTimestamp)));
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
                    _buffer, ref size, false, family, TCP_TABLE_CLASS.TCP_TABLE_OWNER_MODULE_ALL, 0)
                : PInvoke.GetExtendedUdpTable(
                    _buffer, ref size, false, family, UDP_TABLE_CLASS.UDP_TABLE_OWNER_MODULE, 0);

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

    private static string Address(uint value) => new IPAddress(value).ToString();

    private static string Address(ReadOnlySpan<byte> value, uint scope) =>
        new IPAddress(value, scope).ToString();

    private static int Port(uint value) => (int)(((value & 0xFF) << 8) | ((value >> 8) & 0xFF));

    private static DateTimeOffset? Created(long timestamp) =>
        timestamp <= 0 ? null : DateTimeOffset.FromFileTime(timestamp);
}
