using Windsock.Core.Power;
using Xunit;

namespace Windsock.Core.Tests;

/// <summary>
/// What the battery page and the taskbar slot are given, from a battery made
/// up to the driver's documented shapes. Nothing here has met a real battery.
/// </summary>
public sealed class BatteryProbeTests
{
    private static readonly SystemPowerStatus Desktop = new(1, 128, 255, 0, BatteryLayout.Unknown, BatteryLayout.Unknown);

    private static SystemPowerStatus Laptop(bool mains, byte percent, uint secondsLeft = BatteryLayout.Unknown, bool saver = false) =>
        new((byte)(mains ? 1 : 0), 1, percent, (byte)(saver ? 1 : 0), secondsLeft, BatteryLayout.Unknown);

    private static BatteryInformation Information(uint design = 57_000, uint full = 51_300, uint cycles = 312, BatteryCapabilities extra = BatteryCapabilities.None) =>
        new(BatteryCapabilities.System | extra, true, "LION", design, full, 2_850, 1_140, cycles);

    private static BatteryStatus Status(BatteryPowerState state, uint capacity, int rate, uint voltage = 11_820) =>
        new(state, capacity, voltage, rate);

    private static BatteryReading Some(BatteryReading? reading)
    {
        Assert.NotNull(reading);
        return reading;
    }

    /// <summary>A desktop has no battery, and the reading says so by being nothing.</summary>
    [Fact]
    public void Desktop_HasNoReading()
    {
        Assert.Null(BatteryProbe.Combine(BatteryScan.Empty, Desktop));
    }

    /// <summary>Discharging: charge, a negative rate in watts, Windows' own time left.</summary>
    [Fact]
    public void Discharging_ReadsChargeRateAndTimeLeft()
    {
        BatteryScan scan = new([new BatteryFigures(Information(), Status(BatteryPowerState.Discharging, 25_650, -10_260))], false);

        BatteryReading reading = Some(BatteryProbe.Combine(scan, Laptop(mains: false, 50, secondsLeft: 9_000)));

        Assert.Equal(BatteryState.Discharging, reading.State);
        Assert.False(reading.OnMains);
        Assert.False(reading.IsCharging);
        Assert.Equal(50, reading.ChargePercent!.Value, 6);
        Assert.Equal(-10.26, reading.RateWatts!.Value, 6);
        Assert.Equal(TimeSpan.FromSeconds(9_000), reading.TimeLeft);
        Assert.Null(reading.TimeToFull);
        Assert.Equal(25.65, reading.RemainingWattHours!.Value, 6);
        Assert.Equal(51.3, reading.FullWattHours!.Value, 6);
        Assert.Equal(57.0, reading.DesignWattHours!.Value, 6);
        Assert.Equal(90, reading.HealthPercent!.Value, 6);
        Assert.Equal(312, reading.CycleCount);
        Assert.Null(reading.Detail);
    }

    /// <summary>Without Windows' estimate, time left is what is left at the present rate.</summary>
    [Fact]
    public void Discharging_WorksOutTimeLeftWhenWindowsDoesNot()
    {
        BatteryScan scan = new([new BatteryFigures(Information(), Status(BatteryPowerState.Discharging, 20_000, -10_000))], false);

        BatteryReading reading = Some(BatteryProbe.Combine(scan, Laptop(mains: false, 39)));

        Assert.Equal(TimeSpan.FromHours(2), reading.TimeLeft);
    }

    /// <summary>Charging: a positive rate, and a straight-line time to full.</summary>
    [Fact]
    public void Charging_ReadsTimeToFull()
    {
        BatteryScan scan = new([new BatteryFigures(Information(full: 50_000), Status(BatteryPowerState.Charging | BatteryPowerState.OnLine, 30_000, 40_000))], false);

        BatteryReading reading = Some(BatteryProbe.Combine(scan, Laptop(mains: true, 60)));

        Assert.Equal(BatteryState.Charging, reading.State);
        Assert.True(reading.IsCharging);
        Assert.True(reading.OnMains);
        Assert.Equal(40, reading.RateWatts!.Value, 6);
        Assert.Equal(TimeSpan.FromMinutes(30), reading.TimeToFull);
        Assert.Null(reading.TimeLeft);
    }

    /// <summary>Plugged in and full: neither charging nor discharging.</summary>
    [Fact]
    public void Full_OnMainsIsIdle()
    {
        BatteryScan scan = new([new BatteryFigures(Information(), Status(BatteryPowerState.OnLine, 51_300, 0))], false);

        BatteryReading reading = Some(BatteryProbe.Combine(scan, Laptop(mains: true, 100)));

        Assert.Equal(BatteryState.Idle, reading.State);
        Assert.Equal(100, reading.ChargePercent!.Value, 6);
        Assert.Null(reading.TimeLeft);
        Assert.Null(reading.TimeToFull);
    }

