using Windsock.Core.Docking;
using Xunit;

namespace Windsock.Core.Tests;

/// <summary>
/// A full-screen reading that only counts once it has held.
/// </summary>
public sealed class HeldSignalTests
{
    private static readonly TimeSpan Hold = TimeSpan.FromMilliseconds(400);

    private static TimeSpan At(int milliseconds) => TimeSpan.FromMilliseconds(milliseconds);

    [Fact]
    public void ANewAnswer_CountsOnlyOnceItHasHeld()
    {
        HeldSignal signal = new(Hold);

        Assert.False(signal.Update(true, At(0)));
        Assert.False(signal.Update(true, At(300)));
        Assert.False(signal.Value);
        Assert.True(signal.Update(true, At(400)));
        Assert.True(signal.Value);
    }

    /// <summary>
    /// Alt+Tab through a full-screen game passes it for a frame or two; that
    /// must not hide the meter.
    /// </summary>
    [Fact]
    public void AFlicker_ChangesNothing()
    {
        HeldSignal signal = new(Hold);

        signal.Update(true, At(0));
        signal.Update(false, At(100));
        signal.Update(true, At(200));
        signal.Update(true, At(500));

        Assert.False(signal.Value);
        Assert.True(signal.Update(true, At(600)));
    }

    [Fact]
    public void GoingBack_HoldsJustTheSame()
    {
        HeldSignal signal = new(Hold);

        signal.Update(true, At(0));
        signal.Update(true, At(400));
        Assert.False(signal.Update(false, At(500)));
        Assert.True(signal.Value);
        Assert.True(signal.Update(false, At(900)));
        Assert.False(signal.Value);
    }

    [Fact]
    public void Resetting_ForgetsEverything()
    {
        HeldSignal signal = new(Hold);

        signal.Update(true, At(0));
        signal.Update(true, At(400));
        signal.Reset();

        Assert.False(signal.Value);
    }
}
