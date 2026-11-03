using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;
using System.Windows.Media.Effects;
using System.Diagnostics;
using CommunityToolkit.Mvvm.ComponentModel;
using Windsock.App.Theming;
using Windsock.Core.Formatting;
using Windsock.Core.Meter;
using Windsock.Core.Settings;

namespace Windsock.App.ViewModels;

internal static class MeterPalette
{
    private static readonly SolidColorBrush DarkPlain = Frozen(0xFF, 0xFF, 0xFF);
    private static readonly SolidColorBrush DarkSupport = Frozen(0xD2, 0xDC, 0xE2);
    private static readonly SolidColorBrush DarkHover = Translucent(0x24, 0xFF, 0xFF, 0xFF);

    private static readonly SolidColorBrush DarkDownload = Frozen(0x64, 0xCC, 0xF9);
    private static readonly SolidColorBrush DarkUpload = Frozen(0xFF, 0xC1, 0x62);
    private static readonly SolidColorBrush DarkProcessor = Frozen(0x5E, 0xD6, 0xE6);
    private static readonly SolidColorBrush DarkGraphics = Frozen(0x94, 0xD4, 0x97);
    private static readonly SolidColorBrush DarkMemory = Frozen(0xC2, 0xAF, 0xE6);
    private static readonly SolidColorBrush DarkStorage = Frozen(0xFF, 0xC1, 0x62);
    private static readonly SolidColorBrush DarkSystem = Frozen(0xB9, 0xC6, 0xD4);

    private static readonly SolidColorBrush LightPlain = Frozen(0x14, 0x14, 0x14);
    private static readonly SolidColorBrush LightSupport = Frozen(0x38, 0x38, 0x38);
    private static readonly SolidColorBrush LightHover = Translucent(0x1E, 0x00, 0x00, 0x00);

    private static readonly SolidColorBrush LightDownload = Frozen(0x01, 0x5D, 0x8F);
    private static readonly SolidColorBrush LightUpload = Frozen(0x84, 0x4E, 0x00);
    private static readonly SolidColorBrush LightProcessor = Frozen(0x00, 0x6A, 0x74);
    private static readonly SolidColorBrush LightGraphics = Frozen(0x2E, 0x6F, 0x33);
    private static readonly SolidColorBrush LightMemory = Frozen(0x51, 0x3B, 0x8C);
    private static readonly SolidColorBrush LightStorage = Frozen(0x84, 0x4E, 0x00);
    private static readonly SolidColorBrush LightSystem = Frozen(0x44, 0x55, 0x66);

    // Windows 11's own status colours (success, caution, critical), dark and
    // light, so a level reads the way the rest of Windows says good and bad.
    private static readonly SolidColorBrush DarkGood = Frozen(0x6C, 0xCB, 0x5F);
    private static readonly SolidColorBrush DarkFair = Frozen(0xFC, 0xE1, 0x00);
    private static readonly SolidColorBrush DarkSlow = Frozen(0xFF, 0x99, 0xA4);
    private static readonly SolidColorBrush LightGood = Frozen(0x0F, 0x7B, 0x0F);
    private static readonly SolidColorBrush LightFair = Frozen(0x9D, 0x5D, 0x00);
    private static readonly SolidColorBrush LightSlow = Frozen(0xC4, 0x2B, 0x1C);

    /// <summary>The taskbar own text colour, which is what is used by default.</summary>
    public static Brush Plain(bool dark) => dark ? DarkPlain : LightPlain;

    /// <summary>The colour a reading has everywhere else in Windsock.</summary>
    public static Brush Own(MeterField field, MeterGroup group, bool dark)
    {
        return field switch
        {
            MeterField.Download => dark ? DarkDownload : LightDownload,
            MeterField.Upload => dark ? DarkUpload : LightUpload,
            _ => group switch
            {
                MeterGroup.Processor => dark ? DarkProcessor : LightProcessor,
                MeterGroup.Graphics => dark ? DarkGraphics : LightGraphics,
                MeterGroup.Memory => dark ? DarkMemory : LightMemory,
                MeterGroup.Storage => dark ? DarkStorage : LightStorage,
                MeterGroup.System => dark ? DarkSystem : LightSystem,
                _ => dark ? DarkDownload : LightDownload,
            },
        };
    }

