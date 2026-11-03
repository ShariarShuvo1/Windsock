using System.Globalization;
using System.Text.Json;
using Windsock.Core.Apps;
using Windsock.Core.Networking;

namespace Windsock.Core.History;

/// <summary>How much a JSON export held.</summary>
public readonly record struct HistoryExportCounts(int Usage, int Apps, int Machine, int Connection, int Notes, int Networks)
{
    /// <summary>Whether it held anything at all.</summary>
    public bool IsEmpty => Usage == 0 && Apps == 0 && Machine == 0 && Connection == 0 && Notes == 0;
}

/// <summary>
/// Writes what History holds for a stretch as one JSON document: network use
/// and the network it was on, each app, the machine, the connection, and the notes.
/// </summary>
/// <remarks>
/// <para>
/// The shape is stable and versioned (<see cref="FormatVersion"/>). Every member
/// is always written, null where a value is not known; a later version only
/// adds members, and raises the version when it changes what one means.
/// </para>
/// <code>
/// {
///   "format": "windsock-history", "version": 1,
///   "exportedAt": "2026-10-05T04:00:00Z",          // UTC
///   "timeZone": "Bangladesh Standard Time",        // the zone local times are in
///   "from": "2026-10-04T00:00:00+06:00", "until": "2026-10-05T00:00:00+06:00",
///   "granularity": "minute|hour|day",               // of usage, machine and connection
///   "appGranularity": "hour|day",                   // of apps: minutes are given by the hour
///   "networks": [ { "id": "…", "name": "Home", "kind": "wifi|ethernet|cellular|other", "metered": false|null } ],
///   "usage": [ { "start": "…", "minutes": 60, "bytesDown": 0, "bytesUp": 0,
///                "peakDownBytesPerSecond": 0, "peakUpBytesPerSecond": 0,
///                "localBytesDown": 0, "localBytesUp": 0, "localMinutes": 0,   // stayed on the LAN, where known
///                "networks": [ { "network": "…id…"|null, "bytesDown": 0, "bytesUp": 0, "minutes": 0 } ] } ],
///   "apps": [ { "start": "…", "app": "…Windows' id…", "name": "…"|null,
///               "measuredDown": 0, "measuredUp": 0, "windowsDown": 0, "windowsUp": 0 } ],
///   "machine": [ { "start": "…", "minutes": 0, "processorPercent": 0.0, "processorPeakPercent": 0.0,
///                  "processorCelsius": null, "processorPeakCelsius": null, "graphicsPercent": 0.0,
///                  "graphicsPeakPercent": 0.0, "graphicsPeakCelsius": null, "memoryBytes": 0,
///                  "memoryPeakBytes": 0, "memoryTotalBytes": 0, "diskReadBytes": 0, "diskWriteBytes": 0 } ],
///   "connection": [ { "start": "…", "minutes": 0, "checks": 0, "offlineChecks": 0, "routerMs": null,
///                     "routerPeakMs": null, "routerLoss": null, "internetMs": null, "internetPeakMs": null,
///                     "internetLoss": null } ],
///   "notes": [ { "from": "…", "until": "…", "text": "…", "created": "…" } ]
/// }
/// </code>
/// <para>
/// Network use is only what Windsock recorded: what Windows counted while it
/// was closed - History's faint fill - is never exported, as with CSV. Each
/// app's two figures are kept apart for the same reason: "measured" is what
/// Windsock measured, "windows" what Windows counted for time Windsock did not
/// measure, never the same bytes twice. An app's record counts in the bucket
/// it starts in when it lies wholly inside the stretch, as in History.
/// </para>
/// </remarks>
public sealed class HistoryJson
{
    /// <summary>What the document says it is.</summary>
    public const string Format = "windsock-history";

    /// <summary>The version of the shape.</summary>
    public const int FormatVersion = 1;

    private const string Stamp = "yyyy-MM-ddTHH:mm:sszzz";

    private readonly IUsageHistoryStore _recorded;
    private readonly IUsageAggregates _aggregates;
    private readonly IMachineHistoryStore _machine;
    private readonly IHistoryNotes _notes;
    private readonly AppHistory _apps;
    private readonly Func<string, string?>? _nameOf;

    public HistoryJson(
        IUsageHistoryStore recorded,
        IUsageAggregates aggregates,
        IMachineHistoryStore machine,
        IHistoryNotes notes,
        AppHistory apps,
        AppNames names)
        : this(recorded, aggregates, machine, notes, apps, NameFrom(names))
    {
    }

