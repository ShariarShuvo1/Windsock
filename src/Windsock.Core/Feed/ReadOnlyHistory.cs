using System.Globalization;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using Windsock.Core.History;

namespace Windsock.Core.Feed;

/// <summary>
/// The history file opened read-only, for the command line: safe beside a
/// running Windsock, and usable when none is running.
/// </summary>
/// <remarks>
/// <para>
/// It opens the file with SQLite's read-only mode and <c>query_only</c> on, so
/// nothing in it can be changed from here: not a migration, not a checkpoint
/// of the write-ahead log, not the setting-aside of a damaged file, all of which
/// the app's own store would do on opening. A reader in WAL mode never blocks
/// the app's writes, and sees the history as of the moment each read began.
/// SQLite may create the -wal and -shm files beside the database when the app
/// has closed and removed them; that is SQLite's bookkeeping, not a change to
/// history.
/// </para>
/// <para>
/// Only <c>usage_minute</c>'s first five columns are read, which every schema
/// since the first has had, so this keeps working across the store's own
/// schema changes. A file from a newer Windsock reads the same way.
/// </para>
/// </remarks>
public sealed class ReadOnlyHistory : IDisposable
{
    private readonly SqliteConnection _connection;

    private ReadOnlyHistory(SqliteConnection connection) => _connection = connection;

    /// <summary>Opens the file, or nothing with the reason when it cannot be.</summary>
    public static ReadOnlyHistory? Open(string path, out string? problem) => Open(path, out problem, out _);

