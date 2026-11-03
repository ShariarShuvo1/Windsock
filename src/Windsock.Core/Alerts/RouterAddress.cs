using System.Globalization;
using System.Net;
using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.NetworkManagement.IpHelper;
using Windows.Win32.Networking.WinSock;

namespace Windsock.Core.Alerts;

/// <summary>Finds a router's hardware address, for the router-changed alert.</summary>
public interface IRouterAddressSource
{
    /// <summary>
    /// The hardware address of a router on an interface, as "AA-BB-CC-DD-EE-FF",
    /// or nothing when Windows has no live entry for it. Never throws.
    /// </summary>
    string? HardwareAddressOf(IPAddress router, uint interfaceIndex);
}

/// <summary>
/// Reads a router's hardware address from the neighbour table Windows already
/// keeps, through IP Helper's <c>GetIpNetTable2</c>. Reading sends nothing.
/// </summary>
/// <remarks>
/// The router is always in the table while it carries traffic: every packet
/// leaving the network goes to it. Only entries Windows still believes - not
/// unreachable, not half-learned - are taken, so a router that just went
/// away is "not known", never "changed".
/// </remarks>
public sealed class NeighbourTableRouterAddress : IRouterAddressSource
{
    // NL_NEIGHBOR_STATE: NlnsProbe, the first state that holds a learned address.
    private const int Probe = 2;

    private const int MacLength = 6;

    /// <inheritdoc />
    public unsafe string? HardwareAddressOf(IPAddress router, uint interfaceIndex)
    {
        ArgumentNullException.ThrowIfNull(router);

        MIB_IPNET_TABLE2* table = null;

        try
        {
            ADDRESS_FAMILY family = router.AddressFamily == System.Net.Sockets.AddressFamily.InterNetworkV6
                ? ADDRESS_FAMILY.AF_INET6
                : ADDRESS_FAMILY.AF_INET;

            if (PInvoke.GetIpNetTable2(family, out table) != WIN32_ERROR.NO_ERROR || table is null)
            {
                return null;
            }

            ReadOnlySpan<MIB_IPNET_ROW2> rows = table->Table.AsSpan((int)table->NumEntries);

            foreach (ref readonly MIB_IPNET_ROW2 row in rows)
            {
                if ((interfaceIndex != 0 && row.InterfaceIndex != interfaceIndex)
                    || row.PhysicalAddressLength != MacLength
                    || (int)row.State < Probe
                    || row.IsUnreachable)
                {
                    continue;
                }

                if (!router.Equals(AddressOf(row.Address)))
                {
                    continue;
                }

                ReadOnlySpan<byte> mac = row.PhysicalAddress.AsReadOnlySpan()[..MacLength];
                return NetworkPrint.Normalise(string.Join('-', mac.ToArray().Select(octet => octet.ToString("X2", CultureInfo.InvariantCulture))));
            }

            return null;
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException)
        {
            return null;
        }
        finally
        {
            if (table is not null)
            {
                PInvoke.FreeMibTable(table);
            }
        }
    }

    private static unsafe IPAddress? AddressOf(in SOCKADDR_INET socket)
    {
        fixed (SOCKADDR_INET* pointer = &socket)
        {
            return Adapters.SocketAddressReader.Read(new ReadOnlySpan<byte>(pointer, sizeof(SOCKADDR_INET)));
        }
    }
}
