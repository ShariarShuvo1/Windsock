using System.Collections.ObjectModel;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using Windsock.Core.Formatting;
using Windsock.Core.Hardware;
using Windsock.Core.Power;

namespace Windsock.App.ViewModels;

/// <summary>
/// The logical processors of one kind of core, as a group of bars under a
/// heading: the performance cores, the efficiency cores, or - on a chip with
/// one kind - all of them under no heading at all.
/// </summary>
public sealed partial class CoreGroupViewModel(CoreKind kind) : ObservableObject
{
    /// <summary>Which kind of core the group holds.</summary>
    public CoreKind Kind { get; } = kind;

    /// <summary>The group's heading, or nothing on a chip with one kind of core.</summary>
    public string Title { get; } = kind switch
    {
        CoreKind.Performance => "Performance cores",
        CoreKind.Efficiency => "Efficiency cores",
        _ => string.Empty,
    };

    /// <summary>How many cores and threads, and how busy they are together.</summary>
    [ObservableProperty]
    public partial string Note { get; set; } = string.Empty;

    /// <summary>One bar per logical processor in the group.</summary>
    public ObservableCollection<MeterRowViewModel> Rows { get; } = [];

    /// <summary>The group in words, as UI Automation names the item.</summary>
    public override string ToString() => Title.Length > 0 ? string.Concat(Title, ", ", Note) : "Logical processors";
}

/// <summary>One process in a busiest list.</summary>
public sealed partial class BusyProcessRowViewModel : ObservableObject
{
    /// <summary>The program's name, without its <c>.exe</c>.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Spoken))]
    public partial string Name { get; set; } = string.Empty;

    /// <summary>The figure, in the list's unit.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Spoken))]
    public partial string Value { get; set; } = string.Empty;

    /// <summary>How much of the bar is filled, from nothing to one.</summary>
    [ObservableProperty]
    public partial double Fraction { get; set; }

    /// <summary>Which process exactly: its image name, id and start.</summary>
    [ObservableProperty]
    public partial string Detail { get; set; } = string.Empty;

    /// <summary>The row as a screen reader says it.</summary>
    public string Spoken => string.Concat(Name, ", ", Value);

    /// <summary>
    /// The row in words: what UI Automation names a list item by when the
    /// item is a plain object rather than a control.
    /// </summary>
    public override string ToString() => Spoken;

    /// <summary>Which part's list it is in, for the bar's colour.</summary>
    [ObservableProperty]
    public partial SystemPart Part { get; set; }
}

/// <summary>
/// The processes doing the most to one part of the machine: the processor,
/// memory, or files.
/// </summary>
/// <remarks>
/// A short list, refreshed every second while its page is on screen and not
/// read at all otherwise (<see cref="SystemMonitor.WatchBusiest"/>). Its rows
/// are kept and refilled rather than replaced, so the list does not flicker and
/// a screen reader is not handed a new list every second.
/// </remarks>
public sealed partial class BusiestListViewModel : ObservableObject
{
    /// <summary>How many processes the list shows.</summary>
    public const int Shown = 8;

    /// <summary>The processes, busiest first.</summary>
    public ObservableCollection<BusyProcessRowViewModel> Rows { get; } = [];

    /// <summary>The band's title.</summary>
    [ObservableProperty]
    public partial string Title { get; private set; } = "Busiest processes";

    /// <summary>What the figure is, in a line under the title.</summary>
    [ObservableProperty]
    public partial string Note { get; private set; } = string.Empty;

    /// <summary>What the empty list says, while it is empty.</summary>
    [ObservableProperty]
    public partial string EmptyTitle { get; private set; } = "Reading the processes";

    /// <summary>Why it is empty, and what will appear.</summary>
    [ObservableProperty]
    public partial string EmptyNote { get; private set; } = string.Empty;

    /// <summary>Whether there are rows to show.</summary>
    [ObservableProperty]
    public partial bool HasRows { get; private set; }

