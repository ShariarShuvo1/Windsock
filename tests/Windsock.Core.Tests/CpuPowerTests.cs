#if !STORE
// The energy counter is read through PawnIO, which the Store edition is built without.
using System.Diagnostics;
using Windsock.Core.Hardware;
using Xunit;

namespace Windsock.Core.Tests;

/// <summary>
/// The arithmetic that turns a processor's energy counter into watts: the
/// unit in MSR_RAPL_POWER_UNIT (Intel 0x606) or MSR_PWR_UNIT (AMD
/// 0xC0010299), and the 32-bit running count in the package energy register.
/// </summary>
public sealed class CpuPowerTests
{
    private static readonly long Second = Stopwatch.Frequency;

    /// <summary>
    /// A typical Intel client value, 0xA0E03: power unit 3 (1/8 W) in bits
    /// 3:0, energy unit 14 in bits 12:8, time unit 10 in bits 19:16.
    /// </summary>
    [Fact]
    public void Unit_IsBitsTwelveToEight()
    {
        Assert.Equal(1.0 / 16384, CpuEnergyMeter.JoulesPerCount(0xA0E03));
    }

    /// <summary>AMD's usual 0xA1003 is an energy unit of 16: about 15.3 microjoules.</summary>
    [Fact]
    public void Unit_ReadsAmdsUsualValue()
    {
        Assert.Equal(1.0 / 65536, CpuEnergyMeter.JoulesPerCount(0xA1003));
    }

    /// <summary>The unit field is five bits; neighbours on either side do not move it.</summary>
    [Fact]
    public void Unit_IgnoresNeighbouringBits()
    {
        Assert.Equal(1.0 / 16384, CpuEnergyMeter.JoulesPerCount(unchecked((long)0xFFFF_FFFF_FFFF_EEFF)));
    }

    /// <summary>An empty register is not a joule a count.</summary>
    [Fact]
    public void Unit_ZeroIsNothing()
    {
        Assert.Null(CpuEnergyMeter.JoulesPerCount(0));
    }

    /// <summary>The counter is the low 32 bits; a wrap between readings still subtracts right.</summary>
    [Fact]
    public void Difference_SurvivesOneWrap()
    {
        Assert.Equal(0x200u, CpuEnergyMeter.Difference(0xFFFF_FF00, 0x0000_0100));
        Assert.Equal(0x100u, CpuEnergyMeter.Difference(0x1_0000_0100, 0x2_0000_0200));
    }

    /// <summary>16,384 counts of 1/16384 J in a second is a watt; 50 W is 819,200 counts.</summary>
    [Fact]
    public void Meter_GivesWatts()
    {
        CpuEnergyMeter meter = new(1.0 / 16384);

        Assert.Null(meter.Next(1_000_000, 10 * Second));
        Assert.Equal(50, meter.Next(1_000_000 + 819_200, 11 * Second)!.Value, 6);
    }

    /// <summary>Watts across the counter's wrap.</summary>
    [Fact]
    public void Meter_ReadsAcrossTheWrap()
    {
        CpuEnergyMeter meter = new(1.0 / 16384);

        _ = meter.Next(0xFFFF_0000L, 0);
        Assert.Equal(20, meter.Next(0xFFFF_0000L + (20 * 16384), 2 * Second / 2)!.Value, 6);
    }

    /// <summary>
    /// After a gap long enough for the counter to have wrapped twice, the
    /// reading starts again instead of reporting a figure that may be short.
    /// </summary>
    [Fact]
    public void Meter_StartsAgainAfterALongGap()
    {
        CpuEnergyMeter meter = new(1.0 / 16384);

        _ = meter.Next(0, 0);
        Assert.Null(meter.Next(16384 * 100, 120 * Second));
        Assert.Equal(1, meter.Next((16384 * 100) + 16384, 121 * Second)!.Value, 6);
    }

    /// <summary>Two readings milliseconds apart repeat the last figure rather than divide by almost nothing.</summary>
    [Fact]
    public void Meter_KeepsTheLastFigureForAReadingTooSoon()
    {
        CpuEnergyMeter meter = new(1.0 / 16384);

        _ = meter.Next(0, 0);
        double? watts = meter.Next(16384 * 30, Second);

        Assert.Equal(watts, meter.Next((16384 * 30) + 5, Second + (Second / 1000)));
    }

    /// <summary>A counter that did not move is a chip that does not count, not a chip at zero watts.</summary>
    [Fact]
    public void Meter_AStillCounterIsNothing()
    {
        CpuEnergyMeter meter = new(1.0 / 16384);

        _ = meter.Next(5_000, 0);
        Assert.Null(meter.Next(5_000, Second));
    }

    /// <summary>Kilowatts from a client processor are a misread register.</summary>
    [Fact]
    public void Meter_RefusesTheImplausible()
    {
        CpuEnergyMeter meter = new(1.0 / 16384);

        _ = meter.Next(0, 0);
        Assert.Null(meter.Next(16384L * 2000, Second));
    }
}
#endif
