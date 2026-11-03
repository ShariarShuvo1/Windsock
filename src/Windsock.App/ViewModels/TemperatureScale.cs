using Windsock.Core.Formatting;
using Windsock.Core.Settings;

namespace Windsock.App.ViewModels;

/// <summary>
/// The scale the window writes temperatures in, as Settings › Appearance has it.
/// </summary>
/// <remarks>
/// One place every page asks, so a tile, a drive, History's lanes and the
/// System pages all follow the one setting without each holding the settings.
/// Readings stay in Celsius everywhere; only what is written changes. The
/// taskbar meter reads the setting itself.
/// </remarks>
public static class TemperatureScale
{
    private static int _unit;

    /// <summary>Raised on the thread that changed it when the scale changes.</summary>
    public static event EventHandler? Changed;

    /// <summary>The scale in use.</summary>
    public static TemperatureUnit Unit => (TemperatureUnit)Volatile.Read(ref _unit);

    /// <summary>Takes the scale from the settings, as the window opens.</summary>
    public static void Follow(WindsockSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        Set(settings.Temperature);
    }

    /// <summary>Changes the scale, and says so if it changed.</summary>
    public static void Set(TemperatureUnit unit)
    {
        if (Interlocked.Exchange(ref _unit, (int)unit) != (int)unit)
        {
            Changed?.Invoke(null, EventArgs.Empty);
        }
    }

    /// <summary>A temperature in the scale in use: "64°C" or "147°F".</summary>
    public static string Format(double celsius) => TemperatureFormatter.Format(celsius, Unit);

    /// <summary>A temperature, or a dash where there is none.</summary>
    public static string Format(double? celsius) => TemperatureFormatter.Format(celsius, Unit);
}
