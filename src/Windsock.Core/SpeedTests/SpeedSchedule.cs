using Windsock.Core.Adapters;

namespace Windsock.Core.SpeedTests;

/// <summary>When automatic speed tests run.</summary>
/// <remarks>
/// <para>
/// M-Lab asks integrations for no more than four automatic tests a day per
/// device, at randomised times, and the NDT7 spec gives the shape: waits drawn
/// from an exponential distribution with a six-hour mean, held between 36
/// minutes and 15 hours. The mean here is the day divided by the tests asked
/// for, with the same bounds scaled to it; and a hard cap counts the automatic
/// tests of the last 24 hours, so a restart or a clock change can never let
/// more through.
/// </para>
/// <para>
/// Random waits keep many machines from testing at the same moment, which is
/// what M-Lab is guarding against.
/// </para>
/// </remarks>
public static class SpeedSchedule
{
    /// <summary>The window the cap counts over.</summary>
    public static readonly TimeSpan Day = TimeSpan.FromDays(1);

    /// <summary>The wait before the next automatic test.</summary>
    /// <param name="testsADay">How many tests a day are wanted, 1 to 4.</param>
    /// <param name="uniform">A random number in [0, 1).</param>
    public static TimeSpan NextWait(int testsADay, double uniform)
    {
        int tests = Math.Clamp(testsADay, 1, Settings.ConnectionToolsSettings.MostScheduledTestsADay);
        double mean = Day.TotalSeconds / tests;

        // The spec's bounds for a 6 h mean are 2,160 s and 54,000 s: 0.1 and
        // 2.5 times the mean.
        double shortest = mean * 0.1;
        double longest = mean * 2.5;

        double drawn = -Math.Log(1 - Math.Clamp(uniform, 0, 0.999999)) * mean;
        return TimeSpan.FromSeconds(Math.Clamp(drawn, shortest, longest));
    }

    /// <summary>Whether another automatic test may run now, given the automatic tests already run.</summary>
    /// <param name="automaticTestTimes">When earlier automatic tests ran.</param>
    /// <param name="testsADay">The cap.</param>
    /// <param name="now">Now.</param>
    /// <remarks>
    /// A run stamped later than now counts: it means the clock was set back,
    /// and setting the clock back must not free a slot.
    /// </remarks>
    public static bool MayRun(IEnumerable<DateTimeOffset> automaticTestTimes, int testsADay, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(automaticTestTimes);

        int cap = Math.Clamp(testsADay, 1, Settings.ConnectionToolsSettings.MostScheduledTestsADay);
        int recent = automaticTestTimes.Count(at => at > now - Day);
        return recent < cap;
    }

    /// <summary>
    /// Whether the connection is, or may be, metered: the adapter carrying
    /// traffic or the physical one under it is metered, or Windows has said
    /// nothing about the one that decides.
    /// </summary>
    /// <remarks>
    /// The physical adapter decides when there is one: a VPN's own adapter
    /// reads as unmetered whatever it runs over, a phone's hotspot included.
    /// Not knowing is treated as metered, since a test moves a few hundred megabytes.
    /// </remarks>
    public static bool MayBeMetered(AdapterSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);

        AdapterDetails? carrier = snapshot.Carrier;
        AdapterDetails? physical = snapshot.Physical;

        if (carrier?.Cost?.IsMetered == true || physical?.Cost?.IsMetered == true)
        {
            return true;
        }

        return (physical ?? carrier)?.Cost is null;
    }
}