    /// <summary>
    /// Opens the file, or nothing with the reason when it cannot be, saying
    /// whether that is because there is a file and it could not be read (busy,
    /// locked or damaged) rather than because there is no history in it yet.
    /// </summary>
    public static ReadOnlyHistory? Open(string path, out string? problem, out bool unreadable)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);

        problem = null;
        unreadable = false;

        if (!File.Exists(path))
        {
            problem = "There is no history yet: Windsock has not recorded anything on this PC.";
            return null;
        }

        SqliteConnectionStringBuilder builder = new()
        {
            DataSource = path,
            Mode = SqliteOpenMode.ReadOnly,
            Pooling = false,
            DefaultTimeout = 5,
        };

        SqliteConnection connection = new(builder.ConnectionString);

        try
        {
            connection.Open();

            using (SqliteCommand pragma = connection.CreateCommand())
            {
                pragma.CommandText = "PRAGMA query_only=ON;";
                pragma.ExecuteNonQuery();
            }

            using SqliteCommand check = connection.CreateCommand();
            check.CommandText = "SELECT COUNT(*) FROM sqlite_master WHERE type = 'table' AND name = 'usage_minute';";

            if (Convert.ToInt64(check.ExecuteScalar(), CultureInfo.InvariantCulture) == 0)
            {
                connection.Dispose();
                problem = "The history file holds no usage yet.";
                return null;
            }

            return new ReadOnlyHistory(connection);
        }
        catch (SqliteException ex)
        {
            connection.Dispose();
            problem = string.Concat("The history file could not be read: ", ex.Message);
            unreadable = true;
            return null;
        }
    }

    /// <summary>The recorded minutes in a period, oldest first.</summary>
    public IReadOnlyList<UsageMinute> ReadMinutes(DateTimeOffset from, DateTimeOffset until)
    {
        List<UsageMinute> minutes = [];
        using SqliteCommand command = _connection.CreateCommand();
        command.CommandText =
            """
            SELECT minute, down, up, peak_down, peak_up FROM usage_minute
            WHERE minute >= @from AND minute < @until
            ORDER BY minute;
            """;
        command.Parameters.AddWithValue("@from", EpochMinute.From(from));
        command.Parameters.AddWithValue("@until", EpochMinute.From(until));

        using SqliteDataReader reader = command.ExecuteReader();

        while (reader.Read())
        {
            minutes.Add(new UsageMinute(reader.GetInt64(0), reader.GetInt64(1), reader.GetInt64(2), reader.GetInt64(3), reader.GetInt64(4)));
        }

        return minutes;
    }

    /// <summary>A period's recorded use, in its own minutes, hours or days on this PC's clock.</summary>
    public IReadOnlyList<UsageBucket> Read(UsagePeriod period)
    {
        ArgumentNullException.ThrowIfNull(period);
        return Bucket(ReadMinutes(period.From, period.Until), period.Granularity, TimeZoneInfo.Local);
    }

    /// <summary>Gathers minutes into buckets of a clock's minutes, hours or days; empty buckets are left out.</summary>
    public static IReadOnlyList<UsageBucket> Bucket(IReadOnlyList<UsageMinute> minutes, UsageGranularity granularity, TimeZoneInfo zone)
    {
        ArgumentNullException.ThrowIfNull(minutes);
        ArgumentNullException.ThrowIfNull(zone);

        CalendarStep step = granularity switch
        {
            UsageGranularity.Hour => CalendarStep.Hour,
            UsageGranularity.Day => CalendarStep.Day,
            _ => CalendarStep.Minute,
        };

        SortedDictionary<DateTimeOffset, UsageBucket> buckets = [];

        foreach (UsageMinute minute in minutes)
        {
            DateTimeOffset start = LocalCalendar.BucketStart(minute.Minute, step, zone);
            UsageBucket bucket = buckets.GetValueOrDefault(start, new UsageBucket(start, 0, 0, 0, 0, 0));

            buckets[start] = bucket with
            {
                BytesDown = bucket.BytesDown + minute.BytesDown,
                BytesUp = bucket.BytesUp + minute.BytesUp,
                PeakDown = Math.Max(bucket.PeakDown, minute.PeakDown),
                PeakUp = Math.Max(bucket.PeakUp, minute.PeakUp),
                Minutes = bucket.Minutes + 1,
            };
        }

        return [.. buckets.Values];
    }

    /// <summary>
    /// Writes a period as CSV, in the same columns as History's own export:
    /// minute rows one by one, hours and days as buckets.
    /// </summary>
    public void WriteCsv(TextWriter writer, UsagePeriod period)
    {
        ArgumentNullException.ThrowIfNull(writer);
        ArgumentNullException.ThrowIfNull(period);

        if (period.Granularity == UsageGranularity.Minute)
        {
            UsageCsv.WriteMinutes(writer, ReadMinutes(period.From, period.Until));
        }
        else
        {
            UsageCsv.Write(writer, period.Granularity, Read(period));
        }
    }

    /// <summary>
    /// Writes a period as JSON: network use only, in the "windsock-usage" shape.
    /// History's own JSON export, with apps, the machine and notes, is the app's.
    /// </summary>
    /// <remarks>
    /// <code>
    /// { "format": "windsock-usage", "version": 1, "exportedAt": "…Z", "timeZone": "…",
    ///   "from": "…", "until": "…", "granularity": "minute|hour|day",
    ///   "total": { "bytesDown": 0, "bytesUp": 0, "bytesTotal": 0, "minutes": 0 },
    ///   "usage": [ { "start": "…", "minutes": 0, "bytesDown": 0, "bytesUp": 0,
    ///                "peakDownBytesPerSecond": 0, "peakUpBytesPerSecond": 0 } ] }
    /// </code>
    /// </remarks>
    public void WriteJson(Stream stream, UsagePeriod period, DateTimeOffset exportedAt)
    {
        ArgumentNullException.ThrowIfNull(stream);
        ArgumentNullException.ThrowIfNull(period);

        IReadOnlyList<UsageBucket> buckets = Read(period);

        using Utf8JsonWriter json = new(stream, FeedJson.Options with { Indented = true });
        json.WriteStartObject();
        json.WriteString("format", "windsock-usage");
        json.WriteNumber("version", 1);
        json.WriteString("exportedAt", exportedAt.ToUniversalTime().ToString("yyyy-MM-ddTHH:mm:ss'Z'", CultureInfo.InvariantCulture));
        json.WriteString("timeZone", TimeZoneInfo.Local.Id);
        json.WriteString("from", FeedJson.Time(period.From));
        json.WriteString("until", FeedJson.Time(period.Until));
        json.WriteString("granularity", period.Granularity.ToString().ToLowerInvariant());

        json.WriteStartObject("total");
        json.WriteNumber("bytesDown", buckets.Sum(each => each.BytesDown));
        json.WriteNumber("bytesUp", buckets.Sum(each => each.BytesUp));
        json.WriteNumber("bytesTotal", buckets.Sum(each => each.BytesTotal));
        json.WriteNumber("minutes", buckets.Sum(each => each.Minutes));
        json.WriteEndObject();

        json.WriteStartArray("usage");

        foreach (UsageBucket bucket in buckets)
        {
            json.WriteStartObject();
            json.WriteString("start", FeedJson.Time(bucket.Start));
            json.WriteNumber("minutes", bucket.Minutes);
            json.WriteNumber("bytesDown", bucket.BytesDown);
            json.WriteNumber("bytesUp", bucket.BytesUp);
            json.WriteNumber("peakDownBytesPerSecond", bucket.PeakDown);
            json.WriteNumber("peakUpBytesPerSecond", bucket.PeakUp);
            json.WriteEndObject();
        }

        json.WriteEndArray();
        json.WriteEndObject();
        json.Flush();
    }

    public void Dispose() => _connection.Dispose();
}
