using Windsock.Core.Formatting;
using Windsock.Core.Hardware;
using Windsock.Core.Settings;
using Xunit;

namespace Windsock.Core.Tests;

public sealed class MeterReadoutTests
{
    private const double Megabyte = 1024 * 1024;
    private const long Gigabyte = 1024 * 1024 * 1024;

    [Theory]
    [InlineData(MeterField.Download, "↓")]
    [InlineData(MeterField.Upload, "↑")]
    [InlineData(MeterField.ProcessorLoad, "CPU")]
    [InlineData(MeterField.BoardTemperature, "SYS")]
    [InlineData(MeterField.GraphicsTemperature, "GPU")]
    [InlineData(MeterField.MemoryLoad, "RAM")]
    [InlineData(MeterField.StorageUsed, "DISK")]
    public void Compose_LabelsWhatTheFigureIs(MeterField field, string expected)
    {
        MeterLine line = MeterReadout.Compose(field, Reading(), Settings());

        Assert.Equal(expected, line.Label);
    }

    [Fact]
    public void BoardTemperature_IsWrittenInDegrees()
    {
        MeterReading reading = Reading();
        MeterReading warm = reading with
        {
            Machine = reading.Machine with { BoardCelsius = 61.4 },
        };

        MeterLine line = MeterReadout.Compose(MeterField.BoardTemperature, warm, Settings());

        Assert.Equal("°C", line.Unit);
        Assert.Contains("61", line.Value, StringComparison.Ordinal);
    }

    [Fact]
    public void BoardTemperature_WithNoThermalZone_SaysNothing()
    {
        MeterLine line = MeterReadout.Compose(MeterField.BoardTemperature, Reading(), Settings());

        Assert.Equal("—", line.Value);
    }

    [Fact]
    public void BoardTemperature_IsNotAProcessorReading()
    {
        Assert.Equal(MeterGroup.System, MeterReadout.GroupOf(MeterField.BoardTemperature));
        Assert.NotEqual(MeterGroup.Processor, MeterReadout.GroupOf(MeterField.BoardTemperature));
    }

