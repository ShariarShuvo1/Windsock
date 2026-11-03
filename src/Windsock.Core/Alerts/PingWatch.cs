using Windsock.Core.Connectivity;
using Windsock.Core.Settings;

namespace Windsock.Core.Alerts;

/// <summary>Which answers a ping alert is about.</summary>
public enum PingTarget
{
    /// <summary>The router's: a problem on this side of it, usually Wi-Fi.</summary>
    Router,

    /// <summary>The internet address chosen for the checks.</summary>
    Internet,
}

/// <summary>Responses stayed slow, or too many went missing, for the whole stretch.</summary>
/// <param name="Target">Whose answers.</param>
/// <param name="TypicalMs">The median response time over the stretch, or nothing when none answered.</param>
/// <param name="Loss">The share of checks unanswered, from 0 to 1.</param>
/// <param name="Window">The stretch.</param>
/// <param name="Slow">Whether the response time passed its level.</param>
/// <param name="Lossy">Whether the loss passed its level.</param>
public sealed record PingAlarm(PingTarget Target, double? TypicalMs, double Loss, TimeSpan Window, bool Slow, bool Lossy);

/// <summary>
/// Watches the connection checks for response times or loss above a level for
/// a whole stretch.
/// </summary>
/// <remarks>
/// <para>
/// The stretch is judged as a whole: the median response time over it, and the
/// share of checks in it that went unanswered. A median ignores the odd slow
/// answer, so one spike never fires the alert; it takes most of the stretch
/// being slow. The stretch must be watched from end to end, so nothing can
/// fire before a full stretch has been seen after starting, waking or an
/// outage - the first minute after start or wake included.
/// </para>
/// <para>
/// An outage is not loss: the connection alert covers it, and the stretch
/// starts again after it. Nor is a target that answered nothing in the whole
/// stretch, which is one that does not answer pings at all - many routers drop
/// them - and the caller counts a router only once it has answered on its
/// network. Once fired, the alert is not raised again until a
/// whole stretch has been clearly good - typical response at most 80% of the
/// level and loss under half of its level - so a line hovering at the level
/// cannot fire it over and over.
/// </para>
/// </remarks>
public sealed class PingWatch
{
    /// <summary>The fewest checks a stretch must hold before it is judged.</summary>
    public const int FewestChecks = 3;

    private readonly Queue<LatencyPoint> _points = new();
    private DateTimeOffset? _since;
    private DateTimeOffset _last;

    /// <summary>Whether the alert has fired and not yet been re-armed by a good stretch.</summary>
    public bool HasFired { get; private set; }

    /// <summary>Takes one check, and says when the alert should fire.</summary>
    /// <param name="point">The check.</param>
    /// <param name="rule">The levels.</param>
    /// <param name="gapLimit">A longer silence between checks means nobody was watching - asleep, or not watched.</param>
    public PingAlarm? Observe(in LatencyPoint point, PingAlertSettings rule, TimeSpan gapLimit)
    {
        ArgumentNullException.ThrowIfNull(rule);

        if (!rule.Enabled || (rule.Milliseconds <= 0 && rule.LossPercent <= 0))
        {
            Reset();
            HasFired = false;
            return null;
        }

        if (point.Offline)
        {
            Reset();
            return null;
        }

        if (_since is not null && point.At - _last > gapLimit)
        {
            Reset();
        }

        _since ??= point.At;
        _last = point.At;
        _points.Enqueue(point);

        TimeSpan window = rule.Window;

        while (_points.Count > 0 && _points.Peek().At < point.At - window)
        {
            _points.Dequeue();
        }

        if (point.At - _since < window)
        {
            return null;
        }

        PingTarget target = point.InternetTried ? PingTarget.Internet : PingTarget.Router;
        (int tried, double loss, double? typical) = Judge(target);

        if (tried < FewestChecks)
        {
            return null;
        }

        bool slow = rule.Milliseconds > 0 && typical is { } ms && ms > rule.Milliseconds;
        bool lossy = rule.LossPercent > 0 && loss * 100 >= rule.LossPercent;

        if (!HasFired)
        {
            if (!slow && !lossy)
            {
                return null;
            }

            HasFired = true;
            return new PingAlarm(target, typical, loss, window, slow, lossy);
        }

        bool fast = rule.Milliseconds <= 0 || typical is null || typical <= rule.Milliseconds * 0.8;
        bool kept = rule.LossPercent <= 0 || loss * 100 < rule.LossPercent / 2.0;

        if (fast && kept)
        {
            HasFired = false;
        }

        return null;
    }

    /// <summary>Starts the stretch again, as after waking; a fired alert stays fired.</summary>
    public void Reset()
    {
        _points.Clear();
        _since = null;
    }

    private (int Tried, double Loss, double? Typical) Judge(PingTarget target)
    {
        List<double> answers = [];
        int tried = 0;

        foreach (LatencyPoint each in _points)
        {
            bool wasTried = target == PingTarget.Internet ? each.InternetTried : each.RouterTried;
            double? answer = target == PingTarget.Internet ? each.InternetMs : each.RouterMs;

            if (!wasTried)
            {
                continue;
            }

            tried++;

            if (answer is { } ms)
            {
                answers.Add(ms);
            }
        }

        // A target that answered nothing in the whole stretch is one that does
        // not answer pings - many routers drop them - not one losing them all:
        // an outage, the other way to answer nothing, never reaches here.
        if (tried == 0 || answers.Count == 0)
        {
            return (0, 0, null);
        }

        return (tried, (double)(tried - answers.Count) / tried, RateWindow.Median(answers));
    }
}
