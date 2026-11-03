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

    /// <summary>How long the router takes to answer a ping.</summary>
    RouterPing,

    /// <summary>
    /// How long the internet address takes to answer, when one is pinged at
    /// all: nothing public is contacted unless the person chose an address.
    /// </summary>
    InternetPing,

    /// <summary>How much has moved today, both ways, from history.</summary>
    TodayTotal,

    /// <summary>How much has moved this month, both ways, from history.</summary>
    MonthTotal,

    /// <summary>This PC's address on its network.</summary>
    LocalAddress,

    /// <summary>The address the internet sees, and its country, when looking it up is allowed.</summary>
    PublicAddress,

    /// <summary>One value from a JSON document the person points at.</summary>
    Feed,

    /// <summary>What is left of the data plan in use, and for how long.</summary>
    Plan,

    /// <summary>The Wi-Fi signal and band.</summary>
    WifiSignal,

    /// <summary>How full the battery is, marked when it is taking charge.</summary>
    BatteryCharge,

    /// <summary>Time to empty while discharging, or to full while charging.</summary>
    BatteryTime,

    /// <summary>A windsock that flutters faster as more moves.</summary>
    Pacer,

    /// <summary>Watts the processor package draws, where it can be read.</summary>
    ProcessorPower,

    /// <summary>How busy the NPU is, on a machine that has one.</summary>
    NpuLoad,
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

/// <summary>
/// What a click on the meter does - and a double or middle click, and the
/// same choices again in its menus and keyboard shortcuts.
/// </summary>
public enum MeterClick
{
    /// <summary>Opens Windsock where it was.</summary>
    Open,

    /// <summary>Nothing.</summary>
    Nothing,

    /// <summary>Opens Windsock on the Apps page.</summary>
    OpenApps,

    /// <summary>Opens Windsock on History.</summary>
    OpenHistory,

    /// <summary>Opens Windsock on Connection › Status.</summary>
    OpenConnection,

    /// <summary>Opens Windsock on the Taskbar page.</summary>
    OpenTaskbar,

    /// <summary>Opens the compact window, or closes it.</summary>
    Mini,

    /// <summary>Copies the rates on show, as text.</summary>
    CopySpeeds,

    /// <summary>Pauses recording history, or resumes it.</summary>
    PauseRecording,

    /// <summary>Takes the meter off the taskbar until it is switched on again.</summary>
    HideMeter,
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

    /// <summary>A response time.</summary>
    Ping,

    /// <summary>An amount moved over a day or a month.</summary>
    Total,

    /// <summary>An address.</summary>
    Address,

    /// <summary>A value from a JSON document.</summary>
    Feed,

    /// <summary>A data plan.</summary>
    Plan,

    /// <summary>Wi-Fi with no bars, or not connected.</summary>
    Wifi,

    /// <summary>Wi-Fi with one bar.</summary>
    WifiOne,

    /// <summary>Wi-Fi with two bars.</summary>
    WifiTwo,

    /// <summary>Wi-Fi with three bars.</summary>
    WifiThree,

    /// <summary>Wi-Fi with four bars.</summary>
    WifiFour,

    /// <summary>A battery running the machine.</summary>
    Battery,

    /// <summary>A battery taking charge.</summary>
    BatteryCharging,

    /// <summary>The animated windsock, drawn rather than picked from the icon font.</summary>
    Pacer,
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
    private List<MeterSpot> _spots = [];
    private MeterShortcuts _shortcuts = new();

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

    /// <summary>
    /// What a double click does. Nothing by default, so a single click acts at
    /// once rather than waiting to see whether a second one follows.
    /// </summary>
    public MeterClick DoubleClick { get; set; } = MeterClick.Nothing;

    /// <summary>What a middle click does.</summary>
    public MeterClick MiddleClick { get; set; } = MeterClick.Nothing;

    /// <summary>
    /// Whether the notification-area icon draws the rates itself, for people
    /// who keep the meter off. The icon then stays while the window is open.
    /// </summary>
    public bool TrayFigures { get; set; }

    /// <summary>Keyboard shortcuts that work from anywhere, none set by default.</summary>
    public MeterShortcuts Shortcuts
    {
        get => _shortcuts;
        set => _shortcuts = value ?? new MeterShortcuts();
    }

    /// <summary>
    /// Whether places that read a JSON value may ask their addresses for it.
    /// Off by default, and never turned on by a layout file: it is the one
    /// part of the meter that can reach beyond this PC.
    /// </summary>
    public bool Feeds { get; set; }

    /// <summary>
    /// Whether a public address place keeps itself current by asking the
    /// public address service every few minutes. Off by default: without it the
    /// place shows only what Connection's own Look up last found, and asks
    /// nothing of its own. Needs that service's own switch on as well.
    /// </summary>
    public bool RefreshPublicAddress { get; set; }

    /// <summary>Where the compact window was left, if it has been opened.</summary>
    public MeterSpot? MiniSpot { get; set; }

