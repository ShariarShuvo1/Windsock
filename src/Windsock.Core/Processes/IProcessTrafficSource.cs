namespace Windsock.Core.Processes;

/// <summary>Why per-process rates are or are not being collected.</summary>
public enum TrafficSourceState
{
    Stopped,

    Running,

    RequiresElevation,

    Unavailable,
}

/// <summary>Supplies cumulative per-process byte counters.</summary>
public interface IProcessTrafficSource
{
    TrafficSourceState State { get; }

    string? Detail { get; }

    string? Explanation { get; }

    void Start();

    void CopyTo(Dictionary<int, ProcessTraffic> destination);
}

/// <summary>Counts the sockets each process currently holds open.</summary>
public interface IProcessConnectionSource
{
    void CopyTo(Dictionary<int, SocketCounts> destination);
}

/// <summary>Describes a process from its id.</summary>
public interface IProcessIdentityResolver
{
    ProcessIdentity Resolve(int processId);

    void Forget(int processId);
}