    /// <summary>
    /// The colour the unit beside a figure is drawn in.
    /// </summary>
    public static Brush Support(bool dark) => dark ? DarkSupport : LightSupport;

    /// <summary>
    /// The colour a cell was given, if it was given one.
    /// </summary>
    public static Brush? Ink(string? colour, MeterField field, MeterGroup group, bool dark)
    {
        if (colour is not { Length: > 0 })
        {
            return null;
        }

        if (colour == MeterSlot.Own)
        {
            return Own(field, group, dark);
        }

        if (MeterLevels.IsNamed(colour))
        {
            return Level(colour, dark);
        }

        // Asked for every place on every reading: a colour written down is
        // turned into a brush once and the frozen brush kept.
        lock (Mixed)
        {
            if (Mixed.TryGetValue(colour, out SolidColorBrush? kept))
            {
                return kept;
            }
        }

        // Read by hand, never by ColorConverter, which fetches a colour
        // profile named in the text; anything but hex is nothing.
        SolidColorBrush? brush = HexColour.Brush(colour);

        lock (Mixed)
        {
            // A few dozen colours at most are ever chosen; a file full of
            // different ones is not worth keeping every one of.
            if (Mixed.Count >= MostMixed)
            {
                Mixed.Clear();
            }

            Mixed[colour] = brush;
        }

        return brush;
    }

    /// <summary>The colours written down so far, as frozen brushes, or nothing for one that is not a colour.</summary>
    private static readonly Dictionary<string, SolidColorBrush?> Mixed = new(StringComparer.OrdinalIgnoreCase);

    private const int MostMixed = 256;

    /// <summary>The wash that appears behind the meter under the pointer.</summary>
    public static Brush Hover(bool dark) => dark ? DarkHover : LightHover;

    /// <summary>A named level colour, in the shade that reads on the taskbar's theme.</summary>
    public static Brush Level(string colour, bool dark) => colour switch
    {
        MeterLevels.Good => dark ? DarkGood : LightGood,
        MeterLevels.Slow => dark ? DarkSlow : LightSlow,
        _ => dark ? DarkFair : LightFair,
    };

    private static SolidColorBrush Frozen(byte red, byte green, byte blue)
    {
        var brush = new SolidColorBrush(Color.FromRgb(red, green, blue));
        brush.Freeze();
        return brush;
    }

    private static SolidColorBrush Translucent(byte alpha, byte red, byte green, byte blue)
    {
        var brush = new SolidColorBrush(Color.FromArgb(alpha, red, green, blue));
        brush.Freeze();
        return brush;
    }
}

/// <summary>One slot of the meter: a label, a figure and a unit.</summary>
public sealed partial class MeterSlotViewModel : ObservableObject
{
    /// <summary>
    /// The reading in words - "Download rate 144 KB/s" - for a screen
    /// reader, which names a place on the meter by it.
    /// </summary>
    public string Spoken { get; set; } = string.Empty;

    /// <inheritdoc />
    /// <remarks>UI Automation names an item of the meter's list by this.</remarks>
    public override string ToString() => Spoken;

    /// <summary>Short mark saying what the figure is, or empty.</summary>
    [ObservableProperty]
    public partial string Label { get; set; } = string.Empty;

    /// <summary>Which part of the machine this reading is about.</summary>
    [ObservableProperty]
    public partial MeterMark Mark { get; set; } = MeterMark.None;

    /// <summary>Whether the mark is drawn as a picture rather than written.</summary>
    [ObservableProperty]
    public partial bool MarkIsIcon { get; set; } = true;

    /// <summary>How many of the parts sit against the left edge of the place.</summary>
    [ObservableProperty]
    public partial int PartsOnTheLeft { get; set; }

