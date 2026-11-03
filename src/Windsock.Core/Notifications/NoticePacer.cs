namespace Windsock.Core.Notifications;

/// <summary>What flood control made of a notice.</summary>
public enum PaceVerdict
{
    /// <summary>It may go out, and was counted.</summary>
    Admitted,

    /// <summary>The same notice went out a moment ago.</summary>
    Repeat,

    /// <summary>Too many notices went out lately.</summary>
    Burst,
}

/// <summary>
/// Stops Windsock from burying somebody in notices.
/// </summary>
/// <remarks>
/// <para>
/// Two rules. The same notice - same topic, tag and words - is not repeated
/// inside <see cref="RepeatWindow"/>, which catches a flapping condition that
/// keeps re-raising itself. And no more than <see cref="BurstLimit"/> notices go
/// out inside <see cref="BurstWindow"/> altogether, which catches everything
/// else going wrong at once.
/// </para>
/// <para>
/// A notice held back is not queued to be sent later: by the time the window
/// reopens, what it said is stale.
/// </para>
/// </remarks>
public sealed class NoticePacer
{
    /// <summary>How long an identical notice is kept from repeating.</summary>
    public static readonly TimeSpan RepeatWindow = TimeSpan.FromMinutes(1);

    /// <summary>The span the burst limit is counted over.</summary>
    public static readonly TimeSpan BurstWindow = TimeSpan.FromMinutes(5);

    /// <summary>How many notices may go out inside one burst window.</summary>
    public const int BurstLimit = 6;

    private readonly TimeProvider _clock;
    private readonly Queue<DateTimeOffset> _sent = new();
    private readonly Dictionary<string, DateTimeOffset> _lastSaid = new(StringComparer.Ordinal);
    private readonly Lock _gate = new();

    public NoticePacer(TimeProvider? clock = null) => _clock = clock ?? TimeProvider.System;

    /// <summary>
    /// Whether a notice may go out now, counting it as sent when it may.
    /// </summary>
    public bool Admit(Notice notice) => Pace(notice) == PaceVerdict.Admitted;

    /// <summary>
    /// Says what flood control makes of a notice, counting it when it is admitted.
    /// </summary>
    /// <param name="notice">The notice.</param>
    /// <param name="holding">
    /// Whether it is being held for later rather than shown - during quiet
    /// hours. A held notice is remembered, so a flapping condition does not
    /// fill the held list with copies, but it does not use up the burst: it
    /// interrupts nobody now.
    /// </param>
    public PaceVerdict Pace(Notice notice, bool holding = false)
    {
        ArgumentNullException.ThrowIfNull(notice);

        DateTimeOffset now = _clock.GetUtcNow();

        // Joined with a unit separator, which ordinary text never carries.
        string said = string.Join('\u001F', notice.Topic, notice.Tag ?? string.Empty, notice.Title, notice.Message);

        lock (_gate)
        {
            while (_sent.Count > 0 && now - _sent.Peek() >= BurstWindow)
            {
                _sent.Dequeue();
            }

            if (_lastSaid.TryGetValue(said, out DateTimeOffset last) && now - last < RepeatWindow)
            {
                return PaceVerdict.Repeat;
            }

            if (!holding && _sent.Count >= BurstLimit)
            {
                return PaceVerdict.Burst;
            }

            if (!holding)
            {
                _sent.Enqueue(now);
            }

            _lastSaid[said] = now;
            Forget(now);

            return PaceVerdict.Admitted;
        }
    }

    /// <summary>Drops remembered notices too old to matter, so the map cannot grow for ever.</summary>
    private void Forget(DateTimeOffset now)
    {
        if (_lastSaid.Count < 64)
        {
            return;
        }

        foreach (string stale in _lastSaid.Where(each => now - each.Value >= RepeatWindow).Select(each => each.Key).ToArray())
        {
            _lastSaid.Remove(stale);
        }
    }
}
