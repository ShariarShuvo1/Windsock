using Windsock.Core.Settings;

namespace Windsock.Core.Meter;

/// <summary>What a left press on the meter should do now.</summary>
public enum PressStep
{
    /// <summary>Nothing: the second press of a double click nobody gave a job.</summary>
    Nothing,

    /// <summary>The click's action, at once.</summary>
    Click,

    /// <summary>Wait out the double-click time, then the click's action unless a second press came.</summary>
    Wait,

    /// <summary>The double click's action, at once, and the waiting click forgotten.</summary>
    DoubleClick,
}

/// <summary>Decides single against double clicks on the meter.</summary>
/// <remarks>
/// The count comes from the button going down: WPF counts presses in quick
/// succession there, and a button coming up always says one. With no job for a
/// double click, a click acts at once and the second press of a quick pair is
/// ignored, so a double click is not two clicks; with one, a click waits
/// Windows' double-click time first.
/// </remarks>
public static class MeterPresses
{
    /// <summary>What a left press that is press number <paramref name="presses"/> in a row should do.</summary>
    /// <remarks>
    /// A third press in a row (and on) does nothing: a triple click is not a
    /// second double click, which would run the double click's action twice.
    /// </remarks>
    public static PressStep Decide(int presses, MeterClick doubleClick) => presses switch
    {
        <= 1 => doubleClick == MeterClick.Nothing ? PressStep.Click : PressStep.Wait,
        2 => doubleClick == MeterClick.Nothing ? PressStep.Nothing : PressStep.DoubleClick,
        _ => PressStep.Nothing,
    };
}
