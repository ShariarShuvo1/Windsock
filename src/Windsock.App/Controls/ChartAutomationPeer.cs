using System.Globalization;
using System.Windows;
using System.Windows.Automation.Peers;
using Windsock.Core.Connectivity;
using Windsock.Core.Formatting;
using Windsock.Core.History;

namespace Windsock.App.Controls;

/// <summary>
/// What a screen reader is told about a drawn chart: that it is a chart, its
/// name, and a sentence summing up what it shows now.
/// </summary>
/// <remarks>
/// <para>
/// A chart drawn in <c>OnRender</c> has no peer of its own, so UI Automation
/// saw nothing at all. Every chart returns one of these from
/// <c>OnCreateAutomationPeer</c> with a function that sums it up; the summary is
/// read when asked for (help text and item status), never announced on its
/// own, because a chart that changes every second would talk over everything.
/// The name stays the one the page gives the chart with
/// <c>AutomationProperties.Name</c>. A table beside the chart, where there is
/// one, holds every value; the chart offers no child per bar.
/// </para>
/// <para>
/// A chart added later takes one line: <c>protected override AutomationPeer
/// OnCreateAutomationPeer() => new ChartAutomationPeer(this, Summary);</c>
/// with a <c>Summary</c> that says the latest value and the highest, in words.
/// </para>
/// </remarks>
public sealed class ChartAutomationPeer(FrameworkElement owner, Func<string> summary) : FrameworkElementAutomationPeer(owner)
{
    private readonly Func<string> _summary = summary ?? throw new ArgumentNullException(nameof(summary));

    protected override string GetClassNameCore() => Owner.GetType().Name;

    protected override AutomationControlType GetAutomationControlTypeCore() => AutomationControlType.Image;

    protected override string GetLocalizedControlTypeCore() => "chart";

    protected override string GetHelpTextCore()
    {
        string given = base.GetHelpTextCore();
        string now = Summary();

        return string.IsNullOrEmpty(given) ? now : string.Concat(given, " ", now);
    }

    protected override string GetItemStatusCore() => Summary();

    protected override List<AutomationPeer>? GetChildrenCore() => null;

    private string Summary()
    {
        try
        {
            return _summary();
        }
        catch (InvalidOperationException)
        {
            return string.Empty;
        }
    }
}

/// <summary>The sentences each of Windsock's charts sums itself up with.</summary>
internal static class ChartWords
{
    private const string Nothing = "Nothing is drawn yet.";

    /// <summary>The network chart: each direction now, and its peak in view.</summary>
    public static string Rates(IReadOnlyList<double>? down, IReadOnlyList<double>? up, RateFamily family, RateScale scale)
    {
        if (down is not { Count: > 0 } && up is not { Count: > 0 })
        {
            return Nothing;
        }

        return string.Concat(Direction("Download", down, family, scale), " ", Direction("Upload", up, family, scale));
    }

    /// <summary>A share of a part of the machine over time: now, on average and at its highest.</summary>
    public static string Shares(IReadOnlyList<double>? values)
    {
        double[] read = values?.Where(double.IsFinite).ToArray() ?? [];

        if (read.Length == 0)
        {
            return Nothing;
        }

        return string.Create(
            CultureInfo.CurrentCulture,
            $"Now {read[^1]:0} percent, {read.Average():0} on average, {read.Max():0} at the highest.");
    }

    /// <summary>The response-time chart: the latest answers, the slowest, and how many checks were outages.</summary>
    public static string Latency(IReadOnlyList<LatencyPoint>? points)
    {
        if (points is not { Count: > 0 })
        {
            return Nothing;
        }

        LatencyPoint last = points[^1];
        double? slowest = points.Select(point => Math.Max(point.RouterMs ?? 0, point.InternetMs ?? 0)).DefaultIfEmpty().Max();
        int offline = points.Count(point => point.Offline);

        string now = string.Concat(
            "Router ", Milliseconds(last.RouterMs, last.RouterTried),
            last.InternetTried ? string.Concat(", internet ", Milliseconds(last.InternetMs, tried: true)) : string.Empty,
            " at the last check.");

        string most = slowest is > 0
            ? string.Create(CultureInfo.CurrentCulture, $" The slowest answer shown was {slowest:0} milliseconds.")
            : string.Empty;

        string outages = offline > 0
            ? string.Create(CultureInfo.CurrentCulture, $" {offline} checks fell in an outage.")
            : string.Empty;

        return string.Concat(now, most, outages);
    }

    /// <summary>History's bars: what the period moved, and its busiest bar.</summary>
    public static string Usage(IReadOnlyList<UsageBucket>? buckets)
    {
        if (buckets is not { Count: > 0 })
        {
            return Nothing;
        }

        long down = buckets.Sum(bucket => bucket.AllDown);
        long up = buckets.Sum(bucket => bucket.AllUp);
        UsageBucket busiest = buckets.MaxBy(bucket => bucket.AllTotal)!;

        return string.Concat(
            string.Create(CultureInfo.CurrentCulture, $"{buckets.Count} bars. Downloaded "),
            SizeFormatter.Format(down),
            ", uploaded ",
            SizeFormatter.Format(up),
            ". The busiest bar, from ",
            ClockFormat.Day(busiest.Start.LocalDateTime),
            ", moved ",
            SizeFormatter.Format(busiest.AllTotal),
            ".");
    }

    /// <summary>History's machine lanes: the processor over the period, on average and at its peak.</summary>
    public static string Machine(IReadOnlyList<SystemBucket>? buckets)
    {
        if (buckets is not { Count: > 0 })
        {
            return Nothing;
        }

        return string.Create(
            CultureInfo.CurrentCulture,
            $"Processor {buckets.Average(bucket => bucket.ProcessorAverage):0} percent on average, {buckets.Max(bucket => bucket.ProcessorPeak):0} at the peak; graphics {buckets.Average(bucket => bucket.GraphicsAverage):0} percent on average.");
    }

    private static string Direction(string name, IReadOnlyList<double>? series, RateFamily family, RateScale scale)
    {
        double[] read = series?.Where(double.IsFinite).ToArray() ?? [];

        if (read.Length == 0)
        {
            return string.Concat(name, " not drawn.");
        }

        return string.Concat(
            name,
            " now ",
            RateFormatter.Format(read[^1], family, scale),
            ", peak ",
            RateFormatter.Format(read.Max(), family, scale),
            ".");
    }

    private static string Milliseconds(double? value, bool tried) =>
        !tried
            ? "not checked"
            : value is { } ms
                ? string.Create(CultureInfo.CurrentCulture, $"{ms:0} milliseconds")
                : "did not answer";
}