    /// <summary>The figure.</summary>
    [ObservableProperty]
    public partial string Value { get; set; } = string.Empty;

    /// <summary>The unit, or empty.</summary>
    [ObservableProperty]
    public partial string Unit { get; set; } = string.Empty;

    /// <summary>
    /// Room kept for the figure, in device-independent pixels: as wide as the
    /// widest figure it can show in its unit, so the meter holds still while
    /// the digits change. Nothing when the meter is not keeping a steady width.
    /// </summary>
    [ObservableProperty]
    public partial double ValueReserve { get; set; }

    /// <summary>Room kept for the unit: the widest of the units the reading moves between.</summary>
    [ObservableProperty]
    public partial double UnitReserve { get; set; }

    /// <summary>
    /// Whether the unit's place is drawn: when there is a unit, or when room is
    /// kept for one even while a dash stands in for the figure.
    /// </summary>
    [ObservableProperty]
    public partial bool UnitShown { get; set; }

    /// <summary>Colour of the mark saying what the figure is.</summary>
    [ObservableProperty]
    public partial Brush LabelInk { get; set; } = Brushes.White;

    /// <summary>Colour of the figure.</summary>
    [ObservableProperty]
    public partial Brush Figure { get; set; } = Brushes.White;

    /// <summary>Colour of the unit.</summary>
    [ObservableProperty]
    public partial Brush Support { get; set; } = Brushes.Gray;

    /// <summary>How large this figure is drawn.</summary>
    [ObservableProperty]
    public partial double Size { get; set; } = 12;

    /// <summary>How large the mark and the unit beside it are drawn.</summary>
    [ObservableProperty]
    public partial double SupportSize { get; set; } = 10;

    /// <summary>How much weight this figure carries.</summary>
    [ObservableProperty]
    public partial FontWeight Weight { get; set; } = FontWeights.Normal;

    /// <summary>Whether this place draws its last minute, and how.</summary>
    [ObservableProperty]
    public partial MeterGraph Graph { get; set; }

    /// <summary>Whether the graph stands alone, the figure and unit set aside for it.</summary>
    [ObservableProperty]
    public partial bool GraphAlone { get; set; }

    /// <summary>The graph's readings, oldest first; the same array every second.</summary>
    public double[] SparkValues { get; } = new double[MeterHistory.Span];

    /// <summary>How many of <see cref="SparkValues"/> hold readings.</summary>
    [ObservableProperty]
    public partial int SparkCount { get; set; }

    /// <summary>Moved each time the graph's readings change, which redraws it.</summary>
    [ObservableProperty]
    public partial int SparkVersion { get; set; }

    /// <summary>The top of the graph's scale: 100 for a share, NaN to follow the readings.</summary>
    [ObservableProperty]
    public partial double SparkCeiling { get; set; } = double.NaN;

    /// <summary>Whether the mark is the animated windsock.</summary>
    [ObservableProperty]
    public partial bool IsPacer { get; set; }

    /// <summary>Bytes a second moving, for the windsock's pace.</summary>
    [ObservableProperty]
    public partial double Speed { get; set; }
}

/// <summary>
/// Measures text the way the meter draws it - Segoe UI, figures tabular - for
/// the room a steady meter keeps.
/// </summary>
/// <remarks>
/// A text block rather than FormattedText, because only a text block takes the
/// tabular-figures feature, and without it the template measures wrong: Segoe
/// UI's own figures are proportional (measured at 13 px semi-bold, "1111" is
/// 20.92 wide and "0000" 28.87; tabular, both are 28.87). Called on the UI
/// thread only, and remembered, since the same few templates come up again and
/// again.
/// </remarks>
internal static class MeterRuler
{
    private static readonly FontFamily Face = new("Segoe UI");
    private static readonly Dictionary<(string Text, double Size, int Weight), double> Known = [];
    private static TextBlock? _ruler;

