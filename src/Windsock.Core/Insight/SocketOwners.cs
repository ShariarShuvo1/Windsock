using System.Globalization;
using System.Net;
using Windows.Win32;
using Windows.Win32.NetworkManagement.IpHelper;
using Windsock.Core.Processes;

namespace Windsock.Core.Insight;

/// <summary>
/// Which module opened a socket: for a socket of a service host, the service.
/// </summary>
/// <param name="Module">The service's key name for a service, the file name for a program; null when Windows refused.</param>
/// <param name="Refused">Whether Windows would not say, which a protected host does to a standard user.</param>
public readonly record struct SocketOwner(string? Module, bool Refused);

/// <summary>
/// Puts each socket of a process to the module that opened it, through the
/// IP Helper's owner-module lookup.
/// </summary>
/// <remarks>
/// <para>
/// This is how a connection of a shared service host is put to one of its
/// services. Measured on 2026-10-05 as a standard user: 115 of 130 TCP sockets
/// named, among them <c>DoSvc</c>, <c>RpcSs</c>, <c>Schedule</c> and
/// <c>CDPSvc</c> inside their service hosts; 6 refused with access denied
/// (lsass, which runs KeyIso, SamSs and VaultSvc, and three other protected
/// processes); 9 had closed between the table and the lookup. So the
/// expectation that shared hosts need administrator to split held only for
/// protected ones.
/// </para>
/// <para>
/// What it cannot do is split the host's bytes: the kernel trace counts a
/// process, not a socket's service. Asked only for the processes a page shows
/// a breakdown for, since each lookup is a call of its own.
/// </para>
/// </remarks>
public sealed unsafe class SocketOwners
{
    private const uint AddressFamilyIpv4 = 2;   // AF_INET
    private const uint AddressFamilyIpv6 = 23;  // AF_INET6
    private const uint NoError = 0;
    private const uint InsufficientBuffer = 122;  // ERROR_INSUFFICIENT_BUFFER
    private const uint AccessDenied = 5;          // ERROR_ACCESS_DENIED
    private const TCPIP_OWNER_MODULE_INFO_CLASS BasicInfo = TCPIP_OWNER_MODULE_INFO_CLASS.TCPIP_OWNER_MODULE_INFO_BASIC;

    private readonly Lock _gate = new();
    private byte[] _buffer = new byte[64 * 1024];

    /// <summary>
    /// The owner of each socket a process holds, by <see cref="ProcessConnection.Key"/>.
    /// Sockets that closed meanwhile are left out.
    /// </summary>
    public IReadOnlyDictionary<string, SocketOwner> Read(int processId)
    {
        Dictionary<string, SocketOwner> owners = new(StringComparer.Ordinal);

        if (processId <= 0)
        {
            return owners;
        }

        lock (_gate)
        {
            ReadTcp4(processId, owners);
            ReadTcp6(processId, owners);
            ReadUdp4(processId, owners);
            ReadUdp6(processId, owners);
        }

        return owners;
    }

    private void ReadTcp4(int processId, Dictionary<string, SocketOwner> into)
    {
        if (!Fill(tcp: true, AddressFamilyIpv4))
        {
            return;
        }

        fixed (byte* buffer = _buffer)
        {
            MIB_TCPTABLE_OWNER_MODULE* table = (MIB_TCPTABLE_OWNER_MODULE*)buffer;

            fixed (MIB_TCPROW_OWNER_MODULE* rows = table->table.AsSpan((int)table->dwNumEntries))
            {
                for (int at = 0; at < table->dwNumEntries; at++)
                {
                    MIB_TCPROW_OWNER_MODULE* row = rows + at;

                    if ((int)row->dwOwningPid == processId && Ask(row, &PInvoke.GetOwnerModuleFromTcpEntry) is { } owner)
                    {
                        into[Key(processId, TransportProtocol.Tcp, Address(row->dwLocalAddr), Port(row->dwLocalPort), Address(row->dwRemoteAddr), Port(row->dwRemotePort))] = owner;
                    }
                }
            }
        }
    }

    private void ReadTcp6(int processId, Dictionary<string, SocketOwner> into)
    {
        if (!Fill(tcp: true, AddressFamilyIpv6))
        {
            return;
        }

        fixed (byte* buffer = _buffer)
        {
            MIB_TCP6TABLE_OWNER_MODULE* table = (MIB_TCP6TABLE_OWNER_MODULE*)buffer;

            fixed (MIB_TCP6ROW_OWNER_MODULE* rows = table->table.AsSpan((int)table->dwNumEntries))
            {
                for (int at = 0; at < table->dwNumEntries; at++)
                {
                    MIB_TCP6ROW_OWNER_MODULE* row = rows + at;

                    if ((int)row->dwOwningPid == processId && Ask(row, &PInvoke.GetOwnerModuleFromTcp6Entry) is { } owner)
                    {
                        into[Key(
                            processId,
                            TransportProtocol.Tcp,
                            Address(row->ucLocalAddr.AsReadOnlySpan(), row->dwLocalScopeId),
                            Port(row->dwLocalPort),
                            Address(row->ucRemoteAddr.AsReadOnlySpan(), row->dwRemoteScopeId),
                            Port(row->dwRemotePort))] = owner;
                    }
                }
            }
        }
    }

