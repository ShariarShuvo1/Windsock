using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using Windsock.Core.Formatting;

namespace Windsock.App.ViewModels;

/// <summary>Which part of the machine a tile is about.</summary>
public enum SystemPart
{
    Processor,

    Graphics,

    Memory,

    Storage,
}

/// <summary>
/// One part of the machine, at a glance.
/// </summary>
public sealed partial class SystemTileViewModel : ObservableObject
{
    private const int Remembered = 120;
    private const double LargestFigure = 34;

    private readonly ReadoutSizer _sizer = new(LargestFigure);
    private readonly List<double> _history = [];

    public SystemTileViewModel(SystemPart part, string caption)
    {
        Part = part;
        Caption = caption;
        for (int index = 0; index < Remembered; index++)
        {
            _history.Add(0);
        }
    }

    /// <summary>Which part of the machine this is.</summary>
    public SystemPart Part { get; }

    /// <summary>What the tile is called.</summary>
    public string Caption { get; }

    /// <summary>The figure itself, without its unit.</summary>
    [ObservableProperty]
    public partial string Value { get; set; } = "0";

    /// <summary>The unit beside the figure.</summary>
    [ObservableProperty]
    public partial string Unit { get; set; } = "%";

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

    /// <summary>Sets the figure and remembers it.</summary>
    public void Show(double percent)
    {
        string value = percent.ToString("F0", CultureInfo.InvariantCulture);

        Value = value;
        FontSize = _sizer.Update(value.Length);

        _history.RemoveAt(0);
        _history.Add(Math.Clamp(percent, 0, 100));
        Revision++;
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
}
