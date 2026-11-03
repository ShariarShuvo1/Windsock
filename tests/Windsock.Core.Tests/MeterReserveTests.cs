using Windsock.Core.Formatting;
using Windsock.Core.Settings;
using Xunit;

namespace Windsock.Core.Tests;

/// <summary>
/// The room a reading keeps so the meter's width holds still.
/// </summary>
/// <remarks>
/// The widest figures come from the formatters: an automatic rate is at most
/// four digits in any unit (1023 B/s before KB/s, 1023 KB/s before MB/s), a
/// share at most three.
/// </remarks>
public sealed class MeterReserveTests
{
    [Theory]
    [InlineData(0d)]
    [InlineData(512d)]
    [InlineData(1023d)]
    [InlineData(12.3 * 1024)]
    [InlineData(1023.4 * 1024)]
    [InlineData(5.5 * 1024 * 1024 * 1024)]
    public void AnAutomaticRate_NeverOutgrowsFourDigits(double bytesPerSecond)
    {
        (string value, _) = RateFormatter.Split(bytesPerSecond, RateFamily.Bytes, RateScale.Auto);

        Assert.Equal("0000", MeterReserve.Template(MeterField.Download, RateScale.Auto, value, null));
        Assert.True(MeterReserve.Shape(value).Length <= 4);
    }

    [Theory]
    [InlineData(1d)]
    [InlineData(999.6 * 125)]
    [InlineData(80d * 1000 * 1000 / 8)]
    public void AnAutomaticRateInBits_NeverOutgrowsFourDigits(double bytesPerSecond)
    {
        (string value, _) = RateFormatter.Split(bytesPerSecond, RateFamily.Bits, RateScale.Auto);

        Assert.Equal("0000", MeterReserve.Template(MeterField.Upload, RateScale.Auto, value, null));
    }

    [Theory]
    [InlineData(MeterField.ProcessorLoad, "7", "000")]
    [InlineData(MeterField.MemoryLoad, "100", "000")]
    [InlineData(MeterField.StorageUsed, "54", "000")]
    [InlineData(MeterField.ProcessorClock, "4.92", "0.00")]
    [InlineData(MeterField.ProcessorClock, "800", "0.00")]
    [InlineData(MeterField.MemoryUsed, "12.4", "0000")]
    public void FieldsWithACeiling_KeepRoomForIt(MeterField field, string value, string expected)
    {
        Assert.Equal(expected, MeterReserve.Template(field, RateScale.Auto, value, null));
    }

    /// <summary>
    /// Temperatures have no ceiling the formatter enforces: two digits to
    /// begin with, three once the figure has reached them, and kept.
    /// </summary>
    [Fact]
    public void ATemperature_GrowsOnceAndKeepsWhatItReached()
    {
        string first = MeterReserve.Template(MeterField.GraphicsTemperature, RateScale.Auto, "64", null);
        string hot = MeterReserve.Template(MeterField.GraphicsTemperature, RateScale.Auto, "101", first);
        string cooled = MeterReserve.Template(MeterField.GraphicsTemperature, RateScale.Auto, "70", hot);

        Assert.Equal("00", first);
        Assert.Equal("000", hot);
        Assert.Equal("000", cooled);
    }

    [Fact]
    public void ARateInAFixedUnit_KeepsTheWidestItHasShown()
    {
        string small = MeterReserve.Template(MeterField.Download, RateScale.Kilo, "5.12", null);
        string large = MeterReserve.Template(MeterField.Download, RateScale.Kilo, "12,345", small);
        string back = MeterReserve.Template(MeterField.Download, RateScale.Kilo, "3.40", large);

        Assert.Equal("0.00", small);
        Assert.Equal("00,000", large);
        Assert.Equal("00,000", back);
    }

    [Fact]
    public void ADash_KeepsWhateverRoomWasKept()
    {
        Assert.Equal("0000", MeterReserve.Template(MeterField.Download, RateScale.Auto, MeterReadout.Unknown, null));
        Assert.Equal("000", MeterReserve.Template(MeterField.ProcessorTemperature, RateScale.Auto, MeterReadout.Unknown, "000"));
    }

    [Fact]
    public void AnEmptyPlace_KeepsNoRoom()
    {
        Assert.Equal(string.Empty, MeterReserve.Template(MeterField.Empty, RateScale.Auto, string.Empty, null));
        Assert.Empty(MeterReserve.Units(MeterField.Empty, RateFamily.Bytes, RateScale.Auto));
    }

    [Theory]
    [InlineData("12.3", "00.0")]
    [InlineData("1,023", "0,000")]
    [InlineData("0", "0")]
    [InlineData("—", "—")]
    public void AShape_TurnsEveryDigitToAZero(string value, string expected)
    {
        Assert.Equal(expected, MeterReserve.Shape(value));
    }

    [Fact]
    public void AnAutomaticRate_KeepsRoomForEveryUnitItMovesThrough()
    {
        Assert.Equal(["B/s", "KB/s", "MB/s", "GB/s", "TB/s", "PB/s"], MeterReserve.Units(MeterField.Download, RateFamily.Bytes, RateScale.Auto));
        Assert.Equal(
            ["bit/s", "kbit/s", "Mbit/s", "Gbit/s", "Tbit/s", "Pbit/s"],
            MeterReserve.Units(MeterField.NetworkTotal, RateFamily.Bits, RateScale.Auto));
    }

    [Theory]
    [InlineData(MeterField.Download, RateScale.Mega, "MB/s")]
    [InlineData(MeterField.ProcessorLoad, RateScale.Auto, "%")]
    [InlineData(MeterField.GraphicsTemperature, RateScale.Auto, "°C")]
    [InlineData(MeterField.GraphicsPower, RateScale.Auto, "W")]
    public void AFieldWithOneUnit_KeepsRoomForThatOne(MeterField field, RateScale scale, string unit)
    {
        Assert.Equal([unit], MeterReserve.Units(field, RateFamily.Bytes, scale));
    }

    [Fact]
    public void TheClockAndMemory_KeepRoomForEachUnitTheyUse()
    {
        Assert.Equal(["GHz", "MHz"], MeterReserve.Units(MeterField.ProcessorClock, RateFamily.Bytes, RateScale.Auto));
        Assert.Contains("GB", MeterReserve.Units(MeterField.MemoryUsed, RateFamily.Bytes, RateScale.Auto));
    }
}
