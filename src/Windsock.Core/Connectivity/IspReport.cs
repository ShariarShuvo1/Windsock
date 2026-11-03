using System.Globalization;
using System.Net;
using System.Text;
using Windsock.Core.Formatting;
using Windsock.Core.History;
using Windsock.Core.SpeedTests;

namespace Windsock.Core.Connectivity;

/// <summary>Everything an ISP report shows, gathered for one period.</summary>
/// <param name="From">The period's start.</param>
/// <param name="Until">The period's end.</param>
/// <param name="Written">When the report was made.</param>
/// <param name="Network">The network it is about, when the person included its name.</param>
/// <param name="Provider">The provider, from the public address lookup, when one was made.</param>
/// <param name="InternetTarget">The address checks pinged beyond the router, or nothing when only the router was.</param>
/// <param name="Outages">Outages in the period.</param>
/// <param name="Days">The connection checks, a day at a time, from history.</param>
/// <param name="Internet">The internet checks' figures over the period.</param>
/// <param name="Router">The router checks' figures over the period.</param>
/// <param name="Tests">Speed tests in the period.</param>
/// <param name="PlanDownloadMbps">The plan's download, or zero.</param>
/// <param name="PlanUploadMbps">The plan's upload, or zero.</param>
/// <param name="Plan">The comparison with the plan, when there were tests and a plan.</param>
/// <param name="Gaps">Parts that could not be read, in a sentence each.</param>
public sealed record IspReportData(
    DateTimeOffset From,
    DateTimeOffset Until,
    DateTimeOffset Written,
    string? Network,
    string? Provider,
    string? InternetTarget,
    IReadOnlyList<Outage> Outages,
    IReadOnlyList<ConnectionBucket> Days,
    QualitySummary Internet,
    QualitySummary Router,
    IReadOnlyList<SpeedTestResult> Tests,
    double PlanDownloadMbps,
    double PlanUploadMbps,
    PlanSpeedSummary? Plan,
    IReadOnlyList<string> Gaps)
{
    /// <summary>How long the internet was gone in the period, counting only the part inside it.</summary>
    public TimeSpan Downtime => Sum(Outages);

    /// <summary>How much of that was put down to the provider.</summary>
    public TimeSpan ProviderDowntime => Sum(Outages.Where(each => each.Cause == OutageCause.Provider));

    private TimeSpan Sum(IEnumerable<Outage> outages) => outages.Aggregate(TimeSpan.Zero, (total, each) =>
    {
        DateTimeOffset start = each.Start < From ? From : each.Start;
        DateTimeOffset end = each.End is { } finished && finished < Until ? finished : Until;
        return end > start ? total + (end - start) : total;
    });
}

/// <summary>
/// A dated report of a connection's outages, response time, jitter, loss and
/// speed tests over a period, as one self-contained HTML page - no scripts, no
/// outside files - ready to attach to a complaint.
/// </summary>
public static class IspReport
{
    /// <summary>Reads what the report needs. Never throws for a store that cannot be read; the report says what is missing.</summary>
    public static IspReportData Gather(
        DateTimeOffset from,
        DateTimeOffset until,
        DateTimeOffset now,
        IOutageStore outages,
        IQualityStore quality,
        ISpeedTestStore tests,
        IMachineHistoryStore? machine,
        IUsageHistoryStore? usage,
        Settings.WindsockSettings settings,
        string? network,
        string? provider)
    {
        ArgumentNullException.ThrowIfNull(outages);
        ArgumentNullException.ThrowIfNull(quality);
        ArgumentNullException.ThrowIfNull(tests);
        ArgumentNullException.ThrowIfNull(settings);

        List<string> gaps = [];

        IReadOnlyList<Outage> outageList = Read(() => outages.Read(from, until), "The outage log could not be read.", gaps) ?? [];
        IReadOnlyList<QualityQuarter> quarters = Read(() => quality.ReadQuality(QualityQuarter.QuarterOf(from), until), "Jitter could not be read.", gaps) ?? [];
        IReadOnlyList<SpeedTestResult> testList = Read(() => tests.ReadSpeedTests(from, until), "The speed tests could not be read.", gaps) ?? [];
        IReadOnlyList<ConnectionBucket> days = machine is null
            ? []
            : Read(() => machine.ReadConnection(from, until, UsageGranularity.Day), "The daily connection checks could not be read.", gaps) ?? [];
        IReadOnlyList<UsageBucket> hourly = usage is null
            ? []
            : Read(() => usage.Read(from, until, UsageGranularity.Hour), "Usage history could not be read.", gaps) ?? [];

        Settings.ConnectionToolsSettings tools = settings.ConnectionTools;
        PlanSpeedSummary? plan = tools.PlanDownloadMbps > 0 || tools.PlanUploadMbps > 0
            ? PlanSpeed.Compare(tools.PlanDownloadMbps, tools.PlanUploadMbps, testList, hourly)
            : null;

        return new IspReportData(
            from,
            until,
            now,
            network,
            provider,
            settings.Connection.InternetTarget,
            outageList,
            days,
            QualitySummary.Of(quarters.Where(each => each.Target == CheckTarget.Internet)),
            QualitySummary.Of(quarters.Where(each => each.Target == CheckTarget.Router)),
            testList,
            tools.PlanDownloadMbps,
            tools.PlanUploadMbps,
            plan,
            gaps);
    }

