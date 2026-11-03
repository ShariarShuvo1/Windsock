namespace Windsock.Core.Alerts;

/// <summary>What happened between the screen locking and unlocking.</summary>
/// <param name="Since">When it locked.</param>
/// <param name="Until">When it unlocked.</param>
/// <param name="Received">Bytes downloaded meanwhile.</param>
/// <param name="Sent">Bytes uploaded meanwhile.</param>
public sealed record AwayReport(DateTimeOffset Since, DateTimeOffset Until, long Received, long Sent)
{
    /// <summary>How long the screen was locked.</summary>
    public TimeSpan Duration => Until - Since;
}

/// <summary>
/// Counts what moves while the screen is locked, for the summary on unlocking.
/// </summary>
/// <remarks>
/// Counted from the meter's own readings rather than read back from history,
/// so it works with recording paused, and costs nothing while the screen is
/// unlocked: the caller only feeds it while locked.
/// </remarks>
public sealed class AwayTally
{
    private readonly Lock _gate = new();
    private DateTimeOffset? _since;
    private long _received;
    private long _sent;

    /// <summary>Whether the screen is locked, as far as this knows.</summary>
    public bool IsLocked
    {
        get
        {
            lock (_gate)
            {
                return _since is not null;
            }
        }
    }

    /// <summary>The screen locked: start counting.</summary>
    public void Lock(DateTimeOffset at)
    {
        lock (_gate)
        {
            _since ??= at;
            _received = 0;
            _sent = 0;
        }
    }

    /// <summary>Adds what moved over one reading, while locked.</summary>
    public void Count(long received, long sent)
    {
        lock (_gate)
        {
            if (_since is null)
            {
                return;
            }

            _received += Math.Max(0, received);
            _sent += Math.Max(0, sent);
        }
    }

    /// <summary>The screen unlocked: what happened while it was locked, or nothing when it was not known to be.</summary>
    public AwayReport? Unlock(DateTimeOffset at)
    {
        lock (_gate)
        {
            if (_since is not { } since)
            {
                return null;
            }

            _since = null;
            return at > since ? new AwayReport(since, at, _received, _sent) : null;
        }
    }
}
