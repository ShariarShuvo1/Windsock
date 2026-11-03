using System.Collections.ObjectModel;
using System.Globalization;
using System.Windows.Media;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Windsock.App.Services;
using Windsock.App.Theming;
using Windsock.Core.Docking;
using Windsock.Core.Formatting;
using Windsock.Core.Hardware;
using Windsock.Core.Settings;

namespace Windsock.App.ViewModels;

/// <summary>One entry in a picker, carrying the value it selects.</summary>
public sealed record MeterOption<T>(T Value, string Label)
{
    /// <summary>
    /// Returns the label. A record's generated ToString prints every member,
    /// which is what UI Automation would otherwise announce for the item.
    /// </summary>
    public override string ToString() => Label;
}

/// <summary>A colour offered as a swatch: how it is written down, and what it is called.</summary>
/// <param name="Hex">The colour as it is written down, #rrggbb.</param>
/// <param name="Name">What it is called, for anyone who cannot see the swatch.</param>
public sealed record NamedColour(string Hex, string Name)
{
    /// <summary>The name and the colour as written, for the swatch's tooltip.</summary>
    public string Tip => string.Concat(Name, " (", Hex, ")");

    /// <summary>
    /// Returns the name. A record's generated ToString prints every member,
    /// which is what UI Automation would otherwise announce for the item.
    /// </summary>
    public override string ToString() => Name;
}

/// <summary>
/// The ground painted behind the whole meter, and how it is chosen.
/// </summary>
public sealed partial class MeterGroundViewModel : ObservableObject
{
    private static readonly NamedColour[] Palette =
    [
        new("#000000", "Black"), new("#FFFFFF", "White"), new("#0E2231", "Dark navy"), new("#1F2933", "Charcoal"),
        new("#243B53", "Slate blue"), new("#2A2438", "Dark plum"), new("#1E3231", "Dark teal"), new("#3A2618", "Dark brown"),
    ];

    private readonly Func<string?> _read;
    private readonly Action<string?> _write;

    /// <summary>Builds the choice over the meter's ground.</summary>
    public MeterGroundViewModel(Func<string?> read, Action<string?> write)
    {
        _read = read;
        _write = write;
    }

    /// <summary>The grounds offered without mixing one.</summary>
    public IReadOnlyList<NamedColour> Grounds { get; } = Palette;

    /// <summary>What the settings say, as it is written down.</summary>
    public string? Chosen => _read();

    /// <summary>Whether the meter paints no ground at all.</summary>
    public bool IsNone => string.IsNullOrWhiteSpace(Chosen);

    /// <summary>The ground as it is drawn, for the swatch beside the choice.</summary>
    public Brush Showing => Frozen(Now());

    /// <summary>
    /// The same colour at full strength, so a swatch shows the hue rather than
    /// whatever the opacity has faded it to.
    /// </summary>
    public Brush Solid
    {
        get
        {
            Color colour = Now();
            return Frozen(Color.FromArgb(0xFF, colour.R, colour.G, colour.B));
        }
    }

    /// <summary>How much red is in the ground.</summary>
    public byte Red
    {
        get => Now().R;
        set => Mix(Now().A, value, Green, Blue);
    }

    /// <summary>How much green is in it.</summary>
    public byte Green
    {
        get => Now().G;
        set => Mix(Now().A, Red, value, Blue);
    }

    /// <summary>How much blue is in it.</summary>
    public byte Blue
    {
        get => Now().B;
        set => Mix(Now().A, Red, Green, value);
    }

    /// <summary>
    /// How solid the ground is, as a percentage.
    /// </summary>
    public double Opacity
    {
        get => Math.Round(Now().A * 100.0 / 255.0);
        set => Mix(Solidity(value), Red, Green, Blue);
    }

    private static byte Solidity(double percent) =>
        (byte)Math.Clamp(Math.Round(percent * 255.0 / 100.0), 0, 255);

    /// <summary>The ground, written the way it is written down.</summary>
    public string Hex
    {
        get
        {
            Color colour = Now();

            return string.Format(
                CultureInfo.InvariantCulture,
                "#{0:X2}{1:X2}{2:X2}{3:X2}",
                colour.A,
                colour.R,
                colour.G,
                colour.B);
        }

        set
        {
            if (Read(value) is { } colour)
            {
                Mix(colour.A, colour.R, colour.G, colour.B);
            }
        }
    }

    /// <summary>Paints the ground, or takes it away.</summary>
    [RelayCommand]
    public void Paint(string? fill)
    {
        if (fill is not { Length: > 0 } named)
        {
            _write(null);
            Told();
            return;
        }

        if (Read(named) is not { } colour)
        {
            return;
        }

        byte strength = colour.A;

        if (named.Length <= 7)
        {
            strength = IsNone ? (byte)0xC0 : Now().A;
        }

        Mix(strength, colour.R, colour.G, colour.B);
    }

    /// <summary>Says the ground has changed, however it changed.</summary>
    public void Told() => OnPropertyChanged(string.Empty);

    private Color Now() => Chosen is { } named && Read(named) is { } colour
        ? colour
        : Color.FromArgb(0, 0, 0, 0);

    private void Mix(byte alpha, byte red, byte green, byte blue)
    {
        _write(string.Format(
            CultureInfo.InvariantCulture, "#{0:X2}{1:X2}{2:X2}{3:X2}", alpha, red, green, blue));

        Told();
    }

