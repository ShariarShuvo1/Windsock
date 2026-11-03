using System.Globalization;
using System.Text.Encodings.Web;
using System.Text.Json;

namespace Windsock.Core.Feed;

/// <summary>Which part of the snapshot a request asked for.</summary>
public enum FeedPart
{
    All,

    Rates,

    Connection,

    Usage,

    Plan,

    System,

    Household,
}

/// <summary>
/// Writes the feed's JSON. The shape is versioned (<see cref="Version"/>): a
/// later version only adds members, and every member is always written, null
/// where a value is not known.
/// </summary>
/// <remarks>
/// <code>
/// {
///   "feed": 1, "app": "Windsock", "version": "0.2.0", "at": "2026-10-05T12:00:00+06:00",
///   "rates":      { "at": "…"|null, "downBytesPerSecond": 0.0|null, "upBytesPerSecond": 0.0|null },
///   "connection": { "status": "online|offline|checking", "since": "…"|null, "cause": "…"|null,
///                   "network": "…"|null, "routerMs": 0.0|null, "internetMs": 0.0|null, "vpn": false|null },
///   "usage":      { "asOf": "…", "today": { "bytesDown": 0, "bytesUp": 0, "bytesTotal": 0 },
///                   "month": { … } } | null,
///   "plan":       { "name": "…", "limitBytes": 0|null, "usedBytes": 0, "leftBytes": 0|null,
///                   "usedFraction": 0.0|null, "cycleStart": "…"|null, "cycleEnd": "…"|null } | null,
///   "system":     { "at": "…"|null, "processorPercent": 0.0|null, "memoryPercent": 0.0|null,
///                   "graphicsPercent": 0.0|null, "diskPercent": 0.0|null,
///                   "processorCelsius": 0.0|null, "graphicsCelsius": 0.0|null },
///   "household":  { "at": "…"|null, "downBytesPerSecond": 0.0|null, "upBytesPerSecond": 0.0|null,
///                   "router": "…"|null } | null
/// }
/// </code>
/// A request for one part gets <c>feed</c>, <c>app</c>, <c>version</c>, <c>at</c>
/// and that member alone. Times are local, with their offset.
/// </remarks>
public static class FeedJson
{
    /// <summary>The version of the shape.</summary>
    public const int Version = 1;

    private const string Stamp = "yyyy-MM-ddTHH:mm:sszzz";