    /// <summary>
    /// Whether the welcome has been seen, or skipped. Not part of the meter's
    /// look, so putting everything back leaves it alone.
    /// </summary>
    public bool Welcomed { get; set; }

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
    /// Whether a meter fitted to its readings keeps room for the widest figure
    /// each can show, so its width holds still while the digits change.
    /// </summary>
    /// <remarks>
    /// On by default, and on for a meter saved before the choice existed: a
    /// file without it reads as true, which is the upgrade wanted. The width
    /// changing with every digit moved the taskbar's own buttons with it, the
    /// most visible annoyance a taskbar meter has. Only a meter fitted to its
    /// readings uses it; a fixed width is already steady.
    /// </remarks>
    public bool SteadyWidth { get; set; } = true;

    /// <summary>Which taskbars the meter goes on.</summary>
    public MeterMonitors Monitors { get; set; } = MeterMonitors.Main;

    /// <summary>
    /// The monitor picked when <see cref="Monitors"/> is
    /// <see cref="MeterMonitors.Chosen"/>, by the name Windows gives it.
    /// </summary>
    public string? ChosenMonitor { get; set; }

    /// <summary>What the meter does while something runs full screen in front of it.</summary>
    public MeterFullScreen FullScreen { get; set; } = MeterFullScreen.Hide;

    /// <summary>
    /// Whether the figures carry a soft shadow, so they stay legible over a
    /// see-through taskbar and a busy wallpaper.
    /// </summary>
    public bool Shadow { get; set; }

    /// <summary>What the meter does inside a Remote Desktop session.</summary>
    public MeterRemote Remote { get; set; } = MeterRemote.Name;

    /// <summary>Whether a floating meter stays where it is rather than following a drag.</summary>
    public bool Locked { get; set; }

    /// <summary>
    /// Where floating meters were put by hand, one spot per monitor. A monitor
    /// with no spot keeps its meter on its taskbar.
    /// </summary>
    public List<MeterSpot> Spots
    {
        get => _spots;
        set => _spots = value ?? [];
    }

    /// <summary>Where the floating meter on a monitor was put, if it was moved at all.</summary>
    /// <remarks>A loop rather than a search with a lambda: the meter asks on every patrol.</remarks>
    public MeterSpot? SpotOn(string monitor)
    {
        foreach (MeterSpot spot in Spots)
        {
            if (string.Equals(spot.Monitor, monitor, StringComparison.OrdinalIgnoreCase))
            {
                return spot;
            }
        }

        return null;
    }

    /// <summary>Remembers where the floating meter on a monitor was put.</summary>
    public void Remember(string monitor, int x, int y)
    {
        if (string.IsNullOrEmpty(monitor))
        {
            return;
        }

        if (SpotOn(monitor) is { } known)
        {
            known.X = x;
            known.Y = y;
            return;
        }

        Spots.Add(new MeterSpot { Monitor = monitor, X = x, Y = y });
    }

