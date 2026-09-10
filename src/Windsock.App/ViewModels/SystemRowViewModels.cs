using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using Windsock.Core.Formatting;
using Windsock.Core.Hardware;

namespace Windsock.App.ViewModels;

/// <summary>
/// A reading with a bar behind it.
/// </summary>
public sealed partial class MeterRowViewModel(string key, string label) : ObservableObject
{
    /// <summary>What this row is, for matching it between readings.</summary>
    public string Key { get; } = key;

    /// <summary>What the row is called.</summary>
    [ObservableProperty]
    public partial string Label { get; set; } = label;

    /// <summary>How much of the bar is filled, from nothing to one.</summary>
    [ObservableProperty]
    public partial double Fraction { get; set; }

    /// <summary>The figure at the end of the row.</summary>
    [ObservableProperty]
    public partial string Value { get; set; } = "0%";

    /// <summary>Sets both from a percentage.</summary>
    public void Show(double percent)
    {
        Fraction = Math.Clamp(percent / 100, 0, 1);
        Value = string.Create(CultureInfo.InvariantCulture, $"{percent:F0}%");
    }
}

/// <summary>One memory module, as the firmware describes it.</summary>
public sealed class MemoryModuleViewModel(MemoryModule module)
{
    /// <summary>Which slot it is in.</summary>
    public string Slot { get; } = module.Slot.Length > 0 ? module.Slot : "Slot";

    /// <summary>How large it is.</summary>
    public string Size { get; } = SizeFormatter.Format(module.Bytes);

    /// <summary>What kind it is and how fast it is running.</summary>
    public string Speed { get; } = Describe(module);

    /// <summary>Who made it, and what it is.</summary>
    public string Part { get; } = Smbios.Join(module.Maker, module.Part);

    private static string Describe(MemoryModule module)
    {
        if (module.Megatransfers <= 0)
        {
            return module.Kind;
        }

        string speed = string.Create(CultureInfo.InvariantCulture, $"{module.Megatransfers} MT/s");

        return module.Kind.Length > 0
            ? string.Create(CultureInfo.InvariantCulture, $"{module.Kind} · {speed}")
            : speed;
    }
}

/// <summary>One mounted volume, and how full it is.</summary>
public sealed partial class VolumeViewModel(string letter) : ObservableObject
{
    /// <summary>The drive letter, for matching it between readings.</summary>
    public string Key { get; } = letter;

    /// <summary>Its letter and label.</summary>
    [ObservableProperty]
    public partial string Label { get; set; } = letter;

    /// <summary>Its file system.</summary>
    [ObservableProperty]
    public partial string Format { get; set; } = string.Empty;

    /// <summary>How full it is, from nothing to one.</summary>
    [ObservableProperty]
    public partial double Fraction { get; set; }

    /// <summary>What is left, and out of how much.</summary>
    [ObservableProperty]
    public partial string Free { get; set; } = string.Empty;

    /// <summary>Takes the latest reading.</summary>
    public void Update(VolumeReading volume)
    {
        ArgumentNullException.ThrowIfNull(volume);

        Label = volume.Label.Length > 0
            ? string.Create(CultureInfo.InvariantCulture, $"{volume.Letter}  {volume.Label}")
            : volume.Letter;

        Format = volume.Format;
        Fraction = Math.Clamp(volume.UsedPercent / 100, 0, 1);

        Free = string.Create(
            CultureInfo.InvariantCulture,
            $"{SizeFormatter.Format(volume.Free)} free of {SizeFormatter.Format(volume.Size)}");
    }
}

/// <summary>One drive: what it is, how full, how hot, how busy.</summary>
public sealed partial class DriveViewModel(int index) : ObservableObject
{
    /// <summary>Its physical drive number, for matching it between readings.</summary>
    public int Key { get; } = index;

    /// <summary>The model name it reports.</summary>
    [ObservableProperty]
    public partial string Model { get; set; } = string.Empty;

