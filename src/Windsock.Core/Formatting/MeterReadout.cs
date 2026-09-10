using System.Globalization;
using System.Text;
using Windsock.Core.Hardware;
using Windsock.Core.Settings;

namespace Windsock.Core.Formatting;

/// <summary>
/// Everything the meter could be asked about, at one instant.
/// </summary>
public readonly record struct MeterReading(
    double DownloadBytesPerSecond,
    double UploadBytesPerSecond,
    SystemSnapshot Machine)
{
    /// <summary>A reading before anything has been measured.</summary>
    public static MeterReading Empty { get; } = new(0, 0, SystemSnapshot.Empty);
}

/// <summary>
/// One reading on the meter, split into the parts that are styled differently.
/// </summary>
public readonly record struct MeterLine(
    MeterField Field,
    MeterGroup Group,
    string Label,
    string Value,
    string Unit);

/// <summary>
/// Turns a reading into the lines the taskbar meter draws.
/// </summary>
public static class MeterReadout
{
    /// <summary>Shown where a machine will not answer for a field.</summary>
    public const string Unknown = "—";

    /// <summary>Builds the line for one slot.</summary>
    public static MeterLine Compose(MeterField field, in MeterReading reading, TaskbarSettings settings) =>
        Compose(new MeterSlot { Field = field }, reading, settings);

    /// <summary>
    /// Turns one cell into the line it draws, in that cell's own units.
    /// </summary>
    public static MeterLine Compose(MeterSlot slot, in MeterReading reading, TaskbarSettings settings)
    {
        ArgumentNullException.ThrowIfNull(slot);
        ArgumentNullException.ThrowIfNull(settings);

        (string value, string unit) = Measure(
            slot.Field,
            reading,
            slot.Family ?? settings.Family,
            slot.Scale ?? settings.Scale);

        return new MeterLine(
            slot.Field,
            GroupOf(slot.Field),
            (slot.ShowLabel ?? settings.ShowLabels) ? Label(slot.Field) : string.Empty,
            value,
            (slot.ShowUnit ?? settings.ShowUnit) ? unit : string.Empty);
    }

    /// <summary>Which part of the machine a field belongs to.</summary>
    public static MeterGroup GroupOf(MeterField field) => field switch
    {
        MeterField.ProcessorLoad or MeterField.ProcessorClock
            or MeterField.ProcessorTemperature => MeterGroup.Processor,

        MeterField.BoardTemperature => MeterGroup.System,

        MeterField.GraphicsLoad or MeterField.GraphicsTemperature
            or MeterField.GraphicsMemory or MeterField.GraphicsPower => MeterGroup.Graphics,

        MeterField.MemoryLoad or MeterField.MemoryUsed => MeterGroup.Memory,

        MeterField.StorageLoad or MeterField.StorageTemperature
            or MeterField.StorageUsed => MeterGroup.Storage,

        _ => MeterGroup.Network,
    };

    /// <summary>
    /// The short mark drawn beside a figure.
    /// </summary>
    public static string Label(MeterField field) => field switch
    {
        MeterField.Download => "↓",
        MeterField.Upload => "↑",
        MeterField.NetworkTotal => "↕",
        MeterField.ProcessorLoad => "CPU",
        MeterField.ProcessorClock => "CLK",
        MeterField.ProcessorTemperature => "CPU",
        MeterField.BoardTemperature => "SYS",
        MeterField.GraphicsLoad => "GPU",
        MeterField.GraphicsTemperature => "GPU",
        MeterField.GraphicsMemory => "VRAM",
        MeterField.GraphicsPower => "GPU",
        MeterField.MemoryLoad => "RAM",
        MeterField.MemoryUsed => "RAM",
        MeterField.StorageLoad => "DISK",
        MeterField.StorageTemperature => "DISK",
        MeterField.StorageUsed => "DISK",
        _ => string.Empty,
    };

    /// <summary>
    /// Which part of the machine a reading describes, for the picture that
    /// marks it.
    /// </summary>
    public static MeterMark MarkOf(MeterField field) => field switch
    {
        MeterField.Download => MeterMark.Download,
        MeterField.Upload => MeterMark.Upload,
        MeterField.NetworkTotal => MeterMark.Network,
        _ => GroupOf(field) switch
        {
            MeterGroup.Processor => MeterMark.Processor,
            MeterGroup.Graphics => MeterMark.Graphics,
            MeterGroup.Memory => MeterMark.Memory,
            MeterGroup.Storage => MeterMark.Storage,
            MeterGroup.System => MeterMark.System,
            _ => MeterMark.None,
        },
    };

