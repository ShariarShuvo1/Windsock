using System.Globalization;
using System.Text;
using Windsock.Core.Networking;

namespace Windsock.Core.History;

/// <summary>The result of reading a history file back in.</summary>
public readonly record struct UsageImport(IReadOnlyList<UsageMinute> Minutes, int Skipped);

/// <summary>
/// Reads and writes history as CSV.
/// </summary>
/// <remarks>
/// <para>
/// The network columns, and the local-network share after them, came with
/// schema 6 and go last. Columns are found by their names, both here and in
/// 0.1.0, so a file with them reads in an older version (which ignores them) and
/// a file without them reads here (each minute then has no network, and its
/// local share is not known). They are filled only for minute rows: an hour or
/// a day can span networks, and its local share may be known for only some of
/// its minutes.
/// </para>
/// <para>
/// A network's name is the Wi-Fi name somebody chose, so a name a spreadsheet
/// would take for a formula is written with an apostrophe in front, which is
/// taken off again when the file is read back.
/// </para>
/// </remarks>
public static class UsageCsv
{
    private const string UtcColumn = "utc";
    private const string LocalColumn = "local";
    private const string GranularityColumn = "granularity";
    private const string DownColumn = "bytes_down";
    private const string UpColumn = "bytes_up";
    private const string TotalColumn = "bytes_total";
    private const string PeakDownColumn = "peak_down_bytes_per_second";
    private const string PeakUpColumn = "peak_up_bytes_per_second";
    private const string MinutesColumn = "minutes";
    private const string NetworkColumn = "network";
    private const string NetworkKindColumn = "network_kind";
    private const string NetworkIdColumn = "network_id";
    private const string LocalDownColumn = "lan_bytes_down";
    private const string LocalUpColumn = "lan_bytes_up";
    private const string UtcPattern = "yyyy-MM-ddTHH:mm:ss'Z'";
    private const string LocalPattern = "yyyy-MM-ddTHH:mm:sszzz";

    /// <summary>The header row, and the order columns are written in.</summary>
    public static string Header { get; } = string.Join(
        ',',
        UtcColumn,
        LocalColumn,
        GranularityColumn,
        DownColumn,
        UpColumn,
        TotalColumn,
        PeakDownColumn,
        PeakUpColumn,
        MinutesColumn,
        NetworkColumn,
        NetworkKindColumn,
        NetworkIdColumn,
        LocalDownColumn,
        LocalUpColumn);

    /// <summary>Writes buckets as CSV, header first.</summary>
    public static void Write(
        TextWriter writer,
        UsageGranularity granularity,
        IEnumerable<UsageBucket> buckets)
    {
        ArgumentNullException.ThrowIfNull(writer);
        ArgumentNullException.ThrowIfNull(buckets);

        string width = granularity.ToString().ToLowerInvariant();
        writer.Write(Header);
        writer.Write('\n');

        StringBuilder line = new(128);

        foreach (UsageBucket bucket in buckets)
        {
            line.Clear();
            line.Append(bucket.Start.ToUniversalTime().ToString(UtcPattern, CultureInfo.InvariantCulture))
                .Append(',')
                .Append(bucket.Start.ToString(LocalPattern, CultureInfo.InvariantCulture))
                .Append(',')
                .Append(width)
                .Append(',')
                .Append(bucket.BytesDown.ToString(CultureInfo.InvariantCulture))
                .Append(',')
                .Append(bucket.BytesUp.ToString(CultureInfo.InvariantCulture))
                .Append(',')
                .Append(bucket.BytesTotal.ToString(CultureInfo.InvariantCulture))
                .Append(',')
                .Append(bucket.PeakDown.ToString(CultureInfo.InvariantCulture))
                .Append(',')
                .Append(bucket.PeakUp.ToString(CultureInfo.InvariantCulture))
                .Append(',')
                .Append(bucket.Minutes.ToString(CultureInfo.InvariantCulture))
                .Append(",,,,,");

            writer.Write(line);
            writer.Write('\n');
        }
    }