    /// <summary>How wide <paramref name="text"/> is drawn, in device-independent pixels.</summary>
    public static double Width(string text, double size, FontWeight weight)
    {
        if (string.IsNullOrEmpty(text) || size <= 0)
        {
            return 0;
        }

        var key = (text, Math.Round(size, 2), weight.ToOpenTypeWeight());

        if (Known.TryGetValue(key, out double known))
        {
            return known;
        }

        if (_ruler is null)
        {
            _ruler = new TextBlock { FontFamily = Face };
            Typography.SetNumeralAlignment(_ruler, FontNumeralAlignment.Tabular);
        }

        _ruler.Text = text;
        _ruler.FontSize = size;
        _ruler.FontWeight = weight;
        _ruler.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));

        double wide = Math.Ceiling(_ruler.DesiredSize.Width * 4) / 4;

        if (Known.Count > 512)
        {
            Known.Clear();
        }

        Known[key] = wide;
        return wide;
    }

    /// <summary>
    /// The widest of several texts, for the room kept for a unit that moves
    /// between them.
    /// </summary>
    public static double Widest(IReadOnlyList<string> texts, double size, FontWeight weight)
    {
        double widest = 0;

        foreach (string text in texts)
        {
            widest = Math.Max(widest, Width(text, size, weight));
        }

        return widest;
    }

    /// <summary>The height one line of Segoe UI takes at a size.</summary>
    public static double LineHeight(double size) => Face.LineSpacing * size;
}

/// <summary>
/// What the taskbar meter shows, and how.
/// </summary>
public sealed partial class TaskbarMeterViewModel : ObservableObject
{
    private const double SmallText = 11;
    private const double NormalText = 13;
    private const double LargeText = 15;
    private const double HugeText = 17;
    private const double SupportFactor = 0.85;
    private readonly bool _dark;
    private readonly Dictionary<(int Place, MeterField Field), (string Unit, string Template)> _kept = [];
    private readonly Dictionary<int, MeterCycle> _cycles = [];
    private readonly MeterHistory _history = new();
    private readonly HashSet<string> _graphed = new(StringComparer.Ordinal);
    private readonly Dictionary<(int Place, MeterField Field, string? Card), string> _graphKeys = [];
    private readonly Stopwatch _clock = Stopwatch.StartNew();
    private TaskbarSettings _settings = new();
    private MeterReading _reading = MeterReading.Empty;
    private int[] _order = [0, 1];
    private bool _arranged;
    private double _shrink = 1;
    private string? _preface;

    /// <summary>Creates a meter drawn for a taskbar of a given shade.</summary>
    public TaskbarMeterViewModel(bool dark)
    {
        _dark = dark;
        Hover = MeterPalette.Hover(dark);
        Apply(_settings);
    }

    /// <summary>The readings on show, in the order they are laid out.</summary>
    public ObservableCollection<MeterSlotViewModel> Slots { get; } = [];

    /// <summary>Rows the slots are laid out in.</summary>
    [ObservableProperty]
    public partial int Rows { get; private set; } = 2;

    /// <summary>Columns the slots are laid out in.</summary>
    [ObservableProperty]
    public partial int Columns { get; private set; } = 1;

    /// <summary>Size of the figures.</summary>
    [ObservableProperty]
    public partial double FontSize { get; private set; } = NormalText;

    /// <summary>Size of the labels and units beside them.</summary>
    [ObservableProperty]
    public partial double SupportFontSize { get; private set; } = NormalText * SupportFactor;

    /// <summary>Weight of the figures.</summary>
    [ObservableProperty]
    public partial FontWeight Weight { get; private set; } = FontWeights.SemiBold;

    /// <summary>The full reading, shown on hover.</summary>
    [ObservableProperty]
    public partial string Tooltip { get; private set; } = string.Empty;

    /// <summary>The wash drawn behind the meter under the pointer.</summary>
    public Brush Hover { get; }

    /// <summary>
    /// The ground painted behind the readings, transparent unless one is set.
    /// </summary>
    [ObservableProperty]
    public partial Brush Ground { get; private set; } = Brushes.Transparent;