    /// <summary>Some drivers report discharge as a positive number; the flags decide the sign.</summary>
    [Fact]
    public void Rate_TakesItsSignFromTheFlags()
    {
        BatteryScan scan = new([new BatteryFigures(Information(), Status(BatteryPowerState.Discharging, 25_000, 8_000))], false);

        BatteryReading reading = Some(BatteryProbe.Combine(scan, Laptop(mains: false, 48)));

        Assert.Equal(-8, reading.RateWatts!.Value, 6);
    }

    /// <summary>
    /// Plugged into a charger too weak for the load: on mains, yet discharging.
    /// Both are said, rather than one hiding the other.
    /// </summary>
    [Fact]
    public void WeakCharger_IsOnMainsAndDischarging()
    {
        BatteryScan scan = new([new BatteryFigures(Information(), Status(BatteryPowerState.OnLine | BatteryPowerState.Discharging, 25_000, -3_000))], false);

        BatteryReading reading = Some(BatteryProbe.Combine(scan, Laptop(mains: true, 48)));

        Assert.Equal(BatteryState.Discharging, reading.State);
        Assert.True(reading.OnMains);
    }

    /// <summary>A battery that does not count cycles reports zero, which is nothing.</summary>
    [Fact]
    public void Cycles_ZeroIsNotCounted()
    {
        BatteryScan scan = new([new BatteryFigures(Information(cycles: 0), Status(BatteryPowerState.Discharging, 25_000, -5_000))], false);

        Assert.Null(Some(BatteryProbe.Combine(scan, Laptop(mains: false, 48))).CycleCount);
    }

    /// <summary>
    /// A battery in its own units gives a charge but no energy, no watts and
    /// no health: those would be its units dressed as watts.
    /// </summary>
    [Fact]
    public void RelativeUnits_GiveChargeOnly()
    {
        BatteryScan scan = new([new BatteryFigures(Information(design: 100, full: 100, extra: BatteryCapabilities.Relative), Status(BatteryPowerState.Discharging, 64, -12))], false);

        BatteryReading reading = Some(BatteryProbe.Combine(scan, Laptop(mains: false, 64, secondsLeft: 7_200)));

        Assert.Equal(64, reading.ChargePercent!.Value, 6);
        Assert.Null(reading.RateWatts);
        Assert.Null(reading.HealthPercent);
        Assert.Null(reading.FullWattHours);
        Assert.Equal(TimeSpan.FromHours(2), reading.TimeLeft);
    }

    /// <summary>Two batteries add up, as Windows' icon adds them.</summary>
    [Fact]
    public void TwoBatteries_AddUp()
    {
        BatteryScan scan = new(
        [
            new BatteryFigures(Information(design: 24_000, full: 20_000, cycles: 100), Status(BatteryPowerState.Discharging, 10_000, -6_000)),
            new BatteryFigures(Information(design: 48_000, full: 40_000, cycles: 200), Status(BatteryPowerState.Discharging, 30_000, -2_000)),
        ],
        false);

        BatteryReading reading = Some(BatteryProbe.Combine(scan, Laptop(mains: false, 67)));

        Assert.Equal(2, reading.Units.Count);
        Assert.Equal(40.0 / 60 * 100, reading.ChargePercent!.Value, 6);
        Assert.Equal(-8, reading.RateWatts!.Value, 6);
        Assert.Equal(60.0 / 72 * 100, reading.HealthPercent!.Value, 6);
        Assert.Equal(200, reading.CycleCount);
        Assert.Equal(TimeSpan.FromHours(5), reading.TimeLeft);
    }

    /// <summary>
    /// A battery the system would not open still has Windows' summary: the
    /// charge and time left are shown, and the page is told why the rest is not.
    /// </summary>
    [Fact]
    public void Refused_FallsBackToWindowsSummary()
    {
        BatteryReading reading = Some(BatteryProbe.Combine(new BatteryScan([], Refused: true), Laptop(mains: false, 42, secondsLeft: 3_600)));

        Assert.Equal(42, reading.ChargePercent);
        Assert.Equal(BatteryState.Discharging, reading.State);
        Assert.Equal(TimeSpan.FromHours(1), reading.TimeLeft);
        Assert.Null(reading.RateWatts);
        Assert.Null(reading.HealthPercent);
        Assert.Null(reading.CycleCount);
        Assert.NotNull(reading.Detail);
    }

