namespace Windsock.Core.Notifications;

/// <summary>The sound a notice makes, chosen per kind of alert on the Alerts page.</summary>
/// <remarks>
/// The named sounds are Windows' own notification sounds, the ones a notice
/// may ask for by name; nothing is shipped. A balloon - the fallback while
/// Windsock runs as administrator - can only make Windows' default sound or
/// none, so any named sound plays as the default there.
/// </remarks>
public enum NoticeSound
{
    /// <summary>Whatever the notice itself says: Windows' default sound, or none for a notice that is quiet by nature.</summary>
    Default,

    /// <summary>No sound.</summary>
    Silent,

    /// <summary>Windows' reminder sound.</summary>
    Reminder,

    /// <summary>Windows' instant message sound.</summary>
    Message,

    /// <summary>Windows' new mail sound.</summary>
    Mail,

    /// <summary>Windows' text message sound.</summary>
    Text,
}
