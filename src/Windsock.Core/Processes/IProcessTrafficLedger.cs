namespace Windsock.Core.Processes;

/// <summary>What a process that has ended moved in all, and the file it ran.</summary>
/// <param name="ProcessId">The process id it had, which another process may already have been given.</param>
/// <param name="Final">Its counters when it ended: everything since tracing began, or since it started.</param>
/// <param name="Image">The kernel's path of its executable (<c>\Device\HarddiskVolume3\...</c>), when known.</param>
public readonly record struct DepartedTraffic(int ProcessId, ProcessTraffic Final, string? Image);

/// <summary>
/// A traffic source that can account for every byte, including those of
/// processes that ended between two readings.
/// </summary>
/// <remarks>
/// <see cref="IProcessTrafficSource.CopyTo"/> lists running processes only: a
/// process's counters go the moment it ends, which is right for a table of
/// what is running and wrong for a record of what moved. A download that
/// finished and exited within a minute would otherwise vanish from that
/// minute. So an ended process's last counters wait here until taken.
/// </remarks>
public interface IProcessTrafficLedger
{
    /// <summary>Whether traffic is being attributed right now.</summary>
    bool CanAttribute { get; }

    /// <summary>
    /// The counters of every running process, and every process that ended
    /// since the last call, taken together so nothing is in both or neither.
    /// </summary>
    void Take(Dictionary<int, ProcessTraffic> running, List<DepartedTraffic> departed);

    /// <summary>The kernel's path of a running process's executable, when the trace has seen it start.</summary>
    string? ImageOf(int processId);
}
