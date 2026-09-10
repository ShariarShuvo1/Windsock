using Windsock.Core.Formatting;

using System.Text.Json.Serialization;

namespace Windsock.Core.Settings;

/// <summary>Where the taskbar meter puts itself.</summary>
public enum MeterPlacement
{
    Docked,

    Floating,
}

/// <summary>One thing the meter can be asked to show in a slot.</summary>
public enum MeterField
{
    Empty,

    Download,

    Upload,

    NetworkTotal,

    ProcessorLoad,

    ProcessorClock,

    ProcessorTemperature,

    BoardTemperature,

    GraphicsLoad,

    GraphicsTemperature,

    GraphicsMemory,

    GraphicsPower,

    MemoryLoad,

    MemoryUsed,

    StorageLoad,

    StorageTemperature,

    StorageUsed,
}

/// <summary>Which family a field belongs to, which decides its colour.</summary>
public enum MeterGroup
{
    Network,
    Processor,
    Graphics,
    Memory,
    Storage,

    System,
}

/// <summary>How the meter is coloured.</summary>
public enum MeterColouring
{
    Plain,

    ByGroup,
}

/// <summary>What clicking the meter does.</summary>
public enum MeterClick
{
    Open,

    Nothing,
}

/// <summary>Which part of the machine a reading is about.</summary>
public enum MeterMark
{
    None,

    Download,

    Upload,

    Network,

    Processor,

    Graphics,

    Memory,

    Storage,

    System,
}

/// <summary>Which end of its place a part of a reading sits at.</summary>
public enum MeterSide
{
    Left,

    Right,
}

/// <summary>How a reading is marked.</summary>
public enum MeterMarkStyle
{
    Icon,

    Letters,
}

/// <summary>How large the meter's figures are.</summary>
public enum MeterTextSize
{
    Small,
    Normal,
    Large,
    Huge,
}

/// <summary>
/// Everything the reader has chosen about the meter that sits in the taskbar.
/// </summary>
public sealed class TaskbarSettings
{
    /// <summary>The most slots the meter will lay out in each direction.</summary>
    public const int MaximumRows = 2;
    public const int MaximumColumns = 3;

    /// <summary>Width the meter takes when it is not sized to its contents.</summary>
    public const double DefaultWidth = 120;

    /// <summary>Narrowest the meter may be made.</summary>
    public const double MinimumWidth = 48;

    /// <summary>Widest the meter may be made.</summary>
    public const double MaximumWidth = 480;

    /// <summary>How rounded the meter's own surface is by default.</summary>
    public const double DefaultCorner = 6;

    /// <summary>Square corners.</summary>
    public const double MinimumCorner = 0;

    /// <summary>
    /// Round enough that the ends are semicircles on a meter of usual height.
    /// </summary>
    public const double MaximumCorner = 20;
    private int _rows = 2;
    private int _columns = 1;
    private int _asked = 1;
    private double _width = DefaultWidth;
    private double _corner = DefaultCorner;

    private List<MeterSlot> _slots =
    [
        new() { Field = MeterField.Download },
        new() { Field = MeterField.Upload },
    ];

    /// <summary>Whether the meter is shown at all. Off by default.</summary>
    public bool Enabled { get; set; }

    /// <summary>Whether the meter sits inside the taskbar or above it.</summary>
    public MeterPlacement Placement { get; set; } = MeterPlacement.Docked;

    /// <summary>How many rows of readings the meter shows.</summary>
    public int Rows
    {
        get => _rows;
        set => _rows = Math.Clamp(value, 1, MaximumRows);
    }

    /// <summary>How many columns of readings the meter shows.</summary>
    public int Columns
    {
        get => _columns;

        set
        {
            _asked = Math.Max(1, value);
            _columns = Math.Clamp(value, 1, MaximumColumns);
        }
    }

