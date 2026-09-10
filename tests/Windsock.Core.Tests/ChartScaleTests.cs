using Windsock.Core.Formatting;
using Xunit;

namespace Windsock.Core.Tests;

public sealed class ChartScaleTests
{
    private const double Kilobyte = 1024;
    private const double Megabyte = 1024 * 1024;

    [Fact]
    public void NiceMaximum_NeverDropsBelowTheFloor()
    {
        double maximum = ChartScale.NiceMaximum(peak: 12, floor: 64 * Kilobyte);

        Assert.Equal(64 * Kilobyte, maximum);
    }

    [Theory]
    [InlineData(1.2 * Megabyte, 2 * Megabyte)]
    [InlineData(12.4 * Megabyte, 20 * Megabyte)]
    [InlineData(3 * Megabyte, 5 * Megabyte)]
    [InlineData(120 * Kilobyte, 200 * Kilobyte)]
    public void NiceMaximum_RoundsUpToAReadableStep(double peak, double expected)
    {
        double maximum = ChartScale.NiceMaximum(peak, floor: 64 * Kilobyte);

        Assert.Equal(expected, maximum, precision: 3);
    }

    [Fact]
    public void NiceMaximum_IsAlwaysAtLeastThePeak()
    {
        foreach (double peak in new[] { 1d, 999d, 5000d, 1.1 * Megabyte, 700 * Megabyte })
        {
            double maximum = ChartScale.NiceMaximum(peak, floor: 1);
            Assert.True(maximum >= peak, $"maximum {maximum} was below peak {peak}");
        }
    }

    [Theory]
    [InlineData(double.NaN)]
    [InlineData(double.PositiveInfinity)]
    [InlineData(-1)]
    public void NiceMaximum_HandlesDegenerateInput(double peak)
    {
        double maximum = ChartScale.NiceMaximum(peak, floor: 64 * Kilobyte);

        Assert.True(maximum > 0);
        Assert.False(double.IsNaN(maximum));
    }
}
