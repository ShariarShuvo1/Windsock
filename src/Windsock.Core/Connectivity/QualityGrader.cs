using System.Globalization;

namespace Windsock.Core.Connectivity;

/// <summary>A plain verdict.</summary>
public enum Verdict
{
    /// <summary>Not enough measured to say.</summary>
    Unknown,

    Good,

    Fair,

    Poor,
}

/// <summary>What the connection is being judged for.</summary>
public enum QualityUse
{
    /// <summary>Voice and video calls.</summary>
    Calls,

    /// <summary>Online games.</summary>
    Games,
}

/// <summary>One of the figures a verdict is drawn from.</summary>
public enum QualityMeasure
{
    /// <summary>The median round trip, in milliseconds.</summary>
    ResponseTime,

    /// <summary>Variation in round trip, in milliseconds.</summary>
    Jitter,

    /// <summary>Unanswered checks, as a share from 0 to 1.</summary>
    Loss,

    /// <summary>How much the round trip grows while the line is busy, in milliseconds, from the latest speed test.</summary>
    LatencyUnderLoad,
}

/// <summary>One figure's own verdict.</summary>
/// <param name="Measure">Which figure.</param>
/// <param name="Value">Its value: milliseconds, or a share for loss.</param>
/// <param name="Verdict">Its verdict.</param>
/// <param name="FairFrom">Where fair starts.</param>
/// <param name="PoorFrom">Where poor starts.</param>
public sealed record MeasureGrade(QualityMeasure Measure, double Value, Verdict Verdict, double FairFrom, double PoorFrom)
{
    /// <summary>How far past the good range it is, for picking the figure that cost the most.</summary>
    internal double Overshoot => Value / FairFrom;

    /// <summary>The value as a person reads it: "42 ms", "3.5%".</summary>
    public string Describe() => Measure == QualityMeasure.Loss
        ? string.Create(CultureInfo.InvariantCulture, $"{Value * 100:0.#}% loss")
        : string.Create(CultureInfo.InvariantCulture, $"{Value:0} ms {Words(Measure)}");

    private static string Words(QualityMeasure measure) => measure switch
    {
        QualityMeasure.ResponseTime => "response time",
        QualityMeasure.Jitter => "jitter",
        _ => "more under load",
    };
}

/// <summary>A verdict for calls or games, and the figure that cost it.</summary>
/// <param name="Use">Calls or games.</param>
/// <param name="Verdict">The overall verdict: the worst of the figures.</param>
/// <param name="Cost">The figure that set it, when it is not good.</param>
/// <param name="HomeNetworkOnly">
/// Whether only the router was checked, so the verdict speaks for the home
/// network alone and says nothing about the provider or beyond.
/// </param>
/// <param name="Measures">Every figure that could be graded.</param>
public sealed record QualityGrade(QualityUse Use, Verdict Verdict, MeasureGrade? Cost, bool HomeNetworkOnly, IReadOnlyList<MeasureGrade> Measures)
{
    /// <summary>"Good for calls", "Fair for games: 38 ms jitter", "Poor for calls (home network only): 12% loss".</summary>
    public string Describe()
    {
        if (Verdict == Verdict.Unknown)
        {
            return "Not enough checks yet";
        }

        string use = Use == QualityUse.Calls ? "calls" : "games";
        string scope = HomeNetworkOnly ? " (home network only)" : string.Empty;
        string cost = Cost is { } worst && Verdict != Verdict.Good ? string.Concat(": ", worst.Describe()) : string.Empty;

        return string.Concat(Verdict.ToString(), " for ", use, scope, cost);
    }
}

