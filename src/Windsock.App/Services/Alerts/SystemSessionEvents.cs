using Microsoft.Win32;
using Windsock.Core.Alerts;

namespace Windsock.App.Services.Alerts;

/// <summary>
/// The screen locking and unlocking, and the PC waking, from Windows' own
/// session and power notices.
/// </summary>
/// <remarks>
/// Windows' <c>SystemEvents</c> carries both: the session notices that
/// <c>WTSRegisterSessionNotification</c> delivers, and the power broadcast. It
/// keeps its own hidden window for them, so nothing here needs a window of
/// Windsock's, and it listens only while this object lives. A wake also tells
/// <see cref="AlertSettling"/>, so every alert watching a measurement waits out
/// the minute after it.
/// </remarks>
public sealed class SystemSessionEvents : ISessionEvents, IDisposable
{
    private readonly AlertSettling _settling;
    private bool _disposed;

    public SystemSessionEvents(AlertSettling settling)
    {
        ArgumentNullException.ThrowIfNull(settling);

        _settling = settling;
        SystemEvents.SessionSwitch += OnSessionSwitch;
        SystemEvents.PowerModeChanged += OnPowerModeChanged;
    }

    /// <inheritdoc />
    public event EventHandler? Locked;

    /// <inheritdoc />
    public event EventHandler? Unlocked;

    /// <inheritdoc />
    public event EventHandler? Resumed;

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;

        // Static events hold their handlers for the life of the process.
        SystemEvents.SessionSwitch -= OnSessionSwitch;
        SystemEvents.PowerModeChanged -= OnPowerModeChanged;
    }

    private void OnSessionSwitch(object? sender, SessionSwitchEventArgs e)
    {
        switch (e.Reason)
        {
            case SessionSwitchReason.SessionLock:
                Locked?.Invoke(this, EventArgs.Empty);
                break;

            case SessionSwitchReason.SessionUnlock:
                Unlocked?.Invoke(this, EventArgs.Empty);
                break;
        }
    }

    private void OnPowerModeChanged(object? sender, PowerModeChangedEventArgs e)
    {
        if (e.Mode != PowerModes.Resume)
        {
            return;
        }

        _settling.NoteWake();
        Resumed?.Invoke(this, EventArgs.Empty);
    }
}