    private static Color? Read(string fill) => HexColour.TryParse(fill, out Color found) ? found : null;

    private static SolidColorBrush Frozen(Color colour)
    {
        SolidColorBrush brush = new(colour);
        brush.Freeze();
        return brush;
    }
}

/// <summary>
/// The colour of one part of one cell, and every way of choosing it.
/// </summary>
public sealed partial class InkChoiceViewModel : ObservableObject
{
    private static readonly NamedColour[] Palette =
    [
        new("#4C9AFF", "Blue"), new("#4CD97A", "Green"), new("#FF9F43", "Orange"), new("#FF6B6B", "Coral"),
        new("#C77DFF", "Violet"), new("#4ECDC4", "Teal"), new("#FFD93D", "Yellow"), new("#FFFFFF", "White"),
    ];

    private readonly Func<string?> _read;
    private readonly Action<string?> _write;
    private readonly Func<Color> _own;
    private readonly Func<Color> _plain;

    /// <summary>Builds the choice over one part of one cell.</summary>
    public InkChoiceViewModel(
        Func<string?> read,
        Action<string?> write,
        Func<Color> own,
        Func<Color> plain)
    {
        _read = read;
        _write = write;
        _own = own;
        _plain = plain;
    }

    /// <summary>The colours offered without mixing one.</summary>
    public IReadOnlyList<NamedColour> Inks { get; } = Palette;

    /// <summary>What the cell says, as it is written down.</summary>
    public string? Chosen => _read();

    /// <summary>The colour this reading has elsewhere in Windsock.</summary>
    public Brush OwnInk => Frozen(_own());

    /// <summary>The colour this part is actually drawn in.</summary>
    public Brush Showing => Frozen(Now());

    /// <summary>Whether this part follows the taskbar own text colour.</summary>
    public bool IsPlain => Chosen is null;

    /// <summary>Whether it takes the colour its reading has elsewhere.</summary>
    public bool IsOwn => Chosen == MeterSlot.Own;

    /// <summary>How much red is in the colour it is drawn in.</summary>
    public byte Red
    {
        get => Now().R;
        set => Mix(value, Green, Blue);
    }

    /// <summary>How much green is in it.</summary>
    public byte Green
    {
        get => Now().G;
        set => Mix(Red, value, Blue);
    }

    /// <summary>How much blue is in it.</summary>
    public byte Blue
    {
        get => Now().B;
        set => Mix(Red, Green, value);
    }

    /// <summary>
    /// The colour it is drawn in, written the way it is written down.
    /// </summary>
    public string Hex
    {
        get
        {
            Color colour = Now();

            return string.Format(
                CultureInfo.InvariantCulture, "#{0:X2}{1:X2}{2:X2}", colour.R, colour.G, colour.B);
        }

        set
        {
            if (Read(value) is { } colour)
            {
                Mix(colour.R, colour.G, colour.B);
            }
        }
    }

    /// <summary>Paints this part, or puts it back where nothing is named.</summary>
    [RelayCommand]
    public void Paint(string? ink)
    {
        _write(ink is { Length: > 0 } named ? named : null);
        Told();
    }

    /// <summary>Says the part has changed, however it changed.</summary>
    public void Told() => OnPropertyChanged(string.Empty);

    private Color Now() => Chosen switch
    {
        null => _plain(),
        MeterSlot.Own => _own(),
        { } named => Read(named) ?? _plain(),
    };

    private void Mix(byte red, byte green, byte blue) => Paint(string.Format(
        CultureInfo.InvariantCulture, "#{0:X2}{1:X2}{2:X2}", red, green, blue));

    private static Color? Read(string ink) => HexColour.TryParse(ink, out Color found) ? found : null;

    private static SolidColorBrush Frozen(Color colour)
    {
        SolidColorBrush brush = new(colour);
        brush.Freeze();

        return brush;
    }
}

/// <summary>
/// One position on the meter, and what the reader has put in it.
/// </summary>
public sealed partial class MeterSlotChoiceViewModel : ObservableObject
{
    private readonly Action<MeterSlotChoiceViewModel> _changed;
    private readonly TaskbarSettings _meter;
    private readonly MeterSlotContext _context;

    public MeterSlotChoiceViewModel(
        int row,
        int column,
        MeterSlot cell,
        TaskbarSettings meter,
        MeterSlotContext context,
        Action<MeterSlotChoiceViewModel> changed)
    {
        ArgumentNullException.ThrowIfNull(cell);
        ArgumentNullException.ThrowIfNull(context);

        Row = row;
        Column = column;
        Cell = cell;
        _meter = meter;
        _context = context;
        _changed = changed;

        Fields =
        [
            .. Enum.GetValues<MeterField>()
                .Where(field => field != MeterField.Empty && (field == cell.Field || context.Offered(field)))
                .Select(field => new MeterOption<MeterField>(field, MeterReadout.Name(field))),
        ];

        MarkInk = new InkChoiceViewModel(
            () => Cell.LabelColour, ink => Set(() => Cell.LabelColour = ink), Own, Plain);

        ValueInk = new InkChoiceViewModel(
            () => Cell.ValueColour, ink => Set(() => Cell.ValueColour = ink), Own, Plain);

        UnitInk = new InkChoiceViewModel(
            () => Cell.UnitColour, ink => Set(() => Cell.UnitColour = ink), Own, Support);

        ReadMore();
    }

