namespace Windsock.Core.Alerts;

/// <summary>
/// The minute after Windsock starts or the PC wakes, during which no alert
/// watching a measurement may fire.
/// </summary>
/// <remarks>
/// Right after a start or a wake everything is unsettled: Wi-Fi is rejoining,
/// the router is renewing addresses, a download is resuming in a burst, drives
/// are spinning up. What is read then is the machine finding its feet, not
/// something worth interrupting anyone for. Each watcher ignores readings
/// while <see cref="IsSettling"/>, and starts its stretches again on
/// <see cref="Woke"/>.
/// </remarks>
public sealed class AlertSettling
{
    /// <summary>How long after a start or a wake readings are ignored.</summary>
    public static readonly TimeSpan Grace = TimeSpan.FromMinutes(1);

    private readonly TimeProvider _clock;
    private long _beganTicks;

    public AlertSettling(TimeProvider clock)
    {
        ArgumentNullException.ThrowIfNull(clock);

        _clock = clock;
        _beganTicks = clock.GetUtcNow().UtcTicks;
    }

    /// <summary>Raised, on the caller's thread, when the PC wakes or the session comes back.</summary>
    public event EventHandler? Woke;

    /// <summary>When the last start or wake was.</summary>
    public DateTimeOffset Began => new(Interlocked.Read(ref _beganTicks), TimeSpan.Zero);

    /// <summary>Whether readings are still being ignored.</summary>
    public bool IsSettling => _clock.GetUtcNow() - Began < Grace;

    /// <summary>Notes that the PC just woke: the grace starts again, and every watcher starts over.</summary>
    public void NoteWake()
    {
        Interlocked.Exchange(ref _beganTicks, _clock.GetUtcNow().UtcTicks);
        Woke?.Invoke(this, EventArgs.Empty);
    }
}
