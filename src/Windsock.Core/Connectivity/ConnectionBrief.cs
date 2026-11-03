using System.Globalization;

namespace Windsock.Core.Connectivity;

/// <summary>
/// The connection in a few words, for the sidebar beside Connection: Online
/// with how fast the last check came back, Offline, or Sign-in needed.
/// </summary>
/// <remarks>
/// Empty while nothing is settled - just started, just woke, or not watched -
/// so the sidebar shows nothing rather than a word nobody measured.
/// </remarks>
public static class ConnectionBrief
{
    /// <summary>What the sidebar says about the connection.</summary>
    public static string Of(ConnectionState state, ConnectionSample? latest)
    {
        if (latest is { Facts.Reach: WindowsReach.SignInRequired })
        {
            return "Sign-in needed";
        }

        return state.Status switch
        {
            ConnectionStatus.Offline => "Offline",
            ConnectionStatus.Online => Time(latest) is { } answered
                ? string.Concat("Online · ", answered)
                : "Online",
            _ => string.Empty,
        };
    }

    /// <summary>
    /// How long the last check took to come back: the internet address where
    /// one is pinged, since that is the answer that says the internet is
    /// there, else the router.
    /// </summary>
    private static string? Time(ConnectionSample? latest)
    {
        TimeSpan? taken = latest switch
        {
            { InternetChecked: true, Internet: { } internet } => internet,
            { Router: { } router } => router,
            _ => null,
        };

        return taken is not { } round
            ? null
            : round.TotalMilliseconds < 1
                ? "<1 ms"
                : string.Create(CultureInfo.CurrentCulture, $"{Math.Round(round.TotalMilliseconds):0} ms");
    }
}