    /// <summary>How rounded the meter's own surface is.</summary>
    [ObservableProperty]
    public partial CornerRadius Corner { get; private set; } =
        new(TaskbarSettings.DefaultCorner);

    /// <summary>
    /// Whether the meter lights under the pointer.
    /// </summary>
    [ObservableProperty]
    public partial bool LightsOnHover { get; private set; } = true;

    /// <summary>
    /// A soft shadow behind the figures, dark under light ink and light under
    /// dark, or nothing when none was asked for.
    /// </summary>
    [ObservableProperty]
    public partial Effect? Glow { get; private set; }

    /// <summary>
    /// Whether each unit is written under its figure rather than beside it:
    /// on a bar down the side of the screen, which has no width to spare.
    /// </summary>
    [ObservableProperty]
    public partial bool UnitBelow { get; private set; }

    /// <summary>
    /// Room between the readings and the meter's edges: more at the ends of a
    /// meter lying along a bar, less and even all round on one standing in a
    /// side bar's narrow width.
    /// </summary>
    [ObservableProperty]
    public partial Thickness Inset { get; private set; } = Along;

    /// <summary>Raised when a change may have altered how large the meter wants to be.</summary>
    public event EventHandler? Resized;

    /// <summary>
    /// Whether the pointer is over the meter, which holds a rotating place on
    /// the reading being read.
    /// </summary>
    public bool Held { get; set; }

    /// <summary>
    /// Every reading the meter shows, rotations included, so whoever owns it
    /// knows which monitors to keep running.
    /// </summary>
    public IEnumerable<MeterField> Fields =>
        _settings.Slots.SelectMany(slot => slot.Fields()).Where(each => each != MeterField.Empty).Distinct();

    private static readonly Thickness Along = new(4, 0, 7, 0);

    private static readonly Thickness Across = new(3, 2, 3, 2);

    /// <summary>
    /// The height one row of readings needs at the sizes asked for, before any
    /// shrinking: what decides whether the rows fit a thin bar.
    /// </summary>
    public double LineHeight
    {
        get
        {
            double largest = SizeOf(_settings.TextSize);

            foreach (MeterSlot slot in _settings.Slots)
            {
                if (slot.IsFilled && slot.TextSize is { } size)
                {
                    largest = Math.Max(largest, SizeOf(size));
                }
            }

            return MeterRuler.LineHeight(largest);
        }
    }

    /// <summary>
    /// Said first in the tooltip, above every reading: that these are a
    /// remote computer's.
    /// </summary>
    public string? Preface
    {
        get => _preface;

        set
        {
            if (_preface == value)
            {
                return;
            }

            _preface = value;
            Update(_reading);
        }
    }

    /// <summary>
    /// How far the figures are shrunk to fit the bar, as a share of the size
    /// asked for.
    /// </summary>
    public double Shrink
    {
        get => _shrink;

        set
        {
            double wanted = double.IsFinite(value) ? Math.Clamp(value, 0.1, 1) : 1;

            if (Math.Abs(wanted - _shrink) < 0.001)
            {
                return;
            }

            _shrink = wanted;
            FontSize = SizeOf(_settings.TextSize) * _shrink;
            SupportFontSize = SupportOf(FontSize);
            Update(_reading);
        }
    }

    /// <summary>
    /// Takes a new set of choices, rebuilding the slots if their shape changed.
    /// </summary>
    public void Apply(TaskbarSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);

        _settings = settings;
        _kept.Clear();
        _cycles.Clear();

        double size = SizeOf(settings.TextSize) * _shrink;

        FontSize = size;
        SupportFontSize = SupportOf(size);
        Weight = settings.Bold ? FontWeights.SemiBold : FontWeights.Normal;
        Ground = Painted(settings.Background);
        Corner = new CornerRadius(settings.Corner);
        LightsOnHover = settings.Click != MeterClick.Nothing
            || settings.DoubleClick != MeterClick.Nothing
            || settings.MiddleClick != MeterClick.Nothing;
        Glow = settings.Shadow ? Shadow(_dark) : null;

