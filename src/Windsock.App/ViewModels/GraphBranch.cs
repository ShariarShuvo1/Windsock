using Windsock.Core.Geography;

namespace Windsock.App.ViewModels;

/// <summary>
/// One node of the picture the graph draws, with everything that hangs off it.
/// </summary>
public sealed class GraphBranch
{
    public GraphBranch(string id, object? item, IReadOnlyList<GraphBranch> children)
    {
        Id = id;
        Item = item;
        Children = children;
    }

    /// <summary>Where this sits in the picture, held to between rebuilds.</summary>
    public string Id { get; }

    /// <summary>
    /// The peer or hop this node is, or null for the process at the middle.
    /// </summary>
    public object? Item { get; }

    /// <summary>What continues on from here.</summary>
    public IReadOnlyList<GraphBranch> Children { get; }

    /// <summary>
    /// Outbound traffic along the edge that leads here, in bytes per second.
    /// </summary>
    public double SendRate { get; init; }

    /// <summary>Inbound traffic along the edge that leads here.</summary>
    public double ReceiveRate { get; init; }

    /// <summary>Whether this node's traffic is measured at all.</summary>
    public bool HasRates { get; init; }

    /// <summary>Whether something else is being followed, and this is not on it.</summary>
    public bool IsMuted { get; init; }

    /// <summary>
    /// Where on the earth this node is, if anyone knows.
    /// </summary>
    public WorldPlace? Place { get; init; }

    /// <summary>Whether the reader may drag this node somewhere else.</summary>
    public bool CanMove => Children.Count == 0;
}