    /// <summary>
    /// What each slot shows, row by row.
    /// </summary>
    [System.Text.Json.Serialization.JsonConverter(typeof(MeterSlotsConverter))]
    public List<MeterSlot> Slots
    {
        get => _slots;
        set => _slots = value ?? [];
    }

    /// <summary>Whether rates are counted in bytes or bits.</summary>
    public RateFamily Family { get; set; } = RateFamily.Bytes;

    /// <summary>How far a rate is scaled before it is shown.</summary>
    public RateScale Scale { get; set; } = RateScale.Auto;

    /// <summary>Whether the unit is written after the figure.</summary>
    public bool ShowUnit { get; set; } = true;

    /// <summary>Whether each reading carries a mark saying what it is.</summary>
    public bool ShowLabels { get; set; } = true;

    /// <summary>How the figures are coloured.</summary>
    public MeterColouring Colouring { get; set; } = MeterColouring.Plain;

    /// <summary>
    /// How rounded the meter's own surface is, in pixels of corner radius.
    /// </summary>
    public double Corner
    {
        get => _corner;
        set => _corner = double.IsFinite(value)
            ? Math.Clamp(value, MinimumCorner, MaximumCorner)
            : DefaultCorner;
    }

    /// <summary>
    /// A colour painted behind the readings, or <see langword="null"/> for none.
    /// </summary>
    public string? Background { get; set; }

    /// <summary>How large the figures are.</summary>
    public MeterTextSize TextSize { get; set; } = MeterTextSize.Normal;

    /// <summary>Whether the figures are bold.</summary>
    public bool Bold { get; set; } = true;

    /// <summary>What a click on the meter does.</summary>
    public MeterClick Click { get; set; } = MeterClick.Open;

    /// <summary>What the slot at a position shows.</summary>
    public MeterField Slot(int row, int column) => At(row, column)?.Field ?? MeterField.Empty;

    /// <summary>
    /// Whether the meter takes exactly the width its readings need.
    /// </summary>
    public bool FitToContents { get; set; } = true;

    /// <summary>How wide the meter is when <see cref="FitToContents"/> is off.</summary>
    public double Width
    {
        get => _width;
        set => _width = double.IsFinite(value)
            ? Math.Clamp(value, MinimumWidth, MaximumWidth)
            : DefaultWidth;
    }

    /// <summary>
    /// The whole of a place: what it shows and how it shows it.
    /// </summary>
    public MeterSlot? At(int row, int column)
    {
        int index = (row * Columns) + column;

        return index >= 0 && index < Slots.Count ? Slots[index] : null;
    }

    /// <summary>Sets what the slot at a position shows, growing the list to fit.</summary>
    public void SetSlot(int row, int column, MeterField field)
    {
        if (Reach(row, column) is not { } slot)
        {
            return;
        }

        slot.Field = field;
    }

    /// <summary>
    /// The place at a position, made if the list does not reach that far.
    /// </summary>
    public MeterSlot? Reach(int row, int column)
    {
        int index = (row * Columns) + column;

        if (index < 0)
        {
            return null;
        }

        while (Slots.Count <= index)
        {
            Slots.Add(MeterSlot.Empty());
        }

        return Slots[index];
    }

    /// <summary>
    /// Puts a reading in a place, making the meter large enough to have one.
    /// </summary>
    public void Put(int row, int column, MeterField field)
    {
        if (row < 0 || column < 0 || row >= MaximumRows || column >= MaximumColumns)
        {
            return;
        }

        Resize(Math.Max(Rows, row + 1), Math.Max(Columns, column + 1));
        SetSlot(row, column, field);
    }

    private void Resize(int rows, int columns)
    {
        if (rows == Rows && columns == Columns)
        {
            return;
        }

        List<MeterSlot> kept = new(rows * columns);

        for (int row = 0; row < rows; row++)
        {
            for (int column = 0; column < columns; column++)
            {
                kept.Add(row < Rows && column < Columns
                    ? At(row, column)?.Copy() ?? MeterSlot.Empty()
                    : MeterSlot.Empty());
            }
        }

        Rows = rows;
        Columns = columns;
        Slots = kept;
    }