    /// <summary>The colour of the mark, and every way of choosing it.</summary>
    public InkChoiceViewModel MarkInk { get; }

    /// <summary>The colour of the figure.</summary>
    public InkChoiceViewModel ValueInk { get; }

    /// <summary>The colour of the unit.</summary>
    public InkChoiceViewModel UnitInk { get; }

    /// <summary>
    /// Reads the three inks again, for when the scheme underneath has changed.
    /// </summary>
    public void Repaint()
    {
        MarkInk.Told();
        ValueInk.Told();
        UnitInk.Told();
    }

    /// <summary>Which row this slot is in, from zero.</summary>
    public int Row { get; }

    /// <summary>Which column this slot is in, from zero.</summary>
    public int Column { get; }

    /// <summary>
    /// The cell itself: what it shows, and everything about how it shows it.
    /// </summary>
    public MeterSlot Cell { get; }

    /// <summary>How the slot is named in the editor, as row and column.</summary>
    public string Address => string.Format(
        CultureInfo.CurrentCulture,
        "ROW {0} · COL {1}",
        Row + 1,
        Column + 1);

    /// <summary>
    /// Where the slot is and what it shows, as a screen reader names the cell:
    /// "Row 1, column 1: Download rate".
    /// </summary>
    public override string ToString() => string.Format(
        CultureInfo.CurrentCulture,
        "Row {0}, column {1}: {2}",
        Row + 1,
        Column + 1,
        IsFilled ? ValueText : "empty");

    /// <summary>What this slot shows.</summary>
    public MeterField Field
    {
        get => Cell.Field;
        set
        {
            if (value == Cell.Field)
            {
                return;
            }

            Set(() =>
            {
                Cell.Field = value;

                // Levels and a card belong to the reading they were set for;
                // a feed's address is kept only by a slot that still reads one.
                Cell.Levels = null;
                Cell.Card = null;
                Cell.Rotate = Cell.Rotate?.Where(each => each != value).ToList() is { Count: > 0 } left ? left : null;

                if (value == MeterField.Feed)
                {
                    Cell.Feed ??= new MeterFeed();
                    Cell.Rotate = null;
                }
                else
                {
                    Cell.Feed = null;
                }

                if (value == MeterField.Pacer)
                {
                    Cell.Rotate = null;
                    Cell.Graph = MeterGraph.None;
                }
            });

            ReadMore();
        }
    }

    /// <summary>Whether anything is in this slot.</summary>
    public bool IsFilled => Cell.IsFilled;

    /// <summary>Whether the last change was the cross being pressed.</summary>
    public bool WasCleared { get; private set; }

    // ----------------------------------------------------------- the mark

    /// <summary>Whether this reading is marked with what it is.</summary>
    public bool ShowLabel
    {
        get => Cell.ShowLabel ?? _meter.ShowLabels;
        set => Set(() => Cell.ShowLabel = value);
    }

    /// <summary>The mark in letters, as the meter would write it.</summary>
    public string LabelText => MeterReadout.Label(Field, Cell);

    /// <summary>Which part of the machine this reading is about.</summary>
    public MeterMark Mark => MeterReadout.MarkOf(Field);

    /// <summary>Whether the mark is drawn as a picture rather than written.</summary>
    public bool MarkIsIcon => Cell.MarkAs == MeterMarkStyle.Icon;

    [RelayCommand]
    private void MarkAsPicture() => Set(() => Cell.MarkAs = MeterMarkStyle.Icon);

    [RelayCommand]
    private void MarkAsLetters() => Set(() => Cell.MarkAs = MeterMarkStyle.Letters);

    /// <summary>Which end of the place the mark sits at.</summary>
    public MeterSide MarkSide => Cell.MarkSide;

    [RelayCommand]
    private void MarkOnTheLeft() => Set(() => Cell.MarkSide = MeterSide.Left);

    [RelayCommand]
    private void MarkOnTheRight() => Set(() => Cell.MarkSide = MeterSide.Right);

    [RelayCommand(CanExecute = nameof(MarkIsChanged))]
    private void ResetMark() => Set(Cell.PlainMark);

    /// <summary>Whether anything about the mark was chosen.</summary>
    public bool MarkIsChanged => Cell.MarkIsChanged;

    // ---------------------------------------------------------- the figure

    /// <summary>What the reading is called.</summary>
    public string ValueText => MeterReadout.Name(Field);

    /// <summary>Whether this reading counts in bytes or bits.</summary>
    public RateFamily Family
    {
        get => Cell.Family ?? _meter.Family;
        set => Set(() => Cell.Family = value);
    }

    /// <summary>How far this reading is scaled.</summary>
    public RateScale Scale
    {
        get => Cell.Scale ?? _meter.Scale;
        set => Set(() => Cell.Scale = value);
    }

    /// <summary>How large this figure is drawn.</summary>
    public MeterTextSize TextSize
    {
        get => Cell.TextSize ?? _meter.TextSize;
        set => Set(() => Cell.TextSize = value);
    }

