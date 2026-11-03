using System.Net;
using System.Security.Cryptography;
using System.Text;

namespace Windsock.Core.Feed;

/// <summary>What the feed makes of a request before it looks at what was asked for.</summary>
public enum FeedVerdict
{
    /// <summary>Answer it.</summary>
    Allowed,

    /// <summary>Not from this PC. Refused (403).</summary>
    NotLocal,

    /// <summary>Anything but GET or HEAD. Refused (405): the feed only reads.</summary>
    WrongMethod,

    /// <summary>
    /// A Host header that is not the feed's own address: a web page that had a
    /// name of its own pointed at 127.0.0.1 (DNS rebinding). Refused (421).
    /// </summary>
    WrongHost,

    /// <summary>Sent by a web page from another site. Refused (403).</summary>
    ForeignPage,

    /// <summary>No token, or the wrong one. Refused (401).</summary>
    NoToken,
}

/// <summary>
/// The checks every request to the local feed passes, in order, before it is
/// answered. Pure, so each can be tested against the attack it stops.
/// </summary>
/// <remarks>
/// <para>
/// Any web page open in a browser on this PC can send requests to
/// 127.0.0.1. It cannot read the answers - no CORS header is ever sent, so the
/// browser withholds them - unless it gets itself treated as the same site, by
/// pointing a name of its own at 127.0.0.1 (DNS rebinding). Windows' HTTP
/// server does not stop that: measured, a request to the feed's address with
/// <c>Host: evil.example</c> reached the handler. So the Host header must name
/// the feed itself, a browser's <c>Origin</c> must be the feed's own when it
/// is sent at all, and a request the browser marks as from another site
/// (<c>Sec-Fetch-Site</c>) is refused unless it is a page being opened.
/// </para>
/// <para>
/// The token is what keeps everything else on the PC out - other Windows
/// accounts included, which can reach loopback too. It is compared in constant
/// time. A query token is taken only when allowed, and only because a
/// browser's EventSource and an OBS browser source cannot send a header.
/// </para>
/// </remarks>
public static class FeedGuard
{
    /// <summary>The name of the query parameter a token may be given in.</summary>
    public const string TokenParameter = "token";

    /// <summary>Checks a request.</summary>
    /// <param name="remote">Where the request came from.</param>
    /// <param name="method">Its method.</param>
    /// <param name="host">Its Host header.</param>
    /// <param name="origin">Its Origin header, when a browser sent one.</param>
    /// <param name="fetchSite">Its Sec-Fetch-Site header, when a browser sent one.</param>
    /// <param name="fetchMode">Its Sec-Fetch-Mode header, when a browser sent one.</param>
    /// <param name="authorization">Its Authorization header.</param>
    /// <param name="queryToken">The token in its address, if any.</param>
    /// <param name="port">The port the feed listens on.</param>
    /// <param name="token">The feed's token.</param>
    /// <param name="tokenInAddress">Whether a token in the address is accepted.</param>
    public static FeedVerdict Check(
        IPAddress? remote,
        string method,
        string? host,
        string? origin,
        string? fetchSite,
        string? fetchMode,
        string? authorization,
        string? queryToken,
        int port,
        string token,
        bool tokenInAddress)
    {
        ArgumentNullException.ThrowIfNull(method);
        ArgumentException.ThrowIfNullOrEmpty(token);

        if (remote is null || !IPAddress.IsLoopback(remote))
        {
            return FeedVerdict.NotLocal;
        }

        if (!string.Equals(method, "GET", StringComparison.Ordinal) && !string.Equals(method, "HEAD", StringComparison.Ordinal))
        {
            return FeedVerdict.WrongMethod;
        }

        if (!IsOwnHost(host, port))
        {
            return FeedVerdict.WrongHost;
        }

        if (origin is not null && !IsOwnOrigin(origin, port))
        {
            return FeedVerdict.ForeignPage;
        }

        if (fetchSite is not null
            && !string.Equals(fetchSite, "same-origin", StringComparison.OrdinalIgnoreCase)
            && !string.Equals(fetchSite, "none", StringComparison.OrdinalIgnoreCase)
            && !string.Equals(fetchMode, "navigate", StringComparison.OrdinalIgnoreCase))
        {
            return FeedVerdict.ForeignPage;
        }

        string? offered = Bearer(authorization) ?? (tokenInAddress ? queryToken : null);

        return offered is not null && Matches(offered, token) ? FeedVerdict.Allowed : FeedVerdict.NoToken;
    }

    /// <summary>Whether a Host header names the feed: 127.0.0.1, localhost or [::1], with its port.</summary>
    public static bool IsOwnHost(string? host, int port)
    {
        if (string.IsNullOrEmpty(host))
        {
            return false;
        }

        string suffix = ":" + port.ToString(System.Globalization.CultureInfo.InvariantCulture);

        return host.EndsWith(suffix, StringComparison.Ordinal)
            && host[..^suffix.Length].ToLowerInvariant() is "127.0.0.1" or "localhost" or "[::1]";
    }

    /// <summary>Whether an Origin header is the feed's own.</summary>
    public static bool IsOwnOrigin(string origin, int port)
    {
        ArgumentNullException.ThrowIfNull(origin);

        const string Scheme = "http://";
        return origin.StartsWith(Scheme, StringComparison.OrdinalIgnoreCase) && IsOwnHost(origin[Scheme.Length..], port);
    }

    /// <summary>The token in an Authorization header of the Bearer kind, or nothing.</summary>
    public static string? Bearer(string? authorization)
    {
        const string Scheme = "Bearer ";

        return authorization is not null && authorization.StartsWith(Scheme, StringComparison.OrdinalIgnoreCase)
            ? authorization[Scheme.Length..].Trim()
            : null;
    }

    /// <summary>Compares a token offered with the real one in time that does not depend on where they differ.</summary>
    public static bool Matches(string offered, string token)
    {
        ArgumentNullException.ThrowIfNull(offered);
        ArgumentNullException.ThrowIfNull(token);

        byte[] left = SHA256.HashData(Encoding.UTF8.GetBytes(offered));
        byte[] right = SHA256.HashData(Encoding.UTF8.GetBytes(token));
        return CryptographicOperations.FixedTimeEquals(left, right);
    }

    /// <summary>A new token: 32 random bytes, URL-safe base64 without padding (43 characters).</summary>
    public static string NewToken()
    {
        Span<byte> bytes = stackalloc byte[32];
        RandomNumberGenerator.Fill(bytes);
        return Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
    }

    /// <summary>The HTTP status for a refusal.</summary>
    public static int StatusOf(FeedVerdict verdict) => verdict switch
    {
        FeedVerdict.Allowed => 200,
        FeedVerdict.WrongMethod => 405,
        FeedVerdict.WrongHost => 421,
        FeedVerdict.NoToken => 401,
        _ => 403,
    };
}
