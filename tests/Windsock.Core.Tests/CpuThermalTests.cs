using Windsock.Core.Formatting;
using Windsock.Core.Hardware;
using Windsock.Core.Settings;
using Xunit;

namespace Windsock.Core.Tests;

/// <summary>
/// The register arithmetic behind the processor temperature.
/// </summary>
public sealed class CpuThermalTests
{
    /// <summary>Intel publishes a distance below the throttle point, not a temperature.</summary>
    [Fact]
    public void Intel_SubtractsReadoutFromThrottlePoint()
    {
        // TjMax 100 in bits 23:16, 45 degrees below it in bits 22:16.
        Assert.Equal(55, CpuThermalProbe.IntelCelsius(100L << 16, 45L << 16));
    }

    /// <summary>At the throttle point the distance is zero.</summary>
    [Fact]
    public void Intel_ReadsTheThrottlePointItself()
    {
        Assert.Equal(100, CpuThermalProbe.IntelCelsius(100L << 16, 0));
    }

    /// <summary>
    /// Bit 31 of IA32_THERM_STATUS is "reading valid", not part of the figure.
    /// </summary>
    [Fact]
    public void Intel_IgnoresTheValidBit()
    {
        Assert.Equal(55, CpuThermalProbe.IntelCelsius(100L << 16, (1L << 31) | (45L << 16)));
    }

    /// <summary>
    /// The readout is bits 22:16 - seven bits. Bit 23 belongs to something else.
    /// </summary>
    [Fact]
    public void Intel_ReadoutStopsAtBitTwentyTwo()
    {
        Assert.Equal(55, CpuThermalProbe.IntelCelsius(100L << 16, (1L << 23) | (45L << 16)));
    }

    /// <summary>The throttle point is bits 23:16 and nothing on either side of them.</summary>
    [Fact]
    public void Intel_ThrottlePointIgnoresNeighbouringBits()
    {
        Assert.Equal(55, CpuThermalProbe.IntelCelsius(unchecked((long)0xAB64FFFF), 45L << 16));
    }

    /// <summary>
    /// A throttle point of zero is a register that was not answered.
    /// </summary>
    [Fact]
    public void Intel_TreatsAnUnansweredThrottlePointAsNothing()
    {
        Assert.Null(CpuThermalProbe.IntelCelsius(0, 45L << 16));
    }

    /// <summary>A reading further below the throttle point than the throttle point is.</summary>
    [Fact]
    public void Intel_DiscardsAnImpossiblyColdReading()
    {
        Assert.Null(CpuThermalProbe.IntelCelsius(100L << 16, 127L << 16));
    }

    /// <summary>AMD publishes eighths of a degree in bits 31:21.</summary>
    [Fact]
    public void Amd_ReadsEighthsOfADegree()
    {
        // 400 eighths is 50 degrees.
        Assert.Equal(50, CpuThermalProbe.AmdCelsius(400L << 21));
    }

    /// <summary>The eighth is real resolution, not a rounding artefact.</summary>
    [Fact]
    public void Amd_KeepsTheFraction()
    {
        Assert.Equal(50.125, CpuThermalProbe.AmdCelsius(401L << 21));
    }

    /// <summary>
    /// Bit 19 moves the scale down by 49 degrees.
    /// </summary>
    [Fact]
    public void Amd_AppliesTheRangeOffset()
    {
        // 872 eighths is 109; the offset takes it to 60.
        Assert.Equal(60, CpuThermalProbe.AmdCelsius((872L << 21) | (1L << 19)));
    }

    /// <summary>The reading is bits 31:21 and stops there.</summary>
    [Fact]
    public void Amd_IgnoresBitsAboveTheField()
    {
        Assert.Equal(50, CpuThermalProbe.AmdCelsius((1L << 40) | (400L << 21)));
    }

    /// <summary>
    /// A register full of ones is not a processor at 256 degrees.
    /// </summary>
    [Fact]
    public void Amd_DiscardsAnImpossiblyHotReading()
    {
        Assert.Null(CpuThermalProbe.AmdCelsius(0x7FFL << 21));
    }

    /// <summary>
    /// An empty register is a sensor that did not answer, not a chip at freezing.
    /// </summary>
    [Fact]
    public void Amd_TreatsAnEmptyRegisterAsNothing()
    {
        Assert.Null(CpuThermalProbe.AmdCelsius(0));
    }

    /// <summary>
    /// The taskbar slot writes the reading in degrees.
    /// </summary>
    [Fact]
    public void MeterSlot_WritesTheTemperatureInDegrees()
    {
        MeterLine line = MeterReadout.Compose(MeterField.ProcessorTemperature, Warm(64.2), new TaskbarSettings());

        Assert.Equal("°C", line.Unit);
        Assert.Equal("CPU", line.Label);
        Assert.Equal(MeterGroup.Processor, line.Group);
        Assert.Contains("64", line.Value, StringComparison.Ordinal);
    }

    /// <summary>
    /// With no PawnIO the slot shows a dash, which is most machines.
    /// </summary>
    [Fact]
    public void MeterSlot_WithNoReading_SaysNothing()
    {
        MeterLine line = MeterReadout.Compose(MeterField.ProcessorTemperature, Warm(null), new TaskbarSettings());

        Assert.Equal(MeterReadout.Unknown, line.Value);
    }

    private static MeterReading Warm(double? celsius) => new(
        0,
        0,
        new SystemSnapshot(
            DateTimeOffset.UnixEpoch,
            new ProcessorReading(37.4, 6, [30, 44], 4200, 300, 4000, 90000, celsius),
            [],
            new MemoryReading(0, 0, 0, 0, 0, 0, 0),
            []));

    /// <summary>
    /// Asking whether PawnIO is installed works on a machine where it is not.
    /// </summary>
    [Fact]
    public void PawnIo_AnswersWhetherItIsInstalled()
    {
        Version? version = PawnIo.InstalledVersion;

        Assert.Equal(version is not null, PawnIo.IsInstalled);
    }
}