    /// <summary>Whether this figure carries extra weight.</summary>
    public bool Bold
    {
        get => Cell.Bold ?? _meter.Bold;
        set => Set(() => Cell.Bold = value);
    }

    /// <summary>Which end of the place the figure sits at.</summary>
    public MeterSide ValueSide => Cell.ValueSide;

    [RelayCommand]
    private void ValueOnTheLeft() => Set(() => Cell.ValueSide = MeterSide.Left);

    [RelayCommand]
    private void ValueOnTheRight() => Set(() => Cell.ValueSide = MeterSide.Right);

    [RelayCommand(CanExecute = nameof(ValueIsChanged))]
    private void ResetValue()
    {
        Set(Cell.PlainValue);
        ReadMore();
    }

    /// <summary>Whether anything about the figure was chosen.</summary>
    public bool ValueIsChanged => Cell.ValueIsChanged;

    // ------------------------------------------------------------ the unit

    /// <summary>Whether the unit is written after this figure.</summary>
    public bool ShowUnit
    {
        get => Cell.ShowUnit ?? _meter.ShowUnit;
        set => Set(() => Cell.ShowUnit = value);
    }

    /// <summary>The unit itself, in this cell's own units.</summary>
    public string UnitText => Field == MeterField.Feed
        ? Cell.Feed?.Unit ?? string.Empty
        : MeterReadout.Unit(Field, Family, Scale, _context.Temperature());

    /// <summary>Which end of the place the unit sits at.</summary>
    public MeterSide UnitSide => Cell.UnitSide;

    [RelayCommand]
    private void UnitOnTheLeft() => Set(() => Cell.UnitSide = MeterSide.Left);

    [RelayCommand]
    private void UnitOnTheRight() => Set(() => Cell.UnitSide = MeterSide.Right);

    /// <summary>
    /// How many of the three parts are drawn against the left edge.
    /// </summary>
    public int PartsOnTheLeft => Cell.PartsOnTheLeft;

    [RelayCommand(CanExecute = nameof(UnitIsChanged))]
    private void ResetUnit() => Set(Cell.PlainUnit);

    /// <summary>Whether anything about the unit was chosen.</summary>
    public bool UnitIsChanged => Cell.UnitIsChanged;

    private Color Own() => Ink(MeterPalette.Own(Field, MeterReadout.GroupOf(Field), _context.Dark()));

    private Color Plain() => Ink(MeterPalette.Plain(_context.Dark()));

    private Color Support() => Ink(MeterPalette.Support(_context.Dark()));

    private static Color Ink(Brush brush) =>
        brush is SolidColorBrush solid ? solid.Color : Colors.White;

    /// <summary>
    /// Every reading this cell could show on this machine, and the one it
    /// shows even where it is no longer offered, so the picker still says so.
    /// </summary>
    public IReadOnlyList<MeterOption<MeterField>> Fields { get; }

    /// <summary>
    /// Whether this edition has a reading at all. The Store edition has no
    /// processor temperature or processor power: both need a driver in the
    /// kernel, which a Store app may not install.
    /// </summary>
    internal static bool IsOffered(MeterField field) => field switch
    {
        MeterField.Empty => false,
#if STORE
        MeterField.ProcessorTemperature or MeterField.ProcessorPower => false,
#endif
        _ => true,
    };

    /// <summary>Bytes or bits, for this cell.</summary>
    public IReadOnlyList<FamilyOption> Families { get; } =
        [.. RateUnits.Families.Select(family => new FamilyOption(family, RateUnits.Name(family)))];

    /// <summary>
    /// How far to scale, named in this cell's own family.
    /// </summary>
    public IReadOnlyList<ScaleOption> Scales =>
        [.. RateUnits.Scales.Select(scale => new ScaleOption(scale, RateUnits.Name(Family, scale)))];

    /// <summary>How large this figure may be drawn.</summary>
    public IReadOnlyList<MeterOption<MeterTextSize>> TextSizes { get; } =
    [
        new(MeterTextSize.Small, "Small"),
        new(MeterTextSize.Normal, "Normal"),
        new(MeterTextSize.Large, "Large"),
        new(MeterTextSize.Huge, "Extra large"),
    ];

    [RelayCommand(CanExecute = nameof(IsFilled))]
    private void Clear()
    {
        WasCleared = true;
        Field = MeterField.Empty;
        WasCleared = false;
    }

    private void Set(Action change)
    {
        change();

        OnPropertyChanged(string.Empty);
        ClearCommand.NotifyCanExecuteChanged();
        ResetMarkCommand.NotifyCanExecuteChanged();
        ResetValueCommand.NotifyCanExecuteChanged();
        ResetUnitCommand.NotifyCanExecuteChanged();
        MarkInk.Told();
        ValueInk.Told();
        UnitInk.Told();

        _changed(this);
    }
}

/// <summary>
/// A place in the grid with no reading in it yet.
/// </summary>
public sealed class MeterGridAddViewModel
{
    public MeterGridAddViewModel(int row, int column, IRelayCommand add)
    {
        Row = row;
        Column = column;
        Add = add;

        Name = string.Format(
            CultureInfo.CurrentCulture,
            "Add a reading at row {0}, column {1}",
            row + 1,
            column + 1);
    }

    /// <summary>Which row this place is in, from zero.</summary>
    public int Row { get; }