    internal HistoryJson(
        IUsageHistoryStore recorded,
        IUsageAggregates aggregates,
        IMachineHistoryStore machine,
        IHistoryNotes notes,
        AppHistory apps,
        Func<string, string?>? nameOf)
    {
        ArgumentNullException.ThrowIfNull(recorded);
        ArgumentNullException.ThrowIfNull(aggregates);
        ArgumentNullException.ThrowIfNull(machine);
        ArgumentNullException.ThrowIfNull(notes);
        ArgumentNullException.ThrowIfNull(apps);

        _recorded = recorded;
        _aggregates = aggregates;
        _machine = machine;
        _notes = notes;
        _apps = apps;
        _nameOf = nameOf;
    }

    private static Func<string, string?> NameFrom(AppNames names)
    {
        ArgumentNullException.ThrowIfNull(names);
        return id => names.Describe(id).Name;
    }

    /// <summary>
    /// Writes a stretch to a stream. Everything is read before the first byte is
    /// written, so history that cannot be read leaves nothing half written.
    /// </summary>
    public HistoryExportCounts Write(Stream stream, DateTimeOffset from, DateTimeOffset until, UsageGranularity granularity, DateTimeOffset exportedAt)
    {
        ArgumentNullException.ThrowIfNull(stream);

        if (until <= from)
        {
            throw new ArgumentException("A stretch must end after it starts.", nameof(until));
        }

        Gathered data = Gather(from, until, granularity);

        using Utf8JsonWriter json = new(stream, new JsonWriterOptions { Indented = true });
        json.WriteStartObject();
        json.WriteString("format", Format);
        json.WriteNumber("version", FormatVersion);
        json.WriteString("exportedAt", exportedAt.ToUniversalTime().ToString("yyyy-MM-ddTHH:mm:ss'Z'", CultureInfo.InvariantCulture));
        json.WriteString("timeZone", TimeZoneInfo.Local.Id);
        json.WriteString("from", Local(from));
        json.WriteString("until", Local(until));
        json.WriteString("granularity", granularity.ToString().ToLowerInvariant());
        json.WriteString("appGranularity", data.AppStep.ToString().ToLowerInvariant());

        json.WriteStartArray("networks");

        foreach (NetworkTag network in data.Networks.Values.OrderBy(each => each.Key, StringComparer.Ordinal))
        {
            json.WriteStartObject();
            json.WriteString("id", network.Key);
            json.WriteString("name", network.Name);
            json.WriteString("kind", NetworkTag.KindName(network.Kind));
            WriteBool(json, "metered", network.Metered);
            json.WriteEndObject();
        }

        json.WriteEndArray();
        WriteUsage(json, data);
        WriteApps(json, data);
        WriteMachine(json, data.Machine);
        WriteConnection(json, data.Connection);

        json.WriteStartArray("notes");

        foreach (HistoryNote note in data.Notes)
        {
            json.WriteStartObject();
            json.WriteString("from", Local(note.From));
            json.WriteString("until", Local(note.Until));
            json.WriteString("text", note.Text);
            json.WriteString("created", Local(note.Created));
            json.WriteEndObject();
        }

        json.WriteEndArray();
        json.WriteEndObject();
        json.Flush();

        return new HistoryExportCounts(
            data.Usage.Count,
            data.Apps.Sum(bucket => bucket.Shares.Count),
            data.Machine.Count,
            data.Connection.Count,
            data.Notes.Count,
            data.Networks.Count);
    }

    private sealed record Gathered(
        IReadOnlyList<UsageBucket> Usage,
        Dictionary<long, Dictionary<string, (long Down, long Up, int Minutes)>> UsageNetworks,
        Dictionary<string, NetworkTag> Networks,
        UsageGranularity AppStep,
        List<(DateTimeOffset Start, IReadOnlyList<AppShare> Shares)> Apps,
        IReadOnlyList<SystemBucket> Machine,
        IReadOnlyList<ConnectionBucket> Connection,
        IReadOnlyList<HistoryNote> Notes);