    /// <summary>
    /// Writes recorded minutes as CSV, header first, each with the network it
    /// was measured on: the file History reads back in without losing anything.
    /// </summary>
    public static void WriteMinutes(TextWriter writer, IEnumerable<UsageMinute> minutes)
    {
        ArgumentNullException.ThrowIfNull(writer);
        ArgumentNullException.ThrowIfNull(minutes);

        writer.Write(Header);
        writer.Write('\n');

        StringBuilder line = new(160);
        string width = nameof(UsageGranularity.Minute).ToLowerInvariant();

        foreach (UsageMinute minute in minutes)
        {
            DateTimeOffset start = EpochMinute.ToLocal(minute.Minute);
            line.Clear();
            line.Append(start.ToUniversalTime().ToString(UtcPattern, CultureInfo.InvariantCulture))
                .Append(',')
                .Append(start.ToString(LocalPattern, CultureInfo.InvariantCulture))
                .Append(',')
                .Append(width)
                .Append(',')
                .Append(minute.BytesDown.ToString(CultureInfo.InvariantCulture))
                .Append(',')
                .Append(minute.BytesUp.ToString(CultureInfo.InvariantCulture))
                .Append(',')
                .Append(minute.BytesTotal.ToString(CultureInfo.InvariantCulture))
                .Append(',')
                .Append(minute.PeakDown.ToString(CultureInfo.InvariantCulture))
                .Append(',')
                .Append(minute.PeakUp.ToString(CultureInfo.InvariantCulture))
                .Append(",1,");

            if (minute.Network is { } network)
            {
                line.Append(Field(network.Name))
                    .Append(',')
                    .Append(NetworkTag.KindName(network.Kind))
                    .Append(',')
                    .Append(Field(network.Key));
            }
            else
            {
                line.Append(',');
            }

            line.Append(',');

            if (minute.LocalDown is { } localDown && minute.LocalUp is { } localUp)
            {
                line.Append(localDown.ToString(CultureInfo.InvariantCulture))
                    .Append(',')
                    .Append(localUp.ToString(CultureInfo.InvariantCulture));
            }
            else
            {
                line.Append(',');
            }

            writer.Write(line);
            writer.Write('\n');
        }
    }

    /// <summary>A text field as CSV: quoted when it must be, and never read as a formula.</summary>
    internal static string Field(string text)
    {
        if (text.Length > 0 && text[0] is '=' or '+' or '-' or '@' or '\t' or '\r')
        {
            text = "'" + text;
        }

        return text.AsSpan().IndexOfAny(",\"\r\n") >= 0
            ? string.Concat("\"", text.Replace("\"", "\"\"", StringComparison.Ordinal), "\"")
            : text;
    }

    /// <summary>A text field read back: the apostrophe <see cref="Field"/> put in front taken off.</summary>
    internal static string Unfield(string text) =>
        text.Length > 1 && text[0] == '\'' && text[1] is '=' or '+' or '-' or '@' or '\t' or '\r' ? text[1..] : text;

    /// <summary>
    /// Reads minute rows back out of a CSV file.
    /// </summary>
    public static UsageImport Read(TextReader reader)
    {
        ArgumentNullException.ThrowIfNull(reader);

        string? headerLine = reader.ReadLine();

        if (headerLine is null)
        {
            return new UsageImport([], 0);
        }

        Dictionary<string, int> columns = MapColumns(headerLine);

        if (!columns.TryGetValue(UtcColumn, out int utcAt) ||
            !columns.TryGetValue(DownColumn, out int downAt) ||
            !columns.TryGetValue(UpColumn, out int upAt))
        {
            return new UsageImport([], 0);
        }

        columns.TryGetValue(GranularityColumn, out int widthAt);
        int peakDownAt = columns.GetValueOrDefault(PeakDownColumn, -1);
        int peakUpAt = columns.GetValueOrDefault(PeakUpColumn, -1);
        bool hasWidth = columns.ContainsKey(GranularityColumn);
        int networkAt = columns.GetValueOrDefault(NetworkColumn, -1);
        int networkKindAt = columns.GetValueOrDefault(NetworkKindColumn, -1);
        int networkIdAt = columns.GetValueOrDefault(NetworkIdColumn, -1);
        int localDownAt = columns.GetValueOrDefault(LocalDownColumn, -1);
        int localUpAt = columns.GetValueOrDefault(LocalUpColumn, -1);

        List<UsageMinute> minutes = [];
        Dictionary<long, int> seen = [];
        int skipped = 0;

        while (reader.ReadLine() is { } line)
        {
            if (line.Length == 0)
            {
                continue;
            }

            List<string> fields = SplitFields(line);

            if (!TryRead(fields, utcAt, downAt, upAt, peakDownAt, peakUpAt, hasWidth, widthAt, out UsageMinute row))
            {
                skipped++;
                continue;
            }

            if (ReadNetwork(fields, networkAt, networkKindAt, networkIdAt) is { } network)
            {
                row = row with { Network = network };
            }

            if (TryReadLong(fields, localDownAt, out long localDown) && TryReadLong(fields, localUpAt, out long localUp))
            {
                row = row with { LocalDown = Math.Max(0, localDown), LocalUp = Math.Max(0, localUp) };
            }
            if (seen.TryGetValue(row.Minute, out int at))
            {
                UsageMinute existing = minutes[at];
                minutes[at] = existing with
                {
                    BytesDown = existing.BytesDown + row.BytesDown,
                    BytesUp = existing.BytesUp + row.BytesUp,
                    PeakDown = Math.Max(existing.PeakDown, row.PeakDown),
                    PeakUp = Math.Max(existing.PeakUp, row.PeakUp),
                    Network = existing.Network ?? row.Network,
                    LocalDown = existing.LocalDown is { } d && row.LocalDown is { } e ? d + e : null,
                    LocalUp = existing.LocalUp is { } f && row.LocalUp is { } g ? f + g : null,
                };
            }
            else
            {
                seen[row.Minute] = minutes.Count;
                minutes.Add(row);
            }
        }

        return new UsageImport(minutes, skipped);
    }

