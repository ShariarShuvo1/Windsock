namespace Windsock.Core.Processes;

/// <summary>
/// Stands in for the kernel trace in the edition built without it: it counts
/// nothing, attributes nothing, and says why rather than showing zeros.
/// </summary>
/// <remarks>
/// Live per-process byte counts come only from an ETW kernel session, which
/// Windows opens for an administrator and nobody else. The Store edition never
/// asks for administrator rights, so it carries no trace session at all rather
/// than one that could never start. Everything that needs no privilege - the
/// process list, socket counts, connections and each program's use as Windows
/// counts it - still works, and is what the Apps page shows.
/// </remarks>
public sealed class WithheldTrafficSource : IProcessTrafficSource, IProcessEndpointSource
{
    /// <inheritdoc />
    public TrafficSourceState State => TrafficSourceState.NotOffered;

    /// <inheritdoc />
    public string? Detail => "Live per-process rates are not part of this edition.";

    /// <inheritdoc />
    public string? Explanation =>
        "Counting each program's bytes as they move needs a kernel trace session, which Windows opens only for an administrator. This edition never asks for administrator rights.";

    /// <inheritdoc />
    public bool CanAttribute => false;

    /// <inheritdoc />
    public void Start()
    {
    }

    /// <inheritdoc />
    public void CopyTo(Dictionary<int, ProcessTraffic> destination)
    {
        ArgumentNullException.ThrowIfNull(destination);
        destination.Clear();
    }

    /// <inheritdoc />
    public void Watch(int processId)
    {
    }

    /// <inheritdoc />
    public void Unwatch(int processId)
    {
    }

    /// <inheritdoc />
    public void CopyEndpoints(int processId, Dictionary<EndpointKey, EndpointTraffic> destination)
    {
        ArgumentNullException.ThrowIfNull(destination);
        destination.Clear();
    }
}
