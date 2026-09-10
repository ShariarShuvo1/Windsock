using System.Globalization;
using Microsoft.Data.Sqlite;

namespace Windsock.Core.History;

/// <summary>
/// Keeps usage history in a SQLite file, one row per minute.
/// </summary>
public sealed class SqliteUsageHistoryStore : IUsageHistoryStore, IDisposable
{
    // Bumped whenever the schema changes; Migrate applies the difference.
    private const int SchemaVersion = 1;
    private const string HourFormat = "%Y-%m-%d %H:00";
    private const string DayFormat = "%Y-%m-%d 00:00";
    private const string BucketPattern = "yyyy-MM-dd HH:mm";

    private readonly Lock _gate = new();
    private readonly SqliteConnection _connection;
    private bool _disposed;

    /// <summary>Opens, creating and migrating the database as needed.</summary>
    public SqliteUsageHistoryStore(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);

        string? directory = Path.GetDirectoryName(path);

        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        _connection = Open(path);
    }

    /// <summary>Whether the previous file was unreadable and was set aside.</summary>
    public bool RecoveredFromCorruption { get; private set; }
    private const int Corrupt = 11;
    private const int NotADatabase = 26;

    private SqliteConnection Open(string path)
    {
        try
        {
            return Connect(path);
        }
        catch (SqliteException ex) when (
            ex.SqliteErrorCode is Corrupt or NotADatabase)
        {
            SetAside(path);
            RecoveredFromCorruption = true;
            return Connect(path);
        }
    }

    private static SqliteConnection Connect(string path)
    {
        SqliteConnectionStringBuilder builder = new()
        {
            DataSource = path,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Pooling = false,
        };

        SqliteConnection connection = new(builder.ConnectionString);

        try
        {
            connection.Open();
            Execute(connection, "PRAGMA journal_mode=WAL;");
            Execute(connection, "PRAGMA synchronous=NORMAL;");
            Execute(connection, "PRAGMA temp_store=MEMORY;");

            Migrate(connection);
            return connection;
        }
        catch
        {
            connection.Dispose();
            throw;
        }
    }

    private static void SetAside(string path)
    {
        string stamp = DateTime.UtcNow.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture);

        try
        {
            foreach (string suffix in new[] { "", "-wal", "-shm" })
            {
                string existing = path + suffix;

                if (File.Exists(existing))
                {
                    File.Move(existing, $"{path}.{stamp}.corrupt{suffix}", overwrite: true);
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
    }

    private static void Migrate(SqliteConnection connection)
    {
        using SqliteCommand read = connection.CreateCommand();
        read.CommandText = "PRAGMA user_version;";
        long version = Convert.ToInt64(read.ExecuteScalar(), CultureInfo.InvariantCulture);

        if (version >= SchemaVersion)
        {
            return;
        }

        if (version < 1)
        {
            Execute(
                connection,
                """
                CREATE TABLE IF NOT EXISTS usage_minute (
                    minute    INTEGER PRIMARY KEY,
                    down      INTEGER NOT NULL,
                    up        INTEGER NOT NULL,
                    peak_down INTEGER NOT NULL,
                    peak_up   INTEGER NOT NULL
                ) WITHOUT ROWID;

                CREATE TABLE IF NOT EXISTS coverage (
                    start_minute INTEGER PRIMARY KEY,
                    end_minute   INTEGER NOT NULL
                ) WITHOUT ROWID;
                """);
        }

        Execute(connection, $"PRAGMA user_version={SchemaVersion};");
    }

    private static void Execute(SqliteConnection connection, string sql)
    {
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = sql;
        command.ExecuteNonQuery();
    }

    /// <inheritdoc />
    public void Write(IReadOnlyList<UsageMinute> minutes) => Store(
        minutes,
        """
        INSERT INTO usage_minute (minute, down, up, peak_down, peak_up)
        VALUES (@minute, @down, @up, @peakDown, @peakUp)
        ON CONFLICT(minute) DO UPDATE SET
            down      = down + excluded.down,
            up        = up + excluded.up,
            peak_down = MAX(peak_down, excluded.peak_down),
            peak_up   = MAX(peak_up, excluded.peak_up);
        """);

    /// <inheritdoc />
    public int Merge(IReadOnlyList<UsageMinute> minutes) => Store(
        minutes,
        """
        INSERT INTO usage_minute (minute, down, up, peak_down, peak_up)
        VALUES (@minute, @down, @up, @peakDown, @peakUp)
        ON CONFLICT(minute) DO NOTHING;
        """);

    /// <inheritdoc />
    public int Replace(IReadOnlyList<UsageMinute> minutes) => Store(
        minutes,
        """
        INSERT INTO usage_minute (minute, down, up, peak_down, peak_up)
        VALUES (@minute, @down, @up, @peakDown, @peakUp)
        ON CONFLICT(minute) DO UPDATE SET
            down      = excluded.down,
            up        = excluded.up,
            peak_down = excluded.peak_down,
            peak_up   = excluded.peak_up;
        """);

    /// <inheritdoc />
    public int CountExisting(IReadOnlyList<UsageMinute> minutes)
    {
        ArgumentNullException.ThrowIfNull(minutes);

        if (minutes.Count == 0)
        {
            return 0;
        }

        HashSet<long> wanted = new(minutes.Count);
        long lowest = long.MaxValue;
        long highest = long.MinValue;

        foreach (UsageMinute row in minutes)
        {
            wanted.Add(row.Minute);
            lowest = Math.Min(lowest, row.Minute);
            highest = Math.Max(highest, row.Minute);
        }

        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            using SqliteCommand command = _connection.CreateCommand();
            command.CommandText =
                """
                SELECT minute FROM usage_minute
                WHERE minute >= @lowest AND minute <= @highest;
                """;
            command.Parameters.AddWithValue("@lowest", lowest);
            command.Parameters.AddWithValue("@highest", highest);

            using SqliteDataReader reader = command.ExecuteReader();
            int found = 0;

            while (reader.Read())
            {
                if (wanted.Contains(reader.GetInt64(0)))
                {
                    found++;
                }
            }

            return found;
        }
    }

    private int Store(IReadOnlyList<UsageMinute> minutes, string sql)
    {
        ArgumentNullException.ThrowIfNull(minutes);

        if (minutes.Count == 0)
        {
            return 0;
        }

        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);

            using SqliteTransaction transaction = _connection.BeginTransaction();
            using SqliteCommand command = _connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = sql;

            SqliteParameter minute = command.Parameters.Add("@minute", SqliteType.Integer);
            SqliteParameter down = command.Parameters.Add("@down", SqliteType.Integer);
            SqliteParameter up = command.Parameters.Add("@up", SqliteType.Integer);
            SqliteParameter peakDown = command.Parameters.Add("@peakDown", SqliteType.Integer);
            SqliteParameter peakUp = command.Parameters.Add("@peakUp", SqliteType.Integer);

            int written = 0;

            foreach (UsageMinute row in minutes)
            {
                minute.Value = row.Minute;
                down.Value = row.BytesDown;
                up.Value = row.BytesUp;
                peakDown.Value = row.PeakDown;
                peakUp.Value = row.PeakUp;
                written += command.ExecuteNonQuery();
            }

            transaction.Commit();
            return written;
        }
    }

    /// <inheritdoc />
    public void WriteCoverage(long startMinute, long endMinute)
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);

            using SqliteCommand command = _connection.CreateCommand();
            command.CommandText =
                """
                INSERT INTO coverage (start_minute, end_minute)
                VALUES (@start, @end)
                ON CONFLICT(start_minute) DO UPDATE SET
                    end_minute = MAX(end_minute, excluded.end_minute);
                """;
            command.Parameters.AddWithValue("@start", startMinute);
            command.Parameters.AddWithValue("@end", Math.Max(startMinute, endMinute));
            command.ExecuteNonQuery();
        }
    }

    /// <inheritdoc />
    public IReadOnlyList<UsageBucket> Read(
        DateTimeOffset from,
        DateTimeOffset until,
        UsageGranularity granularity) =>
        granularity == UsageGranularity.Minute
            ? ReadMinutes(from, until)
            : ReadGrouped(from, until, granularity == UsageGranularity.Hour ? HourFormat : DayFormat);

    private List<UsageBucket> ReadMinutes(DateTimeOffset from, DateTimeOffset until)
    {
        List<UsageBucket> buckets = [];

        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);

            using SqliteCommand command = _connection.CreateCommand();
            command.CommandText =
                """
                SELECT minute, down, up, peak_down, peak_up
                FROM usage_minute
                WHERE minute >= @from AND minute < @to
                ORDER BY minute;
                """;
            command.Parameters.AddWithValue("@from", EpochMinute.From(from));
            command.Parameters.AddWithValue("@to", EpochMinute.From(until));

            using SqliteDataReader reader = command.ExecuteReader();

            while (reader.Read())
            {
                buckets.Add(new UsageBucket(
                    EpochMinute.ToLocal(reader.GetInt64(0)),
                    reader.GetInt64(1),
                    reader.GetInt64(2),
                    reader.GetInt64(3),
                    reader.GetInt64(4),
                    Minutes: 1));
            }
        }

        return buckets;
    }

    private List<UsageBucket> ReadGrouped(DateTimeOffset from, DateTimeOffset until, string format)
    {
        List<UsageBucket> buckets = [];

        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);

            using SqliteCommand command = _connection.CreateCommand();
            command.CommandText =
                """
                SELECT strftime(@format, minute * 60, 'unixepoch', 'localtime') AS bucket,
                       SUM(down), SUM(up), MAX(peak_down), MAX(peak_up), COUNT(*)
                FROM usage_minute
                WHERE minute >= @from AND minute < @to
                GROUP BY bucket
                ORDER BY bucket;
                """;
            command.Parameters.AddWithValue("@format", format);
            command.Parameters.AddWithValue("@from", EpochMinute.From(from));
            command.Parameters.AddWithValue("@to", EpochMinute.From(until));

            using SqliteDataReader reader = command.ExecuteReader();

            while (reader.Read())
            {
                buckets.Add(new UsageBucket(
                    LocalStart(reader.GetString(0)),
                    reader.GetInt64(1),
                    reader.GetInt64(2),
                    reader.GetInt64(3),
                    reader.GetInt64(4),
                    reader.GetInt32(5)));
            }
        }

        return buckets;
    }

    private static DateTimeOffset LocalStart(string bucket)
    {
        DateTime local = DateTime.ParseExact(
            bucket,
            BucketPattern,
            CultureInfo.InvariantCulture,
            DateTimeStyles.None);
        return new DateTimeOffset(local, TimeZoneInfo.Local.GetUtcOffset(local));
    }

    /// <inheritdoc />
    public UsageSummary Summarise(DateTimeOffset from, DateTimeOffset until)
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);

            using SqliteCommand command = _connection.CreateCommand();
            command.CommandText =
                """
                SELECT COALESCE(SUM(down), 0), COALESCE(SUM(up), 0),
                       COALESCE(MAX(peak_down), 0), COALESCE(MAX(peak_up), 0),
                       COUNT(*)
                FROM usage_minute
                WHERE minute >= @from AND minute < @to;
                """;
            command.Parameters.AddWithValue("@from", EpochMinute.From(from));
            command.Parameters.AddWithValue("@to", EpochMinute.From(until));

            using SqliteDataReader reader = command.ExecuteReader();

            return reader.Read()
                ? new UsageSummary(
                    reader.GetInt64(0),
                    reader.GetInt64(1),
                    reader.GetInt64(2),
                    reader.GetInt64(3),
                    reader.GetInt32(4))
                : UsageSummary.Empty;
        }
    }

    /// <inheritdoc />
    public IReadOnlyList<CoverageWindow> ReadCoverage(DateTimeOffset from, DateTimeOffset until)
    {
        List<CoverageWindow> windows = [];

        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);

            using SqliteCommand command = _connection.CreateCommand();
            command.CommandText =
                """
                SELECT start_minute, end_minute
                FROM coverage
                WHERE end_minute >= @from AND start_minute < @to
                ORDER BY start_minute;
                """;
            command.Parameters.AddWithValue("@from", EpochMinute.From(from));
            command.Parameters.AddWithValue("@to", EpochMinute.From(until));

            using SqliteDataReader reader = command.ExecuteReader();

            while (reader.Read())
            {
                windows.Add(new CoverageWindow(
                    EpochMinute.ToLocal(reader.GetInt64(0)),
                    EpochMinute.ToLocal(reader.GetInt64(1))));
            }
        }

        return windows;
    }

    /// <inheritdoc />
    public int Delete(DateTimeOffset from, DateTimeOffset until)
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);

            using SqliteCommand command = _connection.CreateCommand();
            command.CommandText =
                """
                DELETE FROM usage_minute
                WHERE minute >= @from AND minute < @to;
                """;
            command.Parameters.AddWithValue("@from", EpochMinute.From(from));
            command.Parameters.AddWithValue("@to", EpochMinute.From(until));
            return command.ExecuteNonQuery();
        }
    }

    /// <inheritdoc />
    public event EventHandler? Erased;

    /// <inheritdoc />
    public int RemoveEverything()
    {
        int gone;

        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);

            using (SqliteCommand readings = _connection.CreateCommand())
            {
                readings.CommandText = "DELETE FROM usage_minute;";
                gone = readings.ExecuteNonQuery();
            }

            using (SqliteCommand watched = _connection.CreateCommand())
            {
                watched.CommandText = "DELETE FROM coverage;";
                watched.ExecuteNonQuery();
            }
            using SqliteCommand tidy = _connection.CreateCommand();
            tidy.CommandText = "VACUUM;";
            tidy.ExecuteNonQuery();
        }
        Erased?.Invoke(this, EventArgs.Empty);

        return gone;
    }

    /// <inheritdoc />
    public UsageExtent Extent()
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);

            using SqliteCommand command = _connection.CreateCommand();
            command.CommandText = "SELECT MIN(minute), MAX(minute) FROM usage_minute;";

            using SqliteDataReader reader = command.ExecuteReader();

            if (!reader.Read() || reader.IsDBNull(0))
            {
                return default;
            }

            return new UsageExtent(
                EpochMinute.ToLocal(reader.GetInt64(0)),
                EpochMinute.ToLocal(reader.GetInt64(1)));
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            try
            {
                Execute(_connection, "PRAGMA wal_checkpoint(TRUNCATE);");
            }
            catch (SqliteException)
            {
                // Closing matters more than tidiness.
            }

            _connection.Dispose();
        }
    }
}