    private void ReadUdp4(int processId, Dictionary<string, SocketOwner> into)
    {
        if (!Fill(tcp: false, AddressFamilyIpv4))
        {
            return;
        }

        fixed (byte* buffer = _buffer)
        {
            MIB_UDPTABLE_OWNER_MODULE* table = (MIB_UDPTABLE_OWNER_MODULE*)buffer;

            fixed (MIB_UDPROW_OWNER_MODULE* rows = table->table.AsSpan((int)table->dwNumEntries))
            {
                for (int at = 0; at < table->dwNumEntries; at++)
                {
                    MIB_UDPROW_OWNER_MODULE* row = rows + at;

                    if ((int)row->dwOwningPid == processId && Ask(row, &PInvoke.GetOwnerModuleFromUdpEntry) is { } owner)
                    {
                        into[Key(processId, TransportProtocol.Udp, Address(row->dwLocalAddr), Port(row->dwLocalPort), null, 0)] = owner;
                    }
                }
            }
        }
    }

    private void ReadUdp6(int processId, Dictionary<string, SocketOwner> into)
    {
        if (!Fill(tcp: false, AddressFamilyIpv6))
        {
            return;
        }

        fixed (byte* buffer = _buffer)
        {
            MIB_UDP6TABLE_OWNER_MODULE* table = (MIB_UDP6TABLE_OWNER_MODULE*)buffer;

            fixed (MIB_UDP6ROW_OWNER_MODULE* rows = table->table.AsSpan((int)table->dwNumEntries))
            {
                for (int at = 0; at < table->dwNumEntries; at++)
                {
                    MIB_UDP6ROW_OWNER_MODULE* row = rows + at;

                    if ((int)row->dwOwningPid == processId && Ask(row, &PInvoke.GetOwnerModuleFromUdp6Entry) is { } owner)
                    {
                        into[Key(
                            processId,
                            TransportProtocol.Udp,
                            Address(row->ucLocalAddr.AsReadOnlySpan(), row->dwLocalScopeId),
                            Port(row->dwLocalPort),
                            null,
                            0)] = owner;
                    }
                }
            }
        }
    }

    /// <summary>
    /// One socket's owner: a sizing call, then the call itself. Null when the
    /// socket closed in between or Windows had nothing to say.
    /// </summary>
    private static SocketOwner? Ask<TRow>(
        TRow* row,
        delegate*<TRow*, TCPIP_OWNER_MODULE_INFO_CLASS, void*, uint*, uint> lookup)
        where TRow : unmanaged
    {
        uint size = 0;
        uint first = lookup(row, BasicInfo, null, &size);

        if (first == AccessDenied)
        {
            return new SocketOwner(null, Refused: true);
        }

        if (first != InsufficientBuffer || size is 0 or > 64 * 1024)
        {
            return null;
        }

        byte[] info = new byte[size];

        fixed (byte* space = info)
        {
            uint result = lookup(row, BasicInfo, space, &size);

            if (result == AccessDenied)
            {
                return new SocketOwner(null, Refused: true);
            }

            if (result != NoError)
            {
                return null;
            }

            string name = ((TCPIP_OWNER_MODULE_BASIC_INFO*)space)->pModuleName.ToString();
            return string.IsNullOrWhiteSpace(name) ? null : new SocketOwner(name, Refused: false);
        }
    }

    private bool Fill(bool tcp, uint family)
    {
        for (int attempt = 0; attempt < 3; attempt++)
        {
            uint size = (uint)_buffer.Length;

            uint result = tcp
                ? PInvoke.GetExtendedTcpTable(_buffer, ref size, false, family, TCP_TABLE_CLASS.TCP_TABLE_OWNER_MODULE_ALL, 0)
                : PInvoke.GetExtendedUdpTable(_buffer, ref size, false, family, UDP_TABLE_CLASS.UDP_TABLE_OWNER_MODULE, 0);

            if (result == NoError)
            {
                return true;
            }

            if (result != InsufficientBuffer)
            {
                return false;
            }

            _buffer = new byte[Math.Max(size + 4096, (uint)_buffer.Length * 2)];
        }

        return false;
    }

    /// <summary>The same key <see cref="ProcessConnection.Key"/> builds, from the same parts.</summary>
    internal static string Key(int processId, TransportProtocol protocol, string local, int localPort, string? remote, int remotePort) =>
        string.Create(CultureInfo.InvariantCulture, $"{processId}|{protocol}|{local}|{localPort}|{remote}|{remotePort}");

    private static string Address(uint value) => new IPAddress(value).ToString();

    private static string Address(ReadOnlySpan<byte> value, uint scope) => new IPAddress(value, scope).ToString();

    private static int Port(uint value) => (int)(((value & 0xFF) << 8) | ((value >> 8) & 0xFF));
}