    /// <summary>
    /// Whether every choice about the meter is still the one it shipped with.
    /// </summary>
    [JsonIgnore]
    public bool IsDefault
    {
        get
        {
            TaskbarSettings fresh = new();

            if (Placement != fresh.Placement || Click != fresh.Click
                || Rows != fresh.Rows || Columns != fresh.Columns
                || Family != fresh.Family || Scale != fresh.Scale
                || ShowUnit != fresh.ShowUnit || ShowLabels != fresh.ShowLabels
                || TextSize != fresh.TextSize || Bold != fresh.Bold
                || Background != fresh.Background
                || Math.Abs(Corner - fresh.Corner) > 0.01
                || FitToContents != fresh.FitToContents || Width != fresh.Width
                || Slots.Count != fresh.Slots.Count)
            {
                return false;
            }

            for (int each = 0; each < Slots.Count; each++)
            {
                MeterSlot slot = Slots[each];

                if (slot.Field != fresh.Slots[each].Field
                    || slot.MarkIsChanged || slot.ValueIsChanged || slot.UnitIsChanged)
                {
                    return false;
                }
            }

            return true;
        }
    }

    /// <summary>
    /// Puts every choice about the meter back the way it shipped.
    /// </summary>
    public void Reset()
    {
        TaskbarSettings fresh = new();

        Placement = fresh.Placement;
        Click = fresh.Click;
        Rows = fresh.Rows;
        Columns = fresh.Columns;
        Family = fresh.Family;
        Scale = fresh.Scale;
        ShowUnit = fresh.ShowUnit;
        ShowLabels = fresh.ShowLabels;
        Background = fresh.Background;
        Corner = fresh.Corner;
        Colouring = fresh.Colouring;
        TextSize = fresh.TextSize;
        Bold = fresh.Bold;
        FitToContents = fresh.FitToContents;
        Width = fresh.Width;
        Slots = fresh.Slots;
    }

    /// <summary>
    /// Brings a meter saved by an older Windsock up to date.
    /// </summary>
    public void Settle()
    {
        Narrow();

        if (Colouring != MeterColouring.ByGroup)
        {
            return;
        }

        foreach (MeterSlot slot in Slots)
        {
            slot.LabelColour ??= MeterSlot.Own;
            slot.ValueColour ??= MeterSlot.Own;
        }

        Colouring = MeterColouring.Plain;
    }

    private void Narrow()
    {
        if (_asked <= _columns)
        {
            return;
        }

        List<MeterSlot> kept = new(Rows * Columns);

        for (int row = 0; row < Rows; row++)
        {
            for (int column = 0; column < Columns; column++)
            {
                int index = (row * _asked) + column;

                kept.Add(index < Slots.Count ? Slots[index] : MeterSlot.Empty());
            }
        }

        Slots = kept;
        _asked = _columns;

        Trim();
    }

    /// <summary>
    /// Drops any trailing row or column with nothing in it.
    /// </summary>
    public bool Trim()
    {
        bool changed = false;

        while (Columns > 1 && Blank(column: Columns - 1))
        {
            Resize(Rows, Columns - 1);
            changed = true;
        }

        while (Rows > 1 && Blank(row: Rows - 1))
        {
            // A row is a run at the end of the list, so it needs no reflow.
            int keep = (Rows - 1) * Columns;

            if (Slots.Count > keep)
            {
                Slots.RemoveRange(keep, Slots.Count - keep);
            }

            Rows--;
            changed = true;
        }

        return changed;
    }

    private bool Blank(int row = -1, int column = -1)
    {
        int along = row < 0 ? Rows : Columns;

        for (int each = 0; each < along; each++)
        {
            if ((row < 0 ? Slot(each, column) : Slot(row, each)) != MeterField.Empty)
            {
                return false;
            }
        }

        return true;
    }
}
