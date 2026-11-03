namespace Windsock.Core.Feed;

/// <summary>What a request to the feed asks for.</summary>
public enum FeedRoute
{
    NotFound,

    /// <summary><c>/</c> and <c>/v1</c>: what the feed offers.</summary>
    Index,

    /// <summary><c>/v1/status</c>: everything at once.</summary>
    Status,

    /// <summary><c>/v1/rates</c>.</summary>
    Rates,

    /// <summary><c>/v1/connection</c>.</summary>
    Connection,

    /// <summary><c>/v1/usage</c>: today and this month, or a period asked for in the query.</summary>
    Usage,

    /// <summary><c>/v1/plan</c>.</summary>
    Plan,

    /// <summary><c>/v1/system</c>.</summary>
    System,

    /// <summary><c>/v1/household</c>: the router's counters.</summary>
    Household,

    /// <summary><c>/v1/events</c>: the status every second as Server-Sent Events.</summary>
    Events,

    /// <summary><c>/overlay</c>: a transparent page for a streaming program's browser source.</summary>
    Overlay,
}

/// <summary>The feed's addresses.</summary>
public static class FeedRoutes
{
    /// <summary>Every address a client may ask for, as the index lists them.</summary>
    public static IReadOnlyList<string> Documented { get; } =
    [
        "/v1/status",
        "/v1/rates",
        "/v1/connection",
        "/v1/usage",
        "/v1/plan",
        "/v1/system",
        "/v1/household",
        "/v1/events",
        "/overlay",
    ];

    /// <summary>
    /// Every path the feed answers, as the URL prefixes it registers with
    /// Windows. Each is registered on its own, not merely "/", because HTTP.sys
    /// routes a request to the longest registered prefix: with only "/", another
    /// program of the same user could register "/v1/" and receive /v1/status with
    /// the caller's token (found in review). Owning each path's own prefix leaves
    /// nothing longer that matches a path a client asks for, and a prefix
    /// somebody else holds first makes the feed fail to start, never share. A
    /// prefix "/a/b/" also takes "/a/b".
    /// </summary>
    public static IReadOnlyList<string> Prefixes { get; } =
    [
        "/",
        "/v1/",
        "/v1/status/",
        "/v1/rates/",
        "/v1/connection/",
        "/v1/usage/",
        "/v1/plan/",
        "/v1/system/",
        "/v1/household/",
        "/v1/events/",
        "/overlay/",
    ];

    /// <summary>What a path asks for. Case does not matter, and a trailing slash is allowed.</summary>
    public static FeedRoute Find(string? path)
    {
        string trimmed = (path ?? "/").TrimEnd('/').ToLowerInvariant();

        return trimmed switch
        {
            "" or "/v1" => FeedRoute.Index,
            "/v1/status" => FeedRoute.Status,
            "/v1/rates" => FeedRoute.Rates,
            "/v1/connection" => FeedRoute.Connection,
            "/v1/usage" => FeedRoute.Usage,
            "/v1/plan" => FeedRoute.Plan,
            "/v1/system" => FeedRoute.System,
            "/v1/household" => FeedRoute.Household,
            "/v1/events" => FeedRoute.Events,
            "/overlay" => FeedRoute.Overlay,
            _ => FeedRoute.NotFound,
        };
    }

    /// <summary>The snapshot part a route asks for, or nothing for one that is not a part.</summary>
    public static FeedPart? PartOf(FeedRoute route) => route switch
    {
        FeedRoute.Status => FeedPart.All,
        FeedRoute.Rates => FeedPart.Rates,
        FeedRoute.Connection => FeedPart.Connection,
        FeedRoute.Usage => FeedPart.Usage,
        FeedRoute.Plan => FeedPart.Plan,
        FeedRoute.System => FeedPart.System,
        FeedRoute.Household => FeedPart.Household,
        _ => null,
    };
}
