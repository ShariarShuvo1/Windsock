namespace Windsock.Core.Connectivity;

/// <summary>One check, as the latency chart draws it.</summary>
/// <param name="At">When it was taken.</param>
/// <param name="RouterMs">The router's round trip in milliseconds, or nothing when it did not answer.</param>
/// <param name="InternetMs">The internet target's round trip, or nothing.</param>
/// <param name="RouterTried">Whether there was a router to ping.</param>
/// <param name="InternetTried">Whether an internet target was pinged.</param>
/// <param name="Offline">Whether an outage was in progress.</param>
public readonly record struct LatencyPoint(
    DateTimeOffset At,
    double? RouterMs,
    double? InternetMs,
    bool RouterTried,
    bool InternetTried,
    bool Offline);

/// <summary>Chooses the top of the latency chart.</summary>
public static class LatencyScale
{
    /// <summary>The lowest top the chart is allowed, so a quiet line does not fill the height.</summary>
    public const double Floor = 10;

    private static readonly double[] Steps = [1, 2, 5, 10];

    /// <summary>Rounds a peak in milliseconds up to 1, 2 or 5 of a power of ten, and never below <see cref="Floor"/>.</summary>
    public static double Ceiling(double peakMs)
    {
        if (!double.IsFinite(peakMs) || peakMs <= Floor)
        {
            return Floor;
        }

        double magnitude = Math.Pow(10, Math.Floor(Math.Log10(peakMs)));

        foreach (double step in Steps)
        {
            if (peakMs <= step * magnitude * (1 + 1e-9))
            {
                return step * magnitude;
            }
        }

        return 10 * magnitude;
    }

    /// <summary>The median of the answered round trips, in milliseconds, or nothing when none answered.</summary>
    public static double? Median(IEnumerable<double?> values)
    {
        ArgumentNullException.ThrowIfNull(values);

        double[] answered = [.. values.Where(value => value is not null).Select(value => value!.Value)];

        if (answered.Length == 0)
        {
            return null;
        }

        Array.Sort(answered);
        int middle = answered.Length / 2;

        return answered.Length % 2 == 1 ? answered[middle] : (answered[middle - 1] + answered[middle]) / 2;
    }
}

/// <summary>The recent checks, oldest first, in a fixed amount of memory.</summary>
public sealed class LatencyHistory
{
    private readonly LatencyPoint[] _points;
    private readonly Lock _gate = new();
    private int _next;
    private int _count;

    public LatencyHistory(int capacity)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(capacity, 2);
        _points = new LatencyPoint[capacity];
    }

    /// <summary>How many checks it can hold.</summary>
    public int Capacity => _points.Length;

    /// <summary>Adds a check, dropping the oldest once full.</summary>
    public void Add(in LatencyPoint point)
    {
        lock (_gate)
        {
            _points[_next] = point;
            _next = (_next + 1) % _points.Length;
            _count = Math.Min(_count + 1, _points.Length);
        }
    }

    /// <summary>Forgets everything.</summary>
    public void Clear()
    {
        lock (_gate)
        {
            _next = 0;
            _count = 0;
        }
    }

    /// <summary>The checks taken since a moment, oldest first.</summary>
    public LatencyPoint[] Since(DateTimeOffset from)
    {
        lock (_gate)
        {
            List<LatencyPoint> kept = new(_count);

            for (int each = 0; each < _count; each++)
            {
                LatencyPoint point = _points[(_next - _count + each + _points.Length) % _points.Length];

                if (point.At >= from)
                {
                    kept.Add(point);
                }
            }

            return [.. kept];
        }
    }

    /// <summary>
    /// The share of router pings since a moment that went unanswered, or nothing
    /// when none were sent.
    /// </summary>
    public double? RouterLoss(DateTimeOffset from) => Loss(from, point => point.RouterTried, point => point.RouterMs);

    /// <summary>The share of internet pings since a moment that went unanswered.</summary>
    public double? InternetLoss(DateTimeOffset from) => Loss(from, point => point.InternetTried, point => point.InternetMs);

    private double? Loss(DateTimeOffset from, Func<LatencyPoint, bool> tried, Func<LatencyPoint, double?> answer)
    {
        int sent = 0;
        int lost = 0;

        foreach (LatencyPoint point in Since(from))
        {
            if (!tried(point))
            {
                continue;
            }

            sent++;

            if (answer(point) is null)
            {
                lost++;
            }
        }

        return sent == 0 ? null : (double)lost / sent;
    }
}
