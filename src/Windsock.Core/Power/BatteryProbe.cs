namespace Windsock.Core.Power;

/// <summary>Where battery figures come from.</summary>
public interface IBatterySource : IDisposable
{
    /// <summary>Windows' summary of the machine's power: cheap, and enough to tell whether there is a battery.</summary>
    SystemPowerStatus ReadSystemStatus();

    /// <summary>
    /// Every battery the battery class lists, as its driver answers now. The
    /// static part (capacities, cycle count, names) is asked for again only
    /// when <paramref name="refresh"/> is set or the battery changed.
    /// </summary>
    BatteryScan ReadBatteries(bool refresh);
}

/// <summary>What a pass over the batteries found.</summary>
/// <param name="Batteries">Each battery that answered.</param>
/// <param name="Refused">Whether a battery was listed but could not be opened.</param>
public sealed record BatteryScan(IReadOnlyList<BatteryFigures> Batteries, bool Refused)
{
    /// <summary>A pass that found nothing.</summary>
    public static BatteryScan Empty { get; } = new([], false);
}

/// <summary>
/// Reads the battery at a battery's pace, and makes one reading of however
/// many there are.
/// </summary>
/// <remarks>
/// A battery's status comes from the embedded controller, which on some
/// laptops takes milliseconds to answer, and it moves slowly anyway: it is
/// asked every five seconds whatever the sampling rate, and its static part
/// once a minute. On a machine Windows says has no battery nothing but
/// <c>GetSystemPowerStatus</c> is ever called.
/// </remarks>
public sealed class BatteryProbe : IDisposable
{
    /// <summary>How often the battery's live figures are asked for.</summary>
    public static readonly TimeSpan StatusEvery = TimeSpan.FromSeconds(5);

    /// <summary>How often its capacities and cycle count are asked for again.</summary>
    public static readonly TimeSpan InformationEvery = TimeSpan.FromMinutes(1);

    private readonly IBatterySource _source;
    private TimeSpan? _statusAt;
    private TimeSpan? _informationAt;
    private BatteryReading? _latest;
    private SystemPowerStatus _system = SystemPowerStatus.Unread;

    public BatteryProbe(IBatterySource source)
    {
        ArgumentNullException.ThrowIfNull(source);
        _source = source;
    }

    /// <summary>Windows' power summary as last read.</summary>
    public SystemPowerStatus System => _system;

    /// <summary>
    /// The batteries now, or the last reading if one was taken within
    /// <see cref="StatusEvery"/>; nothing on a machine without one.
    /// </summary>
    /// <param name="now">A monotonic clock, such as <see cref="Environment.TickCount64"/> as a span.</param>
    public BatteryReading? Read(TimeSpan now)
    {
        if (_statusAt is { } last && now - last < StatusEvery && now >= last)
        {
            return _latest;
        }

        _statusAt = now;
        _system = _source.ReadSystemStatus();

        if (!_system.HasBattery)
        {
            _latest = null;
            _informationAt = null;
            return null;
        }

        bool refresh = _informationAt is not { } told || now - told >= InformationEvery || now < told;

        if (refresh)
        {
            _informationAt = now;
        }

        BatteryScan scan = _source.ReadBatteries(refresh);
        _latest = Combine(scan, _system);
        return _latest;
    }

    /// <summary>Makes one reading of every battery's figures and Windows' summary.</summary>
    public static BatteryReading? Combine(BatteryScan scan, SystemPowerStatus system)
    {
        ArgumentNullException.ThrowIfNull(scan);

        List<BatteryUnit> units = [];
        List<(BatteryFigures Figures, BatteryUnit Unit)> read = [];

        foreach (BatteryFigures figures in scan.Batteries)
        {
            if (figures.Information is null && figures.Status is null)
            {
                continue;
            }

            BatteryUnit unit = Describe(figures);
            units.Add(unit);
            read.Add((figures, unit));
        }

        if (units.Count == 0)
        {
            return system.HasBattery ? FromSummary(system, scan.Refused) : null;
        }

        bool charging = false;
        bool discharging = false;
        bool online = false;
        bool critical = false;

        foreach ((BatteryFigures figures, _) in read)
        {
            BatteryPowerState state = figures.Status?.PowerState ?? BatteryPowerState.None;
            charging |= state.HasFlag(BatteryPowerState.Charging);
            discharging |= state.HasFlag(BatteryPowerState.Discharging);
            online |= state.HasFlag(BatteryPowerState.OnLine);
            critical |= state.HasFlag(BatteryPowerState.Critical);
        }

        bool? onMains = system.OnMains ?? (online ? true : (read.Exists(item => item.Figures.Status is not null) ? false : null));

        BatteryState overall = charging ? BatteryState.Charging
            : discharging ? BatteryState.Discharging
            : online || onMains == true ? BatteryState.Idle
            : BatteryState.Unknown;

        double? remaining = SumAll(units, unit => unit.RemainingWattHours);
        double? full = SumAll(units, unit => unit.FullWattHours);
        double? design = SumAll(units, unit => unit.DesignWattHours);
        double? rate = SumAll(units, unit => unit.RateWatts);

        double? charge = remaining is { } now && full is > 0
            ? Math.Clamp(now * 100 / full.Value, 0, 100)
            : Average(units, unit => unit.ChargePercent) ?? system.Percent;

        TimeSpan? timeLeft = null;
        TimeSpan? timeToFull = null;

        if (overall == BatteryState.Discharging)
        {
            timeLeft = system.TimeLeft ?? (remaining is { } left && rate is < 0 ? Hours(left / -rate.Value) : null);
        }
        else if (overall == BatteryState.Charging && remaining is { } have && full is { } most && rate is > 0 && most > have)
        {
            timeToFull = Hours((most - have) / rate.Value);
        }

        int? cycles = null;

        foreach (BatteryUnit unit in units)
        {
            if (unit.CycleCount is { } count)
            {
                cycles = Math.Max(cycles ?? 0, count);
            }
        }

        return new BatteryReading(
            charge,
            overall,
            onMains,
            critical,
            rate,
            timeLeft,
            timeToFull,
            remaining,
            full,
            design,
            Health(full, design),
            cycles,
            units,
            scan.Refused ? RefusedDetail : null);
    }