    /// <summary>How it is attached, what kind it is, and how large.</summary>
    [ObservableProperty]
    public partial string Description { get; set; } = string.Empty;

    /// <summary>Its temperature, or nothing if it will not say.</summary>
    [ObservableProperty]
    public partial string Temperature { get; set; } = string.Empty;

    /// <summary>Whether that temperature is worth noticing.</summary>
    [ObservableProperty]
    public partial bool IsHot { get; set; }

    /// <summary>How much of its rated write life has been spent.</summary>
    [ObservableProperty]
    public partial string Life { get; set; } = string.Empty;

    /// <summary>Whether that figure is far enough along to matter.</summary>
    [ObservableProperty]
    public partial bool IsWorn { get; set; }

    /// <summary>How full it is, from nothing to one.</summary>
    [ObservableProperty]
    public partial double Fraction { get; set; }

    /// <summary>What is used, and out of how much.</summary>
    [ObservableProperty]
    public partial string Capacity { get; set; } = string.Empty;

    /// <summary>What it is reading.</summary>
    [ObservableProperty]
    public partial string Reading { get; set; } = string.Empty;

    /// <summary>What it is writing.</summary>
    [ObservableProperty]
    public partial string Writing { get; set; } = string.Empty;

    /// <summary>How much of the interval it was working.</summary>
    [ObservableProperty]
    public partial string Busy { get; set; } = string.Empty;

    /// <summary>How busy it was, from nothing to one.</summary>
    [ObservableProperty]
    public partial double BusyFraction { get; set; }

    /// <summary>The volumes it carries.</summary>
    public System.Collections.ObjectModel.ObservableCollection<VolumeViewModel> Volumes { get; } = [];
}

/// <summary>One display adapter, and everything it will say about itself.</summary>
public sealed partial class GraphicsCardViewModel(string name) : ObservableObject
{
    /// <summary>Its name, for matching it between readings.</summary>
    public string Key { get; } = name;

    /// <summary>What it is called.</summary>
    [ObservableProperty]
    public partial string Name { get; set; } = name;

    /// <summary>The driver behind it, where one will say.</summary>
    [ObservableProperty]
    public partial string Driver { get; set; } = string.Empty;

    /// <summary>How busy it is.</summary>
    [ObservableProperty]
    public partial string Busy { get; set; } = "0%";

    /// <summary>How busy it is, from nothing to one.</summary>
    [ObservableProperty]
    public partial double BusyFraction { get; set; }

    /// <summary>Its temperature, or nothing if it will not say.</summary>
    [ObservableProperty]
    public partial string Temperature { get; set; } = string.Empty;

    /// <summary>What it is drawing.</summary>
    [ObservableProperty]
    public partial string Power { get; set; } = string.Empty;

    /// <summary>How hard its fan is working.</summary>
    [ObservableProperty]
    public partial string Fan { get; set; } = string.Empty;

    /// <summary>Its core clock.</summary>
    [ObservableProperty]
    public partial string Clock { get; set; } = string.Empty;

    /// <summary>Its memory clock.</summary>
    [ObservableProperty]
    public partial string MemoryClock { get; set; } = string.Empty;

    /// <summary>How much of its memory is in use, from nothing to one.</summary>
    [ObservableProperty]
    public partial double MemoryFraction { get; set; }

    /// <summary>What is in use, and out of how much.</summary>
    [ObservableProperty]
    public partial string Memory { get; set; } = string.Empty;

    /// <summary>Whether this adapter reports any sensors at all.</summary>
    [ObservableProperty]
    public partial bool HasSensors { get; set; }

    /// <summary>What each of its engines is doing.</summary>
    public System.Collections.ObjectModel.ObservableCollection<MeterRowViewModel> Engines { get; } = [];

    /// <summary>Which processes are using it.</summary>
    public System.Collections.ObjectModel.ObservableCollection<MeterRowViewModel> Processes { get; } = [];
}
