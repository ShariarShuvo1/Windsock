using System.ComponentModel;
using System.Text;
using System.Text.Json;
using ModelContextProtocol;
using ModelContextProtocol.Server;
using Windsock.Core.Feed;

namespace Windsock.Mcp;

/// <summary>
/// The tools an assistant may call. Both read Windsock's local feed and return
/// its JSON as it stands; neither changes anything, and neither reaches beyond
/// this PC.
/// </summary>
/// <remarks>
/// What comes back is data, not instructions: network names are what somebody
/// called their Wi-Fi, and a model reading them should treat them as such. The
/// descriptions here are fixed text, so nothing from the network can change
/// what a tool says it does.
/// </remarks>
[McpServerToolType]
public sealed class WindsockTools(FeedClient feed)
{
    private readonly FeedClient _feed = feed ?? throw new ArgumentNullException(nameof(feed));

    [McpServerTool(Name = "get_status", ReadOnly = true, Idempotent = true, OpenWorld = false, Destructive = false)]
    [Description(
        "What Windsock, the network meter on this PC, shows now, as JSON: download and upload rates in bytes per second; "
        + "whether the internet is online, with the router's and internet's response times in milliseconds; bytes used today "
        + "and this month; the data plan's allowance and what is left, if one is set; processor, memory, graphics and disk load "
        + "in percent; and the whole household's traffic from the router, if that is switched on. A null value is not known.")]
    public Task<string> GetStatusAsync(CancellationToken cancellationToken) => AskAsync("/v1/status", cancellationToken);

    [McpServerTool(Name = "get_usage", ReadOnly = true, Idempotent = true, OpenWorld = false, Destructive = false)]
    [Description(
        "How many bytes this PC downloaded and uploaded over a period, from Windsock's history, as JSON with a total and one entry "
        + "per minute, hour or day. Give one of: period, month, day, or from and until. Only time Windsock was recording counts.")]
    public Task<string> GetUsageAsync(
        [Description("A named period: today, yesterday, week, last-week, month, last-month or year.")] string? period = null,
        [Description("A month, written 2026-09.")] string? month = null,
        [Description("A day, written 2026-09-14.")] string? day = null,
        [Description("The first day of a period, written 2026-09-01; give until too.")] string? from = null,
        [Description("The last day of a period, included, written 2026-09-30.")] string? until = null,
        [Description("Break the period up by minute, hour or day. Minutes cover at most two days, hours at most 93.")] string? by = null,
        CancellationToken cancellationToken = default)
    {
        bool nothingGiven = (month ?? day ?? from ?? until ?? by) is null;
        StringBuilder query = new("/v1/usage?");
        Add(query, "period", period ?? (nothingGiven ? "today" : null));
        Add(query, "month", month);
        Add(query, "day", day);
        Add(query, "from", from);
        Add(query, "until", until);
        Add(query, "by", by);
        return AskAsync(query.ToString().TrimEnd('&', '?'), cancellationToken);
    }

    private async Task<string> AskAsync(string pathAndQuery, CancellationToken cancellationToken)
    {
        FeedReply reply = await _feed.GetAsync(pathAndQuery, cancellationToken).ConfigureAwait(false);

        if (reply.Reach == FeedReach.Answered && reply.Body is { } body)
        {
            return body;
        }

        // A 400 carries the feed's own words for what was wrong with the period.
        throw new McpException(reply.Status == 400 && ErrorIn(reply.Body) is { } refused ? refused : reply.Problem);
    }

    private static string? ErrorIn(string? body)
    {
        if (body is null)
        {
            return null;
        }

        try
        {
            using JsonDocument document = JsonDocument.Parse(body);
            return document.RootElement.TryGetProperty("error", out JsonElement error) ? error.GetString() : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static void Add(StringBuilder query, string name, string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return;
        }

        query.Append(name).Append('=').Append(Uri.EscapeDataString(value.Trim())).Append('&');
    }
}
