using Windsock.Core.Formatting;
using Xunit;

namespace Windsock.Core.Tests;

public sealed class DurationLabelTests
{
    [Theory]
    [InlineData(0, "0s")]
    [InlineData(-3, "0s")]
    [InlineData(1, "1s")]
    [InlineData(59, "59s")]
    [InlineData(60, "1m")]
    [InlineData(90, "1m 30s")]
    [InlineData(120, "2m")]
    [InlineData(599, "9m 59s")]
    public void Format_UsesTheShortestReadableForm(double seconds, string expected)
    {
        Assert.Equal(expected, DurationLabel.Format(seconds));
    }

    [Fact]
    public void Format_DropsSecondsPastTenMinutes()
    {
        Assert.Equal("12m", DurationLabel.Format(12 * 60 + 34));
    }

    [Theory]
    [InlineData(3600, "1h")]
    [InlineData(3600 + 1800, "1h 30m")]
    [InlineData(12 * 3600, "12h")]
    [InlineData(86400, "1d")]
    [InlineData(86400 + 3 * 3600, "1d 3h")]
    [InlineData(5 * 86400 + 6 * 3600, "5d 6h")]
    public void Format_ScalesToHoursAndDays(double seconds, string expected)
    {
        Assert.Equal(expected, DurationLabel.Format(seconds));
    }

    [Fact]
    public void Format_HandlesNaN()
    {
        Assert.Equal("0s", DurationLabel.Format(double.NaN));
    }
}
