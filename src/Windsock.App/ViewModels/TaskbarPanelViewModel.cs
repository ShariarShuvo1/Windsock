using System.Collections.ObjectModel;
using System.Globalization;
using System.Windows.Media;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Windsock.App.Services;
using Windsock.App.Theming;
using Windsock.Core.Formatting;
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

/// <summary>
/// The colour of one part of one cell, and every way of choosing it.
/// </summary>
/// <summary>
/// The ground painted behind the whole meter, and how it is chosen.
/// </summary>
public sealed partial class MeterGroundViewModel : ObservableObject
{
    private static readonly string[] Palette =
    [
        "#000000", "#FFFFFF", "#0E2231", "#1F2933",
        "#243B53", "#2A2438", "#1E3231", "#3A2618",
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
    public IReadOnlyList<string> Grounds { get; } = Palette;

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

    private static Color? Read(string fill)
    {
        try
        {
            return ColorConverter.ConvertFromString(fill) is Color found ? found : null;
        }
        catch (FormatException)
        {
            return null;
        }
    }

    private static SolidColorBrush Frozen(Color colour)
    {
        SolidColorBrush brush = new(colour);
        brush.Freeze();
        return brush;
    }
}

public sealed partial class InkChoiceViewModel : ObservableObject
{
    private static readonly string[] Palette =
    [
        "#4C9AFF", "#4CD97A", "#FF9F43", "#FF6B6B",
        "#C77DFF", "#4ECDC4", "#FFD93D", "#FFFFFF",
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
    public IReadOnlyList<string> Inks { get; } = Palette;

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

    private static Color? Read(string ink)
    {
        try
        {
            return ColorConverter.ConvertFromString(ink) is Color found ? found : null;
        }
        catch (FormatException)
        {
            return null;
        }
    }

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
    private readonly Func<bool> _dark;

    public MeterSlotChoiceViewModel(
        int row,
        int column,
        MeterSlot cell,
        TaskbarSettings meter,
        Func<bool> dark,
        Action<MeterSlotChoiceViewModel> changed)
    {
        Row = row;
        Column = column;
        Cell = cell;
        _meter = meter;
        _dark = dark;
        _changed = changed;

        MarkInk = new InkChoiceViewModel(
            () => Cell.LabelColour, ink => Set(() => Cell.LabelColour = ink), Own, Plain);

        ValueInk = new InkChoiceViewModel(
            () => Cell.ValueColour, ink => Set(() => Cell.ValueColour = ink), Own, Plain);

        UnitInk = new InkChoiceViewModel(
            () => Cell.UnitColour, ink => Set(() => Cell.UnitColour = ink), Own, Support);
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

    /// <summary>What this slot shows.</summary>
    public MeterField Field
    {
        get => Cell.Field;
        set => Set(() => Cell.Field = value);
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
    public string LabelText => MeterReadout.Label(Field);

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
    private void ResetValue() => Set(Cell.PlainValue);

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
    public string UnitText => MeterReadout.Unit(Field, Family, Scale);

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

    private Color Own() => Ink(MeterPalette.Own(Field, MeterReadout.GroupOf(Field), _dark()));

    private Color Plain() => Ink(MeterPalette.Plain(_dark()));

    private Color Support() => Ink(MeterPalette.Support(_dark()));

    private static Color Ink(Brush brush) =>
        brush is SolidColorBrush solid ? solid.Color : Colors.White;

    /// <summary>Every reading this cell could show.</summary>
    public IReadOnlyList<MeterOption<MeterField>> Fields { get; } =
    [
        .. Enum.GetValues<MeterField>()
            .Where(IsOffered)
            .Select(field => new MeterOption<MeterField>(field, MeterReadout.Name(field))),
    ];

    internal static bool IsOffered(MeterField field) => field switch
    {
        MeterField.Empty => false,
#if STORE
        MeterField.ProcessorTemperature => false,
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
    private bool _disposed;

    public TaskbarPanelViewModel(
        ISettingsStore store,
        WindsockSettings settings,
        TaskbarMeter meter,
        ThemeManager theme,
        Dispatcher dispatcher)
    {
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(meter);
        ArgumentNullException.ThrowIfNull(theme);

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

        Clicks =
        [
            new MeterOption<MeterClick>(MeterClick.Open, "Open Windsock"),
            new MeterOption<MeterClick>(MeterClick.Nothing, "Nothing"),
        ];

        TaskbarSettings meterSettings = settings.Taskbar;

        IsEnabled = meterSettings.Enabled;
        Placement = meterSettings.Placement;
        Click = meterSettings.Click;
        FitToContents = meterSettings.FitToContents;
        Width = meterSettings.Width;
        Corner = meterSettings.Corner;

        _loaded = true;

        BuildSlots();
        _meter.StatusChanged += OnStatusChanged;
        Describe();
    }

    /// <summary>Whether the meter sits in the taskbar or above it.</summary>
    public IReadOnlyList<MeterOption<MeterPlacement>> Placements { get; }

    /// <summary>What a click on the meter does.</summary>
    public IReadOnlyList<MeterOption<MeterClick>> Clicks { get; }

    /// <summary>Every slot on the meter, row by row.</summary>
    public ObservableCollection<MeterSlotChoiceViewModel> SlotChoices { get; } = [];

    /// <summary>
    /// What the editor draws: every slot, the two cells that add to the grid,
    /// and the gaps around them.
    /// </summary>
    public ObservableCollection<object> GridCells { get; } = [];

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
        FitToContents = meter.FitToContents;
        Width = meter.Width;
        Corner = meter.Corner;

        Ground.Told();
        BuildSlots();
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
        _theme.Changed -= OnThemeChanged;
        if (pending)
        {
            _store.Save(_settings);
        }
    }

    private void BuildSlots()
    {
        TaskbarSettings settings = _settings.Taskbar;

        Rows = settings.Rows;
        Columns = settings.Columns;

        SlotChoices.Clear();
        GridCells.Clear();

        for (int row = 0; row < settings.Rows; row++)
        {
            for (int column = 0; column < settings.Columns; column++)
            {
                SlotChoices.Add(new MeterSlotChoiceViewModel(
                    row,
                    column,
                    settings.Reach(row, column) ?? MeterSlot.Empty(),
                    settings,
                    () => _theme.IsDark,
                    OnSlotChanged));
            }
        }
        for (int row = 0; row < GridRows; row++)
        {
            for (int column = 0; column < GridColumns; column++)
            {
                GridCells.Add(Cell(settings, row, column));
            }
        }

    }

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

    private static MeterField Fresh(TaskbarSettings settings)
    {
        foreach (MeterField field in Enum.GetValues<MeterField>())
        {
            if (MeterSlotChoiceViewModel.IsOffered(field) && !settings.Slots.Any(each => each.Field == field))
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

    partial void OnIsEnabledChanged(bool value) => Change(settings => settings.Enabled = value);

    partial void OnPlacementChanged(MeterPlacement value) => Change(settings => settings.Placement = value);

    partial void OnClickChanged(MeterClick value) => Change(settings => settings.Click = value);

    partial void OnFitToContentsChanged(bool value) => Change(settings => settings.FitToContents = value);

    partial void OnWidthChanged(double value) => Change(settings => settings.Width = value);

    private void Change(Action<TaskbarSettings> edit)
    {
        if (!_loaded)
        {
            return;
        }

        edit(_settings.Taskbar);

        _meter.Apply();
        Describe();
        OnPropertyChanged(nameof(IsChanged));
        OnPropertyChanged(nameof(WidthIsChanged));
        OnPropertyChanged(nameof(GroundIsChanged));
        OnPropertyChanged(nameof(CornerIsChanged));
        ResetAllCommand.NotifyCanExecuteChanged();
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
        _dispatcher.BeginInvoke(DispatcherPriority.Background, new Action(Describe));

    private void OnThemeChanged(object? sender, EventArgs e)
    {
        foreach (MeterSlotChoiceViewModel slot in SlotChoices)
        {
            slot.Repaint();
        }
    }

    private void Describe()
    {
        (Status, IsWarning) = _meter.Status switch
        {
            MeterStatus.Docked => ("Showing in the taskbar, beside the notification area.", false),
            MeterStatus.Floating => ("Showing in its own window, above the taskbar.", false),
            MeterStatus.Unavailable => (
                "The taskbar could not be found, so there is nowhere to dock. "
                + "Switch the position to \"Above the taskbar\" to show the meter anyway.",
                true),
            _ => ("The meter is off. Nothing is added to the taskbar.", false),
        };
    }

}
