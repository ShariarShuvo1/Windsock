namespace Windsock.Core.Plans;

/// <summary>
/// Remembers which levels of each plan have been crossed in each cycle, so a
/// crossing is told once, however many times Windsock starts in that cycle.
/// </summary>
public interface IPlanLevelStore
{
    /// <summary>The levels, in percent, already crossed in a plan's cycle.</summary>
    IReadOnlySet<int> ReadCrossedLevels(string planId, long cycleStartMinute);

    /// <summary>
    /// Notes a level as crossed; true when it was not noted before, so this is
    /// the crossing to tell.
    /// </summary>
    bool MarkCrossed(string planId, long cycleStartMinute, int level, long atMinute);
}