    /// <summary>Which column this place is in, from zero.</summary>
    public int Column { get; }

    /// <summary>What it does, for anything reading the screen aloud.</summary>
    public string Name { get; }

    /// <summary>Puts a reading in this place.</summary>
    public IRelayCommand Add { get; }

    /// <summary>Returns <see cref="Name"/>, which is what a screen reader names the place by.</summary>
    public override string ToString() => Name;
}

/// <summary>
/// Backs the Taskbar tab: every choice about the meter, and where the meter
/// actually ended up.
/// </summary>
public sealed partial class TaskbarPanelViewModel : ObservableObject, IDisposable
{
    private static readonly TimeSpan SaveDelay = TimeSpan.FromMilliseconds(400);

    private readonly ISettingsStore _store;
    private readonly WindsockSettings _settings;
    private readonly TaskbarMeter _meter;
    private readonly ThemeManager _theme;
    private readonly Dispatcher _dispatcher;
    private readonly DispatcherTimer _save;
    private readonly bool _loaded;
    private readonly MeterSlotContext _slotContext;
    private bool _disposed;
    private bool _listing;

    public TaskbarPanelViewModel(
        ISettingsStore store,
        WindsockSettings settings,
        TaskbarMeter meter,
        ThemeManager theme,
        SystemMonitor system,
        HotkeyService hotkeys,
        Dispatcher dispatcher)
    {
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(meter);
        ArgumentNullException.ThrowIfNull(theme);
        ArgumentNullException.ThrowIfNull(system);
        ArgumentNullException.ThrowIfNull(hotkeys);

        _store = store;
        _settings = settings;
        _meter = meter;
        _theme = theme;
        _dispatcher = dispatcher;
        _theme.Changed += OnThemeChanged;

        _save = new DispatcherTimer(DispatcherPriority.Background, dispatcher) { Interval = SaveDelay };
        _save.Tick += OnSaveDue;

        Ground = new MeterGroundViewModel(
            () => _settings.Taskbar.Background,
            ground => Change(meter => meter.Background = ground));

        Placements =
        [
            new MeterOption<MeterPlacement>(MeterPlacement.Docked, "In the taskbar"),
            new MeterOption<MeterPlacement>(MeterPlacement.Floating, "Above the taskbar"),
        ];

        FullScreens =
        [
            new MeterOption<MeterFullScreen>(MeterFullScreen.Hide, "Hide the meter"),
            new MeterOption<MeterFullScreen>(MeterFullScreen.Stay, "Keep showing it"),
        ];

        Remotes =
        [
            new MeterOption<MeterRemote>(MeterRemote.Name, "Show it, naming the computer"),
            new MeterOption<MeterRemote>(MeterRemote.Hide, "Hide the meter"),
        ];

        TaskbarSettings meterSettings = settings.Taskbar;

        IsEnabled = meterSettings.Enabled;
        Placement = meterSettings.Placement;
        Click = meterSettings.Click;
        FitToContents = meterSettings.FitToContents;
        Width = meterSettings.Width;
        Corner = meterSettings.Corner;
        SteadyWidth = meterSettings.SteadyWidth;
        FullScreen = meterSettings.FullScreen;
        Remote = meterSettings.Remote;
        Shadow = meterSettings.Shadow;
        Locked = meterSettings.Locked;

        BuildDisplays();
        MonitorChoice = ChoiceOf(meterSettings);

        _slotContext = new MeterSlotContext(
            () => _theme.IsDark,
            Offered,
            CardNames,
            () => _settings.Temperature,
            () => _settings.Taskbar.Feeds,
            allowed => Feeds = allowed);
        ReadMore(system, hotkeys);

        _loaded = true;

        BuildSlots();
        _meter.StatusChanged += OnStatusChanged;
        _meter.DisplaysChanged += OnDisplaysChanged;
        Describe();
    }

    /// <summary>
    /// Which taskbars get a meter: the main one, every one, or one display's.
    /// Written as the key the picker selects by - "main", "every", or the
    /// display's own name.
    /// </summary>
    public ObservableCollection<MeterOption<string>> Monitors { get; } = [];

    /// <summary>The picker's key for the main taskbar alone.</summary>
    private const string MainKey = "main";

    /// <summary>The picker's key for every taskbar.</summary>
    private const string EveryKey = "every";

    /// <summary>Which taskbars get a meter, as the picker selects it.</summary>
    [ObservableProperty]
    public partial string MonitorChoice { get; set; } = MainKey;

    /// <summary>What the meter does while something runs full screen.</summary>
    public IReadOnlyList<MeterOption<MeterFullScreen>> FullScreens { get; }

    /// <summary>What the meter does in a remote session.</summary>
    public IReadOnlyList<MeterOption<MeterRemote>> Remotes { get; }

    /// <summary>Whether a meter fitted to its readings holds its width still.</summary>
    [ObservableProperty]
    public partial bool SteadyWidth { get; set; }

    /// <summary>What the meter does while something runs full screen.</summary>
    [ObservableProperty]
    public partial MeterFullScreen FullScreen { get; set; }

    /// <summary>What the meter does in a remote session.</summary>
    [ObservableProperty]
    public partial MeterRemote Remote { get; set; }

    /// <summary>Whether the figures carry a soft outline.</summary>
    [ObservableProperty]
    public partial bool Shadow { get; set; }