    /// <summary>Puts one battery's driver figures into words and watts.</summary>
    public static BatteryUnit Describe(BatteryFigures figures)
    {
        ArgumentNullException.ThrowIfNull(figures);

        BatteryInformation? information = figures.Information;
        BatteryStatus? status = figures.Status;
        bool energy = information is { IsEnergy: true };

        double? full = energy ? WattHours(information!.Value.FullChargedCapacity) : null;
        double? design = energy ? WattHours(information!.Value.DesignedCapacity) : null;
        double? remaining = energy && status is { } now ? WattHours(now.Capacity) : null;

        double? charge = null;

        if (status is { Capacity: not BatteryLayout.Unknown } live
            && information is { FullChargedCapacity: > 0 and not BatteryLayout.Unknown } known)
        {
            charge = Math.Clamp(live.Capacity * 100.0 / known.FullChargedCapacity, 0, 100);
        }

        double? rate = energy && status is { } moving && moving.KnownRate is { } milliwatts
            ? Signed(milliwatts, moving.PowerState) / 1000.0
            : null;

        double? volts = status is { Voltage: > 0 and not BatteryLayout.Unknown } charged
            ? charged.Voltage / 1000.0
            : null;

        int? cycles = information is { CycleCount: > 0 and < 100_000 } counted ? (int)counted.CycleCount : null;

        return new BatteryUnit(
            figures.Name,
            figures.Maker,
            information is { } described ? ChemistryName(described.Chemistry) : null,
            figures.ManufactureDate,
            charge,
            rate,
            volts,
            figures.Celsius,
            remaining,
            full,
            design,
            Health(full, design),
            cycles,
            information is { } kind && kind.Capabilities.HasFlag(BatteryCapabilities.ShortTerm));
    }

    /// <summary>The chemistry code in words, as <c>BATTERY_INFORMATION</c> documents the codes.</summary>
    public static string? ChemistryName(string? code)
    {
        if (string.IsNullOrWhiteSpace(code))
        {
            return null;
        }

        return code.Trim().ToUpperInvariant() switch
        {
            "LION" or "LI-I" or "LI-ION" or "LIIO" => "Lithium-ion",
            "LIP" or "LIPO" or "LI-P" => "Lithium polymer",
            "PBAC" => "Lead acid",
            "NICD" => "Nickel-cadmium",
            "NIMH" => "Nickel-metal hydride",
            "NIZN" => "Nickel-zinc",
            "RAM" => "Rechargeable alkaline-manganese",
            _ => code.Trim(),
        };
    }

    /// <summary>
    /// Health is a ratio of today's full charge to the design capacity. A new
    /// battery can read a little over a hundred; anything past double is a
    /// misread field, not a battery.
    /// </summary>
    private static double? Health(double? full, double? design) =>
        full is > 0 && design is > 0 && full.Value / design.Value is var ratio && ratio <= 2
            ? ratio * 100
            : null;

    private const string RefusedDetail =
        "Windows would not let Windsock open the battery itself, so its capacity, rate and cycle count are not shown.";

    private static BatteryReading FromSummary(SystemPowerStatus system, bool refused)
    {
        bool charging = (system.BatteryFlag & 8) != 0;

        BatteryState state = charging ? BatteryState.Charging
            : system.OnMains == true ? BatteryState.Idle
            : system.OnMains == false ? BatteryState.Discharging
            : BatteryState.Unknown;

        return new BatteryReading(
            system.Percent,
            state,
            system.OnMains,
            (system.BatteryFlag & 4) != 0,
            null,
            state == BatteryState.Discharging ? system.TimeLeft : null,
            null,
            null,
            null,
            null,
            null,
            null,
            [],
            refused ? RefusedDetail : "The battery's own figures did not answer, so only Windows' summary is shown.");
    }

    /// <summary>
    /// Drivers are meant to sign the rate, but some report discharge as a
    /// positive number; the state flags decide.
    /// </summary>
    private static int Signed(int milliwatts, BatteryPowerState state) =>
        state.HasFlag(BatteryPowerState.Discharging) ? -Math.Abs(milliwatts)
        : state.HasFlag(BatteryPowerState.Charging) ? Math.Abs(milliwatts)
        : milliwatts;

    private static double? WattHours(uint milliwattHours) =>
        milliwattHours == BatteryLayout.Unknown ? null : milliwattHours / 1000.0;

    private static TimeSpan? Hours(double hours) =>
        double.IsFinite(hours) && hours is > 0 and < 100 ? TimeSpan.FromHours(hours) : null;

    private static double? SumAll(List<BatteryUnit> units, Func<BatteryUnit, double?> value)
    {
        double sum = 0;

        foreach (BatteryUnit unit in units)
        {
            if (value(unit) is not { } figure)
            {
                return null;
            }

            sum += figure;
        }

        return sum;
    }

    private static double? Average(List<BatteryUnit> units, Func<BatteryUnit, double?> value)
    {
        double sum = 0;
        int count = 0;

        foreach (BatteryUnit unit in units)
        {
            if (value(unit) is { } figure)
            {
                sum += figure;
                count++;
            }
        }

        return count > 0 ? sum / count : null;
    }

    public void Dispose() => _source.Dispose();
}
