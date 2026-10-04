namespace Windsock.Core.Networking;

/// <summary>
/// Supplies cumulative byte counters. Rates are derived by the monitor from the
/// difference between consecutive reads, so implementations only report totals.
/// </summary>
/// <remarks>
/// The difference between two reads is recorded as traffic, so a total may only
/// grow by bytes that moved in between. Adapters coming and going must not move
/// it.
/// </remarks>
public interface INetworkTotalsSource
{
    NetworkTotals ReadTotals();
}

/// <summary>Lists the machine's real network adapters.</summary>
public interface INetworkAdapterSource
{
    IReadOnlyList<NetworkAdapter> ListAdapters();
}