    /// <summary>Starts again for a part, before its first reading.</summary>
    public void Reset(SystemPart part)
    {
        Rows.Clear();
        HasRows = false;
        (Title, Note) = Describe(part);
        EmptyTitle = "Reading the processes";
        EmptyNote = part == SystemPart.Memory
            ? "Every running process is read at once, with no need to open any of them."
            : "These figures are differences between two readings a second apart, so they arrive with the second.";
    }

    /// <summary>Takes a reading for a part.</summary>
    /// <param name="part">Processor, memory or storage.</param>
    /// <param name="busiest">The busiest processes, or nothing while they are not being read.</param>
    /// <param name="physicalMemory">The memory fitted, which memory's bars are drawn against.</param>
    public void Show(SystemPart part, BusiestProcesses? busiest, long physicalMemory)
    {
        if (busiest is null)
        {
            return;
        }

        // By program, as Task Manager's Processes tab groups them: a browser's
        // dozen processes are one row with their figures added together.
        IReadOnlyList<ProcessLoad> loads = part switch
        {
            SystemPart.Memory => busiest.MemoryByProgram,
            SystemPart.Storage => busiest.FileIoByProgram,
            _ => busiest.ProcessorByProgram,
        };

        double fileTop = 1;

        foreach (ProcessLoad load in loads)
        {
            fileTop = Math.Max(fileTop, load.FileBytesPerSecond);
        }

        int count = Math.Min(Shown, loads.Count);

        while (Rows.Count > count)
        {
            Rows.RemoveAt(Rows.Count - 1);
        }

        while (Rows.Count < count)
        {
            Rows.Add(new BusyProcessRowViewModel());
        }

        for (int index = 0; index < count; index++)
        {
            ProcessLoad load = loads[index];
            BusyProcessRowViewModel row = Rows[index];

            row.Name = load.Count > 1
                ? string.Create(CultureInfo.InvariantCulture, $"{Program(load.Name)} ({load.Count})")
                : Program(load.Name);
            row.Detail = Identify(load);
            row.Part = part;

            switch (part)
            {
                case SystemPart.Memory:
                    row.Value = SizeFormatter.Format(load.PrivateBytes);
                    row.Fraction = physicalMemory > 0 ? Math.Clamp((double)load.PrivateBytes / physicalMemory, 0, 1) : 0;
                    break;

                case SystemPart.Storage:
                    row.Value = RateFormatter.Format(load.FileBytesPerSecond, RateFamily.Bytes, RateScale.Auto);

                    // Files have no ceiling, so the busiest fills its bar and the
                    // rest are drawn against it.
                    row.Fraction = Math.Clamp(load.FileBytesPerSecond / fileTop, 0, 1);
                    break;

                default:
                    row.Value = Percent(load.ProcessorPercent);
                    row.Fraction = Math.Clamp(load.ProcessorPercent / 100, 0, 1);
                    break;
            }
        }

        HasRows = count > 0;

        if (count > 0)
        {
            return;
        }

        (EmptyTitle, EmptyNote) = !busiest.HasRates && part != SystemPart.Memory
            ? ("Reading the processes", "These figures are differences between two readings a second apart, so they arrive with the second.")
            : part switch
            {
                SystemPart.Storage => ("No file activity", "No process read or wrote a file in the last second."),
                SystemPart.Memory => ("No processes listed", "Windows did not list any process."),
                _ => ("The processor is idle", "No process used the processor in the last second."),
            };
    }

    private static (string Title, string Note) Describe(SystemPart part) => part switch
    {
        SystemPart.Memory => (
            "Busiest processes",
            "Private working set: memory each program's processes hold that no other process shares, as Task Manager's Memory column counts it."),
        SystemPart.Storage => (
            "Busiest by file activity",
            "What each program read and wrote through the file system in the last second, including what Windows' cache answered and files on network shares - not what reached the drives, which Windows only counts per process for an administrator's trace."),
        _ => (
            "Busiest processes",
            "Each program's share of the whole processor over the last second, its processes added together, as Task Manager counts it."),
    };