    /// <summary>
    /// What the unit beside a figure reads as, before there is a figure.
    /// </summary>
    public static string Unit(MeterField field, RateFamily family, RateScale scale) => field switch
    {
        MeterField.Download or MeterField.Upload or MeterField.NetworkTotal =>
            Rate(Middling, family, scale).Unit,

        MeterField.ProcessorLoad or MeterField.GraphicsLoad or MeterField.GraphicsMemory
            or MeterField.MemoryLoad or MeterField.StorageLoad or MeterField.StorageUsed => "%",

        MeterField.ProcessorClock => "GHz",

        MeterField.BoardTemperature or MeterField.GraphicsTemperature
            or MeterField.StorageTemperature or MeterField.ProcessorTemperature => "°C",

        MeterField.GraphicsPower => "W",

        MeterField.MemoryUsed => SizeFormatter.Split(MiddlingBytes).Unit,

        _ => string.Empty,
    };

    private const double Middling = 2 * 1024 * 1024;
    private const long MiddlingBytes = 8L * 1024 * 1024 * 1024;

    /// <summary>The field's name, as a picker shows it.</summary>
    public static string Name(MeterField field) => field switch
    {
        MeterField.Download => "Download rate",
        MeterField.Upload => "Upload rate",
        MeterField.NetworkTotal => "Network total",
        MeterField.ProcessorLoad => "CPU usage",
        MeterField.ProcessorClock => "CPU clock",
        MeterField.ProcessorTemperature => "CPU temperature",
        MeterField.BoardTemperature => "System temperature",
        MeterField.GraphicsLoad => "GPU usage",
        MeterField.GraphicsTemperature => "GPU temperature",
        MeterField.GraphicsMemory => "GPU memory",
        MeterField.GraphicsPower => "GPU power",
        MeterField.MemoryLoad => "Memory usage",
        MeterField.MemoryUsed => "Memory used",
        MeterField.StorageLoad => "Disk activity",
        MeterField.StorageTemperature => "Disk temperature",
        MeterField.StorageUsed => "Disk space used",
        _ => "Nothing",
    };

    /// <summary>Whether a field needs the machine to be measured as well as the network.</summary>
    public static bool NeedsMachine(MeterField field) =>
        field is not (MeterField.Empty
            or MeterField.Download
            or MeterField.Upload
            or MeterField.NetworkTotal);

    /// <summary>
    /// The full reading, for the tooltip that appears on hover.
    /// </summary>
    public static string Tooltip(
        IEnumerable<MeterSlot> slots,
        in MeterReading reading,
        TaskbarSettings settings)
    {
        ArgumentNullException.ThrowIfNull(slots);
        ArgumentNullException.ThrowIfNull(settings);

        var text = new StringBuilder();
        var seen = new HashSet<MeterField>();

        foreach (MeterSlot slot in slots)
        {
            MeterField field = slot.Field;

            if (field == MeterField.Empty || !seen.Add(field))
            {
                continue;
            }
            (string value, string unit) = Measure(
                field,
                reading,
                slot.Family ?? settings.Family,
                slot.Scale ?? settings.Scale);

            if (text.Length > 0)
            {
                text.Append('\n');
            }

            text.Append(CultureInfo.CurrentCulture, $"{Name(field)}  {value}");

            if (unit.Length > 0)
            {
                text.Append(CultureInfo.CurrentCulture, $" {unit}");
            }
        }

        return text.Length > 0 ? text.ToString() : "Windsock";
    }

