using System.Text.Json.Serialization;

namespace Windsock.Core.Settings;

/// <summary>
/// Keyboard shortcuts that work from anywhere in Windows, written the way a
/// person would read them ("Ctrl+Alt+W"). None is set until someone sets it:
/// a shortcut taken by default is one some other program finds already gone.
/// </summary>
public sealed class MeterShortcuts
{
    /// <summary>Brings Windsock's window to the front.</summary>
    public string? ShowWindow { get; set; }

    /// <summary>Takes the meter off the taskbar, or puts it back.</summary>
    public string? ToggleMeter { get; set; }

    /// <summary>Pauses the network chart, or resumes it.</summary>
    public string? PauseChart { get; set; }

    /// <summary>Whether any shortcut is set. Worked out, never written down.</summary>
    [JsonIgnore]
    public bool Any =>
        !string.IsNullOrWhiteSpace(ShowWindow)
        || !string.IsNullOrWhiteSpace(ToggleMeter)
        || !string.IsNullOrWhiteSpace(PauseChart);

    /// <summary>A copy.</summary>
    public MeterShortcuts Copy() => (MeterShortcuts)MemberwiseClone();
}
