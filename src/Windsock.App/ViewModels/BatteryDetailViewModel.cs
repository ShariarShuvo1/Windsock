using System.Collections.ObjectModel;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using Windsock.Core.Formatting;
using Windsock.Core.Power;

namespace Windsock.App.ViewModels;

/// <summary>
/// Everything the Battery page says under its chart: the figures, each
/// battery on its own, and the machine's power.
/// </summary>
public sealed partial class BatteryDetailViewModel : ObservableObject
{
    /// <summary>What it holds now, against what it holds full.</summary>
    [ObservableProperty]
    public partial string Capacity { get; private set; } = MeterReadout.Unknown;

    /// <summary>What a full charge holds, in a note under the figure.</summary>
    [ObservableProperty]
    public partial string CapacityNote { get; private set; } = string.Empty;

    /// <summary>Health against design.</summary>
    [ObservableProperty]
    public partial string Health { get; private set; } = MeterReadout.Unknown;

    /// <summary>The capacities behind the health figure, or why there is none.</summary>
    [ObservableProperty]
    public partial string HealthNote { get; private set; } = string.Empty;

    /// <summary>Charge cycles.</summary>
    [ObservableProperty]
    public partial string Cycles { get; private set; } = MeterReadout.Unknown;

    /// <summary>What a cycle is, or why there is no count.</summary>
    [ObservableProperty]
    public partial string CyclesNote { get; private set; } = string.Empty;

    /// <summary>Watts in or out.</summary>
    [ObservableProperty]
    public partial string Rate { get; private set; } = MeterReadout.Unknown;

    /// <summary>What the rate means now.</summary>
    [ObservableProperty]
    public partial string RateNote { get; private set; } = string.Empty;

    /// <summary>Time left or time to full.</summary>
    [ObservableProperty]
    public partial string Time { get; private set; } = MeterReadout.Unknown;

    /// <summary>Where the time comes from.</summary>
    [ObservableProperty]
    public partial string TimeNote { get; private set; } = string.Empty;

    /// <summary>Mains or battery.</summary>
    [ObservableProperty]
    public partial string Source { get; private set; } = MeterReadout.Unknown;

    /// <summary>The effective power mode, in Settings' words.</summary>
    [ObservableProperty]
    public partial string Mode { get; private set; } = MeterReadout.Unknown;

    /// <summary>Whether battery (energy) saver is on.</summary>
    [ObservableProperty]
    public partial string Saver { get; private set; } = MeterReadout.Unknown;

    /// <summary>What battery saver is called on this Windows, as a figure's label.</summary>
    public string SaverLabel { get; } = PowerModeNames.Saver(Environment.OSVersion.Version.Build).ToUpperInvariant();

    /// <summary>Why some figures are missing, when they are; otherwise empty.</summary>
    [ObservableProperty]
    public partial string Detail { get; private set; } = string.Empty;

    /// <summary>Each battery on its own.</summary>
    public ObservableCollection<BatteryUnitViewModel> Units { get; } = [];

    /// <summary>Whether the battery's own controller described it, so it can be listed.</summary>
    [ObservableProperty]
    public partial bool HasUnits { get; private set; }

    /// <summary>One line about the battery, for under the page's title.</summary>
    [ObservableProperty]
    public partial string Summary { get; private set; } = string.Empty;

    private string _unitsSeen = string.Empty;

    /// <summary>Takes the latest battery and power readings.</summary>
    public void Update(BatteryReading? battery, PowerReading power)
    {
        ArgumentNullException.ThrowIfNull(power);

        Source = power.Source switch
        {
            PowerSource.Mains => "Plugged in",
            PowerSource.Battery => "On battery",
            _ => MeterReadout.Unknown,
        };

        Mode = power.Mode is { } mode ? PowerModeNames.Describe(mode) : MeterReadout.Unknown;
        Saver = power.SaverOn ? "On" : "Off";

        if (battery is null)
        {
            return;
        }

        Detail = battery.Detail ?? string.Empty;

        Capacity = battery.RemainingWattHours is { } now
            ? string.Create(CultureInfo.InvariantCulture, $"{now:F1} Wh")
            : battery.ChargePercent is { } charge ? string.Create(CultureInfo.InvariantCulture, $"{charge:F0}%") : MeterReadout.Unknown;

        CapacityNote = battery.FullWattHours is { } holds
            ? string.Create(CultureInfo.InvariantCulture, $"of {holds:F1} Wh when full")
            : "Charge known only as a share";

        Health = battery.HealthPercent is { } health
            ? string.Create(CultureInfo.InvariantCulture, $"{health:F0}%")
            : MeterReadout.Unknown;

        HealthNote = battery.HealthPercent is not null && battery.DesignWattHours is { } design && battery.FullWattHours is { } fullNow
            ? string.Create(CultureInfo.InvariantCulture, $"{fullNow:F1} of {design:F1} Wh new")
            : "Design capacity not reported";

        Cycles = battery.CycleCount is { } cycles
            ? cycles.ToString("N0", CultureInfo.CurrentCulture)
            : MeterReadout.Unknown;

        CyclesNote = battery.CycleCount is not null
            ? "Counted by the battery"
            : "Not counted by this battery";

        Rate = battery.RateWatts is { } watts ? BatteryText.Watts(Math.Abs(watts)) : MeterReadout.Unknown;

        RateNote = battery.RateWatts switch
        {
            > 0.05 => "Going in",
            < -0.05 => "Being used",
            { } => "Neither going in nor out",
            null => "Not measured by this battery",
        };

        Time = BatteryText.Time(battery);
        TimeNote = battery switch
        {
            { TimeLeft: not null } => "Windows' estimate",
            { TimeToFull: not null } => "At today's rate; slows near full",
            _ => BatteryText.State(battery),
        };

        // Rebuilt only when what is fitted changes; the figures in a tile are
        // a minute old at most anyway, the static part being read once a minute.
        string seen = string.Join("|", battery.Units.Select(unit => string.Create(
            CultureInfo.InvariantCulture,
            $"{unit.Name}{unit.FullWattHours}{unit.CycleCount}{unit.ChargePercent:F0}{unit.RateWatts:F1}")));

        if (!string.Equals(seen, _unitsSeen, StringComparison.Ordinal))
        {
            _unitsSeen = seen;
            Units.Clear();

            foreach (BatteryUnit unit in battery.Units)
            {
                Units.Add(new BatteryUnitViewModel(unit));
            }
        }

        HasUnits = Units.Count > 0;

        BatteryUnit? first = battery.Units.Count > 0 ? battery.Units[0] : null;

        Summary = first is null
            ? "Windows' summary of the battery"
            : battery.Units.Count > 1
                ? string.Create(CultureInfo.InvariantCulture, $"{battery.Units.Count} batteries")
                : string.Join("  ·  ", new[] { first.Name, first.Chemistry }.Where(part => !string.IsNullOrWhiteSpace(part)).DefaultIfEmpty("Battery"));
    }
}