    /// <summary>Whether a floating meter ignores drags.</summary>
    [ObservableProperty]
    public partial bool Locked { get; set; }

    /// <summary>Whether the meter floats in a window of its own, which is when it can be dragged.</summary>
    public bool IsFloating => Placement == MeterPlacement.Floating;

    /// <summary>Whether any floating meter was dragged somewhere of its own.</summary>
    public bool HasSpots => _settings.Taskbar.Spots.Count > 0;

    /// <summary>Sends every dragged floating meter back to its taskbar.</summary>
    [RelayCommand(CanExecute = nameof(HasSpots))]
    private void ForgetSpots() => Change(meter => meter.Spots = []);

    partial void OnMonitorChoiceChanged(string value)
    {
        // Emptying the list empties the picker, which writes that back; it is
        // not a choice anybody made.
        if (_listing)
        {
            return;
        }

        Pick(value);
    }

    private void Pick(string value) => Change(meter =>
    {
        switch (value)
        {
            case EveryKey:
                meter.Monitors = MeterMonitors.Every;
                meter.ChosenMonitor = null;
                break;

            case { Length: > 0 } name when name != MainKey:
                meter.Monitors = MeterMonitors.Chosen;
                meter.ChosenMonitor = name;
                break;

            default:
                meter.Monitors = MeterMonitors.Main;
                meter.ChosenMonitor = null;
                break;
        }
    });

    partial void OnSteadyWidthChanged(bool value) => Change(meter => meter.SteadyWidth = value);

    partial void OnFullScreenChanged(MeterFullScreen value) => Change(meter => meter.FullScreen = value);

    partial void OnRemoteChanged(MeterRemote value) => Change(meter => meter.Remote = value);

    partial void OnShadowChanged(bool value) => Change(meter => meter.Shadow = value);

    partial void OnLockedChanged(bool value) => Change(meter => meter.Locked = value);

    private static string ChoiceOf(TaskbarSettings settings) => settings.Monitors switch
    {
        MeterMonitors.Every => EveryKey,
        MeterMonitors.Chosen when settings.ChosenMonitor is { Length: > 0 } name => name,
        _ => MainKey,
    };

    /// <summary>
    /// Lists the choices of taskbar: the main one, every one, and each display
    /// by name - the picked one included when it is not connected, so the
    /// picker still says what was picked.
    /// </summary>
    private void BuildDisplays()
    {
        List<MeterOption<string>> options =
        [
            new(MainKey, "The main taskbar"),
            new(EveryKey, "Every taskbar"),
        ];

        foreach (DisplayChoice display in _meter.Displays)
        {
            options.Add(new MeterOption<string>(display.Name, display.HasTaskbar
                ? display.Label
                : string.Concat(display.Label, ", no taskbar")));
        }

        if (_settings.Taskbar is { Monitors: MeterMonitors.Chosen, ChosenMonitor: { Length: > 0 } chosen }
            && !options.Exists(option => string.Equals(option.Value, chosen, StringComparison.OrdinalIgnoreCase)))
        {
            options.Add(new MeterOption<string>(chosen, string.Concat(MeterWords.DisplayName(chosen), ", not connected")));
        }

        if (options.SequenceEqual(Monitors))
        {
            return;
        }

        string keep = MonitorChoice;

        _listing = true;

        try
        {
            Monitors.Clear();

            foreach (MeterOption<string> option in options)
            {
                Monitors.Add(option);
            }

            // Clearing the list cleared the picker's choice with it.
            MonitorChoice = keep;
        }
        finally
        {
            _listing = false;
        }
    }

    private void OnDisplaysChanged(object? sender, EventArgs e) =>
        _dispatcher.BeginInvoke(DispatcherPriority.Background, new Action(BuildDisplays));

    /// <summary>Whether the meter sits in the taskbar or above it.</summary>
    public IReadOnlyList<MeterOption<MeterPlacement>> Placements { get; }

    /// <summary>Every slot on the meter, row by row.</summary>
    public ObservableCollection<MeterSlotChoiceViewModel> SlotChoices { get; } = [];

    /// <summary>
    /// What the editor draws: every slot, the two cells that add to the grid,
    /// and the gaps around them.
    /// </summary>
    public ObservableCollection<object> GridCells { get; } = [];

    /// <summary>
    /// What the board shows in a narrow window, where the grid would not fit:
    /// the meter's own places in reading order, an empty one as a place to add
    /// a reading, and nothing beyond them - adding a row or a column is a
    /// button under the list.
    /// </summary>
    public ObservableCollection<object> ListCells { get; } = [];

    /// <summary>How many rows of places the editor draws.</summary>
    public static int GridRows => TaskbarSettings.MaximumRows;

    /// <summary>How many columns of places the editor draws.</summary>
    public static int GridColumns => TaskbarSettings.MaximumColumns;

    /// <summary>Whether the meter is shown at all.</summary>
    [ObservableProperty]
    public partial bool IsEnabled { get; set; }

    /// <summary>Whether it sits inside the taskbar or above it.</summary>
    [ObservableProperty]
    public partial MeterPlacement Placement { get; set; }

    /// <summary>How many rows of readings the meter shows.</summary>
    [ObservableProperty]
    public partial int Rows { get; private set; } = 2;

