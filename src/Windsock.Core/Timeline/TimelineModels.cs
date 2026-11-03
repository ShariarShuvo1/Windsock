namespace Windsock.Core.Timeline;

/// <summary>One app's traffic with one far end over a stretch, ready to be kept.</summary>
/// <param name="AttributionId">The app, named as Windows' data usage names it.</param>
/// <param name="AppName">What to call the app, for search.</param>
/// <param name="Address">The far end's address; null for far ends folded together past the trace's cap.</param>
/// <param name="Port">The far end's port.</param>
/// <param name="Host">The name the address was reached by, when known.</param>
/// <param name="Country">The address's country, two letters, when known.</param>
/// <param name="Organisation">Who runs the address's network, when known.</param>
/// <param name="IsLocal">Whether the far end is on a local network.</param>
/// <param name="First">When the first packet of the stretch moved.</param>
/// <param name="Last">When the last one did.</param>
/// <param name="Received">Bytes received.</param>
/// <param name="Sent">Bytes sent.</param>
/// <param name="Packets">Packets either way.</param>
public sealed record TimelineFlow(
    string AttributionId,
    string? AppName,
    string? Address,
    int Port,
    string? Host,
    string? Country,
    string? Organisation,
    bool IsLocal,
    DateTimeOffset First,
    DateTimeOffset Last,
    long Received,
    long Sent,
    long Packets);

/// <summary>One kept row of the timeline: an app's conversation with one far end.</summary>
/// <param name="Id">The row.</param>
/// <param name="AttributionId">The app, named as Windows' data usage names it.</param>
/// <param name="AppName">What the app was called when kept.</param>
/// <param name="Address">The far end's address; null for "other places".</param>
/// <param name="Port">The far end's port.</param>
/// <param name="Host">The name it was reached by, when known.</param>
/// <param name="Country">Its country, two letters, when known.</param>
/// <param name="Organisation">Who runs its network, when known.</param>
/// <param name="IsLocal">Whether it is on a local network.</param>
/// <param name="First">When the conversation began, as far as the trace saw.</param>
/// <param name="Last">When it last moved anything.</param>
/// <param name="Received">Bytes received.</param>
/// <param name="Sent">Bytes sent.</param>
public sealed record TimelineEntry(
    long Id,
    string AttributionId,
    string? AppName,
    string? Address,
    int Port,
    string? Host,
    string? Country,
    string? Organisation,
    bool IsLocal,
    DateTimeOffset First,
    DateTimeOffset Last,
    long Received,
    long Sent)
{
    /// <summary>Both directions.</summary>
    public long Total => Received + Sent;
}

/// <summary>What to find in the timeline.</summary>
/// <param name="From">The start of the period; rows that moved anything in it are found.</param>
/// <param name="Until">The end of the period, exclusive.</param>
/// <param name="Text">Words to find in the app, its name, the address, the host, the country or the organisation; null for everything.</param>
/// <param name="AttributionId">Only this app; null for every app.</param>
/// <param name="Offset">Rows to skip, for paging.</param>
/// <param name="Limit">Most rows to return.</param>
public sealed record TimelineQuery(
    DateTimeOffset From,
    DateTimeOffset Until,
    string? Text = null,
    string? AttributionId = null,
    int Offset = 0,
    int Limit = 100);

/// <summary>A day's traffic with one place.</summary>
/// <param name="Day">The local day.</param>
/// <param name="Country">The country, two letters, or empty when not known.</param>
/// <param name="Site">The site (a host folded to its domain), the organisation, or the address.</param>
/// <param name="Received">Bytes received from it that day.</param>
/// <param name="Sent">Bytes sent to it.</param>
/// <param name="Conversations">How many app-and-far-end conversations it took part in.</param>
public sealed record PlaceDay(DateOnly Day, string Country, string Site, long Received, long Sent, long Conversations)
{
    /// <summary>Both directions.</summary>
    public long Total => Received + Sent;
}

/// <summary>One service host's bytes on one day, to be added to what is kept.</summary>
public readonly record struct ServiceDayBytes(DateOnly Day, string ServiceKey, long Received, long Sent);
