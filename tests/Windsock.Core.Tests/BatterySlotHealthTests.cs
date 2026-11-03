using Windsock.Core.Power;
using Xunit;

namespace Windsock.Core.Tests;

/// <summary>
/// When a battery device that will not answer is let go and opened afresh.
/// </summary>
public sealed class BatterySlotHealthTests
{
    private const int NotReady = 21;
    private const int Busy = 170;
    private const int FileNotFound = 2;
    private const int NoSuchDevice = 433;
    private const int DeviceRemoved = 1617;
    private const int DeviceNotConnected = 1167;

    /// <summary>
    /// One "not ready" on waking from sleep is a hiccup, not a battery gone
    /// for the rest of the run, which is what it used to be.
    /// </summary>
    [Fact]
    public void OneHiccup_IsPassing()
    {
        BatterySlotHealth health = new();

        Assert.Equal(BatterySlotAnswer.Passing, health.Failed(NotReady));
        Assert.False(health.IsGone);
    }

    /// <summary>An answer forgets every failure before it.</summary>
    [Fact]
    public void AnAnswer_ClearsTheCount()
    {
        BatterySlotHealth health = new();

        _ = health.Failed(NotReady);
        _ = health.Failed(Busy);
        health.Answered();

        Assert.Equal(0, health.Failures);
        Assert.Equal(BatterySlotAnswer.Passing, health.Failed(NotReady));
        Assert.False(health.IsGone);
    }

    /// <summary>Failures running long enough mean the device is let go and opened afresh.</summary>
    [Fact]
    public void RepeatedFailures_AreGone()
    {
        BatterySlotHealth health = new();
        BatterySlotAnswer answer = BatterySlotAnswer.Answered;

        for (int count = 0; count < BatterySlotHealth.FailuresBeforeGone; count++)
        {
            answer = health.Failed(NotReady);
        }

        Assert.Equal(BatterySlotAnswer.Gone, answer);
        Assert.True(health.IsGone);
    }

    /// <summary>A device that says it was removed is gone at once.</summary>
    [Theory]
    [InlineData(DeviceRemoved)]
    [InlineData(DeviceNotConnected)]
    public void ARemovedDevice_IsGoneAtOnce(int error)
    {
        BatterySlotHealth health = new();

        Assert.Equal(BatterySlotAnswer.Gone, health.Failed(error));
        Assert.True(health.IsGone);
    }

    /// <summary>An empty slot is how the battery class says there is no battery: the device is fine.</summary>
    [Theory]
    [InlineData(NoSuchDevice)]
    [InlineData(FileNotFound)]
    public void AnEmptySlot_IsNeitherPassingNorGone(int error)
    {
        BatterySlotHealth health = new();

        _ = health.Failed(NotReady);
        _ = health.Failed(NotReady);

        Assert.Equal(BatterySlotAnswer.Empty, health.Failed(error));
        Assert.Equal(0, health.Failures);
        Assert.False(health.IsGone);
    }

    /// <summary>Gone is not for ever: a device that answers again is kept.</summary>
    [Fact]
    public void Gone_IsUndoneByAnAnswer()
    {
        BatterySlotHealth health = new();
        _ = health.Failed(DeviceRemoved);

        health.Answered();

        Assert.False(health.IsGone);
    }
}
