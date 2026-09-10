using Windsock.Core.Formatting;
using Xunit;

namespace Windsock.Core.Tests;

public sealed class FilterQueryTests
{
    [Theory]
    [InlineData("4", Comparison.AtLeast, 4)]
    [InlineData(">2", Comparison.GreaterThan, 2)]
    [InlineData(">= 2", Comparison.AtLeast, 2)]
    [InlineData("<10", Comparison.LessThan, 10)]
    [InlineData("<= 10", Comparison.AtMost, 10)]
    [InlineData("=1", Comparison.Exactly, 1)]
    [InlineData("== 1", Comparison.Exactly, 1)]
    public void ReadsTheComparisonInFrontOfTheValue(string text, Comparison expected, double value)
    {
        Assert.True(FilterQuery.TryParse(text, FilterQuery.Count, out NumericFilter filter));
        Assert.Equal(expected, filter.Comparison);
        Assert.Equal(value, filter.Value, precision: 6);
    }

    [Fact]
    public void NoComparison_MeansAtLeast()
    {
        Assert.True(FilterQuery.TryParse("3", FilterQuery.Count, out NumericFilter filter));

        Assert.True(filter.Matches(3));
        Assert.True(filter.Matches(9));
        Assert.False(filter.Matches(2));
    }

    [Fact]
    public void LessThan_ExcludesTheValueItself()
    {
        Assert.True(FilterQuery.TryParse("<3", FilterQuery.Count, out NumericFilter filter));

        Assert.True(filter.Matches(2));
        Assert.False(filter.Matches(3));
    }

    [Fact]
    public void AtMost_IncludesTheValueItself()
    {
        Assert.True(FilterQuery.TryParse("<=3", FilterQuery.Count, out NumericFilter filter));

        Assert.True(filter.Matches(3));
        Assert.False(filter.Matches(4));
    }

    [Fact]
    public void GreaterThan_ExcludesTheValueItself()
    {
        Assert.True(FilterQuery.TryParse(">3", FilterQuery.Count, out NumericFilter filter));

        Assert.False(filter.Matches(3));
        Assert.True(filter.Matches(4));
    }

    [Fact]
    public void RateColumns_ReadTheirOwnShorthand()
    {
        Assert.True(FilterQuery.TryParse(">=100k", RateThreshold.TryParse, out NumericFilter filter));

        Assert.Equal(102_400, filter.Value, precision: 6);
        Assert.True(filter.Matches(200_000));
        Assert.False(filter.Matches(50_000));
    }

    [Fact]
    public void DurationColumns_ReadTheirOwnShorthand()
    {
        Assert.True(FilterQuery.TryParse(">2h", DurationThreshold.TryParse, out NumericFilter filter));

        Assert.Equal(7_200, filter.Value, precision: 6);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData(">")]
    [InlineData("chrome")]
    [InlineData("-4")]
    public void UnreadableInput_IsRefusedRatherThanGuessedAt(string? text)
    {
        Assert.False(FilterQuery.TryParse(text, FilterQuery.Count, out _));
    }
}

public sealed class DurationThresholdTests
{
    [Theory]
    [InlineData("45s", 45)]
    [InlineData("30m", 1_800)]
    [InlineData("2h", 7_200)]
    [InlineData("1d", 86_400)]
    [InlineData("2h 30m", 9_000)]
    [InlineData("1d 6h", 108_000)]
    [InlineData("1.5h", 5_400)]
    [InlineData("90 min", 5_400)]
    [InlineData("10 sec", 10)]
    public void ReadsTheSameShorthandTheColumnPrints(string text, double expected)
    {
        Assert.True(DurationThreshold.TryParse(text, out double seconds), $"could not parse '{text}'");
        Assert.Equal(expected, seconds, precision: 6);
    }

    [Fact]
    public void ABareNumber_MeansMinutes()
    {
        // For an uptime, "30" is far more often half an hour than half a minute.
        Assert.True(DurationThreshold.TryParse("30", out double seconds));
        Assert.Equal(1_800, seconds, precision: 6);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("h")]
    [InlineData("2x")]
    [InlineData("chrome")]
    public void RefusesWhatIsNotADuration(string? text)
    {
        Assert.False(DurationThreshold.TryParse(text, out double seconds));
        Assert.Equal(0, seconds);
    }
}
