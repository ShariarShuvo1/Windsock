using System.Globalization;
using Windsock.Core.Settings;

namespace Windsock.App.ViewModels;

/// <summary>A page the window can open on, for the General page's picker.</summary>
/// <param name="Link">The page's link name, or empty for the page last open.</param>
/// <param name="Label">What the picker says.</param>
public sealed record OpenOnOption(string Link, string Label)
{
    /// <summary>The label, which is what UI Automation announces for the item.</summary>
    public override string ToString() => Label;
}

/// <summary>A density, for the Appearance page's picker.</summary>
public sealed record DensityOption(Density Density, string Label)
{
    /// <summary>The label, which is what UI Automation announces for the item.</summary>
    public override string ToString() => Label;
}

/// <summary>
/// Settings' choices about where the window opens and how it is drawn beyond
/// light and dark: the page to open on, the Windows accent, the density, and
/// what Windows' own text size and contrast settings are doing.
/// </summary>
public sealed partial class SettingsPanelViewModel
{
    /// <summary>The pages the window can open on, the last one open first.</summary>
    public static IReadOnlyList<OpenOnOption> OpenOnChoices { get; } =
    [
        new(WindowSettings.LastPageChoice, "Where it was last closed"),
        new("taskbar", "Taskbar"),
        new("overview", "Network › Overview"),
        new("apps", "Network › Apps"),
        new("map", "Network › Map"),
        new("status", "Connection › Status"),
        new("outages", "Connection › Outages"),
        new("history", "Usage › History"),
        new("plan", "Usage › Data plan"),
        new("processor", "System › Processor"),
        new("graphics", "System › Graphics"),
        new("memory", "System › Memory"),
        new("storage", "System › Storage"),
        new("alerts", "Alerts"),
    ];

    /// <summary>The densities, roomier first.</summary>
    public static IReadOnlyList<DensityOption> Densities { get; } =
    [
        new(Density.Comfortable, "Comfortable"),
        new(Density.Compact, "Compact"),
    ];

    /// <summary>The page the window opens on, by link name; empty for the page last open.</summary>
    public string SelectedOpenOn
    {
        get => _settings.Window.OpenOn;
        set
        {
            value ??= WindowSettings.LastPageChoice;

            if (string.Equals(_settings.Window.OpenOn, value, StringComparison.Ordinal))
            {
                return;
            }

            _settings.Window.OpenOn = value;
            _store.Save(_settings);
            OnPropertyChanged();
        }
    }

    /// <summary>Whether the accent takes the colour chosen in Windows.</summary>
    public bool UseWindowsAccent
    {
        get => _settings.Appearance.UseWindowsAccent;
        set
        {
            if (_settings.Appearance.UseWindowsAccent == value)
            {
                return;
            }

            _settings.Appearance.UseWindowsAccent = value;
            _store.Save(_settings);
            _theme.ApplyAppearance(_settings.Appearance);
            OnPropertyChanged();
        }
    }

    /// <summary>How much room rows, plates and controls are given.</summary>
    public Density SelectedDensity
    {
        get => _settings.Appearance.Density;
        set
        {
            if (_settings.Appearance.Density == value)
            {
                return;
            }

            _settings.Appearance.Density = value;
            _store.Save(_settings);
            _theme.ApplyAppearance(_settings.Appearance);
            OnPropertyChanged();
        }
    }

    /// <summary>What Windows' Text size and contrast settings are doing to the window, in a sentence.</summary>
    public string SystemLookNote
    {
        get
        {
            string size = _theme.TextScale > 1.0
                ? string.Create(CultureInfo.CurrentCulture, $"Text is drawn larger, at Windows' Text size of {_theme.TextScale:P0}.")
                : "Text follows Windows' Text size, which is at 100%.";

            return _theme.IsHighContrast
                ? string.Concat(size, " A contrast theme is on, so its colours are used and the accent and tones above step aside.")
                : size;
        }
    }
}