    private Gathered Gather(DateTimeOffset from, DateTimeOffset until, UsageGranularity granularity)
    {
        IReadOnlyList<UsageBucket> usage = _recorded.Read(from, until, granularity);
        long fromMinute = EpochMinute.From(from);
        long untilMinute = EpochMinute.From(until);

        // Each bucket's networks, keyed as the store keys the bucket - by the
        // start of its local minute, hour or day - and each network by its key,
        // "" where it was not known. Minutes come one by one; hours and days
        // from the quarter hours inside them.
        Dictionary<long, Dictionary<string, (long, long, int)>> byBucket = [];
        Dictionary<string, NetworkTag> used = new(StringComparer.Ordinal);

        void Add(long minute, NetworkTag? network, long down, long up, int minutes)
        {
            long bucket = HistoryFill.BucketStart(minute, granularity).UtcTicks;

            if (!byBucket.TryGetValue(bucket, out Dictionary<string, (long, long, int)>? networks))
            {
                networks = new(StringComparer.Ordinal);
                byBucket[bucket] = networks;
            }

            string key = network?.Key ?? string.Empty;
            (long d, long u, int m) = networks.GetValueOrDefault(key);
            networks[key] = (d + down, u + up, m + minutes);

            if (network is not null)
            {
                used[network.Key] = network;
            }
        }

        if (granularity == UsageGranularity.Minute)
        {
            foreach (UsageMinute row in _aggregates.ReadMinuteRows(fromMinute, untilMinute))
            {
                Add(row.Minute, row.Network, row.BytesDown, row.BytesUp, 1);
            }
        }
        else
        {
            IReadOnlyDictionary<long, NetworkTag> tags = _aggregates.ReadNetworkTags();

            foreach (UsageQuarter quarter in _aggregates.ReadQuarters(fromMinute, untilMinute))
            {
                Add(quarter.StartMinute, tags.GetValueOrDefault(quarter.NetworkId), quarter.BytesDown, quarter.BytesUp, quarter.Minutes);
            }
        }

        // Apps by the hour at most: Windows keeps older records an hour wide,
        // and a minute of each app is more than anybody reads in an export.
        UsageGranularity appStep = granularity == UsageGranularity.Minute ? UsageGranularity.Hour : granularity;
        List<(DateTimeOffset, IReadOnlyList<AppShare>)> apps = [];
        CalendarStep step = appStep == UsageGranularity.Day ? CalendarStep.Day : CalendarStep.Hour;

        for (DateTimeOffset start = LocalCalendar.BucketStart(EpochMinute.From(from), step, TimeZoneInfo.Local);
             start < until;
             start = LocalCalendar.NextBucket(start, step, TimeZoneInfo.Local))
        {
            DateTimeOffset next = LocalCalendar.NextBucket(start, step, TimeZoneInfo.Local);
            DateTimeOffset bucketFrom = start < from ? from : start;
            DateTimeOffset bucketUntil = next < until ? next : until;
            IReadOnlyList<AppShare> shares = _apps.Breakdown(bucketFrom, bucketUntil, until);

            if (shares.Count > 0)
            {
                apps.Add((start, shares));
            }
        }

        return new Gathered(
            usage,
            byBucket,
            used,
            appStep,
            apps,
            _machine.ReadSystem(from, until, granularity),
            _machine.ReadConnection(from, until, granularity),
            _notes.ReadNotes(from, until));
    }

    private static void WriteUsage(Utf8JsonWriter json, Gathered data)
    {
        json.WriteStartArray("usage");

        foreach (UsageBucket bucket in data.Usage)
        {
            json.WriteStartObject();
            json.WriteString("start", Local(bucket.Start));
            json.WriteNumber("minutes", bucket.Minutes);
            json.WriteNumber("bytesDown", bucket.BytesDown);
            json.WriteNumber("bytesUp", bucket.BytesUp);
            json.WriteNumber("peakDownBytesPerSecond", bucket.PeakDown);
            json.WriteNumber("peakUpBytesPerSecond", bucket.PeakUp);
            json.WriteNumber("localBytesDown", bucket.LocalDown);
            json.WriteNumber("localBytesUp", bucket.LocalUp);
            json.WriteNumber("localMinutes", bucket.LocalMinutes);
            json.WriteStartArray("networks");

            if (data.UsageNetworks.TryGetValue(bucket.Start.UtcTicks, out Dictionary<string, (long Down, long Up, int Minutes)>? networks))
            {
                foreach ((string key, (long down, long up, int minutes)) in networks.OrderBy(each => each.Key, StringComparer.Ordinal))
                {
                    json.WriteStartObject();
                    WriteText(json, "network", key.Length == 0 ? null : key);

                    json.WriteNumber("bytesDown", down);
                    json.WriteNumber("bytesUp", up);
                    json.WriteNumber("minutes", minutes);
                    json.WriteEndObject();
                }
            }

            json.WriteEndArray();
            json.WriteEndObject();
        }

        json.WriteEndArray();
    }

