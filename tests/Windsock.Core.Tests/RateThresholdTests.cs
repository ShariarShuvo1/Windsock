using Windsock.Core.Formatting;
using Xunit;

namespace Windsock.Core.Tests;

public sealed class RateThresholdTests
{
    [Theory]
    [InlineData("500", 500)]
    [InlineData("10k", 10 * 1024)]
    [InlineData("10K", 10 * 1024)]
    [InlineData("1.5M", 1.5 * 1024 * 1024)]
    [InlineData("2G", 2d * 1024 * 1024 * 1024)]
    [InlineData(" 64 k ", 64 * 1024)]
    [InlineData(">100", 100)]
    [InlineData(">= 2M", 2 * 1024 * 1024)]
    [InlineData("10 KB/s", 10 * 1024)]
    [InlineData("512 B/s", 512)]
    public void ReadsTheShorthandPeopleActuallyType(string text, double expected)
    {
        Assert.True(RateThreshold.TryParse(text, out double value), $"could not parse '{text}'");
        Assert.Equal(expected, value, precision: 6);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("chrome")]
    [InlineData("-5")]
    [InlineData("k")]
    public void RefusesWhatIsNotARate(string? text)
    {
        Assert.False(RateThreshold.TryParse(text, out double value));
        Assert.Equal(0, value);
    }
}