    /// <summary>How many columns of readings the meter shows.</summary>
    [ObservableProperty]
    public partial int Columns { get; private set; } = 1;

    /// <summary>What a click on the meter does.</summary>
    [ObservableProperty]
    public partial MeterClick Click { get; set; }

    /// <summary>Whether the meter takes exactly the width its readings need.</summary>
    [ObservableProperty]
    public partial bool FitToContents { get; set; }

    /// <summary>How wide the meter is when it is not sized to its contents.</summary>
    [ObservableProperty]
    public partial double Width { get; set; }

    /// <summary>Whether anything on this tab has been changed from the start.</summary>
    public bool IsChanged => !_settings.Taskbar.IsDefault;

    /// <summary>Whether the fixed width is still the one it shipped at.</summary>
    public bool WidthIsChanged =>
        Math.Abs(_settings.Taskbar.Width - TaskbarSettings.DefaultWidth) > 0.01;

    /// <summary>The ground painted behind the meter, and how it is chosen.</summary>
    public MeterGroundViewModel Ground { get; }

    /// <summary>
    /// What to say under the slot editor about processor temperature.
    /// </summary>
    public static string TemperatureNote =>
#if STORE
        "Processor temperature is not offered in this edition: reading it needs a driver in the kernel, which an app from the Microsoft Store may not install.";
#else
        "Processor temperature is read through PawnIO, a signed driver installed separately, and only while Windsock is running as an administrator. Without both it shows a dash. Everything else on the meter works without either.";
#endif

    /// <summary>How rounded the meter's own surface is.</summary>
    [ObservableProperty]
    public partial double Corner { get; set; } = TaskbarSettings.DefaultCorner;

    /// <summary>Squarest the meter may be made.</summary>
    public static double MinimumCorner => TaskbarSettings.MinimumCorner;

    /// <summary>Roundest the meter may be made.</summary>
    public static double MaximumCorner => TaskbarSettings.MaximumCorner;

    /// <summary>Whether the roundness has been moved from where it shipped.</summary>
    public bool CornerIsChanged =>
        Math.Abs(_settings.Taskbar.Corner - TaskbarSettings.DefaultCorner) > 0.01;

    [RelayCommand(CanExecute = nameof(CornerIsChanged))]
    private void ResetCorner() => Corner = TaskbarSettings.DefaultCorner;

    partial void OnCornerChanged(double value) => Change(meter => meter.Corner = value);

    /// <summary>Whether a ground has been chosen at all.</summary>
    public bool GroundIsChanged => !string.IsNullOrWhiteSpace(_settings.Taskbar.Background);

    [RelayCommand(CanExecute = nameof(GroundIsChanged))]
    private void ResetGround() => Ground.Paint(null);

    [RelayCommand(CanExecute = nameof(IsChanged))]
    private void ResetAll()
    {
        Change(settings => settings.Reset());

        TaskbarSettings meter = _settings.Taskbar;

        Placement = meter.Placement;
        Click = meter.Click;
        DoubleClick = meter.DoubleClick;
        MiddleClick = meter.MiddleClick;
        TrayFigures = meter.TrayFigures;
        FitToContents = meter.FitToContents;
        Width = meter.Width;
        Corner = meter.Corner;
        SteadyWidth = meter.SteadyWidth;
        FullScreen = meter.FullScreen;
        Remote = meter.Remote;
        Shadow = meter.Shadow;
        Locked = meter.Locked;
        MonitorChoice = ChoiceOf(meter);

        Ground.Told();
        BuildSlots();
        ReadKeys();
        ReadProblems();
    }

    [RelayCommand(CanExecute = nameof(WidthIsChanged))]
    private void ResetWidth() => Width = TaskbarSettings.DefaultWidth;

    /// <summary>Narrowest the meter may be made.</summary>
    public static double MinimumWidth => TaskbarSettings.MinimumWidth;

    /// <summary>Widest the meter may be made.</summary>
    public static double MaximumWidth => TaskbarSettings.MaximumWidth;

    /// <summary>What the meter is doing, in words.</summary>
    [ObservableProperty]
    public partial string Status { get; private set; } = string.Empty;

    /// <summary>Whether <see cref="Status"/> is reporting something wrong.</summary>
    [ObservableProperty]
    public partial bool IsWarning { get; private set; }

    /// <inheritdoc />
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        bool pending = _save.IsEnabled;