    /// <summary>Writes the page.</summary>
    /// <param name="data">What the report shows.</param>
    /// <param name="clock">The clock times are written in; Windows' own when not given.</param>
    public static string Render(IspReportData data, ClockPatterns? clock = null)
    {
        ArgumentNullException.ThrowIfNull(data);

        // English words, as the rest of the page, with the reader's own clock.
        CultureInfo culture = CultureInfo.InvariantCulture;
        ClockPatterns times = clock ?? ClockFormat.Current;
        string longDay = string.Concat("d MMMM yyyy ", times.ShortTime);
        string dayToMinute = string.Concat("yyyy-MM-dd ", times.ShortTime);
        string dayToSecond = string.Concat("yyyy-MM-dd ", times.LongTime);
        StringBuilder html = new();
        string period = string.Concat(data.From.ToString(longDay, culture), " to ", data.Until.ToString(longDay, culture));
        TimeSpan length = data.Until - data.From;
        double availability = length > TimeSpan.Zero ? 1 - (data.Downtime / length) : 1;

        html.Append("<!doctype html>\n<html lang=\"en\">\n<head>\n<meta charset=\"utf-8\">\n")
            .Append("<meta name=\"viewport\" content=\"width=device-width, initial-scale=1\">\n")
            .Append("<title>Connection report, ").Append(E(period)).Append("</title>\n<style>\n")
            .Append(Style)
            .Append("</style>\n</head>\n<body>\n<main>\n");

        html.Append("<h1>Connection report</h1>\n<p class=\"lead\">").Append(E(period)).Append("</p>\n<dl class=\"facts\">\n");
        Fact(html, "Written", data.Written.ToString(string.Concat(longDay, " zzz"), culture));

        if (data.Network is { Length: > 0 } network)
        {
            Fact(html, "Network", network);
        }

        if (data.Provider is { Length: > 0 } provider)
        {
            Fact(html, "Provider", provider);
        }

        Fact(html, "Checked against", data.InternetTarget is { } target
            ? string.Concat("the router, and ", target, " beyond it")
            : "the router only (no internet address was chosen, so response times are for the home network)");
        html.Append("</dl>\n");

        html.Append("<section>\n<h2>Summary</h2>\n<div class=\"plates\">\n");
        Plate(html, "Outages", data.Outages.Count.ToString(culture));
        Plate(html, "Time without internet", OutageWords.Brief(data.Downtime));
        Plate(html, "Put down to the provider", OutageWords.Brief(data.ProviderDowntime));
        Plate(html, "Available", string.Create(culture, $"{availability * 100:0.00}%"));

        QualitySummary main = data.InternetTarget is not null ? data.Internet : data.Router;
        Plate(html, "Mean response time", Ms(main.MeanMs));
        Plate(html, "Jitter", Ms(main.JitterMs));
        Plate(html, "Checks lost", main.Loss is { } loss ? string.Create(culture, $"{loss * 100:0.0}%") : "—");

        SpeedTestResult[] good = [.. data.Tests.Where(each => each.Succeeded)];
        Plate(html, "Speed tests", good.Length.ToString(culture));

        if (data.Plan is { } plan)
        {
            Plate(html, "Download, share of plan", plan.DownloadShare is { } share ? string.Create(culture, $"{share * 100:0}%") : "—");
            Plate(html, "Upload, share of plan", plan.UploadShare is { } upShare ? string.Create(culture, $"{upShare * 100:0}%") : "—");
        }

        html.Append("</div>\n</section>\n");

        html.Append("<section>\n<h2>Outages</h2>\n");

        if (data.Outages.Count == 0)
        {
            html.Append("<p>No outages were recorded in this period.</p>\n");
        }
        else
        {
            html.Append("<table>\n<thead><tr><th>Began</th><th>Ended</th><th>Lasted</th><th>Likely cause</th></tr></thead>\n<tbody>\n");

            foreach (Outage outage in data.Outages.OrderBy(each => each.Start))
            {
                html.Append("<tr><td>").Append(E(outage.Start.ToString(dayToSecond, culture)))
                    .Append("</td><td>").Append(E(outage.End?.ToString(dayToSecond, culture) ?? "still going"))
                    .Append("</td><td>").Append(E(outage.Duration is { } lasted ? OutageWords.Brief(lasted) : "—"))
                    .Append(outage.Interrupted ? " (end not seen)" : string.Empty)
                    .Append("</td><td>").Append(E(OutageWords.Blame(outage.Cause)))
                    .Append("</td></tr>\n");
            }

            html.Append("</tbody>\n</table>\n");
        }

        html.Append("</section>\n");

        if (data.Days.Count > 0)
        {
            html.Append("<section>\n<h2>Day by day</h2>\n<table>\n<thead><tr><th>Day</th><th>Checks</th><th>Found offline</th>")
                .Append("<th>Router, mean</th><th>Router lost</th><th>Internet, mean</th><th>Internet, slowest</th><th>Internet lost</th></tr></thead>\n<tbody>\n");

            foreach (ConnectionBucket day in data.Days.Where(each => each.Checks > 0))
            {
                html.Append("<tr><td>").Append(E(day.Start.ToString("ddd d MMM", culture)))
                    .Append("</td><td>").Append(day.Checks.ToString(culture))
                    .Append("</td><td>").Append(day.Offline.ToString(culture))
                    .Append("</td><td>").Append(E(Ms(day.RouterAverageMs)))
                    .Append("</td><td>").Append(E(Share(day.RouterLoss)))
                    .Append("</td><td>").Append(E(Ms(day.InternetAverageMs)))
                    .Append("</td><td>").Append(E(Ms(day.InternetPeakMs)))
                    .Append("</td><td>").Append(E(Share(day.InternetLoss)))
                    .Append("</td></tr>\n");
            }

            html.Append("</tbody>\n</table>\n</section>\n");
        }

        html.Append("<section>\n<h2>Speed tests</h2>\n");

        if (data.Tests.Count == 0)
        {
            html.Append("<p>No speed tests were run in this period.</p>\n");
        }
        else
        {
            html.Append("<table>\n<thead><tr><th>When</th><th>Server</th><th>Download</th><th>Upload</th><th>Quiet response</th><th>Under load</th><th>Grade</th></tr></thead>\n<tbody>\n");

            foreach (SpeedTestResult test in data.Tests.OrderBy(each => each.At))
            {
                html.Append("<tr><td>").Append(E(test.At.ToString(dayToMinute, culture)))
                    .Append(test.Scheduled ? " (automatic)" : string.Empty)
                    .Append("</td><td>").Append(E(test.Server))
                    .Append("</td><td>").Append(E(SpeedGrades.Mbps(test.DownloadBitsPerSecond)))
                    .Append("</td><td>").Append(E(SpeedGrades.Mbps(test.UploadBitsPerSecond)))
                    .Append("</td><td>").Append(E(Ms(test.IdleMs)))
                    .Append("</td><td>").Append(E(test.LoadedIncreaseMs is { } more ? string.Create(culture, $"+{more:0} ms") : "—"))
                    .Append("</td><td>").Append(E(SpeedGrades.Letter(test.Bloat)))
                    .Append(test.Problem is { } problem ? string.Concat(" <span class=\"note\">", E(problem), "</span>") : string.Empty)
                    .Append("</td></tr>\n");
            }

            html.Append("</tbody>\n</table>\n</section>\n");
        }

        if (data.Plan is { Tests: > 0 } compared)
        {
            html.Append("<section>\n<h2>Against the plan, by hour of day</h2>\n<p>The plan promises ")
                .Append(E(string.Create(culture, $"{data.PlanDownloadMbps:0.#} Mbit/s down and {data.PlanUploadMbps:0.#} Mbit/s up")))
                .Append(". Bars show the median speed test in each hour as a share of the plan's download.</p>\n<div class=\"hours\">\n");

            foreach (HourOfDay hour in compared.Hours)
            {
                double share = Math.Clamp(hour.DownloadShare ?? 0, 0, 1.2);
                html.Append("<div class=\"hour\"><span class=\"label\">").Append(hour.Hour.ToString("00", culture))
                    .Append("</span><span class=\"track\"><span class=\"bar\" style=\"width:")
                    .Append((share / 1.2 * 100).ToString("0.#", culture)).Append("%\"></span></span><span class=\"value\">")
                    .Append(E(hour.DownloadShare is { } value ? string.Create(culture, $"{value * 100:0}% ({hour.Tests})") : "—"))
                    .Append("</span></div>\n");
            }

            html.Append("</div>\n</section>\n");
        }

        html.Append("<section>\n<h2>How this was measured</h2>\n<ul>\n")
            .Append("<li>Outages: Windsock checked the connection every few seconds, asking Windows whether it reached the internet and pinging the router")
            .Append(data.InternetTarget is not null ? " and the internet address above" : string.Empty)
            .Append(". An outage is counted after the internet has been unreachable for a set time, from the first failed check. A cause is the likeliest one: a router that answers while nothing beyond it does points at the provider.</li>\n")
            .Append("<li>Response time and loss are from those same checks. Jitter is the mean difference between consecutive response times.</li>\n")
            .Append("<li>Speed tests measure what arrived at this PC, through everything between it and the test server, including Wi-Fi when used. Response time under load is how much the round trip grew while the line was full.</li>\n")
            .Append("<li>Times are this PC's local time. Nothing in this report was sent anywhere by Windsock.</li>\n")
            .Append("</ul>\n");

        if (data.Gaps.Count > 0)
        {
            html.Append("<p class=\"note\">");

            foreach (string gap in data.Gaps)
            {
                html.Append(E(gap)).Append(' ');
            }

            html.Append("</p>\n");
        }

        html.Append("</section>\n<footer>Made by Windsock, ").Append(E(Lookups.WindsockAgent.Home)).Append("</footer>\n</main>\n</body>\n</html>\n");
        return html.ToString();
    }