        // Laid out as the reader set it until whoever owns the meter says
        // otherwise, and again whenever the grid shrank under an arrangement.
        int places = settings.Rows * settings.Columns;

        if (!_arranged || _order.Any(index => index >= places))
        {
            _order = [.. Enumerable.Range(0, places)];
            Rows = settings.Rows;
            Columns = settings.Columns;
        }

        Update(_reading);
        Resized?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>
    /// Lays the readings out again for the bar they are on: which of the
    /// reader's places each place on the bar shows, in how many rows and
    /// columns, and whether units go under their figures. Says whether
    /// anything changed.
    /// </summary>
    public bool Arrange(int[] order, int rows, int columns, bool unitBelow = false)
    {
        ArgumentNullException.ThrowIfNull(order);

        _arranged = true;

        if (rows == Rows && columns == Columns && unitBelow == UnitBelow && order.AsSpan().SequenceEqual(_order))
        {
            return false;
        }

        _order = order;
        Rows = Math.Max(1, rows);
        Columns = Math.Max(1, columns);
        UnitBelow = unitBelow;
        Inset = unitBelow ? Across : Along;
        Update(_reading);

        return true;
    }

    /// <summary>The meter's ground: a hex colour, or nothing painted for anything else.</summary>
    private static SolidColorBrush Painted(string? colour) => HexColour.Brush(colour) ?? Brushes.Transparent;

    /// <summary>
    /// A shadow with no offset and a soft edge, which reads as an outline
    /// round each figure rather than a second copy of it beside the first.
    /// </summary>
    private static DropShadowEffect Shadow(bool dark)
    {
        DropShadowEffect effect = new()
        {
            Color = dark ? Colors.Black : Colors.White,
            ShadowDepth = 0,
            BlurRadius = 5,
            Opacity = 0.9,
        };

        effect.Freeze();
        return effect;
    }

    private Brush? Painted(string? colour, MeterLine line) =>
        MeterPalette.Ink(colour, line.Field, line.Group, _dark);

    private static double SizeOf(MeterTextSize size) => size switch
    {
        MeterTextSize.Small => SmallText,
        MeterTextSize.Large => LargeText,
        MeterTextSize.Huge => HugeText,
        _ => NormalText,
    };

    private static double SupportOf(double size) => Math.Round(size * SupportFactor, 1);