/// <summary>Grades a connection for calls and for games from its figures.</summary>
/// <remarks>
/// <para>
/// Each figure is graded on its own and the worst decides, so the verdict can
/// always name the number behind it. The lines between good, fair and poor
/// come from published sources (read 2026-10-05, see the network notes):
/// </para>
/// <list type="bullet">
/// <item>Cloudflare's AIM scoring (cloudflare/speedtest, MIT): latency scores
/// points below 50 ms and goes negative from 100 ms; jitter scores below 20 ms
/// and goes negative from 100 ms; loss scores fully below 1% and nothing from
/// 5%; loaded latency increase goes negative from 100 ms.</item>
/// <item>ITU-T G.114 and Cisco's voice guidance: one-way delay within 150 ms -
/// about 300 ms round trip - for real-time speech; jitter within 30 ms; loss
/// within 1%.</item>
/// <item>Microsoft Teams' call quality dashboard: a stream is poor above 500 ms
/// round trip, 10% loss or 30 ms jitter.</item>
/// <item>Waveform's bufferbloat grades (secondary sources): A below 30 ms of
/// added latency.</item>
/// </list>
/// <para>
/// Calls: response time fair from 100 ms (AIM), poor from 300 ms (G.114);
/// jitter fair from 20 ms (AIM), poor from 30 ms (Cisco, Teams); loss fair
/// from 1% (Cisco), poor from 10% (Teams); latency under load fair from 30 ms
/// (Waveform's A), poor from 100 ms (AIM).
/// Games: response time fair from 50 ms, poor from 100 ms (AIM); jitter fair
/// from 20 ms, poor from 100 ms (AIM); loss fair from 1%, poor from 5% (AIM);
/// latency under load as for calls.
/// </para>
/// </remarks>
public static class QualityGrader
{
    /// <summary>Where fair and poor begin for a figure and a use.</summary>
    public static (double FairFrom, double PoorFrom) Lines(QualityUse use, QualityMeasure measure) => (use, measure) switch
    {
        (QualityUse.Calls, QualityMeasure.ResponseTime) => (100, 300),
        (QualityUse.Calls, QualityMeasure.Jitter) => (20, 30),
        (QualityUse.Calls, QualityMeasure.Loss) => (0.01, 0.10),
        (QualityUse.Games, QualityMeasure.ResponseTime) => (50, 100),
        (QualityUse.Games, QualityMeasure.Jitter) => (20, 100),
        (QualityUse.Games, QualityMeasure.Loss) => (0.01, 0.05),
        (_, QualityMeasure.LatencyUnderLoad) => (30, 100),
        _ => (double.PositiveInfinity, double.PositiveInfinity),
    };

    /// <summary>Grades one figure.</summary>
    public static MeasureGrade GradeOne(QualityUse use, QualityMeasure measure, double value)
    {
        (double fair, double poor) = Lines(use, measure);
        Verdict verdict = value >= poor ? Verdict.Poor : value >= fair ? Verdict.Fair : Verdict.Good;
        return new MeasureGrade(measure, value, verdict, fair, poor);
    }

    /// <summary>
    /// The verdict for a use from the steadiness figures and, when known, the
    /// latency added under load. Unknown before ten answered checks, and until
    /// response time, jitter and loss are all measured unless what is measured
    /// is already poor.
    /// </summary>
    /// <param name="use">Calls or games.</param>
    /// <param name="steadiness">From the internet target when one is checked, otherwise from the router.</param>
    /// <param name="loadedIncreaseMs">The latest speed test's added latency, when there is a recent one.</param>
    public static QualityGrade Grade(QualityUse use, Steadiness steadiness, double? loadedIncreaseMs = null)
    {
        ArgumentNullException.ThrowIfNull(steadiness);

        List<MeasureGrade> measures = [];

        if (steadiness.MedianMs is { } median)
        {
            measures.Add(GradeOne(use, QualityMeasure.ResponseTime, median));
        }

        if (steadiness.JitterMs is { } jitter)
        {
            measures.Add(GradeOne(use, QualityMeasure.Jitter, jitter));
        }

        if (steadiness.Loss is { } loss)
        {
            measures.Add(GradeOne(use, QualityMeasure.Loss, loss));
        }

        if (loadedIncreaseMs is { } loaded && double.IsFinite(loaded))
        {
            measures.Add(GradeOne(use, QualityMeasure.LatencyUnderLoad, Math.Max(0, loaded)));
        }

        bool home = steadiness.Target == CheckTarget.Router;

        // A verdict of any kind waits for a few answered checks: one slow ping,
        // or a speed test's figure with no checks beside it, says nothing yet.
        if (measures.Count == 0 || steadiness.Answered < Steadiness.FewestForJitter)
        {
            return new QualityGrade(use, Verdict.Unknown, null, home, measures);
        }

        MeasureGrade worst = measures
            .OrderByDescending(each => each.Verdict)
            .ThenByDescending(each => each.Overshoot)
            .First();

        // A figure not measured yet can only make the verdict worse, so poor
        // stands on part of them; good or fair needs response time, jitter
        // and loss all measured. Latency under load waits for a speed test.
        bool whole = steadiness.MedianMs is not null && steadiness.JitterMs is not null && steadiness.Loss is not null;

        if (!whole && worst.Verdict != Verdict.Poor)
        {
            return new QualityGrade(use, Verdict.Unknown, null, home, measures);
        }

        return new QualityGrade(use, worst.Verdict, worst.Verdict == Verdict.Good ? null : worst, home, measures);
    }
}
