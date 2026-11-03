using Windsock.Core.Appearance;
using Xunit;

namespace Windsock.Core.Tests;

public sealed class TextScalingTests
{
    [Theory]
    [InlineData(12, 1.0, 12)]
    [InlineData(12, 1.5, 18)]
    [InlineData(14, 2.25, 31.5)]
    [InlineData(46, 1.5, 57.5)]
    [InlineData(32, 2.0, 48)]
    [InlineData(23, 1.5, 31.75)]
    public void Scale_GrowsSmallTextFullyAndLargeTextByHalf(double size, double factor, double expected) =>
        Assert.Equal(expected, TextScaling.Scale(size, factor));

    [Fact]
    public void Scale_NeverShrinksAndNeverOrdersSizesTheOtherWayRound()
    {
        double previous = 0;

        for (double size = 8; size <= 60; size += 0.5)
        {
            double scaled = TextScaling.Scale(size, 1.75);

            Assert.True(scaled >= size, $"{size} shrank to {scaled}");
            Assert.True(scaled >= previous, $"{size} drew smaller than a smaller size");
            previous = scaled;
        }
    }

    [Theory]
    [InlineData(0.5, 1.0)]
    [InlineData(3.0, 2.25)]
    [InlineData(double.NaN, 1.0)]
    [InlineData(1.3, 1.3)]
    public void Settle_HoldsTheFactorToWhatWindowsDocuments(double factor, double expected) =>
        Assert.Equal(expected, TextScaling.Settle(factor));
}
