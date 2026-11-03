using System.Collections.Concurrent;
using System.Net;

namespace Windsock.Core.Home;

/// <summary>
/// Looks for every device on a home link by asking, address by address, who
/// holds it: one ARP request each, which never leaves the link.
/// </summary>
/// <remarks>
/// The neighbour table only knows the hosts this PC has talked to lately, so a
/// device that never talks to it stays unseen until something asks. This is
/// the asking, and it is only ever done because the person pressed for it: it
/// is a scan, if a polite one, and security software on a shared network would
/// say so. Each answer also lands in the neighbour table, where the ordinary
/// reading finds it. A link wider than a /22 (1,022 hosts) is not swept.
/// </remarks>
public static class ArpSweep
{
    /// <summary>How many addresses are asked about at once; each ask blocks while it waits.</summary>
    private const int Askers = 32;

    /// <summary>Every device that answered, by address.</summary>
    public static async Task<IReadOnlyList<(IPAddress Address, MacAddress Mac)>> SweepAsync(
        INeighbourSource neighbours,
        HomeLink link,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(neighbours);
        ArgumentNullException.ThrowIfNull(link);

        if (link.Address is not { } own)
        {
            return [];
        }

        ConcurrentQueue<IPAddress> waiting = new(Subnets.Hosts(own, link.PrefixLength).Where(each => !each.Equals(own)));
        ConcurrentBag<(IPAddress, MacAddress)> found = [];

        void Ask()
        {
            while (!cancellationToken.IsCancellationRequested && waiting.TryDequeue(out IPAddress? target))
            {
                if (neighbours.Ask(target, own) is { } mac && mac.IsUnicast)
                {
                    found.Add((target, mac));
                }
            }
        }

        Task[] askers = [.. Enumerable.Range(0, Math.Min(Askers, Math.Max(1, waiting.Count)))
            .Select(_ => Task.Factory.StartNew(Ask, CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default))];

        await Task.WhenAll(askers).ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();

        return [.. found.OrderBy(each => Subnets.ToNumber(each.Item1))];
    }
}
