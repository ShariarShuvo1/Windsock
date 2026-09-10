using Windsock.Core.Formatting;
using Xunit;

namespace Windsock.Core.Tests;

public sealed class TimeUnitsTests
{
    [Theory]
    [InlineData(500, TimeUnit.Milliseconds, 500)]
    [InlineData(2, TimeUnit.Seconds, 2000)]
    [InlineData(3, TimeUnit.Minutes, 180_000)]
    [InlineData(1, TimeUnit.Hours, 3_600_000)]
    public void ToTimeSpan_ScalesByUnit(double value, TimeUnit unit, double expectedMilliseconds)
    {
        Assert.Equal(expectedMilliseconds, TimeUnits.ToTimeSpan(value, unit).TotalMilliseconds, precision: 6);
    }

    [Theory]
    [InlineData(500, 500, TimeUnit.Milliseconds)]
    [InlineData(1500, 1500, TimeUnit.Milliseconds)]
    [InlineData(2000, 2, TimeUnit.Seconds)]
    [InlineData(90_000, 90, TimeUnit.Seconds)]
    [InlineData(120_000, 2, TimeUnit.Minutes)]
    [InlineData(3_600_000, 1, TimeUnit.Hours)]
    public void Split_PicksTheLargestUnitLeavingAWholeNumber(
        double milliseconds,
        double expectedValue,
        TimeUnit expectedUnit)
    {
        (double value, TimeUnit unit) = TimeUnits.Split(TimeSpan.FromMilliseconds(milliseconds));

        Assert.Equal(expectedValue, value, precision: 6);
        Assert.Equal(expectedUnit, unit);
    }

    [Fact]
    public void Split_RoundTripsWhatWasEntered()
    {
        // 500 ms should come back as "500 ms", not "0.5 s".
        foreach ((double input, TimeUnit unit) in new[]
                 {
                     (500d, TimeUnit.Milliseconds),
                     (250d, TimeUnit.Milliseconds),
                     (5d, TimeUnit.Seconds),
                     (10d, TimeUnit.Minutes),
                     (2d, TimeUnit.Hours),
                 })
        {
            TimeSpan span = TimeUnits.ToTimeSpan(input, unit);
            (double value, TimeUnit back) = TimeUnits.Split(span);

            Assert.Equal(input, value, precision: 6);
            Assert.Equal(unit, back);
        }
    }

    [Fact]
    public void Split_TreatsNonPositiveAsZeroMilliseconds()
    {
        (double value, TimeUnit unit) = TimeUnits.Split(TimeSpan.Zero);

        Assert.Equal(0, value);
        Assert.Equal(TimeUnit.Milliseconds, unit);
    }

    [Fact]
    public void All_CoversEveryUnitAndHasUniqueLabels()
    {
        Assert.Equal(Enum.GetValues<TimeUnit>().Order(), TimeUnits.All.Order());

        string[] labels = [.. TimeUnits.All.Select(TimeUnits.Label)];
        Assert.Equal(labels.Length, labels.Distinct(StringComparer.Ordinal).Count());
    }
}