    private void WriteApps(Utf8JsonWriter json, Gathered data)
    {
        json.WriteStartArray("apps");

        foreach ((DateTimeOffset start, IReadOnlyList<AppShare> shares) in data.Apps)
        {
            foreach (AppShare share in shares)
            {
                json.WriteStartObject();
                json.WriteString("start", Local(start));
                json.WriteString("app", share.AttributionId);
                WriteText(json, "name", NameOf(share.AttributionId));
                json.WriteNumber("measuredDown", share.MeasuredDown);
                json.WriteNumber("measuredUp", share.MeasuredUp);
                json.WriteNumber("windowsDown", share.WindowsDown);
                json.WriteNumber("windowsUp", share.WindowsUp);
                json.WriteEndObject();
            }
        }

        json.WriteEndArray();
    }

    private string? NameOf(string id)
    {
        if (_nameOf is null)
        {
            return null;
        }

        try
        {
            return _nameOf(id);
        }
#pragma warning disable CA1031 // A name that cannot be read costs the app its name, not the export.
        catch (Exception)
#pragma warning restore CA1031
        {
            return null;
        }
    }

    private static void WriteMachine(Utf8JsonWriter json, IReadOnlyList<SystemBucket> machine)
    {
        json.WriteStartArray("machine");

        foreach (SystemBucket bucket in machine)
        {
            json.WriteStartObject();
            json.WriteString("start", Local(bucket.Start));
            json.WriteNumber("minutes", bucket.Minutes);
            json.WriteNumber("processorPercent", Math.Round(bucket.ProcessorAverage, 1));
            json.WriteNumber("processorPeakPercent", Math.Round(bucket.ProcessorPeak, 1));
            WriteNumber(json, "processorCelsius", bucket.ProcessorHeatAverage);
            WriteNumber(json, "processorPeakCelsius", bucket.ProcessorHeatPeak);
            json.WriteNumber("graphicsPercent", Math.Round(bucket.GraphicsAverage, 1));
            json.WriteNumber("graphicsPeakPercent", Math.Round(bucket.GraphicsPeak, 1));
            WriteNumber(json, "graphicsPeakCelsius", bucket.GraphicsHeatPeak);
            json.WriteNumber("memoryBytes", bucket.MemoryAverage);
            json.WriteNumber("memoryPeakBytes", bucket.MemoryPeak);
            json.WriteNumber("memoryTotalBytes", bucket.MemoryTotal);
            json.WriteNumber("diskReadBytes", bucket.DiskRead);
            json.WriteNumber("diskWriteBytes", bucket.DiskWrite);
            json.WriteEndObject();
        }

        json.WriteEndArray();
    }

    private static void WriteConnection(Utf8JsonWriter json, IReadOnlyList<ConnectionBucket> connection)
    {
        json.WriteStartArray("connection");

        foreach (ConnectionBucket bucket in connection)
        {
            json.WriteStartObject();
            json.WriteString("start", Local(bucket.Start));
            json.WriteNumber("minutes", bucket.Minutes);
            json.WriteNumber("checks", bucket.Checks);
            json.WriteNumber("offlineChecks", bucket.Offline);
            WriteNumber(json, "routerMs", bucket.RouterAverageMs);
            WriteNumber(json, "routerPeakMs", bucket.RouterPeakMs);
            WriteNumber(json, "routerLoss", bucket.RouterLoss, 4);
            WriteNumber(json, "internetMs", bucket.InternetAverageMs);
            WriteNumber(json, "internetPeakMs", bucket.InternetPeakMs);
            WriteNumber(json, "internetLoss", bucket.InternetLoss, 4);
            json.WriteEndObject();
        }

        json.WriteEndArray();
    }

    private static string Local(DateTimeOffset moment) =>
        moment.ToLocalTime().ToString(Stamp, CultureInfo.InvariantCulture);

    private static void WriteNumber(Utf8JsonWriter json, string name, double? value, int digits = 1)
    {
        if (value is { } known && double.IsFinite(known))
        {
            json.WriteNumber(name, Math.Round(known, digits));
        }
        else
        {
            json.WriteNull(name);
        }
    }

    private static void WriteBool(Utf8JsonWriter json, string name, bool? value)
    {
        if (value is { } known)
        {
            json.WriteBoolean(name, known);
        }
        else
        {
            json.WriteNull(name);
        }
    }

    private static void WriteText(Utf8JsonWriter json, string name, string? value)
    {
        if (value is null)
        {
            json.WriteNull(name);
        }
        else
        {
            json.WriteString(name, value);
        }
    }
}
