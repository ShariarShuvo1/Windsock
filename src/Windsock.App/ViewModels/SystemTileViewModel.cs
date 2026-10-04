using System.Collections.ObjectModel;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using Windsock.Core.Formatting;

namespace Windsock.App.ViewModels;

/// <summary>Which part of the machine a tile is about.</summary>
public enum SystemPart
{
    Network,

    Processor,

    Graphics,

    Memory,

    Storage,
}

/// <summary>One label and its figure, beside a tile's chart.</summary>
public sealed partial class TileFact(string label) : ObservableObject
{
    /// <summary>What the figure is.</summary>
    public string Label { get; } = label;

    /// <summary>The figure.</summary>
    [ObservableProperty]
    public partial string Value { get; set; } = string.Empty;
}

/// <summary>
/// One part of the machine, at a glance.
/// </summary>
public sealed partial class SystemTileViewModel : ObservableObject
{
    /// <summary>How many readings a tile keeps: five minutes, one a second.</summary>
    public const int Remembered = 300;

    private const double LargestFigure = 30;

    private readonly ReadoutSizer _sizer = new(LargestFigure);
    private readonly List<double> _history = [];

    public SystemTileViewModel(SystemPart part, string caption, string heading, params string[] facts)
    {
        ArgumentNullException.ThrowIfNull(facts);

        Part = part;
        Caption = caption;
        Heading = heading;

        // Not a reading at all rather than a reading of nothing: the lines are
        // drawn from where readings begin, instead of a flat line at zero for
        // the minutes before the section was first opened.
        for (int index = 0; index < Remembered; index++)
        {
            _history.Add(double.NaN);
        }

        foreach (string fact in facts)
        {
            Facts.Add(new TileFact(fact));
        }
    }

    /// <summary>Which part of the machine this is.</summary>
    public SystemPart Part { get; }

    /// <summary>What the tile is called.</summary>
    public string Caption { get; }

    /// <summary>What the chart for this part is called.</summary>
    public string Heading { get; }

    /// <summary>
    /// The top of the tile's scale: a hundred for a share, nothing - take it
    /// from the readings - for a rate.
    /// </summary>
    public double Ceiling => Part == SystemPart.Network ? 0 : 100;

    /// <summary>The figures beside the chart, in the order they are shown.</summary>
    public ObservableCollection<TileFact> Facts { get; } = [];

    /// <summary>The figure itself, without its unit.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Readout))]
    public partial string Value { get; set; } = string.Empty;

    /// <summary>The unit beside the figure.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Readout))]
    public partial string Unit { get; set; } = "%";

    /// <summary>
    /// The figure and its unit as one short string, for the sidebar beside the
    /// part's name: 17%, 6.33 MB/s. Empty until there is a figure, rather than a
    /// zero nobody measured.
    /// </summary>
    public string Readout => Value.Length == 0 ? string.Empty : Unit == "%" ? Value + Unit : Value + " " + Unit;

    /// <summary>How large to set the figure so that it fits.</summary>
    [ObservableProperty]
    public partial double FontSize { get; set; } = LargestFigure;

    /// <summary>The line under the figure.</summary>
    [ObservableProperty]
    public partial string Detail { get; set; } = string.Empty;

    /// <summary>The reading in the tile's corner, usually a temperature.</summary>
    [ObservableProperty]
    public partial string Badge { get; set; } = string.Empty;

    /// <summary>Whether the badge is warm enough to be worth noticing.</summary>
    [ObservableProperty]
    public partial bool IsHot { get; set; }

    /// <summary>Whether this is the tile whose detail is showing.</summary>
    [ObservableProperty]
    public partial bool IsSelected { get; set; }

    /// <summary>Changed whenever a reading is added, to redraw the line.</summary>
    [ObservableProperty]
    public partial int Revision { get; set; }

    /// <summary>The readings behind the figure, oldest first.</summary>
    public IReadOnlyList<double> History => _history;

    /// <summary>Sets the figure from a share, and remembers it.</summary>
    public void Show(double percent)
    {
        Figure(percent.ToString("F0", CultureInfo.InvariantCulture), "%");
        Remember(Math.Clamp(percent, 0, 100));
    }

    /// <summary>Sets the figure from a rate already put into words, and remembers the rate.</summary>
    public void Show(string value, string unit, double bytesPerSecond)
    {
        Figure(value, unit);
        Remember(Math.Max(0, bytesPerSecond));
    }

    /// <summary>
    /// Replaces everything remembered, oldest first, as a tile does when it
    /// is shown again after readings went on without it.
    /// </summary>
    public void Seed(ReadOnlySpan<double> readings)
    {
        int keep = Math.Min(readings.Length, Remembered);

        for (int index = 0; index < Remembered - keep; index++)
        {
            _history[index] = double.NaN;
        }

        for (int index = 0; index < keep; index++)
        {
            _history[Remembered - keep + index] = readings[readings.Length - keep + index];
        }

        Revision++;
    }

    /// <summary>Sets one of the figures beside the chart.</summary>
    public void Fact(int index, string value)
    {
        if (index >= 0 && index < Facts.Count)
        {
            Facts[index].Value = value;
        }
    }

    /// <summary>Sets the corner reading from a temperature.</summary>
    public void Warmth(double? celsius, double hotAt)
    {
        if (celsius is not { } degrees)
        {
            Badge = string.Empty;
            IsHot = false;
            return;
        }

        Badge = Temperature(degrees);
        IsHot = degrees >= hotAt;
    }

    /// <summary>A temperature in the form the tiles show it.</summary>
    public static string Temperature(double celsius) =>
        string.Create(CultureInfo.InvariantCulture, $"{celsius:F0}°C");

    /// <summary>Puts the same reading into different words, remembering nothing.</summary>
    public void Restate(string value, string unit) => Figure(value, unit);

    private void Figure(string value, string unit)
    {
        if (!string.Equals(Value, value, StringComparison.Ordinal))
        {
            Value = value;
            FontSize = _sizer.Update(value.Length);
        }

        Unit = unit;
    }

    private void Remember(double reading)
    {
        _history.RemoveAt(0);
        _history.Add(reading);
        Revision++;
    }
}
