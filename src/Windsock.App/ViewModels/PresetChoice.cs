using CommunityToolkit.Mvvm.ComponentModel;
using Windsock.Core.Formatting;
using Windsock.Core.Meter;
using Windsock.Core.Settings;

namespace Windsock.App.ViewModels;

/// <summary>A one-click layout as the Taskbar page and the welcome offer it.</summary>
public sealed partial class PresetChoice : ObservableObject
{
    public PresetChoice(MeterPreset preset)
    {
        ArgumentNullException.ThrowIfNull(preset);

        Preset = preset;
        Readings = string.Join(" · ", preset.Places
            .Where(place => place.Field != MeterField.Empty)
            .Select(place => MeterReadout.Name(place.Field)));
    }

    /// <summary>The layout itself.</summary>
    public MeterPreset Preset { get; }

    /// <summary>Its name.</summary>
    public string Name => Preset.Name;

    /// <summary>What it is for, in a sentence.</summary>
    public string Note => Preset.Note;

    /// <summary>What it puts on the bar, named.</summary>
    public string Readings { get; }

    /// <summary>Whether the meter is laid out exactly this way now.</summary>
    [ObservableProperty]
    public partial bool IsChosen { get; set; }

    /// <summary>The presets that suit this machine, marked as the meter stands.</summary>
    public static IReadOnlyList<PresetChoice> For(bool hasBattery, TaskbarSettings settings)
    {
        List<PresetChoice> choices = [.. MeterPresets.For(hasBattery).Select(preset => new PresetChoice(preset))];
        Mark(choices, settings);

        return choices;
    }

    /// <summary>Marks the one the meter now matches, if any.</summary>
    public static void Mark(IEnumerable<PresetChoice> choices, TaskbarSettings settings)
    {
        ArgumentNullException.ThrowIfNull(choices);
        ArgumentNullException.ThrowIfNull(settings);

        foreach (PresetChoice choice in choices)
        {
            choice.IsChosen = choice.Preset.Matches(settings);
        }
    }

    /// <inheritdoc />
    public override string ToString() => Name;
}
