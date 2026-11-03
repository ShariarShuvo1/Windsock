using System.Globalization;
using System.Xml;
using System.Xml.Linq;

namespace Windsock.Core.Wifi;

/// <summary>What happened to a Wi-Fi connection, as WLAN AutoConfig logged it.</summary>
public enum WifiEventKind
{
    /// <summary>Event 8001: connected.</summary>
    Connected,

    /// <summary>Event 8002: a connection attempt failed.</summary>
    ConnectFailed,

    /// <summary>Event 8003: disconnected.</summary>
    Disconnected,
}

/// <summary>One connect, failure or disconnect from the WLAN AutoConfig log.</summary>
/// <param name="At">When Windows logged it.</param>
/// <param name="Kind">What happened.</param>
/// <param name="Interface">The adapter's GUID, when given.</param>
/// <param name="Network">The network's name (the SSID, else the profile), when given.</param>
/// <param name="Reason">Windows' own words for why, when it gave them.</param>
/// <param name="ReasonCode">Windows' reason number, which is the log's own and not a WLAN_REASON_CODE.</param>
/// <param name="Rssi">The signal at a failure, in dBm, when given.</param>
/// <param name="Phy">The 802.11 type at a connect, such as "802.11ax", when given.</param>
public sealed record WifiEvent(
    DateTimeOffset At,
    WifiEventKind Kind,
    Guid? Interface,
    string? Network,
    string? Reason,
    long? ReasonCode,
    int? Rssi,
    string? Phy)
{
    /// <summary>Event 8001.</summary>
    public const int ConnectedId = 8001;

    /// <summary>Event 8002.</summary>
    public const int FailedId = 8002;

    /// <summary>Event 8003.</summary>
    public const int DisconnectedId = 8003;

    /// <summary>
    /// Whether a disconnect looks deliberate rather than a drop: moving to
    /// another network (reason 3, "the user wants to establish a new
    /// connection") or the adapter being switched off, put to sleep or put in
    /// flight mode (reason 11, "an Operation state change request on this
    /// interface"). Both texts were measured on the development machine; other
    /// reasons count as drops.
    /// </summary>
    public bool SeemsDeliberate => Kind == WifiEventKind.Disconnected && ReasonCode is 3 or 11;

    /// <summary>The same event with the network's name taken out.</summary>
    public WifiEvent WithoutName() => this with { Network = null };
}

/// <summary>A Wi-Fi drop: a disconnect and, when it came, the reconnect after it.</summary>
/// <param name="At">When the connection went.</param>
/// <param name="Reason">Windows' words for why.</param>
/// <param name="ReasonCode">Windows' reason number.</param>
/// <param name="Network">The network it was on, when names are shown.</param>
/// <param name="BackAt">When Wi-Fi connected again, when it has.</param>
/// <param name="Deliberate">Whether it looks like something somebody did rather than a fault.</param>
public sealed record WifiDrop(DateTimeOffset At, string? Reason, long? ReasonCode, string? Network, DateTimeOffset? BackAt, bool Deliberate)
{
    /// <summary>How long Wi-Fi was gone, when it came back.</summary>
    public TimeSpan? Duration => BackAt is { } back ? back - At : null;
}

/// <summary>Reads WLAN AutoConfig events and pairs them into drops.</summary>
/// <remarks>
/// The Microsoft-Windows-WLAN-AutoConfig/Operational log is readable by any
/// interactive user (its channel grants INTERACTIVE read), which was checked
/// unelevated on 2026-10-05. Fields are read by their names in EventData, not
/// by position. The log is about a megabyte and circular: on a quiet PC it
/// reaches back weeks, on a busy one days.
/// </remarks>
public static class WifiEvents
{
    /// <summary>The log's name.</summary>
    public const string LogName = "Microsoft-Windows-WLAN-AutoConfig/Operational";

    /// <summary>An XPath query for every connect, failure and disconnect, for watching new ones.</summary>
    public const string WatchQuery = "*[System[(EventID=8001 or EventID=8002 or EventID=8003)]]";

