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
    private readonly Lock _gate = new();
    private readonly AdapterLedger _ledger = new();
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

    /// <summary>
    /// Everything the counted adapters have moved since this source was made.
    /// </summary>
    /// <remarks>
    /// Not the sum of their counters: see <see cref="AdapterLedger"/> for why
    /// that sum jumps by an adapter's whole lifetime when it comes back up.
    /// </remarks>
    public unsafe NetworkTotals ReadTotals()
    {
        lock (_gate)
        {
            MIB_IF_TABLE2* table = null;
            WIN32_ERROR result = PInvoke.GetIfTable2(&table);

            // Nothing read is nothing moved. Returning zero here would make the
            // next good reading look like everything since this source began.
            if (result != WIN32_ERROR.NO_ERROR || table is null)
            {
                return _ledger.Total;
            }

            ulong[] selection = _selection;

            try
            {
                ReadOnlySpan<MIB_IF_ROW2> rows = table->Table.AsSpan((int)table->NumEntries);

                _ledger.Begin();

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

                    _ledger.Count(row.InterfaceLuid.Value, (long)row.InOctets, (long)row.OutOctets);
                }

                return _ledger.End();
            }
            finally
            {
                PInvoke.FreeMibTable(table);
            }
        }
    }
}
