using System.Globalization;

namespace Windsock.Core.Networking;

/// <summary>One far end to be placed on the graph, and the route to it.</summary>
public sealed record RouteLeaf(string Key, IReadOnlyList<PathHop> Route);

/// <summary>
/// One point in the picture of where a process's traffic goes: the process
/// itself, a router on the way, or a far end.
/// </summary>
public sealed class RouteNode
{
    private readonly List<RouteNode> _branches = [];
    private readonly List<string> _reaching = [];

    internal RouteNode(string id, string? address, int distance, int silences)
    {
        Id = id;
        Address = address;
        Distance = distance;
        Silences = silences;
    }

    /// <summary>
    /// What this node is, held to between rebuilds.
    /// </summary>
    public string Id { get; }

    /// <summary>
    /// The router that answered here, or null where nothing did.
    /// </summary>
    public string? Address { get; }

    /// <summary>How many hops out from the process this node sits.</summary>
    public int Distance { get; }

    /// <summary>
    /// How many unanswered hops this node stands in for.
    /// </summary>
    public int Silences { get; }

    /// <summary>The quickest this hop has been seen to answer.</summary>
    public TimeSpan? RoundTrip { get; internal set; }

    /// <summary>The peer this node is, or null when it is only on the way.</summary>
    public string? LeafKey { get; internal set; }

    /// <summary>What continues on from here, nearest first.</summary>
    public IReadOnlyList<RouteNode> Children => _branches;

    /// <summary>
    /// Every peer at or below this node.
    /// </summary>
    public IReadOnlyList<string> Reach => _reaching;

    /// <summary>Whether this node stands in for hops that never answered.</summary>
    public bool IsSilent => Silences > 0;

    /// <summary>Whether a peer sits here.</summary>
    public bool IsPeer => LeafKey is not null;
    internal List<RouteNode> Branches => _branches;
    internal List<string> Reaching => _reaching;
}

/// <summary>
/// Folds the routes to many peers into the one picture they make together.
/// </summary>
public static class RouteTree
{
    /// <summary>
    /// Builds the picture the given peers and their routes make together.
    /// </summary>
    public static RouteNode Build(IEnumerable<RouteLeaf> leaves)
    {
        ArgumentNullException.ThrowIfNull(leaves);

        RouteNode root = new(string.Empty, null, 0, 0);
        HashSet<string> placed = new(StringComparer.Ordinal);

        foreach (RouteLeaf leaf in leaves)
        {
            if (leaf is null || leaf.Key.Length == 0 || !placed.Add(leaf.Key))
            {
                continue;
            }

            Graft(root, leaf);
        }

        Gather(root);
        return root;
    }

    /// <summary>
    /// The part of several routes that all of them agree on.
    /// </summary>
    public static IReadOnlyList<PathHop> Shared(IReadOnlyList<IReadOnlyList<PathHop>> routes)
    {
        ArgumentNullException.ThrowIfNull(routes);

        if (routes.Count == 0)
        {
            return [];
        }

        if (routes.Count == 1)
        {
            return routes[0] ?? [];
        }

        List<PathHop> agreed = [];

        for (int distance = 0; ; distance++)
        {
            PathHop? first = null;
            bool arrived = true;

            foreach (IReadOnlyList<PathHop> route in routes)
            {
                if (route is null || distance >= route.Count)
                {
                    return agreed;
                }

                PathHop hop = route[distance];

                if (first is null)
                {
                    first = hop;
                }
                else if (!string.Equals(first.Address, hop.Address, StringComparison.Ordinal))
                {
                    return agreed;
                }

                arrived &= hop.IsDestination;
            }

            if (first is null)
            {
                return agreed;
            }
            agreed.Add(first.IsDestination == arrived ? first : first with { IsDestination = arrived });

            if (arrived)
            {
                return agreed;
            }
        }
    }

    private static void Graft(RouteNode root, RouteLeaf leaf)
    {
        IReadOnlyList<PathHop> route = leaf.Route ?? [];
        int last = route.Count;
        string? arrival = null;
        if (last > 0 && route[last - 1].IsDestination)
        {
            arrival = route[last - 1].Address;
            last--;
        }
        if (arrival is null)
        {
            while (last > 0 && route[last - 1].Address is null)
            {
                last--;
            }
        }

        RouteNode at = root;
        HashSet<string> walked = new(StringComparer.Ordinal);
        int index = 0;

        while (index < last)
        {
            PathHop hop = route[index];

            if (hop.Address is null)
            {
                int silences = 0;

                while (index + silences < last && route[index + silences].Address is null)
                {
                    silences++;
                }

                at = Descend(at, null, silences, null);
                index += silences;
                continue;
            }
            if (!walked.Add(hop.Address))
            {
                break;
            }

            at = Descend(at, hop.Address, 1, hop.RoundTrip);
            index++;
        }

        Attach(at, leaf.Key, arrival);
    }

    private static RouteNode Descend(RouteNode parent, string? address, int steps, TimeSpan? round)
    {
        RouteNode? found = null;

        foreach (RouteNode child in parent.Branches)
        {
            bool same = address is null
                ? child.Address is null && child.Silences == steps
                : string.Equals(child.Address, address, StringComparison.Ordinal);

            if (same)
            {
                found = child;
                break;
            }
        }

        if (found is null)
        {
            string id = address is null
                ? parent.Id + "/*" + steps.ToString(CultureInfo.InvariantCulture)
                : parent.Id + "/" + address;

            found = new RouteNode(id, address, parent.Distance + steps, address is null ? steps : 0);
            parent.Branches.Add(found);
        }

        if (round is { } took && (found.RoundTrip is not { } already || took < already))
        {
            found.RoundTrip = took;
        }

        return found;
    }

    private static void Attach(RouteNode parent, string key, string? arrival)
    {
        if (arrival is not null)
        {
            foreach (RouteNode child in parent.Branches)
            {
                if (child.LeafKey is null && string.Equals(child.Address, arrival, StringComparison.Ordinal))
                {
                    child.LeafKey = key;
                    return;
                }
            }
        }
        parent.Branches.Add(new RouteNode("peer:" + key, arrival, parent.Distance + 1, 0) { LeafKey = key });
    }

    private static void Gather(RouteNode node)
    {
        if (node.LeafKey is { } key)
        {
            node.Reaching.Add(key);
        }

        foreach (RouteNode child in node.Branches)
        {
            Gather(child);
            node.Reaching.AddRange(child.Reach);
        }
    }
}
