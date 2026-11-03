using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;
using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.NetworkManagement.IpHelper;
using Windows.Win32.Networking.WinSock;

namespace Windsock.Core.Home;

/// <summary>Reads the neighbour table through IP Helper's <c>GetIpNetTable2</c>.</summary>
/// <remarks>
/// Reading sends nothing and needs no elevation. The table holds only the
/// hosts this PC has talked to lately, so it is not a scan of the network;
/// <see cref="Ask"/> is what adds one more, by asking for it.
/// </remarks>
public sealed class IpHelperNeighbours : INeighbourSource
{
    /// <inheritdoc />
    public unsafe IReadOnlyList<Neighbour> Read()
    {
        MIB_IPNET_TABLE2* table = null;

        try
        {
            if (PInvoke.GetIpNetTable2(ADDRESS_FAMILY.AF_UNSPEC, out table) != WIN32_ERROR.NO_ERROR || table is null)
            {
                return [];
            }

            List<Neighbour> rows = new((int)table->NumEntries);
            ReadOnlySpan<MIB_IPNET_ROW2> all = table->Table.AsSpan((int)table->NumEntries);

            foreach (ref readonly MIB_IPNET_ROW2 row in all)
            {
                if (row.PhysicalAddressLength != MacAddress.Length
                    || Address(row.Address) is not { } address
                    || MacAddress.From(row.PhysicalAddress.AsReadOnlySpan()[..MacAddress.Length]) is not { } mac)
                {
                    continue;
                }

                rows.Add(new Neighbour(
                    address,
                    mac,
                    row.InterfaceIndex,
                    (NeighbourState)(int)row.State,
                    row.IsRouter,
                    row.IsUnreachable));
            }

            return rows;
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException)
        {
            return [];
        }
        finally
        {
            if (table is not null)
            {
                PInvoke.FreeMibTable(table);
            }
        }
    }

    /// <inheritdoc />
    public MacAddress? Ask(IPAddress target, IPAddress from)
    {
        ArgumentNullException.ThrowIfNull(target);
        ArgumentNullException.ThrowIfNull(from);

        if (Network(target) is not { } destination || Network(from) is not { } source)
        {
            return null;
        }

        Span<byte> octets = stackalloc byte[8];
        uint length = (uint)octets.Length;

        try
        {
            return PInvoke.SendARP(destination, source, octets, ref length) == 0 && length == MacAddress.Length
                ? MacAddress.From(octets[..MacAddress.Length])
                : null;
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException)
        {
            return null;
        }
    }

    /// <summary>An IPv4 address as IP Helper takes it: the octets in memory order, read as a little-endian number.</summary>
    private static uint? Network(IPAddress address)
    {
        if (address.AddressFamily != AddressFamily.InterNetwork)
        {
            return null;
        }

        Span<byte> octets = stackalloc byte[4];
        address.TryWriteBytes(octets, out _);
        return BinaryPrimitives.ReadUInt32LittleEndian(octets);
    }

    private static unsafe IPAddress? Address(in SOCKADDR_INET socket)
    {
        fixed (SOCKADDR_INET* pointer = &socket)
        {
            return Adapters.SocketAddressReader.Read(new ReadOnlySpan<byte>(pointer, sizeof(SOCKADDR_INET)));
        }
    }
}