    /// <summary>A share, to a tenth below ten so a quiet process does not read as nothing.</summary>
    private static string Percent(double percent) =>
        percent < 10
            ? string.Create(CultureInfo.InvariantCulture, $"{percent:F1}%")
            : string.Create(CultureInfo.InvariantCulture, $"{percent:F0}%");

    /// <summary>An image name as a reader knows the program: <c>chrome.exe</c> is Chrome's <c>chrome</c>.</summary>
    public static string Program(string image) =>
        image.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) && image.Length > 4
            ? image[..^4]
            : image;

    private static string Identify(ProcessLoad load)
    {
        if (load.Count > 1)
        {
            return string.Create(CultureInfo.CurrentCulture, $"{load.Count} processes running {load.Name}, added together; the busiest is process {load.ProcessId}");
        }

        string started = load.Started is { } at
            ? string.Concat(", started ", ClockFormat.Current.Day(at.ToLocalTime()))
            : string.Empty;

        return string.Create(CultureInfo.CurrentCulture, $"{load.Name}, process {load.ProcessId}{started}");
    }
}

/// <summary>One neural processor, as a card on the Graphics page.</summary>
public sealed partial class NpuCardViewModel(string luid, string name) : ObservableObject
{
    /// <summary>Its identifier, for matching it between readings.</summary>
    public string Key { get; } = luid;

    /// <summary>What the driver calls it.</summary>
    public string Name { get; } = name;

    /// <summary>Its busiest engine.</summary>
    [ObservableProperty]
    public partial string Busy { get; set; } = MeterReadout.Unknown;

    /// <summary>The same, from nothing to one.</summary>
    [ObservableProperty]
    public partial double BusyFraction { get; set; }

    /// <summary>System memory it is using, and its own where it has any.</summary>
    [ObservableProperty]
    public partial string Memory { get; set; } = MeterReadout.Unknown;

    /// <summary>Each kind of engine and how busy it is.</summary>
    public ObservableCollection<MeterRowViewModel> Engines { get; } = [];

    /// <summary>The processes using it.</summary>
    public ObservableCollection<MeterRowViewModel> Processes { get; } = [];

    /// <summary>The card in words, as UI Automation names the item.</summary>
    public override string ToString() => string.Concat(Name, ", ", Busy);
}

/// <summary>One battery, as a tile on the Battery page.</summary>
public sealed class BatteryUnitViewModel(BatteryUnit unit)
{
    /// <summary>What it is called, by its driver or failing that by what it is.</summary>
    public string Name { get; } = unit.Name ?? (unit.IsShortTerm ? "Uninterruptible power supply" : "Battery");

    /// <summary>Who made it, its chemistry and when, as far as it says.</summary>
    public string Description { get; } = string.Join(
        " · ",
        new[]
        {
            unit.Maker,
            unit.Chemistry,
            unit.ManufactureDate is { } made ? "made " + made.ToString("MMMM yyyy", ClockFormat.Current.Format) : null,
        }.Where(part => !string.IsNullOrWhiteSpace(part)));

    /// <summary>How full it is.</summary>
    public string Charge { get; } = unit.ChargePercent is { } charge
        ? string.Create(CultureInfo.InvariantCulture, $"{charge:F0}%")
        : MeterReadout.Unknown;

    /// <summary>Health, from nothing to one, for its bar.</summary>
    public double HealthFraction { get; } = unit.HealthPercent is { } health ? Math.Clamp(health / 100, 0, 1) : 0;

    /// <summary>Whether a health figure is known.</summary>
    public bool HasHealth { get; } = unit.HealthPercent is not null;

    /// <summary>Health in words: what it holds against what it held new.</summary>
    public string Health { get; } = BatteryText.Health(unit.HealthPercent, unit.FullWattHours, unit.DesignWattHours);

    /// <summary>Its cycles, voltage, temperature and rate, as far as it reports them.</summary>
    public string Reading { get; } = string.Join(
        " · ",
        new[]
        {
            unit.CycleCount is { } counted ? string.Create(CultureInfo.CurrentCulture, $"{counted:N0} cycles") : "cycles not counted",
            unit.Volts is { } volts ? string.Create(CultureInfo.InvariantCulture, $"{volts:F2} V") : null,
            unit.Celsius is { } celsius ? TemperatureScale.Format(celsius) : null,
            unit.RateWatts is { } watts ? BatteryText.Rate(watts) : null,
        }.Where(part => part is not null));

