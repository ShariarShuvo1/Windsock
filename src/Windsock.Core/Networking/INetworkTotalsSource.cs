namespace Windsock.Core.Networking;

/// <summary>
/// Supplies cumulative byte counters. Rates are derived by the monitor from the
/// difference between consecutive reads, so implementations only report totals.
/// </summary>
public interface INetworkTotalsSource
{
    NetworkTotals ReadTotals();
}

/// <summary>Lists the machine's real network adapters.</summary>
public interface INetworkAdapterSource
{
    IReadOnlyList<NetworkAdapter> ListAdapters();
}
