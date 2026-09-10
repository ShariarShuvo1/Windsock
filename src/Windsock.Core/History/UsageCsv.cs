using System.Globalization;
using System.Text;

namespace Windsock.Core.History;

/// <summary>The result of reading a history file back in.</summary>
public readonly record struct UsageImport(IReadOnlyList<UsageMinute> Minutes, int Skipped);

/// <summary>
/// Reads and writes history as CSV.
/// </summary>
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
        MinutesColumn);

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
                .Append(bucket.Minutes.ToString(CultureInfo.InvariantCulture));

            writer.Write(line);
            writer.Write('\n');
        }
    }

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
            if (seen.TryGetValue(row.Minute, out int at))
            {
                UsageMinute existing = minutes[at];
                minutes[at] = existing with
                {
                    BytesDown = existing.BytesDown + row.BytesDown,
                    BytesUp = existing.BytesUp + row.BytesUp,
                    PeakDown = Math.Max(existing.PeakDown, row.PeakDown),
                    PeakUp = Math.Max(existing.PeakUp, row.PeakUp),
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