    /// <summary>A UPS calls itself short-term, and the unit says so.</summary>
    [Fact]
    public void Ups_IsMarkedShortTerm()
    {
        BatteryUnit unit = BatteryProbe.Describe(new BatteryFigures(Information(extra: BatteryCapabilities.ShortTerm), null, "Back-UPS"));

        Assert.True(unit.IsShortTerm);
        Assert.Equal("Back-UPS", unit.Name);
    }

    /// <summary>The chemistry codes BATTERY_INFORMATION documents, in words.</summary>
    [Theory]
    [InlineData("LION", "Lithium-ion")]
    [InlineData("Li-I", "Lithium-ion")]
    [InlineData("PbAc", "Lead acid")]
    [InlineData("NiMH", "Nickel-metal hydride")]
    [InlineData("NiCd", "Nickel-cadmium")]
    [InlineData("NiZn", "Nickel-zinc")]
    [InlineData("RAM", "Rechargeable alkaline-manganese")]
    [InlineData("LiP", "Lithium polymer")]
    [InlineData("ABCD", "ABCD")]
    public void Chemistry_InWords(string code, string expected)
    {
        Assert.Equal(expected, BatteryProbe.ChemistryName(code));
    }

    /// <summary>Health past double the design capacity is a misread field.</summary>
    [Fact]
    public void Health_RefusesTheImplausible()
    {
        BatteryUnit unit = BatteryProbe.Describe(new BatteryFigures(Information(design: 10_000, full: 50_000), null));

        Assert.Null(unit.HealthPercent);
    }

    /// <summary>
    /// The probe asks the battery at most every five seconds, and the static
    /// part once a minute; a desktop never touches a battery device.
    /// </summary>
    [Fact]
    public void Probe_PacesItsQuestions()
    {
        FakeBatterySource source = new(Laptop(mains: false, 50), new BatteryFigures(Information(), Status(BatteryPowerState.Discharging, 25_650, -10_000)));
        using BatteryProbe probe = new(source);

        Assert.NotNull(probe.Read(TimeSpan.FromSeconds(100)));
        Assert.NotNull(probe.Read(TimeSpan.FromSeconds(101)));
        Assert.NotNull(probe.Read(TimeSpan.FromSeconds(104)));
        Assert.Equal(1, source.Scans);
        Assert.Equal(1, source.Refreshes);

        Assert.NotNull(probe.Read(TimeSpan.FromSeconds(105)));
        Assert.Equal(2, source.Scans);
        Assert.Equal(1, source.Refreshes);

        Assert.NotNull(probe.Read(TimeSpan.FromSeconds(160)));
        Assert.Equal(3, source.Scans);
        Assert.Equal(2, source.Refreshes);
    }

    /// <summary>On a desktop only Windows' summary is asked.</summary>
    [Fact]
    public void Probe_OnADesktopAsksNoBattery()
    {
        FakeBatterySource source = new(Desktop);
        using BatteryProbe probe = new(source);

        Assert.Null(probe.Read(TimeSpan.FromSeconds(10)));
        Assert.Null(probe.Read(TimeSpan.FromSeconds(20)));
        Assert.Equal(0, source.Scans);
        Assert.Equal(2, source.Summaries);
    }

    /// <summary>The power reading takes mains, saver and the mode together.</summary>
    [Fact]
    public void Power_ReadsMainsSaverAndMode()
    {
        PowerReading onBattery = PowerReading.From(Laptop(mains: false, 30, saver: true), PowerMode.Saver);
        PowerReading desktop = PowerReading.From(Desktop, PowerMode.MaxPerformance);

        Assert.Equal(PowerSource.Battery, onBattery.Source);
        Assert.True(onBattery.SaverOn);
        Assert.Equal(PowerSource.Mains, desktop.Source);
        Assert.False(desktop.SaverOn);
        Assert.Equal(PowerMode.MaxPerformance, desktop.Mode);
    }

    /// <summary>
    /// A battery made up for tests and for a harness on a machine without one.
    /// </summary>
    private sealed class FakeBatterySource(SystemPowerStatus system, params BatteryFigures[] batteries) : IBatterySource
    {
        public int Scans { get; private set; }

        public int Refreshes { get; private set; }

        public int Summaries { get; private set; }

        public SystemPowerStatus ReadSystemStatus()
        {
            Summaries++;
            return system;
        }

        public BatteryScan ReadBatteries(bool refresh)
        {
            Scans++;
            Refreshes += refresh ? 1 : 0;
            return new BatteryScan(batteries, false);
        }

        public void Dispose()
        {
        }
    }
}