        _save.Stop();
        _save.Tick -= OnSaveDue;
        _meter.StatusChanged -= OnStatusChanged;
        _meter.DisplaysChanged -= OnDisplaysChanged;
        _theme.Changed -= OnThemeChanged;
        ForgetMore();
        if (pending)
        {
            _store.Save(_settings);
        }
    }

    private void BuildSlots()
    {
        TaskbarSettings settings = _settings.Taskbar;

        _builtFrom = settings.Slots;

        Rows = settings.Rows;
        Columns = settings.Columns;

        SlotChoices.Clear();
        GridCells.Clear();
        ListCells.Clear();

        for (int row = 0; row < settings.Rows; row++)
        {
            for (int column = 0; column < settings.Columns; column++)
            {
                SlotChoices.Add(new MeterSlotChoiceViewModel(
                    row,
                    column,
                    settings.Reach(row, column) ?? MeterSlot.Empty(),
                    settings,
                    _slotContext,
                    OnSlotChanged));
            }
        }
        for (int row = 0; row < GridRows; row++)
        {
            for (int column = 0; column < GridColumns; column++)
            {
                object cell = Cell(settings, row, column);
                GridCells.Add(cell);

                if (row < settings.Rows && column < settings.Columns)
                {
                    ListCells.Add(cell);
                }
            }
        }

        PresetChoice.Mark(Presets, settings);
        AddRowCommand.NotifyCanExecuteChanged();
        AddColumnCommand.NotifyCanExecuteChanged();
    }

    /// <summary>Whether the meter can take another row.</summary>
    public bool CanAddRow => _settings.Taskbar.Rows < TaskbarSettings.MaximumRows;

    /// <summary>Whether the meter can take another column.</summary>
    public bool CanAddColumn => _settings.Taskbar.Columns < TaskbarSettings.MaximumColumns;

    /// <summary>A reading at the start of a new row, for the list the board becomes in a narrow window.</summary>
    [RelayCommand(CanExecute = nameof(CanAddRow))]
    private void AddRow() => Fill(_settings.Taskbar.Rows, 0);

    /// <summary>A reading at the top of a new column.</summary>
    [RelayCommand(CanExecute = nameof(CanAddColumn))]
    private void AddColumn() => Fill(0, _settings.Taskbar.Columns);

    private object Cell(TaskbarSettings settings, int row, int column)
    {
        bool inside = row < settings.Rows && column < settings.Columns;

        if (inside && settings.Slot(row, column) != MeterField.Empty)
        {
            return SlotChoices[(row * settings.Columns) + column];
        }

        int here = row;
        int across = column;

        return new MeterGridAddViewModel(row, column, new RelayCommand(() => Fill(here, across)));
    }

    private void Fill(int row, int column)
    {
        Change(settings => settings.Put(row, column, Fresh(settings)));

        BuildSlots();
    }

    /// <summary>The first reading this machine offers that the meter does not show yet.</summary>
    private MeterField Fresh(TaskbarSettings settings)
    {
        foreach (MeterField field in Enum.GetValues<MeterField>())
        {
            // Nothing that reaches beyond this PC arrives on its own: the
            // public address and a feed are chosen, never handed out.
            if (field is MeterField.PublicAddress or MeterField.Feed or MeterField.Pacer)
            {
                continue;
            }

            if (Offered(field) && !settings.Slots.Any(each => each.Field == field))
            {
                return field;
            }
        }

        return MeterField.Download;
    }

    private void OnSlotChanged(MeterSlotChoiceViewModel slot)
    {
        Change(_ => { });

        if (!slot.WasCleared)
        {
            return;
        }
        Change(settings => settings.Trim());
        BuildSlots();
    }

    partial void OnIsEnabledChanged(bool value)
    {
        if (!_mirroring)
        {
            Change(settings => settings.Enabled = value);
        }
    }

    partial void OnPlacementChanged(MeterPlacement value)
    {
        Change(settings => settings.Placement = value);
        OnPropertyChanged(nameof(IsFloating));
    }

    partial void OnClickChanged(MeterClick value) => Change(settings => settings.Click = value);

    partial void OnFitToContentsChanged(bool value) => Change(settings => settings.FitToContents = value);

    partial void OnWidthChanged(double value) => Change(settings => settings.Width = value);

    private void Change(Action<TaskbarSettings> edit)
    {
        // Reading the settings back into the page is not a change of them.
        if (!_loaded || _mirroring)
        {
            return;
        }

        edit(_settings.Taskbar);

        _changing = true;

        try
        {
            _meter.Apply();
        }
        finally
        {
            _changing = false;
        }

        Describe();
        OnPropertyChanged(nameof(IsChanged));
        OnPropertyChanged(nameof(WidthIsChanged));
        OnPropertyChanged(nameof(GroundIsChanged));
        OnPropertyChanged(nameof(CornerIsChanged));
        OnPropertyChanged(nameof(HasSpots));
        ResetAllCommand.NotifyCanExecuteChanged();
        ForgetSpotsCommand.NotifyCanExecuteChanged();
        ResetWidthCommand.NotifyCanExecuteChanged();
        ResetGroundCommand.NotifyCanExecuteChanged();
        ResetCornerCommand.NotifyCanExecuteChanged();
        _save.Stop();
        _save.Start();
    }

    private void OnSaveDue(object? sender, EventArgs e)
    {
        _save.Stop();
        _store.Save(_settings);
    }

    private void OnStatusChanged(object? sender, EventArgs e) =>
        _dispatcher.BeginInvoke(DispatcherPriority.Background, new Action(() =>
        {
            // A floating meter dragged somewhere is a choice made off the page.
            OnPropertyChanged(nameof(IsChanged));
            OnPropertyChanged(nameof(HasSpots));
            ResetAllCommand.NotifyCanExecuteChanged();
            ForgetSpotsCommand.NotifyCanExecuteChanged();
            Describe();
        }));

    private void OnThemeChanged(object? sender, EventArgs e)
    {
        foreach (MeterSlotChoiceViewModel slot in SlotChoices)
        {
            slot.Repaint();
        }
    }

    private void Describe() => (Status, IsWarning) = MeterWords.Describe(_meter.Situation);

}
