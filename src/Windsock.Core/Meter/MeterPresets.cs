using Windsock.Core.Settings;

namespace Windsock.Core.Meter;

/// <summary>A ready-made meter: a name, a line saying what it is for, and its places.</summary>
/// <param name="Key">A stable name for it.</param>
/// <param name="Name">What the page calls it.</param>
/// <param name="Note">One line on who it suits.</param>
/// <param name="Rows">Its rows.</param>
/// <param name="Columns">Its columns.</param>
/// <param name="Places">What each place shows, row by row.</param>
/// <param name="NeedsBattery">Whether it only makes sense on a machine with a battery.</param>
public sealed record MeterPreset(
    string Key,
    string Name,
    string Note,
    int Rows,
    int Columns,
    IReadOnlyList<MeterSlot> Places,
    bool NeedsBattery = false)
{
    /// <summary>
    /// Puts this layout on a meter: its rows, columns and places replace the
    /// meter's, and every other choice - where it sits, how it looks, what a
    /// click does - is left alone.
    /// </summary>
    public void ApplyTo(TaskbarSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);

        settings.Rows = Rows;
        settings.Columns = Columns;
        settings.Slots = [.. Places.Select(place => place.Copy())];
    }

    /// <summary>Whether a meter is laid out exactly as this preset lays it out.</summary>
    public bool Matches(TaskbarSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);

        if (settings.Rows != Rows || settings.Columns != Columns)
        {
            return false;
        }

        for (int each = 0; each < Rows * Columns; each++)
        {
            MeterSlot? have = each < settings.Slots.Count ? settings.Slots[each] : null;
            MeterSlot want = Places[each];

            if ((have?.Field ?? MeterField.Empty) != want.Field || (have?.Graph ?? MeterGraph.None) != want.Graph)
            {
                return false;
            }
        }

        return true;
    }
}

/// <summary>The layouts the Taskbar page and the welcome offer with one click.</summary>
/// <remarks>
/// Each is built from readings that need nothing installed and nothing sent,
/// so a preset never turns on anything that reaches beyond this PC: the
/// response time is the router's, which never leaves the network.
/// </remarks>
public static class MeterPresets
{
    /// <summary>Download over upload: what the meter ships with.</summary>
    public static MeterPreset Network { get; } = new(
        "network",
        "Network only",
        "Download over upload, and nothing else",
        2,
        1,
        [Place(MeterField.Download), Place(MeterField.Upload)]);

    /// <summary>The rates, the router's response time and what today has used.</summary>
    public static MeterPreset Connection { get; } = new(
        "connection",
        "Network and response",
        "The rates, how fast the router answers, and today's total",
        2,
        2,
        [Place(MeterField.Download), Place(MeterField.RouterPing), Place(MeterField.Upload), Place(MeterField.TodayTotal)]);

    /// <summary>For games: the rates, the processor and the graphics card.</summary>
    public static MeterPreset Gamer { get; } = new(
        "gamer",
        "Gamer",
        "The rates beside the processor, the graphics card and its temperature",
        2,
        3,
        [
            Place(MeterField.Download), Place(MeterField.ProcessorLoad), Place(MeterField.GraphicsLoad),
            Place(MeterField.Upload), Place(MeterField.RouterPing), Place(MeterField.GraphicsTemperature),
        ]);

    /// <summary>For a laptop: the rates and the battery.</summary>
    public static MeterPreset Laptop { get; } = new(
        "laptop",
        "Laptop",
        "The rates beside the battery's charge and time left",
        2,
        2,
        [Place(MeterField.Download), Place(MeterField.BatteryCharge), Place(MeterField.Upload), Place(MeterField.BatteryTime)],
        NeedsBattery: true);

    /// <summary>The whole machine, no network at all.</summary>
    public static MeterPreset Machine { get; } = new(
        "machine",
        "The PC",
        "Processor, graphics, memory and disk, without the network",
        2,
        2,
        [Place(MeterField.ProcessorLoad), Place(MeterField.MemoryLoad), Place(MeterField.GraphicsLoad), Place(MeterField.StorageLoad)]);

    /// <summary>One figure with its last minute behind it, for a crowded taskbar.</summary>
    public static MeterPreset Compact { get; } = new(
        "compact",
        "Compact",
        "Both ways in one figure, with the last minute drawn behind it",
        1,
        1,
        [new MeterSlot { Field = MeterField.NetworkTotal, Graph = MeterGraph.Bars }]);

    /// <summary>The windsock that flutters with the traffic, and the total beside it.</summary>
    public static MeterPreset Playful { get; } = new(
        "playful",
        "Windsock",
        "A windsock that flutters harder the more moves, beside the total",
        1,
        2,
        [Place(MeterField.Pacer), Place(MeterField.NetworkTotal)]);

    /// <summary>Every preset, in the order the page offers them.</summary>
    public static IReadOnlyList<MeterPreset> All { get; } = [Network, Connection, Gamer, Laptop, Machine, Compact, Playful];

    /// <summary>The presets that suit a machine: the laptop one only where there is a battery.</summary>
    public static IReadOnlyList<MeterPreset> For(bool hasBattery) =>
        [.. All.Where(preset => hasBattery || !preset.NeedsBattery)];

    /// <summary>A preset by its key, or nothing.</summary>
    public static MeterPreset? Find(string? key) =>
        All.FirstOrDefault(preset => string.Equals(preset.Key, key, StringComparison.Ordinal));

    private static MeterSlot Place(MeterField field) => new() { Field = field };
}
