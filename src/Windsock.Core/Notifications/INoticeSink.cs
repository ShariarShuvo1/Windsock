namespace Windsock.Core.Notifications;

/// <summary>
/// Somewhere a notice can be sent, so a feature in this project can send one
/// without knowing how the app shows it.
/// </summary>
public interface INoticeSink
{
    /// <summary>Sends a notice; false when it was not sent - switched off, held back, or refused.</summary>
    bool Send(Notice notice);
}

/// <summary>A sink that hands notices to a method, such as the app's notification service.</summary>
public sealed class NoticeSink(Func<Notice, bool> send) : INoticeSink
{
    private readonly Func<Notice, bool> _send = send ?? throw new ArgumentNullException(nameof(send));

    /// <inheritdoc />
    public bool Send(Notice notice) => _send(notice);
}