    private const string Style =
        """
        :root { color-scheme: light; --ink: #1b1f24; --soft: #5b6470; --page: #f2f4f7; --band: #ffffff; --plate: #f1f3f6; --accent: #0e7490; }
        * { box-sizing: border-box; }
        body { margin: 0; background: var(--page); color: var(--ink); font: 15px/1.5 "Segoe UI", system-ui, sans-serif; }
        main { max-width: 960px; margin: 0 auto; padding: 32px 16px; }
        h1 { font-size: 28px; margin: 0 0 4px; }
        h2 { font-size: 18px; margin: 0 0 12px; }
        .lead { color: var(--soft); margin: 0 0 16px; }
        section { background: var(--band); border-radius: 8px; padding: 20px; margin: 16px 0; overflow-x: auto; }
        dl.facts { display: grid; grid-template-columns: max-content 1fr; gap: 4px 16px; margin: 0; }
        dl.facts dt { color: var(--soft); }
        dl.facts dd { margin: 0; }
        .plates { display: grid; grid-template-columns: repeat(auto-fill, minmax(150px, 1fr)); gap: 2px; }
        .plate { background: var(--plate); padding: 12px; }
        .plate .value { font-size: 20px; font-weight: 600; display: block; }
        .plate .name { color: var(--soft); font-size: 13px; }
        table { border-collapse: collapse; width: 100%; font-size: 14px; }
        th { text-align: left; color: var(--soft); font-weight: 600; padding: 6px 8px; }
        td { padding: 6px 8px; }
        tbody tr:nth-child(odd) { background: var(--plate); }
        .note { color: var(--soft); font-size: 13px; }
        .hours { display: grid; gap: 2px; }
        .hour { display: grid; grid-template-columns: 32px 1fr 90px; align-items: center; gap: 8px; font-size: 13px; }
        .track { background: var(--plate); height: 12px; border-radius: 3px; overflow: hidden; display: block; }
        .bar { background: var(--accent); height: 100%; display: block; }
        footer { color: var(--soft); font-size: 13px; margin-top: 24px; }
        @media print { body { background: #fff; } section { break-inside: avoid; } }

        """;

    private static T? Read<T>(Func<T> read, string gap, List<string> gaps)
        where T : class
    {
        try
        {
            return read();
        }
        catch (Exception ex) when (HistoryFailure.IsStorage(ex) || ex is InvalidOperationException)
        {
            gaps.Add(gap);
            return null;
        }
    }

    private static void Fact(StringBuilder html, string name, string value) =>
        html.Append("<dt>").Append(E(name)).Append("</dt><dd>").Append(E(value)).Append("</dd>\n");

    private static void Plate(StringBuilder html, string name, string value) =>
        html.Append("<div class=\"plate\"><span class=\"value\">").Append(E(value)).Append("</span><span class=\"name\">").Append(E(name)).Append("</span></div>\n");

    private static string Ms(double? ms) => ms is { } value && double.IsFinite(value)
        ? string.Create(CultureInfo.InvariantCulture, $"{value:0.#} ms")
        : "—";

    private static string Share(double? share) => share is { } value
        ? string.Create(CultureInfo.InvariantCulture, $"{value * 100:0.#}%")
        : "—";

    private static string E(string text) => WebUtility.HtmlEncode(text);
}
