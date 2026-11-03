using Windsock.Core.Formatting;
using Windsock.Core.Meter;
using Windsock.Core.Settings;
using Xunit;

namespace Windsock.Core.Tests;

/// <summary>Colours a reading takes past levels the person set.</summary>
public sealed class MeterLevelsTests
{
    private static List<MeterLevel> Rising() =>
    [
        new() { At = 50, Colour = MeterLevels.Fair },
        new() { At = 100, Colour = MeterLevels.Slow },
    ];

    [Theory]
    [InlineData(10, null)]
    [InlineData(49.9, null)]
    [InlineData(50, MeterLevels.Fair)]
    [InlineData(99, MeterLevels.Fair)]
    [InlineData(100, MeterLevels.Slow)]
    [InlineData(5000, MeterLevels.Slow)]
    public void ARisingReading_TakesTheHighestLevelItReached(double value, string? colour)
    {
        Assert.Equal(colour, MeterLevels.Colour(Rising(), value));
    }

    [Fact]
    public void TheOrderLevelsAreListedIn_DoesNotMatterGoingOneWay()
    {
        List<MeterLevel> shuffled = [Rising()[1], Rising()[0]];

        Assert.Equal(MeterLevels.Fair, MeterLevels.Colour(shuffled, 70));
        Assert.Equal(MeterLevels.Slow, MeterLevels.Colour(shuffled, 120));
    }

    [Theory]
    [InlineData(80, null)]
    [InlineData(20, MeterLevels.Fair)]
    [InlineData(15, MeterLevels.Fair)]
    [InlineData(10, MeterLevels.Slow)]
    [InlineData(3, MeterLevels.Slow)]
    public void AFallingReading_TakesTheLowestLevelItPassed(double value, string? colour)
    {
        Assert.Equal(colour, MeterLevels.Colour(MeterLevels.Defaults(MeterField.BatteryCharge), value));
    }

    [Fact]
    public void AnyNumberOfLevels_AndAColourOfOnesOwn()
    {
        List<MeterLevel> levels =
        [
            new() { At = 10, Colour = "#00FF00" },
            new() { At = 20, Colour = "#FFFF00" },
            new() { At = 30, Colour = "#FF8000" },
            new() { At = 40, Colour = "#FF0000" },
            new() { At = 50, Colour = "#FF00FF" },
        ];

        Assert.Equal("#FF8000", MeterLevels.Colour(levels, 35));
        Assert.Equal("#FF00FF", MeterLevels.Colour(levels, 51));
    }

    [Fact]
    public void LevelsBothWays_TheOneListedLaterWins()
    {
        List<MeterLevel> levels =
        [
            new() { At = 50, Colour = MeterLevels.Slow },
            new() { At = 60, Colour = MeterLevels.Good, Below = true },
        ];

        Assert.Equal(MeterLevels.Good, MeterLevels.Colour(levels, 55));
        Assert.Equal(MeterLevels.Slow, MeterLevels.Colour(levels, 70));
    }

    [Fact]
    public void NoReading_HasNoColour()
    {
        Assert.Null(MeterLevels.Colour(Rising(), null));
        Assert.Null(MeterLevels.Colour(Rising(), double.NaN));
        Assert.Null(MeterLevels.Colour([], 10));
    }

    /// <summary>
    /// A response time is tinted before anyone sets anything: good, fair from
    /// 50 ms, slow from 100 ms, the AIM lines for games the Connection page
    /// grades with.
    /// </summary>
    [Theory]
    [InlineData(MeterField.RouterPing, 3, MeterLevels.Good)]
    [InlineData(MeterField.InternetPing, 62, MeterLevels.Fair)]
    [InlineData(MeterField.InternetPing, 180, MeterLevels.Slow)]
    public void AResponseTime_IsTintedFromTheStart(MeterField field, double milliseconds, string colour)
    {
        MeterSlot slot = new() { Field = field };

        Assert.Equal(colour, MeterLevels.Colour(MeterLevels.For(slot, field), milliseconds));
    }

    [Fact]
    public void LevelsSetOnAPlace_ReplaceTheDefaults_ForItsOwnReadingOnly()
    {
        MeterSlot slot = new()
        {
            Field = MeterField.RouterPing,
            Levels = [new MeterLevel { At = 5, Colour = "#123456" }],
            Rotate = [MeterField.InternetPing],
        };

        Assert.Equal("#123456", MeterLevels.Colour(MeterLevels.For(slot, MeterField.RouterPing), 10));
        Assert.Equal(MeterLevels.Good, MeterLevels.Colour(MeterLevels.For(slot, MeterField.InternetPing), 10));
    }

    [Fact]
    public void MostReadings_HaveNoLevelsUntilSomeoneSetsThem()
    {
        Assert.Empty(MeterLevels.Defaults(MeterField.Download));
        Assert.Empty(MeterLevels.Defaults(MeterField.ProcessorLoad));
    }

    [Theory]
    [InlineData(MeterField.Download, RateFamily.Bytes, "MB/s", 5, 5d * 1024 * 1024)]
    [InlineData(MeterField.Download, RateFamily.Bits, "Mbit/s", 100, 12_500_000d)]
    [InlineData(MeterField.TodayTotal, RateFamily.Bytes, "GB", 2, 2d * 1024 * 1024 * 1024)]
    [InlineData(MeterField.ProcessorClock, RateFamily.Bytes, "GHz", 4.5, 4500)]
    [InlineData(MeterField.RouterPing, RateFamily.Bytes, "ms", 80, 80)]
    [InlineData(MeterField.ProcessorLoad, RateFamily.Bytes, "%", 90, 90)]
    public void ALevel_IsTypedInTheUnitThePersonThinksIn(
        MeterField field, RateFamily family, string label, double typed, double kept)
    {
        LevelUnit unit = MeterLevels.UnitOf(field, family, TemperatureUnit.Celsius)!.Value;

        Assert.Equal(label, unit.Label);
        Assert.Equal(kept, unit.Keep(typed), 6);
        Assert.Equal(typed, unit.Show(kept), 6);
    }

    [Fact]
    public void ATemperatureLevel_IsKeptInCelsius_WhateverItIsTypedIn()
    {
        LevelUnit unit = MeterLevels.UnitOf(MeterField.GraphicsTemperature, RateFamily.Bytes, TemperatureUnit.Fahrenheit)!.Value;

        Assert.Equal("°F", unit.Label);
        Assert.Equal(80, unit.Keep(176), 6);
        Assert.Equal(176, unit.Show(80), 6);
    }

    [Theory]
    [InlineData(MeterField.LocalAddress)]
    [InlineData(MeterField.PublicAddress)]
    [InlineData(MeterField.Pacer)]
    public void AReadingWithNoNumber_HasNoLevels(MeterField field)
    {
        Assert.Null(MeterLevels.UnitOf(field, RateFamily.Bytes, TemperatureUnit.Celsius));
    }

    [Fact]
    public void ALevel_IsDescribedAsItWasTyped()
    {
        LevelUnit unit = new("MB/s", 1024 * 1024);

        Assert.Equal("≥ 5 MB/s", MeterLevels.Describe(new MeterLevel { At = 5 * 1024 * 1024 }, unit));
        Assert.Equal("≤ 20 %", MeterLevels.Describe(new MeterLevel { At = 20, Below = true }, new LevelUnit("%", 1)));
    }
}
