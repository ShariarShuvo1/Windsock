using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using Windsock.Core.Formatting;
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

        try
        {
            if (ColorConverter.ConvertFromString(colour) is Color found)
            {
                SolidColorBrush brush = new(found);
                brush.Freeze();
                return brush;
            }
        }
        catch (FormatException)
        {
            // Not a colour, so nothing was said.
        }

        return null;
    }

    /// <summary>The wash that appears behind the meter under the pointer.</summary>
    public static Brush Hover(bool dark) => dark ? DarkHover : LightHover;

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
    private TaskbarSettings _settings = new();
    private MeterReading _reading = MeterReading.Empty;

    /// <summary>Creates a meter drawn for a taskbar of a given shade.</summary>
    public TaskbarMeterViewModel(bool dark)
    {
        _dark = dark;
        Hover = MeterPalette.Hover(dark);
        Apply(_settings);
    }

    /// <summary>The readings on show, row by row.</summary>
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

    /// <summary>Raised when a change may have altered how wide the meter wants to be.</summary>
    public event EventHandler? Resized;

    /// <summary>
    /// Takes a new set of choices, rebuilding the slots if their shape changed.
    /// </summary>
    public void Apply(TaskbarSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);

        _settings = settings;

        double size = SizeOf(settings.TextSize);

        FontSize = size;
        SupportFontSize = SupportOf(size);
        Weight = settings.Bold ? FontWeights.SemiBold : FontWeights.Normal;
        Rows = settings.Rows;
        Columns = settings.Columns;
        Ground = Painted(settings.Background);
        Corner = new CornerRadius(settings.Corner);
        LightsOnHover = settings.Click != MeterClick.Nothing;

        Update(_reading);
        Resized?.Invoke(this, EventArgs.Empty);
    }

    private static SolidColorBrush Painted(string? colour)
    {
        if (string.IsNullOrWhiteSpace(colour))
        {
            return Brushes.Transparent;
        }

        try
        {
            if (ColorConverter.ConvertFromString(colour) is Color found)
            {
                SolidColorBrush brush = new(found);
                brush.Freeze();
                return brush;
            }
        }
        catch (FormatException)
        {
            // Half-typed, or nonsense. Either way there is nothing to paint.
        }

        return Brushes.Transparent;
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

        int wanted = Math.Max(1, _settings.Rows * _settings.Columns);
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
        bool changed = false;

        for (int index = 0; index < wanted; index++)
        {
            MeterSlot cell =
                _settings.At(index / _settings.Columns, index % _settings.Columns)
                ?? MeterSlot.Empty();

            cells[index] = cell;

            MeterLine line = MeterReadout.Compose(cell, reading, _settings);
            MeterSlotViewModel slot = Slots[index];
            changed |= slot.Label.Length != line.Label.Length
                || slot.Value.Length != line.Value.Length
                || slot.Unit.Length != line.Unit.Length;

            slot.Label = line.Label;
            slot.Mark = MeterReadout.MarkOf(cell.Field);
            bool icon = line.Label.Length > 0
                && cell.MarkAs == MeterMarkStyle.Icon
                && slot.Mark != MeterMark.None;

            changed |= slot.MarkIsIcon != icon;
            slot.MarkIsIcon = icon;
            slot.PartsOnTheLeft = cell.PartsOnTheLeft;
            slot.Value = line.Value;
            slot.Unit = line.Unit;
            slot.Figure = Painted(cell.ValueColour, line) ?? plain;
            slot.LabelInk = Painted(cell.LabelColour, line) ?? plain;
            slot.Support = Painted(cell.UnitColour, line) ?? support;
            double size = SizeOf(cell.TextSize ?? _settings.TextSize);

            changed |= Math.Abs(slot.Size - size) > 0.01
                || slot.Weight != ((cell.Bold ?? _settings.Bold) ? FontWeights.SemiBold : FontWeights.Normal);

            slot.Size = size;
            slot.SupportSize = SupportOf(size);
            slot.Weight = (cell.Bold ?? _settings.Bold) ? FontWeights.SemiBold : FontWeights.Normal;
        }

        Tooltip = MeterReadout.Tooltip(cells, reading, _settings);

        if (changed)
        {
            Resized?.Invoke(this, EventArgs.Empty);
        }
    }
}
