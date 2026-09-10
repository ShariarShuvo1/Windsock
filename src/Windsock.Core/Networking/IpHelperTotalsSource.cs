using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.NetworkManagement.IpHelper;

namespace Windsock.Core.Networking;

/// <summary>
/// Reads cumulative interface byte counters through the IP Helper
/// <c>GetIfTable2</c> API.
/// </summary>
public sealed class IpHelperTotalsSource : INetworkTotalsSource, INetworkAdapterSource
{
    // IF_OPER_STATUS.IfOperStatusUp
    private const uint OperationalStatusUp = 1;
    private volatile ulong[] _selection = [];

    /// <summary>
    /// Which adapters to count, or empty for every one.
    /// </summary>
    public ulong[] Selection
    {
        get => _selection;
        set => _selection = value ?? [];
    }

    internal static bool Counts(uint status, bool hardware, bool filter) =>
        status == OperationalStatusUp && hardware && !filter;

    /// <inheritdoc />
    public unsafe IReadOnlyList<NetworkAdapter> ListAdapters()
    {
        List<NetworkAdapter> adapters = [];
        MIB_IF_TABLE2* table = null;

        if (PInvoke.GetIfTable2(&table) != WIN32_ERROR.NO_ERROR || table is null)
        {
            return adapters;
        }

        try
        {
            ReadOnlySpan<MIB_IF_ROW2> rows = table->Table.AsSpan((int)table->NumEntries);

            foreach (ref readonly MIB_IF_ROW2 row in rows)
            {
                if (!row.InterfaceAndOperStatusFlags.HardwareInterface
                    || row.InterfaceAndOperStatusFlags.FilterInterface)
                {
                    continue;
                }

                adapters.Add(new NetworkAdapter(
                    row.InterfaceLuid.Value,
                    row.Alias.ToString(),
                    row.Description.ToString(),
                    (uint)row.OperStatus == OperationalStatusUp));
            }
        }
        finally
        {
            PInvoke.FreeMibTable(table);
        }

        return adapters;
    }

    public unsafe NetworkTotals ReadTotals()
    {
        MIB_IF_TABLE2* table = null;
        WIN32_ERROR result = PInvoke.GetIfTable2(&table);

        if (result != WIN32_ERROR.NO_ERROR || table is null)
        {
            return default;
        }

        ulong[] selection = _selection;

        try
        {
            long received = 0;
            long sent = 0;

            ReadOnlySpan<MIB_IF_ROW2> rows = table->Table.AsSpan((int)table->NumEntries);

            foreach (ref readonly MIB_IF_ROW2 row in rows)
            {
                if (!Counts(
                        (uint)row.OperStatus,
                        row.InterfaceAndOperStatusFlags.HardwareInterface,
                        row.InterfaceAndOperStatusFlags.FilterInterface))
                {
                    continue;
                }

                if (selection.Length > 0 && Array.IndexOf(selection, row.InterfaceLuid.Value) < 0)
                {
                    continue;
                }

                received += (long)row.InOctets;
                sent += (long)row.OutOctets;
            }

            return new NetworkTotals(received, sent);
        }
        finally
        {
            PInvoke.FreeMibTable(table);
        }
    }
}
