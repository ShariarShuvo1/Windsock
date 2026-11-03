using Xunit;

namespace Windsock.Core.Tests;

public sealed class WakeSignalTests
{
    [Fact]
    public void ANudge_WakesTheLoopOnce()
    {
        using SemaphoreSlim nudge = new(0, 1);

        nudge.Signal();
        nudge.Signal();

        Assert.Equal(1, nudge.CurrentCount);
        Assert.True(nudge.Wait(0, TestContext.Current.CancellationToken));
        Assert.False(nudge.Wait(0, TestContext.Current.CancellationToken));
    }

    [Fact]
    public void AfterTheLoopWoke_AnotherNudgeIsHeard()
    {
        using SemaphoreSlim nudge = new(0, 1);

        nudge.Signal();
        Assert.True(nudge.Wait(0, TestContext.Current.CancellationToken));

        nudge.Signal();
        Assert.Equal(1, nudge.CurrentCount);
    }

    /// <summary>Nudges racing past the look at the count still never throw.</summary>
    [Fact]
    public void NudgesFromManyThreads_NeverThrow()
    {
        using SemaphoreSlim nudge = new(0, 1);

        Parallel.For(0, 10_000, _ => nudge.Signal());

        Assert.Equal(1, nudge.CurrentCount);
    }
}
