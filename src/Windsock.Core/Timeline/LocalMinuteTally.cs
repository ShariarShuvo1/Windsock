using Windsock.Core.History;

namespace Windsock.Core.Timeline;

/// <summary>
/// Turns readings of the trace's local/internet totals into whole minutes'
/// local shares, by the same rules as each app's minutes.
/// </summary>
/// <remarks>
/// A minute is handed over only when it was watched from its first second to
/// its last, readings no more than 35 seconds apart, and not in the first
/// minute of watching, when the trace hands over what it buffered as it
/// started. The stretch between two readings belongs to the minute its middle
/// is in; the reading between two minutes must be within two seconds of the
/// edge. These are <see cref="AppMinuteTally"/>'s rules, measured there, so the
/// local share of a minute is cut from the same stretch its apps were.
/// </remarks>
public sealed class LocalMinuteTally
{
    private static readonly TimeSpan Slack = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan Warmup = TimeSpan.FromSeconds(60);

    private DateTimeOffset? _lastAt;
    private TrafficSplit _last;
    private DateTimeOffset _watchingSince;
    private long _openMinute = long.MinValue;
    private bool _openWhole;
    private TrafficSplit _open;

    /// <summary>
    /// Takes one reading. Returns the minute finished by it, if one was, and
    /// was watched whole.
    /// </summary>
    /// <param name="at">When the reading was taken.</param>
    /// <param name="measuring">Whether the trace is attributing and history is being kept.</param>
    /// <param name="totals">The trace's running totals.</param>
    public LocalMinute? Observe(DateTimeOffset at, bool measuring, TrafficSplit totals)
    {
        if (!measuring || _lastAt is not { } lastAt || at - lastAt > AppMinuteTally.LongestGap || at <= lastAt)
        {
            Restart(measuring ? at : null, totals);
            return null;
        }

        long minute = EpochMinute.From(lastAt + ((at - lastAt) / 2));
        LocalMinute? finished = null;

        if (minute != _openMinute)
        {
            bool onTheEdge = (lastAt - EpochMinute.ToUtc(minute)).Duration() <= Slack;

            if (_openWhole && onTheEdge && minute == _openMinute + 1)
            {
                finished = new LocalMinute(_openMinute, _open.LocalReceived, _open.LocalSent);
            }

            _open = default;
            _openMinute = minute;
            _openWhole = onTheEdge && EpochMinute.ToUtc(minute) >= _watchingSince + Warmup - Slack;
        }

        TrafficSplit moved = totals.Since(_last);
        _open = new TrafficSplit(
            _open.LocalSent + moved.LocalSent,
            _open.LocalReceived + moved.LocalReceived,
            _open.InternetSent + moved.InternetSent,
            _open.InternetReceived + moved.InternetReceived);

        _last = totals;
        _lastAt = at;
        return finished;
    }

    private void Restart(DateTimeOffset? at, TrafficSplit totals)
    {
        _open = default;
        _openMinute = long.MinValue;
        _openWhole = false;
        _lastAt = at;
        _watchingSince = at ?? default;
        _last = totals;
    }
}
