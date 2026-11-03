using Windsock.Core.Formatting;

namespace Windsock.App.ViewModels;

/// <summary>One entry in the temperature scale picker.</summary>
public sealed record TemperatureOption(TemperatureUnit Unit, string Label)
{
    /// <summary>Returns the label, so automation announces it by name.</summary>
    public override string ToString() => Label;
}

/// <summary>Settings › Appearance: the scale temperatures are written in.</summary>
public sealed partial class SettingsPanelViewModel
{
    /// <summary>The scales on offer.</summary>
    public IReadOnlyList<TemperatureOption> TemperatureUnits { get; } =
    [
        new(TemperatureUnit.Celsius, "Celsius (°C)"),
        new(TemperatureUnit.Fahrenheit, "Fahrenheit (°F)"),
    ];

    /// <summary>
    /// The scale every temperature is written in, the taskbar meter's
    /// included. Read straight from the settings, so showing the page saves
    /// nothing; choosing saves it and tells every page at once.
    /// </summary>
    public TemperatureUnit SelectedTemperature
    {
        get => _settings.Temperature;
        set
        {
            if (_settings.Temperature == value)
            {
                return;
            }

            _settings.Temperature = value;
            _store.Save(_settings);
            TemperatureScale.Set(value);
            OnPropertyChanged();
        }
    }
}
