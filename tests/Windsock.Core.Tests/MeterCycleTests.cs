using Windsock.Core.Meter;
using Xunit;

namespace Windsock.Core.Tests;

/// <summary>A place rotating through readings, holding still while pointed at.</summary>
public sealed class MeterCycleTests
{
    private static readonly TimeSpan Each = TimeSpan.FromSeconds(4);

    private static TimeSpan At(double seconds) => TimeSpan.FromSeconds(seconds);

    [Fact]
    public void ItTurns_EveryFewSeconds_AndComesRound()
    {
        MeterCycle cycle = new();

        cycle.Advance(3, Each, At(100), held: false);
        Assert.Equal(0, cycle.Index);

        Assert.False(cycle.Advance(3, Each, At(103.9), held: false));
        Assert.True(cycle.Advance(3, Each, At(104), held: false));
        Assert.Equal(1, cycle.Index);

        cycle.Advance(3, Each, At(108), held: false);
        Assert.Equal(2, cycle.Index);

        cycle.Advance(3, Each, At(112), held: false);
        Assert.Equal(0, cycle.Index);
    }

    /// <summary>
    /// Pointed at, it stays on the reading being read, and carries on from
    /// there afterwards rather than jumping to wherever the clock has got to.
    /// </summary>
    [Fact]
    public void Pointed_At_ItHoldsStill_AndCarriesOnFromWhereItStopped()
    {
        MeterCycle cycle = new();

        cycle.Advance(3, Each, At(0), held: false);
        cycle.Advance(3, Each, At(5), held: false);
        Assert.Equal(1, cycle.Index);

        cycle.Advance(3, Each, At(6), held: true);
        cycle.Advance(3, Each, At(60), held: true);
        Assert.Equal(1, cycle.Index);

        // Five seconds counted before the pointer came, and the clock reads
        // from the last moment it was seen: a second more makes six, two
        // more make eight, and the rotation moves on.
        cycle.Advance(3, Each, At(61), held: false);
        Assert.Equal(1, cycle.Index);

        cycle.Advance(3, Each, At(63), held: false);
        Assert.Equal(2, cycle.Index);
    }

    [Fact]
    public void OneReading_NeverTurns()
    {
        MeterCycle cycle = new();

        for (int each = 0; each < 20; each++)
        {
            Assert.False(cycle.Advance(1, Each, At(each * 5), held: false));
        }

        Assert.Equal(0, cycle.Index);
    }

    [Fact]
    public void Restarting_GoesBackToTheFirst()
    {
        MeterCycle cycle = new();

        cycle.Advance(3, Each, At(0), held: false);
        cycle.Advance(3, Each, At(9), held: false);
        cycle.Restart();

        Assert.Equal(0, cycle.Index);
        cycle.Advance(3, Each, At(20), held: false);
        Assert.Equal(0, cycle.Index);
    }

    [Fact]
    public void AClockGoingBack_ChangesNothing()
    {
        MeterCycle cycle = new();

        cycle.Advance(3, Each, At(10), held: false);
        cycle.Advance(3, Each, At(5), held: false);

        Assert.Equal(0, cycle.Index);
    }
}