    [Fact]
    public void BoardTemperature_IsNamedForTheSystemNotTheProcessor()
    {
        string name = MeterReadout.Name(MeterField.BoardTemperature);

        Assert.DoesNotContain("CPU", name, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("processor", name, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData(double.NaN)]
    [InlineData(double.NegativeInfinity)]
    [InlineData(-5000)]
    public void Compose_TotalIgnoresUnusableRates(double upload)
    {
        MeterLine line = MeterReadout.Compose(
            MeterField.NetworkTotal,
            new MeterReading(2 * Megabyte, upload, SystemSnapshot.Empty),
            Settings());

        Assert.Equal("2.00", line.Value);
    }

    [Fact]
    public void Compose_FollowsTheChosenFamilyForRates()
    {
        MeterLine line = MeterReadout.Compose(
            MeterField.Download,
            Reading(),
            Settings(settings => settings.Family = RateFamily.Bits));

        Assert.Equal("Mbit/s", line.Unit);
    }

    [Theory]
    [InlineData(MeterField.ProcessorLoad, "37", "%")]
    [InlineData(MeterField.ProcessorClock, "4.20", "GHz")]
    [InlineData(MeterField.GraphicsLoad, "62", "%")]
    [InlineData(MeterField.GraphicsTemperature, "54", "°C")]
    [InlineData(MeterField.MemoryLoad, "50", "%")]
    [InlineData(MeterField.StorageTemperature, "41", "°C")]
    public void Compose_ReadsTheMachine(MeterField field, string value, string unit)
    {
        MeterLine line = MeterReadout.Compose(field, Reading(), Settings());

        Assert.Equal(value, line.Value);
        Assert.Equal(unit, line.Unit);
    }

    /// <summary>
    /// A laptop has an integrated adapter that is always there and a discrete
    /// one that does the work. One figure should describe whichever is working.
    /// </summary>
    [Fact]
    public void Compose_TakesTheBusiestGraphicsAdapter()
    {
        MeterLine line = MeterReadout.Compose(MeterField.GraphicsTemperature, Reading(), Settings());

        Assert.Equal("54", line.Value);
    }

    /// <summary>
    /// Every sensor is optional, and a machine that will not answer must be
    /// told to the reader rather than reported as a zero.
    /// </summary>
    [Theory]
    [InlineData(MeterField.ProcessorLoad)]
    [InlineData(MeterField.ProcessorClock)]
    [InlineData(MeterField.GraphicsLoad)]
    [InlineData(MeterField.GraphicsTemperature)]
    [InlineData(MeterField.GraphicsPower)]
    [InlineData(MeterField.MemoryLoad)]
    [InlineData(MeterField.MemoryUsed)]
    [InlineData(MeterField.StorageLoad)]
    [InlineData(MeterField.StorageTemperature)]
    [InlineData(MeterField.StorageUsed)]
    public void Compose_SaysSoWhenTheMachineHasNotBeenRead(MeterField field)
    {
        MeterLine line = MeterReadout.Compose(field, MeterReading.Empty, Settings());

        Assert.Equal(MeterReadout.Unknown, line.Value);
        Assert.Empty(line.Unit);
    }

    [Fact]
    public void Compose_SaysSoWhenASensorIsAbsent()
    {
        var machine = new SystemSnapshot(
            DateTimeOffset.UnixEpoch,
            ProcessorReading.Empty,
            [Card(busy: 10, celsius: null, watts: null)],
            MemoryReading.Empty,
            []);

        MeterLine line = MeterReadout.Compose(
            MeterField.GraphicsTemperature,
            new MeterReading(0, 0, machine),
            Settings());

        Assert.Equal(MeterReadout.Unknown, line.Value);
    }

    [Fact]
    public void Compose_MarksEveryFieldWithItsGroup()
    {
        Assert.Equal(MeterGroup.Network, MeterReadout.GroupOf(MeterField.Download));
        Assert.Equal(MeterGroup.Processor, MeterReadout.GroupOf(MeterField.ProcessorClock));
        Assert.Equal(MeterGroup.Graphics, MeterReadout.GroupOf(MeterField.GraphicsMemory));
        Assert.Equal(MeterGroup.Memory, MeterReadout.GroupOf(MeterField.MemoryUsed));
        Assert.Equal(MeterGroup.Storage, MeterReadout.GroupOf(MeterField.StorageLoad));
    }

    /// <summary>
    /// Reading the machine costs a little over one percent of a core, so a
    /// meter showing nothing but network rates must not switch it on.
    /// </summary>
    [Theory]
    [InlineData(MeterField.Empty, false)]
    [InlineData(MeterField.Download, false)]
    [InlineData(MeterField.Upload, false)]
    [InlineData(MeterField.NetworkTotal, false)]
    [InlineData(MeterField.ProcessorLoad, true)]
    [InlineData(MeterField.GraphicsTemperature, true)]
    [InlineData(MeterField.MemoryUsed, true)]
    [InlineData(MeterField.StorageUsed, true)]
    public void NeedsMachine_IsTrueOnlyForTheMachine(MeterField field, bool expected)
    {
        Assert.Equal(expected, MeterReadout.NeedsMachine(field));
    }

    [Fact]
    public void Name_NamesEveryField()
    {
        foreach (MeterField field in Enum.GetValues<MeterField>())
        {
            Assert.False(string.IsNullOrWhiteSpace(MeterReadout.Name(field)));
        }
    }

    private static MeterSlot Cell(MeterField field) => new() { Field = field };

    /// <summary>
    /// The unit the editor shows beside a reading has to be the unit the meter
    /// will actually draw, or the choice is being shown back wrongly.
    /// </summary>
    [Theory]
    [InlineData(MeterField.Download, RateFamily.Bytes, RateScale.Auto)]
    [InlineData(MeterField.Download, RateFamily.Bits, RateScale.Auto)]
    [InlineData(MeterField.Upload, RateFamily.Bits, RateScale.Mega)]
    [InlineData(MeterField.NetworkTotal, RateFamily.Bytes, RateScale.Kilo)]
    public void TheUnitShownForAChoice_IsTheUnitTheMeterDraws(
        MeterField field,
        RateFamily family,
        RateScale scale)
    {
        MeterSlot cell = new() { Field = field, Family = family, Scale = scale };
        MeterReading reading = new(2 * 1024 * 1024, 2 * 1024 * 1024, SystemSnapshot.Empty);

        Assert.Equal(
            MeterReadout.Compose(cell, reading, Settings()).Unit,
            MeterReadout.Unit(field, family, scale));
    }

    /// <summary>
    /// A cell counted in bits is named in bits, in the meter and in the tooltip
    /// alike: a tooltip that disagreed with the figure above it would be worse
    /// than no tooltip.
    /// </summary>
    [Fact]
    public void ACellInBits_IsReadInBitsEverywhere()
    {
        MeterSlot bits = new() { Field = MeterField.Download, Family = RateFamily.Bits };

        MeterLine line = MeterReadout.Compose(bits, Reading(), Settings());
        string tooltip = MeterReadout.Tooltip([bits], Reading(), Settings());
        Assert.Equal("16.78", line.Value);
        Assert.Equal("Mbit/s", line.Unit);
        Assert.Contains("16.78 Mbit/s", tooltip, StringComparison.Ordinal);
    }

    /// <summary>
    /// And the cell beside it is unaffected: that is the whole point of the
    /// choice living on the cell.
    /// </summary>
    [Fact]
    public void ACellSayingNothing_FollowsTheMeter()
    {
        MeterLine line = MeterReadout.Compose(Cell(MeterField.Download), Reading(), Settings());

        Assert.Equal("2.00", line.Value);
        Assert.Equal("MB/s", line.Unit);
    }

    [Fact]
    public void Tooltip_NamesEveryFieldOnShow()
    {
        string tooltip = MeterReadout.Tooltip(
            [Cell(MeterField.Download), Cell(MeterField.ProcessorLoad)],
            Reading(),
            Settings());

        Assert.Contains("Download rate  2.00 MB/s", tooltip, StringComparison.Ordinal);
        Assert.Contains("CPU usage  37 %", tooltip, StringComparison.Ordinal);
    }

    /// <summary>
    /// A meter can carry the same field twice, and blank slots are common. The
    /// tooltip is a list of what is being shown, not of what was configured.
    /// </summary>
    [Fact]
    public void Tooltip_SkipsBlanksAndRepeats()
    {
        string tooltip = MeterReadout.Tooltip(
            [Cell(MeterField.Download), Cell(MeterField.Empty), Cell(MeterField.Download)],
            Reading(),
            Settings());

        Assert.Single(tooltip.Split('\n'));
    }

    [Fact]
    public void Tooltip_FallsBackToTheAppNameWhenNothingIsShown()
    {
        Assert.Equal("Windsock", MeterReadout.Tooltip([Cell(MeterField.Empty)], Reading(), Settings()));
    }

    [Fact]
    public void Compose_RejectsMissingSettings()
    {
        Assert.Throws<ArgumentNullException>(
            () => MeterReadout.Compose(MeterField.Download, MeterReading.Empty, null!));
    }

    private static MeterReading Reading() => new(
        2 * Megabyte,
        Megabyte,
        new SystemSnapshot(
            DateTimeOffset.UnixEpoch,
            new ProcessorReading(37.4, 6, [30, 44], 4200, 300, 4000, 90000),
            [
                Card(busy: 3, celsius: 38, watts: 9),
                Card(busy: 62, celsius: 54, watts: 180),
            ],
            new MemoryReading(32 * Gigabyte, 16 * Gigabyte, 0, 0, 0, 0, 0),
            [
                Drive(index: 0, busy: 12, celsius: 41),
                Drive(index: 1, busy: 3, celsius: 33),
            ]));

    private static GraphicsReading Card(double busy, double? celsius, double? watts) => new(
        "Adapter",
        busy,
        celsius,
        watts,
        null,
        null,
        null,
        4 * Gigabyte,
        8 * Gigabyte,
        [],
        []);

    private static DriveReading Drive(int index, double busy, double? celsius) => new(
        index,
        "Model",
        "NVMe",
        IsSpinning: false,
        Size: 512 * Gigabyte,
        celsius,
        LifeUsedPercent: 4,
        ReadBytesPerSecond: 0,
        WriteBytesPerSecond: 0,
        busy,
        [new VolumeReading("C:", "Windows", "NTFS", 256 * Gigabyte, 64 * Gigabyte)]);

    private static TaskbarSettings Settings(Action<TaskbarSettings>? edit = null)
    {
        var settings = new TaskbarSettings { Scale = RateScale.Mega };
        edit?.Invoke(settings);
        return settings;
    }
}