    /// <summary>Puts a new reading on the meter.</summary>
    public void Update(in MeterReading reading)
    {
        _reading = reading;

        int wanted = Math.Max(1, _order.Length);
        while (Slots.Count > wanted)
        {
            Slots.RemoveAt(Slots.Count - 1);
        }

        while (Slots.Count < wanted)
        {
            Slots.Add(new MeterSlotViewModel());
        }

        var cells = new MeterSlot[wanted];
        Brush support = MeterPalette.Support(_dark);
        Brush plain = MeterPalette.Plain(_dark);
        bool steady = _settings.FitToContents && _settings.SteadyWidth;
        int columns = Math.Max(1, _settings.Columns);
        bool changed = false;
        TimeSpan now = _clock.Elapsed;

        _graphed.Clear();

        for (int index = 0; index < wanted; index++)
        {
            int place = index < _order.Length ? _order[index] : index;
            MeterSlot cell = _settings.At(place / columns, place % columns) ?? MeterSlot.Empty();

            cells[index] = cell;

            MeterField field = Showing(place, cell, now);
            MeterLine line = MeterReadout.Compose(cell, field, reading, _settings);
            MeterSlotViewModel slot = Slots[index];
            double? number = MeterReadout.Number(field, cell, reading);
            string? level = MeterLevels.Colour(MeterLevels.For(cell, field), number);
            double size = SizeOf(cell.TextSize ?? _settings.TextSize) * _shrink;
            FontWeight weight = (cell.Bold ?? _settings.Bold) ? FontWeights.SemiBold : FontWeights.Normal;

            // Kept to its room, a new figure cannot move the meter, so there
            // is nothing to measure again.
            changed |= slot.Label.Length != line.Label.Length
                || (!steady && (slot.Value.Length != line.Value.Length || slot.Unit.Length != line.Unit.Length));

            slot.Label = line.Label;
            slot.Mark = MeterReadout.MarkOf(field, reading);
            slot.IsPacer = field == MeterField.Pacer;
            slot.Speed = slot.IsPacer
                ? Math.Max(0, reading.DownloadBytesPerSecond) + Math.Max(0, reading.UploadBytesPerSecond)
                : 0;

            // The windsock is drawn whatever the marks are set to: it is the
            // whole of the reading.
            bool icon = (line.Label.Length > 0 || slot.IsPacer)
                && (cell.MarkAs == MeterMarkStyle.Icon || slot.IsPacer)
                && slot.Mark != MeterMark.None;

            changed |= slot.MarkIsIcon != icon;
            slot.MarkIsIcon = icon;
            slot.PartsOnTheLeft = cell.PartsOnTheLeft;
            slot.Value = line.Value;
            slot.Unit = line.Unit;
            slot.Spoken = field == MeterField.Pacer
                ? MeterReadout.Name(field)
                : string.Concat(field == MeterField.Feed ? MeterReadout.Label(field, cell) : MeterReadout.Name(field), " ", line.Value, " ", line.Unit).TrimEnd();
            slot.Figure = (level is not null ? Painted(level, line) : null) ?? Painted(cell.ValueColour, line) ?? plain;
            slot.LabelInk = Painted(cell.LabelColour, line) ?? plain;
            slot.Support = Painted(cell.UnitColour, line) ?? support;

            changed |= Math.Abs(slot.Size - size) > 0.01 || slot.Weight != weight;

            slot.Size = size;
            slot.SupportSize = SupportOf(size);
            slot.Weight = weight;

            changed |= slot.Graph != cell.Graph || slot.GraphAlone != cell.GraphAlone;
            slot.Graph = cell.Graph;
            slot.GraphAlone = cell.Graph != MeterGraph.None && cell.GraphAlone;

            if (cell.Graph != MeterGraph.None)
            {
                Draw(slot, place, field, cell, reading, now);
            }

            (double value, double unit) = steady && !slot.GraphAlone ? Reserve(place, cell, field, line, size, weight) : (0, 0);

            changed |= Math.Abs(slot.ValueReserve - value) > 0.01 || Math.Abs(slot.UnitReserve - unit) > 0.01;

            slot.ValueReserve = value;
            slot.UnitReserve = unit;
            slot.UnitShown = line.Unit.Length > 0 || unit > 0;
        }

        _history.Keep(_graphed);

        string tooltip = MeterReadout.Tooltip(cells, reading, _settings);

        Tooltip = _preface is { Length: > 0 } preface ? string.Concat(preface, "\n", tooltip) : tooltip;

        if (changed)
        {
            Resized?.Invoke(this, EventArgs.Empty);
        }
    }