    /// <summary>An XPath query for the connects, failures and disconnects in a window.</summary>
    public static string Query(TimeSpan lookBack) => string.Create(
        CultureInfo.InvariantCulture,
        $"*[System[(EventID=8001 or EventID=8002 or EventID=8003) and TimeCreated[timediff(@SystemTime) <= {(long)Math.Max(0, lookBack.TotalMilliseconds)}]]]");

    /// <summary>Reads one event's XML, or nothing when it is not one of the three.</summary>
    public static WifiEvent? Parse(string? xml)
    {
        if (string.IsNullOrWhiteSpace(xml))
        {
            return null;
        }

        XElement root;

        try
        {
            root = XElement.Parse(xml);
        }
        catch (XmlException)
        {
            return null;
        }

        XElement? system = Child(root, "System");

        if (!int.TryParse(Child(system, "EventID")?.Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out int id))
        {
            return null;
        }

        WifiEventKind? kind = id switch
        {
            WifiEvent.ConnectedId => WifiEventKind.Connected,
            WifiEvent.FailedId => WifiEventKind.ConnectFailed,
            WifiEvent.DisconnectedId => WifiEventKind.Disconnected,
            _ => null,
        };

        string? stamp = Child(system, "TimeCreated")?.Attribute("SystemTime")?.Value;

        if (kind is null
            || !DateTimeOffset.TryParse(stamp, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out DateTimeOffset at))
        {
            return null;
        }

        Dictionary<string, string> data = new(StringComparer.OrdinalIgnoreCase);

        foreach (XElement item in Child(root, "EventData")?.Elements() ?? [])
        {
            if (item.Name.LocalName == "Data" && item.Attribute("Name")?.Value is { Length: > 0 } name)
            {
                data[name] = item.Value.Trim();
            }
        }

        string? network = Text(data, "SSID") ?? Text(data, "ProfileName");
        string? reason = Text(data, kind == WifiEventKind.ConnectFailed ? "FailureReason" : "Reason");

        return new WifiEvent(
            at.ToLocalTime(),
            kind.Value,
            Guid.TryParse(Text(data, "InterfaceGuid"), out Guid adapter) ? adapter : null,
            network,
            reason,
            Number(Text(data, "ReasonCode")),
            Number(Text(data, "RSSI")) is { } rssi && rssi is < 0 and > -128 ? (int)rssi : null,
            Text(data, "PHYType"));
    }

    /// <summary>
    /// Pairs disconnects with the connect that followed each on the same
    /// adapter, newest first.
    /// </summary>
    /// <param name="events">Events in any order.</param>
    /// <param name="withNames">Whether to keep network names.</param>
    public static IReadOnlyList<WifiDrop> Drops(IEnumerable<WifiEvent> events, bool withNames)
    {
        ArgumentNullException.ThrowIfNull(events);

        WifiEvent[] ordered = [.. events.OrderBy(each => each.At)];
        List<WifiDrop> drops = [];

        for (int each = 0; each < ordered.Length; each++)
        {
            WifiEvent gone = ordered[each];

            if (gone.Kind != WifiEventKind.Disconnected)
            {
                continue;
            }

            DateTimeOffset? back = null;

            for (int later = each + 1; later < ordered.Length; later++)
            {
                WifiEvent next = ordered[later];

                if (next.Kind == WifiEventKind.Connected && (gone.Interface is null || next.Interface is null || next.Interface == gone.Interface))
                {
                    back = next.At;
                    break;
                }
            }

            drops.Add(new WifiDrop(gone.At, gone.Reason, gone.ReasonCode, withNames ? gone.Network : null, back, gone.SeemsDeliberate));
        }

        drops.Reverse();
        return drops;
    }

    private static XElement? Child(XElement? parent, string name) =>
        parent?.Elements().FirstOrDefault(each => each.Name.LocalName == name);

    private static string? Text(Dictionary<string, string> data, string name) =>
        data.TryGetValue(name, out string? value) && value.Length > 0 ? value : null;

    private static long? Number(string? text)
    {
        if (text is null)
        {
            return null;
        }

        if (text.StartsWith("0x", StringComparison.OrdinalIgnoreCase)
            && long.TryParse(text.AsSpan(2), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out long hex))
        {
            return hex;
        }

        return long.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out long plain) ? plain : null;
    }
}
