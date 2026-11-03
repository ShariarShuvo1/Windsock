namespace Windsock.Core.Connectivity;

/// <summary>Which of the connection checks' targets figures are about.</summary>
public enum CheckTarget
{
    /// <summary>The router: the home network alone.</summary>
    Router,

    /// <summary>The internet address chosen in the connection settings.</summary>
    Internet,
}

/// <summary>How steady the connection was over a stretch of checks.</summary>
/// <param name="Target">Whose answers these are.</param>
/// <param name="Sent">Checks sent.</param>
/// <param name="Answered">Checks answered.</param>
/// <param name="MedianMs">The median round trip.</param>
/// <param name="WorstMs">The slowest round trip.</param>
/// <param name="JitterMs">
/// Variation: the mean absolute difference between consecutive answered round
/// trips (RFC 5481's IPDV, averaged, as Cloudflare's speed test reports it).
/// </param>
/// <param name="SmoothedJitterMs">The same differences through RFC 3550's running estimate (gain 1/16), for comparison.</param>
public sealed record Steadiness(
    CheckTarget Target,
    int Sent,
    int Answered,
    double? MedianMs,
    double? WorstMs,
    double? JitterMs,
    double? SmoothedJitterMs)
{
    /// <summary>The fewest answers a jitter figure is given from.</summary>
    public const int FewestForJitter = 10;

    /// <summary>The fewest checks a loss figure is given from.</summary>
    public const int FewestForLoss = 20;

    /// <summary>The share of checks not answered, 0 to 1, when enough were sent to say.</summary>
    public double? Loss => Sent >= FewestForLoss ? (double)(Sent - Answered) / Sent : null;

    /// <summary>The loss over whatever was sent, however few.</summary>
    public double? RawLoss => Sent > 0 ? (double)(Sent - Answered) / Sent : null;

    /// <summary>Whether there is enough to say anything.</summary>
    public bool HasFigures => Answered > 0;
}

/// <summary>
/// Works out jitter and loss from the checks the connection monitor already
/// makes, sending nothing of its own.
/// </summary>
/// <remarks>
/// <para>
/// Jitter here is round-trip variation between checks a few seconds apart,
/// not RFC 3550's one-way jitter between packets of a call: the checks are
/// ICMP echoes, both directions are in every figure, and a router answering
/// echoes slowly when busy adds its own share. So the page says "variation in
/// response time", and the grade's thresholds are ones published for that kind
/// of figure (see <see cref="QualityGrader"/>).
/// </para>
/// <para>
/// The mean absolute difference was chosen over RFC 3550's filter because the
/// filter weights the last sixteen samples, which at one check every five
/// seconds is the last minute and a half; the mean over the period chosen is
/// the honest figure for that period. The filtered value is kept beside it.
/// Unanswered checks count as loss and are skipped for jitter.
/// </para>
/// </remarks>
public static class SteadinessMath
{
    /// <summary>The figures for one target over a stretch of checks.</summary>
    public static Steadiness Of(IEnumerable<LatencyPoint> points, CheckTarget target)
    {
        ArgumentNullException.ThrowIfNull(points);

        int sent = 0;
        List<double> answers = [];

        foreach (LatencyPoint point in points)
        {
            (bool tried, double? ms) = target == CheckTarget.Router
                ? (point.RouterTried, point.RouterMs)
                : (point.InternetTried, point.InternetMs);

            if (!tried || point.Offline)
            {
                continue;
            }

            sent++;

            if (ms is { } value && double.IsFinite(value))
            {
                answers.Add(value);
            }
        }

        return new Steadiness(
            target,
            sent,
            answers.Count,
            LatencyScale.Median(answers.Select(each => (double?)each)),
            answers.Count == 0 ? null : answers.Max(),
            answers.Count >= Steadiness.FewestForJitter ? MeanAbsoluteDifference(answers) : null,
            answers.Count >= Steadiness.FewestForJitter ? Smoothed(answers) : null);
    }

    /// <summary>The mean of |x[i] - x[i-1]|, or nothing with fewer than two values.</summary>
    public static double? MeanAbsoluteDifference(IReadOnlyList<double> values)
    {
        ArgumentNullException.ThrowIfNull(values);

        if (values.Count < 2)
        {
            return null;
        }

        double sum = 0;

        for (int each = 1; each < values.Count; each++)
        {
            sum += Math.Abs(values[each] - values[each - 1]);
        }

        return sum / (values.Count - 1);
    }

    /// <summary>
    /// RFC 3550's running estimate J = J + (|D| - J) / 16 over consecutive
    /// differences, or nothing with fewer than two values.
    /// </summary>
    public static double? Smoothed(IReadOnlyList<double> values)
    {
        ArgumentNullException.ThrowIfNull(values);

        if (values.Count < 2)
        {
            return null;
        }

        double jitter = 0;

        for (int each = 1; each < values.Count; each++)
        {
            jitter += (Math.Abs(values[each] - values[each - 1]) - jitter) / 16;
        }

        return jitter;
    }
}
