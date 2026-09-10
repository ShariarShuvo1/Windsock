using Windsock.Core.Formatting;
using Xunit;

namespace Windsock.Core.Tests;

public sealed class RateFormatterTests
{
    private const double Kilobyte = 1024;
    private const double Megabyte = 1024 * 1024;

    [Theory]
    [InlineData(0, "0", "B/s")]
    [InlineData(-5, "0", "B/s")]
    [InlineData(512, "512", "B/s")]
    [InlineData(Kilobyte, "1.00", "KB/s")]
    [InlineData(1536, "1.50", "KB/s")]
    [InlineData(Megabyte, "1.00", "MB/s")]
    public void Auto_InBytes_ScalesByBinarySteps(double bytesPerSecond, string value, string unit)
    {
        (string actualValue, string actualUnit) =
            RateFormatter.Split(bytesPerSecond, RateFamily.Bytes, RateScale.Auto);

        Assert.Equal(value, actualValue);
        Assert.Equal(unit, actualUnit);
    }

    [Theory]
    [InlineData(125, "1.00", "kbit/s")]        // 125 B/s = 1000 bit/s
    [InlineData(125_000, "1.00", "Mbit/s")]
    [InlineData(100, "800", "bit/s")]
    public void Auto_InBits_ScalesByDecimalSteps(double bytesPerSecond, string value, string unit)
    {
        (string actualValue, string actualUnit) =
            RateFormatter.Split(bytesPerSecond, RateFamily.Bits, RateScale.Auto);

        Assert.Equal(value, actualValue);
        Assert.Equal(unit, actualUnit);
    }

    [Fact]
    public void Auto_FollowsTheSelectedFamily()
    {
        (_, string byteUnit) = RateFormatter.Split(Megabyte, RateFamily.Bytes, RateScale.Auto);
        (_, string bitUnit) = RateFormatter.Split(Megabyte, RateFamily.Bits, RateScale.Auto);

        Assert.Equal("MB/s", byteUnit);
        Assert.Equal("Mbit/s", bitUnit);
    }

    [Fact]
    public void Auto_ClampsToTheLargestScale()
    {
        (_, string unit) = RateFormatter.Split(double.MaxValue, RateFamily.Bytes, RateScale.Auto);

        Assert.Equal("PB/s", unit);
    }

    [Fact]
    public void PinnedScale_InBits_ConvertsUsingEightBitsPerByte()
    {
        // 1 MB/s is 8.388608 Mbit under the decimal convention bits are quoted in.
        (string value, string unit) =
            RateFormatter.Split(Megabyte, RateFamily.Bits, RateScale.Mega);

        Assert.Equal("8.39", value);
        Assert.Equal("Mbit/s", unit);
    }

    [Fact]
    public void PinnedSmallScale_GroupsLargeNumbers()
    {
        (string value, string unit) =
            RateFormatter.Split(11 * Megabyte, RateFamily.Bytes, RateScale.Base);

        Assert.Equal("11,534,336", value);
        Assert.Equal("B/s", unit);
    }

    [Fact]
    public void PinnedLargeScale_CollapsesAValueThatRoundsAway()
    {
        (string value, string unit) =
            RateFormatter.Split(Kilobyte, RateFamily.Bytes, RateScale.Peta);

        Assert.Equal("0", value);
        Assert.Equal("PB/s", unit);
    }

    [Theory]
    [InlineData(RateFamily.Bytes, RateScale.Base, "B/s")]
    [InlineData(RateFamily.Bits, RateScale.Peta, "Pbit/s")]
    [InlineData(RateFamily.Bytes, RateScale.Auto, "B/s")]
    public void Zero_ReportsZeroInTheRequestedUnit(RateFamily family, RateScale scale, string unit)
    {
        (string value, string label) = RateFormatter.Split(0, family, scale);

        Assert.Equal("0", value);
        Assert.Equal(unit, label);
    }

    [Fact]
    public void NaN_IsTreatedAsZero()
    {
        (string value, _) = RateFormatter.Split(double.NaN, RateFamily.Bytes, RateScale.Auto);

        Assert.Equal("0", value);
    }

    [Fact]
    public void Format_JoinsValueAndUnit()
    {
        Assert.Equal("1.50 MB/s", RateFormatter.Format(1.5 * Megabyte, RateFamily.Bytes, RateScale.Auto));
    }
}
