namespace Windsock.Core.Power;

/// <summary>What the battery is doing.</summary>
public enum BatteryState
{
    /// <summary>Nothing says.</summary>
    Unknown,

    /// <summary>On mains power and taking charge.</summary>
    Charging,

    /// <summary>Running the machine.</summary>
    Discharging,

    /// <summary>On mains power and neither charging nor discharging: full, or held below full on purpose.</summary>
    Idle,
}

/// <summary>
/// The machine's batteries, taken together the way Windows' own battery icon
/// takes them.
/// </summary>
/// <param name="ChargePercent">How full, 0 to 100.</param>
/// <param name="State">Charging, discharging, or neither.</param>
/// <param name="OnMains">Whether the machine is plugged in, or nothing where Windows does not know.</param>
/// <param name="Critical">Whether a battery says it is critically low.</param>
/// <param name="RateWatts">
/// Watts going in (positive) or out (negative), or nothing where the battery
/// does not measure it or counts in units of its own.
/// </param>
/// <param name="TimeLeft">Windows' estimate of the time to empty, while discharging.</param>
/// <param name="TimeToFull">
/// How long to full at the rate it is charging now, while charging. A straight
/// line: batteries slow down as they fill, so it reads short near the end.
/// </param>
/// <param name="RemainingWattHours">What it holds now.</param>
/// <param name="FullWattHours">What it holds full today.</param>
/// <param name="DesignWattHours">What it held full when new.</param>
/// <param name="HealthPercent">Today's full charge against the design capacity.</param>
/// <param name="CycleCount">Charge cycles, or nothing where the battery does not count them.</param>
/// <param name="Units">Each battery on its own: most laptops have one, some two.</param>
/// <param name="Detail">
/// Why some figures are missing, when the battery's own figures could not be
/// read and only Windows' summary was; otherwise nothing.
/// </param>
public sealed record BatteryReading(
    double? ChargePercent,
    BatteryState State,
    bool? OnMains,
    bool Critical,
    double? RateWatts,
    TimeSpan? TimeLeft,
    TimeSpan? TimeToFull,
    double? RemainingWattHours,
    double? FullWattHours,
    double? DesignWattHours,
    double? HealthPercent,
    int? CycleCount,
    IReadOnlyList<BatteryUnit> Units,
    string? Detail = null)
{
    /// <summary>Whether it is taking charge now.</summary>
    public bool IsCharging => State == BatteryState.Charging;
}

/// <summary>One battery, as its driver describes it.</summary>
/// <param name="Name">The driver's name for it, such as the model.</param>
/// <param name="Maker">Who made it.</param>
/// <param name="Chemistry">Its chemistry, in words, or the driver's code where it is not one Windows documents.</param>
/// <param name="ManufactureDate">When it was made, where the battery says.</param>
/// <param name="ChargePercent">How full it is.</param>
/// <param name="RateWatts">Watts in (positive) or out (negative).</param>
/// <param name="Volts">Its voltage now.</param>
/// <param name="Celsius">Its temperature, where it reports one; most do not.</param>
/// <param name="RemainingWattHours">What it holds now.</param>
/// <param name="FullWattHours">What it holds full today.</param>
/// <param name="DesignWattHours">What it held full new.</param>
/// <param name="HealthPercent">Full against design.</param>
/// <param name="CycleCount">Charge cycles, or nothing where it does not count them.</param>
/// <param name="IsShortTerm">Whether it is a UPS rather than a laptop's own cell.</param>
public sealed record BatteryUnit(
    string? Name,
    string? Maker,
    string? Chemistry,
    DateOnly? ManufactureDate,
    double? ChargePercent,
    double? RateWatts,
    double? Volts,
    double? Celsius,
    double? RemainingWattHours,
    double? FullWattHours,
    double? DesignWattHours,
    double? HealthPercent,
    int? CycleCount,
    bool IsShortTerm);

/// <summary>
/// One battery's figures as its driver answered them, before anything is
/// made of them.
/// </summary>
/// <param name="Information">The static part, or nothing where it was not answered.</param>
/// <param name="Status">The live part, or nothing.</param>
/// <param name="Name"><c>BatteryDeviceName</c>.</param>
/// <param name="Maker"><c>BatteryManufactureName</c>.</param>
/// <param name="ManufactureDate"><c>BatteryManufactureDate</c>.</param>
/// <param name="Celsius"><c>BatteryTemperature</c>, converted.</param>
/// <param name="EstimatedTime"><c>BatteryEstimatedTime</c> at the present rate.</param>
public sealed record BatteryFigures(
    BatteryInformation? Information,
    BatteryStatus? Status,
    string? Name = null,
    string? Maker = null,
    DateOnly? ManufactureDate = null,
    double? Celsius = null,
    TimeSpan? EstimatedTime = null);