    /// <summary>
    /// The room one place keeps for its figure and its unit, so the meter's
    /// width only changes when what it can show does.
    /// </summary>
    /// <remarks>
    /// A place that rotates keeps room for the widest of every reading it
    /// turns to, so turning from one to the next does not move the meter.
    /// </remarks>
    private (double Value, double Unit) Reserve(int place, MeterSlot cell, MeterField showing, MeterLine line, double size, FontWeight weight)
    {
        if (cell.Field == MeterField.Empty)
        {
            return (0, 0);
        }

        RateFamily family = cell.Family ?? _settings.Family;
        RateScale scale = cell.Scale ?? _settings.Scale;
        TemperatureUnit temperature = _reading.Extras.Temperature;
        bool dash = line.Value == MeterReadout.Unknown;
        bool units = cell.ShowUnit ?? _settings.ShowUnit;
        double value = 0;
        double unit = 0;

        foreach (MeterField field in cell.Fields())
        {
            bool current = field == showing;

            // What a figure in a unit with no ceiling has reached is kept until
            // the unit changes. A dash says nothing about either, so it keeps both.
            string? kept = _kept.TryGetValue((place, field), out (string Unit, string Template) known)
                && (!current || dash || known.Unit == line.Unit)
                ? known.Template
                : null;

            string template = MeterReserve.Template(field, scale, current ? line.Value : null, kept, temperature);

            if (current && !dash)
            {
                _kept[(place, field)] = (line.Unit, template);
            }

            value = Math.Max(value, MeterRuler.Width(template, size, weight));

            if (units)
            {
                IReadOnlyList<string> names = field == MeterField.Feed && cell.Feed is { Unit.Length: > 0 } feed
                    ? [feed.Unit.Trim()]
                    : MeterReserve.Units(field, family, scale, temperature);

                unit = Math.Max(unit, MeterRuler.Widest(names, SupportOf(size), FontWeights.Normal));
            }
        }

        return (value, unit);
    }

    /// <summary>
    /// Which reading a place shows now: its own, or for a rotating place the
    /// one it has turned to, held while the pointer is over the meter.
    /// </summary>
    private MeterField Showing(int place, MeterSlot cell, TimeSpan now)
    {
        if (cell.Rotate is not { Count: > 0 })
        {
            return cell.Field;
        }

        int count = 1;

        foreach (MeterField other in cell.Rotate)
        {
            if (other != MeterField.Empty && other != cell.Field)
            {
                count++;
            }
        }

        if (!_cycles.TryGetValue(place, out MeterCycle? cycle))
        {
            cycle = new MeterCycle();
            _cycles[place] = cycle;
        }

        cycle.Advance(count, TimeSpan.FromSeconds(Math.Clamp(cell.RotateSeconds, 1, 60)), now, Held);

        int wanted = Math.Min(cycle.Index, count - 1);

        if (wanted == 0)
        {
            return cell.Field;
        }

        // The turns in the order Fields gives them, without its iterator.
        int at = 0;

        foreach (MeterField other in cell.Rotate)
        {
            if (other != MeterField.Empty && other != cell.Field && ++at == wanted)
            {
                return other;
            }
        }

        return cell.Field;
    }

    /// <summary>
    /// Adds this second's reading to a place's graph, once a second, and
    /// draws the minute of the reading on show.
    /// </summary>
    /// <remarks>
    /// A place that takes turns keeps a minute for every reading it turns to,
    /// each added to every second whichever is on show, so turning back to a
    /// reading finds its graph whole rather than started again.
    /// </remarks>
    private void Draw(MeterSlotViewModel slot, int place, MeterField showing, MeterSlot cell, in MeterReading reading, TimeSpan now)
    {
        bool added = false;

        foreach (MeterField field in cell.Fields())
        {
            if (field is MeterField.Empty or MeterField.Pacer)
            {
                continue;
            }

            string key = GraphKey(place, field, cell.Card);

            _graphed.Add(key);
            added |= _history.Add(key, MeterReadout.Number(field, cell, reading) ?? double.NaN, now) && field == showing;
        }

        if (!added)
        {
            return;
        }

        slot.SparkCount = _history.CopyTo(GraphKey(place, showing, cell.Card), slot.SparkValues);
        slot.SparkCeiling = MeterLevels.UnitOf(showing, RateFamily.Bytes, TemperatureUnit.Celsius) is { Label: "%" } ? 100 : double.NaN;
        slot.SparkVersion++;
    }

    /// <summary>A graph's key, made once and reused, since it is asked for every second.</summary>
    private string GraphKey(int place, MeterField field, string? card)
    {
        if (!_graphKeys.TryGetValue((place, field, card), out string? key))
        {
            key = string.Concat(place.ToString(System.Globalization.CultureInfo.InvariantCulture), ":", field.ToString(), ":", card);
            _graphKeys[(place, field, card)] = key;
        }

        return key;
    }
}