    /// <summary>The battery in words, as UI Automation names the item.</summary>
    public override string ToString() => string.Concat(Name, ", ", Charge, ", ", Health);
}

/// <summary>The battery's figures put into words, shared by the page and its tiles.</summary>
public static class BatteryText
{
    /// <summary>Watts in or out: "Charging at 40 W", "Using 10.3 W".</summary>
    public static string Rate(double watts) =>
        watts switch
        {
            > 0.05 => string.Create(CultureInfo.InvariantCulture, $"Charging at {Watts(watts)}"),
            < -0.05 => string.Create(CultureInfo.InvariantCulture, $"Using {Watts(-watts)}"),
            _ => "Neither charging nor using",
        };

    /// <summary>Watts to one decimal below ten, whole above.</summary>
    public static string Watts(double watts) =>
        Math.Abs(watts) < 10
            ? string.Create(CultureInfo.InvariantCulture, $"{watts:F1} W")
            : string.Create(CultureInfo.InvariantCulture, $"{watts:F0} W");

    /// <summary>Health against design, with the two capacities behind it.</summary>
    public static string Health(double? percent, double? full, double? design)
    {
        if (percent is not { } health)
        {
            return "This battery does not report its design capacity";
        }

        return full is { } holds && design is { } held
            ? string.Create(CultureInfo.InvariantCulture, $"{health:F0}% of new · {holds:F1} Wh of {held:F1} Wh")
            : string.Create(CultureInfo.InvariantCulture, $"{health:F0}% of new");
    }

    /// <summary>A span of hours and minutes: "2 h 35 min", "40 min".</summary>
    public static string Span(TimeSpan span)
    {
        int minutes = (int)Math.Round(span.TotalMinutes);

        return minutes >= 60
            ? string.Create(CultureInfo.InvariantCulture, $"{minutes / 60} h {minutes % 60:D2} min")
            : string.Create(CultureInfo.InvariantCulture, $"{Math.Max(1, minutes)} min");
    }

    /// <summary>What the battery is doing, in a few words.</summary>
    public static string State(BatteryReading reading)
    {
        ArgumentNullException.ThrowIfNull(reading);

        return reading.State switch
        {
            BatteryState.Charging => "Charging",
            BatteryState.Discharging when reading.OnMains == true => "Plugged in, but running down",
            BatteryState.Discharging => "On battery",
            BatteryState.Idle when reading.ChargePercent >= 99 => "Plugged in, full",
            BatteryState.Idle => "Plugged in, not charging",
            _ => reading.OnMains == true ? "Plugged in" : "On battery",
        };
    }

    /// <summary>Time left or time to full, whichever applies, in words.</summary>
    public static string Time(BatteryReading reading)
    {
        ArgumentNullException.ThrowIfNull(reading);

        return reading switch
        {
            { TimeLeft: { } left } => Span(left) + " left",
            { TimeToFull: { } full } => Span(full) + " to full",
            { State: BatteryState.Idle } => "Not running down",
            _ => MeterReadout.Unknown,
        };
    }

    /// <summary>The power mode and saver, in Settings' words.</summary>
    public static string Mode(PowerReading power)
    {
        ArgumentNullException.ThrowIfNull(power);

        if (power.Mode is not { } mode)
        {
            return power.SaverOn ? PowerModeNames.Saver(Environment.OSVersion.Version.Build) + " on" : string.Empty;
        }

        string described = PowerModeNames.Describe(mode);

        return power.SaverOn && mode != PowerMode.Saver
            ? string.Concat(described, ", ", PowerModeNames.Saver(Environment.OSVersion.Version.Build).ToLowerInvariant(), " on")
            : mode == PowerMode.Saver ? described + " on" : described;
    }
}