    private static (string Value, string Unit) Measure(
        MeterField field,
        in MeterReading reading,
        RateFamily family,
        RateScale scale)
    {
        SystemSnapshot machine = reading.Machine;

        return field switch
        {
            MeterField.Download => Rate(reading.DownloadBytesPerSecond, family, scale),
            MeterField.Upload => Rate(reading.UploadBytesPerSecond, family, scale),
            MeterField.NetworkTotal =>
                Rate(Usable(reading.DownloadBytesPerSecond) + Usable(reading.UploadBytesPerSecond), family, scale),

            MeterField.ProcessorLoad => machine.HasValue
                ? Percent(machine.Processor.BusyPercent)
                : Missing,

            MeterField.ProcessorClock => Clock(machine),
            MeterField.ProcessorTemperature => machine.Processor.Celsius is { } degrees
                ? Temperature(degrees)
                : Missing,
            MeterField.BoardTemperature => machine.BoardCelsius is { } heat
                ? Temperature(heat)
                : Missing,

            MeterField.GraphicsLoad => Adapter(machine) is { } load
                ? Percent(load.BusyPercent)
                : Missing,

            MeterField.GraphicsTemperature => Adapter(machine)?.Celsius is { } celsius
                ? Temperature(celsius)
                : Missing,

            MeterField.GraphicsMemory => Adapter(machine) is { MemoryTotal: > 0 } card
                ? Percent(card.MemoryUsed * 100.0 / card.MemoryTotal)
                : Missing,

            MeterField.GraphicsPower => Adapter(machine)?.Watts is { } watts
                ? (Whole(watts), "W")
                : Missing,

            MeterField.MemoryLoad => machine is { HasValue: true, Memory.Total: > 0 }
                ? Percent(machine.Memory.UsedPercent)
                : Missing,

            MeterField.MemoryUsed => machine is { HasValue: true, Memory.Total: > 0 }
                ? SizeFormatter.Split(machine.Memory.Used)
                : Missing,

            MeterField.StorageLoad => Busiest(machine) is { } drive
                ? Percent(drive.BusyPercent)
                : Missing,

            MeterField.StorageTemperature => Hottest(machine) is { } celsius
                ? Temperature(celsius)
                : Missing,

            MeterField.StorageUsed => Fullest(machine) is { } percent
                ? Percent(percent)
                : Missing,

            _ => (string.Empty, string.Empty),
        };
    }

    private static (string Value, string Unit) Missing => (Unknown, string.Empty);

    private static (string Value, string Unit) Rate(double bytesPerSecond, RateFamily family, RateScale scale) =>
        RateFormatter.Split(bytesPerSecond, family, scale);

    private static (string Value, string Unit) Percent(double percent) =>
        (Whole(Math.Clamp(percent, 0, 100)), "%");

    private static (string Value, string Unit) Temperature(double celsius) =>
        (Whole(celsius), "°C");

    private static (string Value, string Unit) Clock(SystemSnapshot machine)
    {
        if (!machine.HasValue || machine.Processor.Megahertz <= 0)
        {
            return Missing;
        }

        double megahertz = machine.Processor.Megahertz;

        return megahertz >= 1000
            ? ((megahertz / 1000).ToString("F2", CultureInfo.InvariantCulture), "GHz")
            : (Whole(megahertz), "MHz");
    }

    private static GraphicsReading? Adapter(SystemSnapshot machine)
    {
        if (!machine.HasValue)
        {
            return null;
        }

        GraphicsReading? busiest = null;

        foreach (GraphicsReading card in machine.Graphics)
        {
            if (busiest is null || card.BusyPercent > busiest.BusyPercent)
            {
                busiest = card;
            }
        }

        return busiest;
    }

    private static DriveReading? Busiest(SystemSnapshot machine)
    {
        if (!machine.HasValue)
        {
            return null;
        }

        DriveReading? busiest = null;

        foreach (DriveReading drive in machine.Drives)
        {
            if (busiest is null || drive.BusyPercent > busiest.BusyPercent)
            {
                busiest = drive;
            }
        }

        return busiest;
    }

    private static double? Hottest(SystemSnapshot machine)
    {
        if (!machine.HasValue)
        {
            return null;
        }

        double? hottest = null;

        foreach (DriveReading drive in machine.Drives)
        {
            if (drive.Celsius is { } celsius && (hottest is null || celsius > hottest))
            {
                hottest = celsius;
            }
        }

        return hottest;
    }

    private static double? Fullest(SystemSnapshot machine)
    {
        if (!machine.HasValue)
        {
            return null;
        }

        double? fullest = null;

        foreach (DriveReading drive in machine.Drives)
        {
            if (drive.Used + drive.Free > 0 && (fullest is null || drive.UsedPercent > fullest))
            {
                fullest = drive.UsedPercent;
            }
        }

        return fullest;
    }

    private static string Whole(double value) =>
        Math.Round(value).ToString("F0", CultureInfo.InvariantCulture);

    private static double Usable(double rate) =>
        double.IsFinite(rate) && rate > 0 ? rate : 0;
}