    /// <summary>
    /// How the feed writes JSON: compact, and without escaping "+" and the like,
    /// which the default encoder does for the sake of HTML the feed never writes
    /// into. Control characters and quotes are still escaped.
    /// </summary>
    public static JsonWriterOptions Options { get; } = new() { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    /// <summary>The snapshot, or one part of it, as compact UTF-8 JSON.</summary>
    public static byte[] Write(FeedSnapshot snapshot, FeedPart part, string appVersion)
    {
        ArgumentNullException.ThrowIfNull(snapshot);

        using MemoryStream buffer = new();

        using (Utf8JsonWriter json = new(buffer, Options))
        {
            json.WriteStartObject();
            WriteHead(json, snapshot.At, appVersion);

            if (part is FeedPart.All or FeedPart.Rates)
            {
                json.WriteStartObject("rates");
                WriteTime(json, "at", snapshot.Rates.At);
                WriteNumber(json, "downBytesPerSecond", snapshot.Rates.DownBytesPerSecond);
                WriteNumber(json, "upBytesPerSecond", snapshot.Rates.UpBytesPerSecond);
                json.WriteEndObject();
            }

            if (part is FeedPart.All or FeedPart.Connection)
            {
                FeedConnection connection = snapshot.Connection;
                json.WriteStartObject("connection");
                json.WriteString("status", connection.Status);
                WriteTime(json, "since", connection.Since);
                WriteText(json, "cause", connection.Cause);
                WriteText(json, "network", connection.Network);
                WriteNumber(json, "routerMs", connection.RouterMs);
                WriteNumber(json, "internetMs", connection.InternetMs);
                WriteBool(json, "vpn", connection.Vpn);
                json.WriteEndObject();
            }

            if (part is FeedPart.All or FeedPart.Usage)
            {
                if (snapshot.Usage is { } usage)
                {
                    json.WriteStartObject("usage");
                    WriteTime(json, "asOf", usage.AsOf);
                    WriteTotals(json, "today", usage.Today);
                    WriteTotals(json, "month", usage.Month);
                    json.WriteEndObject();
                }
                else
                {
                    json.WriteNull("usage");
                }
            }

            if (part is FeedPart.All or FeedPart.Plan)
            {
                if (snapshot.Plan is { } plan)
                {
                    json.WriteStartObject("plan");
                    json.WriteString("name", plan.Name);
                    WriteWhole(json, "limitBytes", plan.LimitBytes);
                    json.WriteNumber("usedBytes", plan.UsedBytes);
                    WriteWhole(json, "leftBytes", plan.LeftBytes);
                    WriteNumber(json, "usedFraction", plan.UsedFraction, 4);
                    WriteTime(json, "cycleStart", plan.CycleStart);
                    WriteTime(json, "cycleEnd", plan.CycleEnd);
                    json.WriteEndObject();
                }
                else
                {
                    json.WriteNull("plan");
                }
            }

            if (part is FeedPart.All or FeedPart.System)
            {
                FeedSystem system = snapshot.System;
                json.WriteStartObject("system");
                WriteTime(json, "at", system.At);
                WriteNumber(json, "processorPercent", system.ProcessorPercent);
                WriteNumber(json, "memoryPercent", system.MemoryPercent);
                WriteNumber(json, "graphicsPercent", system.GraphicsPercent);
                WriteNumber(json, "diskPercent", system.DiskPercent);
                WriteNumber(json, "processorCelsius", system.ProcessorCelsius);
                WriteNumber(json, "graphicsCelsius", system.GraphicsCelsius);
                json.WriteEndObject();
            }

            if (part is FeedPart.All or FeedPart.Household)
            {
                if (snapshot.Household is { } household)
                {
                    json.WriteStartObject("household");
                    WriteTime(json, "at", household.At);
                    WriteNumber(json, "downBytesPerSecond", household.DownBytesPerSecond);
                    WriteNumber(json, "upBytesPerSecond", household.UpBytesPerSecond);
                    WriteText(json, "router", household.Router);
                    json.WriteEndObject();
                }
                else
                {
                    json.WriteNull("household");
                }
            }

            json.WriteEndObject();
        }

        return buffer.ToArray();
    }

    /// <summary>Recorded use over a period.</summary>
    /// <remarks>
    /// <code>
    /// { "feed": 1, "app": "Windsock", "version": "…", "at": "…",
    ///   "from": "…", "until": "…", "granularity": "minute|hour|day",
    ///   "total": { "bytesDown": 0, "bytesUp": 0, "bytesTotal": 0 },
    ///   "buckets": [ { "start": "…", "minutes": 0, "bytesDown": 0, "bytesUp": 0 } ] }
    /// </code>
    /// </remarks>
    public static byte[] WriteUsage(FeedUsageRange range, DateTimeOffset at, string appVersion)
    {
        ArgumentNullException.ThrowIfNull(range);

        using MemoryStream buffer = new();

        using (Utf8JsonWriter json = new(buffer, Options))
        {
            json.WriteStartObject();
            WriteHead(json, at, appVersion);
            WriteTime(json, "from", range.From);
            WriteTime(json, "until", range.Until);
            json.WriteString("granularity", range.Granularity.ToString().ToLowerInvariant());
            WriteTotals(json, "total", range.Total);
            json.WriteStartArray("buckets");

            foreach (FeedBucket bucket in range.Buckets)
            {
                json.WriteStartObject();
                WriteTime(json, "start", bucket.Start);
                json.WriteNumber("minutes", bucket.Minutes);
                json.WriteNumber("bytesDown", bucket.BytesDown);
                json.WriteNumber("bytesUp", bucket.BytesUp);
                json.WriteEndObject();
            }

            json.WriteEndArray();
            json.WriteEndObject();
        }

        return buffer.ToArray();
    }

    /// <summary>A small JSON body for a refusal or a mistake: <c>{"error": "…"}</c>.</summary>
    public static byte[] WriteError(string message)
    {
        using MemoryStream buffer = new();

        using (Utf8JsonWriter json = new(buffer, Options))
        {
            json.WriteStartObject();
            json.WriteString("error", message);
            json.WriteEndObject();
        }

        return buffer.ToArray();
    }

    /// <summary>What the feed offers, for a client finding its way.</summary>
    public static byte[] WriteIndex(DateTimeOffset at, string appVersion)
    {
        using MemoryStream buffer = new();

        using (Utf8JsonWriter json = new(buffer, Options))
        {
            json.WriteStartObject();
            WriteHead(json, at, appVersion);
            json.WriteStartArray("endpoints");

            foreach (string path in FeedRoutes.Documented)
            {
                json.WriteStringValue(path);
            }

            json.WriteEndArray();
            json.WriteEndObject();
        }

        return buffer.ToArray();
    }

    /// <summary>A time as the feed writes it: local, with its offset.</summary>
    public static string Time(DateTimeOffset moment) => moment.ToLocalTime().ToString(Stamp, CultureInfo.InvariantCulture);

    private static void WriteHead(Utf8JsonWriter json, DateTimeOffset at, string appVersion)
    {
        json.WriteNumber("feed", Version);
        json.WriteString("app", "Windsock");
        json.WriteString("version", appVersion);
        json.WriteString("at", Time(at));
    }

    private static void WriteTotals(Utf8JsonWriter json, string name, FeedTotals totals)
    {
        json.WriteStartObject(name);
        json.WriteNumber("bytesDown", totals.BytesDown);
        json.WriteNumber("bytesUp", totals.BytesUp);
        json.WriteNumber("bytesTotal", totals.BytesTotal);
        json.WriteEndObject();
    }

    private static void WriteTime(Utf8JsonWriter json, string name, DateTimeOffset? moment)
    {
        if (moment is { } known && known != DateTimeOffset.MinValue)
        {
            json.WriteString(name, Time(known));
        }
        else
        {
            json.WriteNull(name);
        }
    }

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

    private static void WriteWhole(Utf8JsonWriter json, string name, long? value)
    {
        if (value is { } known)
        {
            json.WriteNumber(name, known);
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
