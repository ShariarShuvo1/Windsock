namespace Windsock.Core.Connectivity;

/// <summary>
/// A quarter hour of the connection checks for one target, summed so any
/// stretch's figures are the sums over it: the round trips, and the
/// differences between consecutive answers that make the jitter.
/// </summary>
/// <param name="Start">The quarter's start.</param>
/// <param name="Target">Router or internet.</param>
/// <param name="Sent">Checks sent.</param>
/// <param name="Answered">Checks answered.</param>
/// <param name="SumMs">The answered round trips, summed.</param>
/// <param name="WorstMs">The slowest.</param>
/// <param name="JitterSumMs">The absolute differences between consecutive answers, summed.</param>
/// <param name="JitterPairs">How many differences went into that sum.</param>
public sealed record QualityQuarter(
    DateTimeOffset Start,
    CheckTarget Target,
    int Sent,
    int Answered,
    double SumMs,
    double WorstMs,
    double JitterSumMs,
    int JitterPairs)
{
    /// <summary>A quarter's length.</summary>
    public static readonly TimeSpan Length = TimeSpan.FromMinutes(15);

    /// <summary>The quarter a moment falls in, in UTC so a clock change cannot split one.</summary>
    public static DateTimeOffset QuarterOf(DateTimeOffset moment)
    {
        long ticks = moment.UtcTicks - (moment.UtcTicks % Length.Ticks);
        return new DateTimeOffset(ticks, TimeSpan.Zero);
    }
}

/// <summary>Figures over any stretch of <see cref="QualityQuarter"/>s.</summary>
/// <param name="Sent">Checks sent.</param>
/// <param name="Answered">Checks answered.</param>
/// <param name="MeanMs">The mean round trip.</param>
/// <param name="WorstMs">The slowest.</param>
/// <param name="JitterMs">The mean absolute difference between consecutive answers.</param>
public sealed record QualitySummary(int Sent, int Answered, double? MeanMs, double? WorstMs, double? JitterMs)
{
    /// <summary>Nothing measured.</summary>
    public static QualitySummary Empty { get; } = new(0, 0, null, null, null);

    /// <summary>The share not answered.</summary>
    public double? Loss => Sent > 0 ? (double)(Sent - Answered) / Sent : null;

    /// <summary>Adds up quarters.</summary>
    public static QualitySummary Of(IEnumerable<QualityQuarter> quarters)
    {
        ArgumentNullException.ThrowIfNull(quarters);

        int sent = 0;
        int answered = 0;
        double sum = 0;
        double worst = double.NaN;
        double jitter = 0;
        int pairs = 0;

        foreach (QualityQuarter quarter in quarters)
        {
            sent += quarter.Sent;
            answered += quarter.Answered;
            sum += quarter.SumMs;
            jitter += quarter.JitterSumMs;
            pairs += quarter.JitterPairs;

            if (quarter.Answered > 0 && (double.IsNaN(worst) || quarter.WorstMs > worst))
            {
                worst = quarter.WorstMs;
            }
        }

        return new QualitySummary(
            sent,
            answered,
            answered > 0 ? sum / answered : null,
            double.IsNaN(worst) ? null : worst,
            pairs > 0 ? jitter / pairs : null);
    }
}

/// <summary>Where quarter-hour quality sums are kept, for reports over long periods.</summary>
public interface IQualityStore
{
    /// <summary>Adds sums to their quarter, creating it as needed.</summary>
    void AddQuality(IReadOnlyList<QualityQuarter> quarters);

    /// <summary>The quarters that start within a period, oldest first.</summary>
    IReadOnlyList<QualityQuarter> ReadQuality(DateTimeOffset from, DateTimeOffset until);

    /// <summary>Forgets every quarter.</summary>
    int ClearQuality();
}