    /// <summary>
    /// Forgets where a monitor's floating meter was put, which sends it back
    /// to its taskbar. Says whether there was anything to forget.
    /// </summary>
    public bool Forget(string monitor) =>
        Spots.RemoveAll(spot => string.Equals(spot.Monitor, monitor, StringComparison.OrdinalIgnoreCase)) > 0;

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
                || DoubleClick != fresh.DoubleClick || MiddleClick != fresh.MiddleClick
                || TrayFigures != fresh.TrayFigures || Shortcuts.Any || Feeds != fresh.Feeds
                || RefreshPublicAddress != fresh.RefreshPublicAddress
                || MiniSpot is not null
                || Rows != fresh.Rows || Columns != fresh.Columns
                || Family != fresh.Family || Scale != fresh.Scale
                || ShowUnit != fresh.ShowUnit || ShowLabels != fresh.ShowLabels
                || TextSize != fresh.TextSize || Bold != fresh.Bold
                || Background != fresh.Background
                || Math.Abs(Corner - fresh.Corner) > 0.01
                || FitToContents != fresh.FitToContents || Width != fresh.Width
                || SteadyWidth != fresh.SteadyWidth
                || Monitors != fresh.Monitors
                || FullScreen != fresh.FullScreen || Shadow != fresh.Shadow
                || Remote != fresh.Remote || Locked != fresh.Locked
                || Spots.Count != 0
                || Slots.Count != fresh.Slots.Count)
            {
                return false;
            }

            for (int each = 0; each < Slots.Count; each++)
            {
                MeterSlot slot = Slots[each];

                if (slot.Field != fresh.Slots[each].Field
                    || slot.MarkIsChanged || slot.ValueIsChanged || slot.UnitIsChanged || slot.ShowsMore)
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
        DoubleClick = fresh.DoubleClick;
        MiddleClick = fresh.MiddleClick;
        TrayFigures = fresh.TrayFigures;
        Shortcuts = fresh.Shortcuts;
        Feeds = fresh.Feeds;
        RefreshPublicAddress = fresh.RefreshPublicAddress;
        MiniSpot = fresh.MiniSpot;
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
        SteadyWidth = fresh.SteadyWidth;
        Monitors = fresh.Monitors;
        ChosenMonitor = fresh.ChosenMonitor;
        FullScreen = fresh.FullScreen;
        Shadow = fresh.Shadow;
        Remote = fresh.Remote;
        Locked = fresh.Locked;
        Spots = fresh.Spots;
        Slots = fresh.Slots;
    }

    /// <summary>
    /// Brings a meter saved by an older Windsock up to date.
    /// </summary>
    public void Settle()
    {
        Narrow();

        // A choice of monitor that names none, or a spot that belongs to no
        // monitor, can only have come from a file edited by hand. Neither can
        // be honoured, and both would read as "changed" for ever.
        if (Monitors == MeterMonitors.Chosen && string.IsNullOrWhiteSpace(ChosenMonitor))
        {
            Monitors = MeterMonitors.Main;
            ChosenMonitor = null;
        }

        Spots.RemoveAll(spot => spot is null || string.IsNullOrWhiteSpace(spot.Monitor));

        // More places than the meter has came from a file edited by hand.
        int places = Rows * Columns;

        if (Slots.Count > places)
        {
            Slots.RemoveRange(places, Slots.Count - places);
        }

        // The same rules a layout file is held to (MeterLayoutFile), applied
        // to settings.json as it is loaded and to an imported settings file,
        // repairing rather than refusing: anything that would not pass is put
        // back to what it is when nothing was chosen. A colour above all - only
        // the hex forms are ever read as one, never "ContextColor <address>",
        // which WPF would fetch from that address.
        if (!Meter.MeterLayoutFile.IsColour(Background) || Background is MeterSlot.Own || Meter.MeterLevels.IsNamed(Background))
        {
            Background = null;
        }

        foreach (MeterSlot slot in Slots)
        {
            Settle(slot);
        }

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

    /// <summary>One place as a hand-edited or imported file left it, made safe to draw.</summary>
    private static void Settle(MeterSlot slot)
    {
        if (!Enum.IsDefined(slot.Field))
        {
            slot.Field = MeterField.Empty;
        }

        if (!Enum.IsDefined(slot.MarkSide) || !Enum.IsDefined(slot.ValueSide) || !Enum.IsDefined(slot.UnitSide))
        {
            slot.MarkSide = MeterSide.Right;
            slot.ValueSide = MeterSide.Right;
            slot.UnitSide = MeterSide.Right;
        }

        if (!Enum.IsDefined(slot.MarkAs))
        {
            slot.MarkAs = MeterMarkStyle.Icon;
        }

        if (!Enum.IsDefined(slot.Graph))
        {
            slot.Graph = MeterGraph.None;
        }

        if (slot.Family is { } family && !Enum.IsDefined(family))
        {
            slot.Family = null;
        }

        if (slot.Scale is { } scale && !Enum.IsDefined(scale))
        {
            slot.Scale = null;
        }

        if (slot.TextSize is { } size && !Enum.IsDefined(size))
        {
            slot.TextSize = null;
        }

        slot.LabelColour = Colour(slot.LabelColour);
        slot.ValueColour = Colour(slot.ValueColour);
        slot.UnitColour = Colour(slot.UnitColour);

        if (slot.Levels is { } levels)
        {
            levels.RemoveAll(level => level is null || !double.IsFinite(level.At));

            foreach (MeterLevel level in levels)
            {
                if (!Meter.MeterLayoutFile.IsColour(level.Colour) || level.Colour == MeterSlot.Own)
                {
                    level.Colour = Meter.MeterLevels.Fair;
                }
            }

            if (levels.Count > Meter.MeterLevels.Most)
            {
                levels.RemoveRange(Meter.MeterLevels.Most, levels.Count - Meter.MeterLevels.Most);
            }
        }

        if (slot.Rotate is { } turns)
        {
            turns.RemoveAll(field => !Enum.IsDefined(field) || field is MeterField.Empty or MeterField.Feed or MeterField.Pacer);

            if (turns.Count > MeterSlot.MostTurns)
            {
                turns.RemoveRange(MeterSlot.MostTurns, turns.Count - MeterSlot.MostTurns);
            }
        }

        slot.RotateSeconds = Math.Clamp(slot.RotateSeconds, 1, 60);

        if (slot.Card is { Length: > MeterSlot.LongestCard })
        {
            slot.Card = null;
        }

        if (slot.Feed is { } feed)
        {
            feed.Seconds = Math.Clamp(feed.Seconds, MeterFeed.SoonestSeconds, MeterFeed.LatestSeconds);

            if (!Meter.JsonField.IsPath(feed.Path))
            {
                feed.Path = string.Empty;
            }

            feed.Unit = Cut(feed.Unit);
            feed.Label = Cut(feed.Label);
        }
    }

    /// <summary>A place's colour if it is one a place may take, else nothing chosen.</summary>
    private static string? Colour(string? colour) =>
        Meter.MeterLayoutFile.IsColour(colour) ? colour : null;

    private static string Cut(string text) =>
        text.Length > MeterFeed.LongestText ? text[..MeterFeed.LongestText] : text;

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
                // Long: the columns asked for are whatever a file said, and a
                // file can say two thousand million.
                long index = ((long)row * _asked) + column;

                kept.Add(index < Slots.Count ? Slots[(int)index] : MeterSlot.Empty());
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
