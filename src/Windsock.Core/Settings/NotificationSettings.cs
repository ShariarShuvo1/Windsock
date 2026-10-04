namespace Windsock.Core.Settings;

/// <summary>
/// How Windsock tells the person using it about something.
/// </summary>
/// <remarks>
/// One switch for everything and one for sound, which is all most people will
/// ever change. The choices about each kind of notice live with the feature that
/// raises it, so a new kind of alert adds its own switch rather than this class
/// growing a list nobody can read.
/// </remarks>
public sealed class NotificationSettings
{
    /// <summary>Whether Windsock sends notifications at all.</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>Whether they make a sound. Windows' own quiet hours still win.</summary>
    public bool Sound { get; set; } = true;
}
