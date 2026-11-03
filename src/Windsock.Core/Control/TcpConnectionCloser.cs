using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using Windsock.Core.Processes;

namespace Windsock.Core.Control;

/// <summary>How asking to close one connection went.</summary>
public enum CloseOutcome
{
    /// <summary>Windows reset it; both ends have been told.</summary>
    Closed,

    /// <summary>Only an administrator may close a connection, and Windsock is not running as one.</summary>
    NeedsAdministrator,

    /// <summary>An IPv6 connection: Windows offers no way to close one from outside its program.</summary>
    NotOnIpv6,

    /// <summary>Not a TCP connection that is talking: UDP has no connection, and a listener is not one.</summary>
    NotAConnection,

    /// <summary>Windows refused for another reason, such as the connection having closed already.</summary>
    Failed,
}

/// <summary>What happened to one connection.</summary>
public sealed record CloseResult(CloseOutcome Outcome, string Message);

/// <summary>
/// Closes one TCP connection, as TCPView does, by asking Windows to delete its
/// control block. Full edition, as administrator.
/// </summary>
/// <remarks>
/// <para>
/// <c>SetTcpEntry</c> with <c>MIB_TCP_STATE_DELETE_TCB</c> (12) is the only
/// change it accepts. The far end is sent a reset, and the program holding the
/// connection sees it fail; a program that reconnects at once will.
/// </para>
/// <para>
/// IPv4 only: <c>iphlpapi.h</c> declares nothing like it for IPv6 (checked
/// against the 10.0.26100 SDK). An IPv6 connection can only be ended by ending
/// its program. Refused with access denied unless the caller is elevated, so
/// that is checked first rather than tried.
/// </para>
/// <para>
/// Declared here rather than through CsWin32 so that the call is not in the
/// Store edition at all, which is built without this folder.
/// </para>
/// </remarks>
public static partial class TcpConnectionCloser
{
    private const uint DeleteTcb = 12;
    private const uint AccessDenied = 5;
    private const uint RunningUnelevated = 317;   // documented for SetTcpEntry as "not elevated"

    /// <summary>Closes one connection, after checking it can be.</summary>
    public static CloseResult Close(ProcessConnection connection)
    {
        ArgumentNullException.ThrowIfNull(connection);

        if (CannotClose(connection) is { } refused)
        {
            return refused;
        }

        if (!Environment.IsPrivilegedProcess)
        {
            return new CloseResult(
                CloseOutcome.NeedsAdministrator,
                "Closing a connection needs administrator rights. Restart Windsock as administrator to do it.");
        }

        if (!TryRow(connection, out TcpRow row))
        {
            return new CloseResult(CloseOutcome.Failed, "The connection's addresses could not be read.");
        }

        uint result = SetTcpEntry(ref row);

        return result switch
        {
            0 => new CloseResult(CloseOutcome.Closed, "Closed. The far end was sent a reset."),
            AccessDenied or RunningUnelevated => new CloseResult(CloseOutcome.NeedsAdministrator, "Windows refused: closing a connection needs administrator rights."),
            _ => new CloseResult(
                CloseOutcome.Failed,
                string.Concat("Windows refused (error ", result.ToString(System.Globalization.CultureInfo.InvariantCulture), "); the connection may have closed already.")),
        };
    }

    /// <summary>Why a connection cannot be closed at all, whatever the rights, or null when it can.</summary>
    public static CloseResult? CannotClose(ProcessConnection connection)
    {
        ArgumentNullException.ThrowIfNull(connection);

        if (connection.Protocol != TransportProtocol.Tcp || connection.IsListening || connection.RemoteAddress is null)
        {
            return new CloseResult(CloseOutcome.NotAConnection, "Only a TCP connection that is talking can be closed; this is not one.");
        }

        if (connection.IsIpv6
            && !(IPAddress.TryParse(connection.RemoteAddress, out IPAddress? remote) && remote.IsIPv4MappedToIPv6))
        {
            return new CloseResult(
                CloseOutcome.NotOnIpv6,
                "Windows offers no way to close an IPv6 connection from outside its program. Ending the program closes it.");
        }

        return null;
    }

    /// <summary>
    /// The <c>MIB_TCPROW</c> for a connection: addresses as their bytes in
    /// network order, ports in network order in the low 16 bits.
    /// </summary>
    internal static bool TryRow(ProcessConnection connection, out TcpRow row)
    {
        row = default;

        if (!IPAddress.TryParse(connection.LocalAddress, out IPAddress? local)
            || !IPAddress.TryParse(connection.RemoteAddress, out IPAddress? remote))
        {
            return false;
        }

        if (local.IsIPv4MappedToIPv6)
        {
            local = local.MapToIPv4();
        }

        if (remote.IsIPv4MappedToIPv6)
        {
            remote = remote.MapToIPv4();
        }

        if (local.AddressFamily != AddressFamily.InterNetwork || remote.AddressFamily != AddressFamily.InterNetwork)
        {
            return false;
        }

        row = new TcpRow
        {
            State = DeleteTcb,
            LocalAddr = Packed(local),
            LocalPort = Port(connection.LocalPort),
            RemoteAddr = Packed(remote),
            RemotePort = Port(connection.RemotePort),
        };

        return true;
    }

    /// <summary>An IPv4 address as the table keeps it: its four bytes in order, read as a native integer.</summary>
    internal static uint Packed(IPAddress address)
    {
        Span<byte> bytes = stackalloc byte[4];
        address.TryWriteBytes(bytes, out _);
        return BinaryPrimitives.ReadUInt32LittleEndian(bytes);
    }

    /// <summary>A port in network byte order, in the low 16 bits.</summary>
    internal static uint Port(int port) => (uint)(((port & 0xFF) << 8) | ((port >> 8) & 0xFF));

    [LibraryImport("iphlpapi.dll")]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static partial uint SetTcpEntry(ref TcpRow row);

    /// <summary>MIB_TCPROW: five DWORDs, 20 bytes.</summary>
    [StructLayout(LayoutKind.Sequential)]
    internal struct TcpRow
    {
        public uint State;
        public uint LocalAddr;
        public uint LocalPort;
        public uint RemoteAddr;
        public uint RemotePort;
    }
}