    private static bool TryRead(
        List<string> fields,
        int utcAt,
        int downAt,
        int upAt,
        int peakDownAt,
        int peakUpAt,
        bool hasWidth,
        int widthAt,
        out UsageMinute row)
    {
        row = default;

        if (fields.Count <= Math.Max(utcAt, Math.Max(downAt, upAt)))
        {
            return false;
        }

        if (hasWidth &&
            widthAt < fields.Count &&
            !fields[widthAt].Equals(nameof(UsageGranularity.Minute), StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        if (!DateTimeOffset.TryParse(
                fields[utcAt],
                CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal,
                out DateTimeOffset moment))
        {
            return false;
        }

        if (!TryReadLong(fields, downAt, out long down) ||
            !TryReadLong(fields, upAt, out long up))
        {
            return false;
        }

        TryReadLong(fields, peakDownAt, out long peakDown);
        TryReadLong(fields, peakUpAt, out long peakUp);

        row = new UsageMinute(
            EpochMinute.From(moment),
            Math.Max(0, down),
            Math.Max(0, up),
            Math.Max(0, peakDown),
            Math.Max(0, peakUp));

        return true;
    }

    /// <summary>The network a row names, or null when it names none.</summary>
    private static NetworkTag? ReadNetwork(List<string> fields, int nameAt, int kindAt, int idAt)
    {
        if (idAt < 0 || idAt >= fields.Count || fields[idAt].Length == 0)
        {
            return null;
        }

        string key = Unfield(fields[idAt]);
        string name = nameAt >= 0 && nameAt < fields.Count && fields[nameAt].Length > 0 ? Unfield(fields[nameAt]) : key;
        string? kind = kindAt >= 0 && kindAt < fields.Count ? fields[kindAt] : null;

        return new NetworkTag(key, name, NetworkTag.KindFrom(kind));
    }

    private static bool TryReadLong(List<string> fields, int at, out long value)
    {
        value = 0;

        return at >= 0
            && at < fields.Count
            && long.TryParse(fields[at], NumberStyles.Integer, CultureInfo.InvariantCulture, out value);
    }

    private static Dictionary<string, int> MapColumns(string header)
    {
        Dictionary<string, int> columns = new(StringComparer.OrdinalIgnoreCase);
        List<string> names = SplitFields(header);

        for (int i = 0; i < names.Count; i++)
        {
            string name = names[i].Trim().TrimStart('﻿');

            if (name.Length > 0)
            {
                columns[name] = i;
            }
        }

        return columns;
    }

    private static List<string> SplitFields(string line)
    {
        List<string> fields = [];
        StringBuilder field = new(32);
        bool quoted = false;

        for (int i = 0; i < line.Length; i++)
        {
            char c = line[i];

            if (quoted)
            {
                if (c != '"')
                {
                    field.Append(c);
                }
                else if (i + 1 < line.Length && line[i + 1] == '"')
                {
                    field.Append('"');
                    i++;
                }
                else
                {
                    quoted = false;
                }
            }
            else if (c == '"')
            {
                quoted = true;
            }
            else if (c == ',')
            {
                fields.Add(field.ToString().Trim());
                field.Clear();
            }
            else if (c != '\r')
            {
                field.Append(c);
            }
        }

        fields.Add(field.ToString().Trim());
        return fields;
    }
}
