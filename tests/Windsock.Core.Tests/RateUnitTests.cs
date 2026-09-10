using Windsock.Core.Formatting;
using Xunit;

namespace Windsock.Core.Tests;

public sealed class RateUnitTests
{
    [Fact]
    public void Scales_ListAutoFirstThenEveryFixedScale()
    {
        Assert.Equal(RateScale.Auto, RateUnits.Scales[0]);
        Assert.Equal(Enum.GetValues<RateScale>().Length, RateUnits.Scales.Count);
        Assert.Equal(Enum.GetValues<RateScale>().Order(), RateUnits.Scales.Order());
    }

    [Fact]
    public void Families_CoverEveryValue()
    {
        Assert.Equal(Enum.GetValues<RateFamily>().Order(), RateUnits.Families.Order());
    }

    [Theory]
    [InlineData(RateFamily.Bytes, RateScale.Base, 1)]
    [InlineData(RateFamily.Bytes, RateScale.Kilo, 1024)]
    [InlineData(RateFamily.Bytes, RateScale.Mega, 1024 * 1024)]
    [InlineData(RateFamily.Bits, RateScale.Base, 0.125)]
    [InlineData(RateFamily.Bits, RateScale.Kilo, 125)]
    [InlineData(RateFamily.Bits, RateScale.Mega, 125_000)]
    public void BytesPerUnit_UsesBinaryStepsForBytesAndDecimalForBits(
        RateFamily family,
        RateScale scale,
        double expected)
    {
        Assert.Equal(expected, RateUnits.BytesPerUnit(family, scale), precision: 6);
    }

    [Theory]
    [InlineData(RateFamily.Bytes, RateScale.Auto, "Auto")]
    [InlineData(RateFamily.Bits, RateScale.Auto, "Auto")]
    [InlineData(RateFamily.Bytes, RateScale.Giga, "GB/s")]
    [InlineData(RateFamily.Bits, RateScale.Giga, "Gbit/s")]
    public void Label_NamesTheUnitForTheFamily(RateFamily family, RateScale scale, string expected)
    {
        Assert.Equal(expected, RateUnits.Label(family, scale));
    }

    [Fact]
    public void Label_IsUniqueWithinAFamily()
    {
        foreach (RateFamily family in RateUnits.Families)
        {
            string[] labels = [.. RateUnits.Scales.Select(scale => RateUnits.Label(family, scale))];
            Assert.Equal(labels.Length, labels.Distinct(StringComparer.Ordinal).Count());
        }
    }
}
