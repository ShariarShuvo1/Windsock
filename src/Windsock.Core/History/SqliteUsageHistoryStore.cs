using System.Data.Common;
using System.Globalization;
using Microsoft.Data.Sqlite;
using Windsock.Core.Apps;
using Windsock.Core.Plans;

namespace Windsock.Core.History;

/// <summary>
/// Keeps usage history in a SQLite file, one row per minute, and beside it each
/// app's use as Windows counted it.
/// </summary>
/// <remarks>
/// App usage lives in the same file rather than one of its own because it is
/// history too: erasing history, or a period of it, has to take both, and a
/// damaged file is set aside as one.
/// </remarks>
public sealed partial class SqliteUsageHistoryStore
    : IUsageHistoryStore, IAppUsageStore, IAppDetailStore, IMachineHistoryStore,
      IUsageAggregates, IHistoryNotes, IHistoryFile, IPlanLevelStore, IDisposable
{
    // Bumped whenever the schema changes; Migrate applies the difference. When
    // a change would let this version damage a file written by the next one -
    // a table an erase must also clear, say - raise FormatFloor with it too.
    internal const int SchemaVersion = 6;
    private const string CursorKey = "app_cursor";
    private const string RunStartKey = "app_minute_run";
    private const string MissingKey = "app_missing_since";

    // Windows are at most an hour wide; a day bounds the search generously.
    private const long WidestWindow = 24 * 60;
    private const string HourFormat = "%Y-%m-%d %H:00";
    private const string DayFormat = "%Y-%m-%d 00:00";
    private const string BucketPattern = "yyyy-MM-dd HH:mm";

    private readonly Lock _gate = new();
    private readonly SqliteConnection? _connection;
    private readonly Exception? _openFailure;
    private readonly TimeProvider _clock;
    private readonly string _path;
    private bool _disposed;

    /// <summary>Opens, creating and migrating the database as needed.</summary>
    /// <remarks>
    /// A file that cannot be opened at all - locked, a directory, access denied -
    /// does not fail construction, because the store is built while the app
    /// starts and would take the app with it. Every call then fails instead, in
    /// a way <see cref="HistoryFailure.IsStorage"/> recognises.
    /// </remarks>
    public SqliteUsageHistoryStore(string path)
        : this(path, TimeProvider.System)
    {
    }

    internal SqliteUsageHistoryStore(string path, TimeProvider clock)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        ArgumentNullException.ThrowIfNull(clock);

        _clock = clock;
        _path = path;

        try
        {
            string? directory = Path.GetDirectoryName(path);

            if (!string.IsNullOrEmpty(directory))
            {
                Directory.CreateDirectory(directory);
            }

            _connection = Open(path);
        }
        catch (Exception ex) when (ex is DbException or IOException or UnauthorizedAccessException)
        {
            _openFailure = ex;
        }

        if (_connection is not null)
        {
            Inspect(_connection);
        }
    }

    /// <inheritdoc />
    public string FilePath => _path;

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
            // A newer Windsock may have written something this SQLite cannot
            // parse; the file is not damaged, and setting it aside would hide
            // all its history after a downgrade. Its own header says so.
            if (NewerByHeader(path) is { } schema)
            {
                _fileSchema = schema;
                throw;
            }

            SetAside(path);
            RecoveredFromCorruption = true;
            return Connect(path);
        }
    }

    /// <summary>
    /// The schema a newer Windsock wrote, read from the file's header without
    /// SQLite - user_version at byte 60, application_id at byte 68, both big
    /// endian - or null when the header does not say this is Windsock's file
    /// from a version newer than this one.
    /// </summary>
    internal static int? NewerByHeader(string path)
    {
        const int HeaderLength = 100;
        ReadOnlySpan<byte> magic = "SQLite format 3\0"u8;

        try
        {
            Span<byte> header = stackalloc byte[HeaderLength];
            using FileStream stream = new(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);

            if (stream.ReadAtLeast(header, HeaderLength, throwOnEndOfStream: false) < HeaderLength || !header[..16].SequenceEqual(magic))
            {
                return null;
            }

            int version = System.Buffers.Binary.BinaryPrimitives.ReadInt32BigEndian(header[60..]);
            int application = System.Buffers.Binary.BinaryPrimitives.ReadInt32BigEndian(header[68..]);
            return application == ApplicationId && version > SchemaVersion ? version : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
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

    private static long ReadVersion(SqliteConnection connection)
    {
        using SqliteCommand read = connection.CreateCommand();
        read.CommandText = "PRAGMA user_version;";
        return Convert.ToInt64(read.ExecuteScalar(), CultureInfo.InvariantCulture);
    }

    /// <summary>
    /// Brings an older file up to this schema, in one transaction, so a file is
    /// never left half migrated. A newer file is left exactly as it is.
    /// </summary>
    private static void Migrate(SqliteConnection connection)
    {
        long version = ReadVersion(connection);

        if (version >= SchemaVersion)
        {
            return;
        }

        using SqliteTransaction transaction = connection.BeginTransaction();

        if (version < 1)
        {
            Execute(
                connection,
                transaction,
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

        if (version < 2)
        {
            // An app is stored once and referred to by number: a program is named
            // by its full path, and a minute can hold a dozen of them.
            //
            // A window is keyed by its start alone. Windows are laid end to end,
            // so two never start at the same minute, and a window read again has
            // the start it had the first time.
            //
            // app_state is not history. Erasing history leaves it, so collection
            // does not go back and fetch what was just erased.
            Execute(
                connection,
                transaction,
                """
                CREATE TABLE IF NOT EXISTS app (
                    id          INTEGER PRIMARY KEY,
                    attribution TEXT NOT NULL UNIQUE
                );

                CREATE TABLE IF NOT EXISTS app_usage (
                    start_minute INTEGER NOT NULL,
                    minutes      INTEGER NOT NULL,
                    app          INTEGER NOT NULL REFERENCES app(id),
                    down         INTEGER NOT NULL,
                    up           INTEGER NOT NULL,
                    PRIMARY KEY (start_minute, app)
                ) WITHOUT ROWID;

                CREATE INDEX IF NOT EXISTS app_usage_by_app ON app_usage (app, start_minute);

                CREATE TABLE IF NOT EXISTS app_state (
                    name  TEXT PRIMARY KEY,
                    value INTEGER NOT NULL
                ) WITHOUT ROWID;
                """);
        }

        if (version < 3)
        {
            // What has been erased of the time app usage is still collected for.
            // Collection reads a few settled minutes again on every pass, and a
            // pass can be reading Windows while somebody erases; without this, a
            // period erased would be written straight back and shown in History
            // as time Windsock missed.
            Execute(
                connection,
                transaction,
                """
                CREATE TABLE IF NOT EXISTS app_erased (
                    from_minute  INTEGER NOT NULL,
                    until_minute INTEGER NOT NULL
                );
                """);
        }

        if (version < 4)
        {
            MigrateAppDetail(connection, transaction);
        }

        if (version < 5)
        {
            MigrateMachine(connection, transaction);
        }

        if (version < 6)
        {
            MigrateFormat(connection, transaction);
        }

        Execute(connection, transaction, $"PRAGMA user_version={SchemaVersion};");
        transaction.Commit();
    }

    private static void Execute(SqliteConnection connection, string sql) => Execute(connection, null, sql);

    private static void Execute(SqliteConnection connection, SqliteTransaction? transaction, string sql)
    {
        using SqliteCommand command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = sql;
        command.ExecuteNonQuery();
    }

    /// <summary>The open connection. Call with the gate held.</summary>
    private SqliteConnection Connection()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        return _connection
            ?? throw new InvalidOperationException("The history database could not be opened.", _openFailure);
    }

    /// <inheritdoc />
    /// <remarks>
    /// A minute written in two goes - a restart inside it - adds up, and keeps
    /// the network it was first written with.
    /// </remarks>
    public void Write(IReadOnlyList<UsageMinute> minutes) => Store(
        minutes,
        """
        INSERT INTO usage_minute (minute, down, up, peak_down, peak_up, network)
        VALUES (@minute, @down, @up, @peakDown, @peakUp, @network)
        ON CONFLICT(minute) DO UPDATE SET
            down      = down + excluded.down,
            up        = up + excluded.up,
            peak_down = MAX(peak_down, excluded.peak_down),
            peak_up   = MAX(peak_up, excluded.peak_up),
            network   = COALESCE(network, excluded.network);
        """,
        imported: false);

    /// <inheritdoc />
    public int Merge(IReadOnlyList<UsageMinute> minutes) => Store(
        minutes,
        """
        INSERT INTO usage_minute (minute, down, up, peak_down, peak_up, network, lan_down, lan_up)
        VALUES (@minute, @down, @up, @peakDown, @peakUp, @network, @lanDown, @lanUp)
        ON CONFLICT(minute) DO NOTHING;
        """,
        imported: true);

    /// <inheritdoc />
    /// <remarks>
    /// A file that does not say which network a minute was on leaves the
    /// network history already knows for it. The local-network share goes
    /// with the figures it is a share of: the file's, or not known.
    /// </remarks>
    public int Replace(IReadOnlyList<UsageMinute> minutes) => Store(
        minutes,
        """
        INSERT INTO usage_minute (minute, down, up, peak_down, peak_up, network, lan_down, lan_up)
        VALUES (@minute, @down, @up, @peakDown, @peakUp, @network, @lanDown, @lanUp)
        ON CONFLICT(minute) DO UPDATE SET
            down      = excluded.down,
            up        = excluded.up,
            peak_down = excluded.peak_down,
            peak_up   = excluded.peak_up,
            network   = COALESCE(excluded.network, network),
            lan_down  = excluded.lan_down,
            lan_up    = excluded.lan_up;
        """,
        imported: true);

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
            using SqliteCommand command = Connection().CreateCommand();
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

    /// <summary>Writes minutes, each with its network, in one transaction.</summary>
    /// <param name="minutes">The minutes.</param>
    /// <param name="sql">The statement for one minute.</param>
    /// <param name="imported">
    /// Whether the minutes come from a file: history read back in is history
    /// changed, so anything caching totals of the past hears of it. A minute
    /// before 1970 cannot have been recorded and is not taken.
    /// </param>
    private int Store(IReadOnlyList<UsageMinute> minutes, string sql, bool imported)
    {
        ArgumentNullException.ThrowIfNull(minutes);

        if (minutes.Count == 0)
        {
            return 0;
        }

        int written = 0;

        lock (_gate)
        {
            SqliteConnection connection = WritableConnection();

            using SqliteTransaction transaction = connection.BeginTransaction();
            Dictionary<string, long> networks = NetworkIds(connection, transaction, minutes);

            using SqliteCommand command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = sql;

            SqliteParameter minute = command.Parameters.Add("@minute", SqliteType.Integer);
            SqliteParameter down = command.Parameters.Add("@down", SqliteType.Integer);
            SqliteParameter up = command.Parameters.Add("@up", SqliteType.Integer);
            SqliteParameter peakDown = command.Parameters.Add("@peakDown", SqliteType.Integer);
            SqliteParameter peakUp = command.Parameters.Add("@peakUp", SqliteType.Integer);
            SqliteParameter network = command.Parameters.Add("@network", SqliteType.Integer);
            SqliteParameter lanDown = command.Parameters.Add("@lanDown", SqliteType.Integer);
            SqliteParameter lanUp = command.Parameters.Add("@lanUp", SqliteType.Integer);

            foreach (UsageMinute row in minutes)
            {
                if (imported && row.Minute < 0)
                {
                    continue;
                }

                minute.Value = row.Minute;
                down.Value = row.BytesDown;
                up.Value = row.BytesUp;
                peakDown.Value = row.PeakDown;
                peakUp.Value = row.PeakUp;
                network.Value = row.Network is { } tag && networks.TryGetValue(tag.Key, out long id) ? id : DBNull.Value;

                // Both or neither, and never more than the minute itself.
                bool local = row.LocalDown is not null && row.LocalUp is not null;
                lanDown.Value = local ? Math.Clamp(row.LocalDown!.Value, 0, Math.Max(0, row.BytesDown)) : DBNull.Value;
                lanUp.Value = local ? Math.Clamp(row.LocalUp!.Value, 0, Math.Max(0, row.BytesUp)) : DBNull.Value;
                written += command.ExecuteNonQuery();
            }

            transaction.Commit();
        }

        if (imported && written > 0)
        {
            Interlocked.Increment(ref _revision);
        }

        return written;
    }

    /// <inheritdoc />
    public void WriteCoverage(long startMinute, long endMinute)
    {
        lock (_gate)
        {
            using SqliteCommand command = WritableConnection().CreateCommand();
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
            using SqliteCommand command = Connection().CreateCommand();
            command.CommandText =
                """
                SELECT minute, down, up, peak_down, peak_up, COALESCE(lan_down, 0), COALESCE(lan_up, 0), lan_down IS NOT NULL
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
                    Minutes: 1)
                {
                    LocalDown = reader.GetInt64(5),
                    LocalUp = reader.GetInt64(6),
                    LocalMinutes = reader.GetInt32(7),
                });
            }
        }

        return buckets;
    }

    /// <remarks>
    /// Read from the quarter-hour totals, with the minutes at a range's edges
    /// one by one: a quarter hour always lies inside one local hour, so the
    /// buckets are what grouping the minutes gave, and a year by the day reads
    /// thirty-five thousand rows rather than half a million.
    /// </remarks>
    private List<UsageBucket> ReadGrouped(DateTimeOffset from, DateTimeOffset until, string format)
    {
        List<UsageBucket> buckets = [];
        long fromMinute = EpochMinute.From(from);
        long untilMinute = EpochMinute.From(until);

        if (untilMinute <= fromMinute)
        {
            return buckets;
        }

        lock (_gate)
        {
            using SqliteCommand command = Connection().CreateCommand();
            command.CommandText = string.Concat(
                """
                SELECT strftime(@format, start * 60, 'unixepoch', 'localtime') AS bucket,
                       SUM(down), SUM(up), MAX(peak_down), MAX(peak_up), SUM(minutes),
                       SUM(lan_down), SUM(lan_up), SUM(lan_minutes)
                FROM (
                """,
                RangeRows,
                """
                )
                GROUP BY bucket
                ORDER BY bucket;
                """);
            command.Parameters.AddWithValue("@format", format);
            BindRange(command, fromMinute, untilMinute);

            using SqliteDataReader reader = command.ExecuteReader();

            while (reader.Read())
            {
                buckets.Add(new UsageBucket(
                    LocalStart(reader.GetString(0)),
                    reader.GetInt64(1),
                    reader.GetInt64(2),
                    reader.GetInt64(3),
                    reader.GetInt64(4),
                    reader.GetInt32(5))
                {
                    LocalDown = reader.GetInt64(6),
                    LocalUp = reader.GetInt64(7),
                    LocalMinutes = reader.GetInt32(8),
                });
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
        long fromMinute = EpochMinute.From(from);
        long untilMinute = EpochMinute.From(until);

        if (untilMinute <= fromMinute)
        {
            return UsageSummary.Empty;
        }

        lock (_gate)
        {
            using SqliteCommand command = Connection().CreateCommand();
            command.CommandText = string.Concat(
                """
                SELECT COALESCE(SUM(down), 0), COALESCE(SUM(up), 0),
                       COALESCE(MAX(peak_down), 0), COALESCE(MAX(peak_up), 0),
                       COALESCE(SUM(minutes), 0),
                       COALESCE(SUM(lan_down), 0), COALESCE(SUM(lan_up), 0), COALESCE(SUM(lan_minutes), 0)
                FROM (
                """,
                RangeRows,
                ");");
            BindRange(command, fromMinute, untilMinute);

            using SqliteDataReader reader = command.ExecuteReader();

            return reader.Read()
                ? new UsageSummary(
                    reader.GetInt64(0),
                    reader.GetInt64(1),
                    reader.GetInt64(2),
                    reader.GetInt64(3),
                    reader.GetInt32(4))
                {
                    LocalDown = reader.GetInt64(5),
                    LocalUp = reader.GetInt64(6),
                    LocalMinutes = reader.GetInt32(7),
                }
                : UsageSummary.Empty;
        }
    }

    /// <inheritdoc />
    public IReadOnlyList<CoverageWindow> ReadCoverage(DateTimeOffset from, DateTimeOffset until)
    {
        List<CoverageWindow> windows = [];

        lock (_gate)
        {
            using SqliteCommand command = Connection().CreateCommand();
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
    /// <remarks>
    /// Everything history keeps for the period goes in one transaction: the
    /// minutes and the networks they name, their quarter-hour totals, Windows'
    /// figures, each app's minutes, the machine and the connection, and the
    /// notes inside it (see <see cref="TrimNotes"/>).
    /// </remarks>
    public int Delete(DateTimeOffset from, DateTimeOffset until)
    {
        int removed;
        long fromMinute = EpochMinute.From(from);
        long untilMinute = EpochMinute.From(until);

        lock (_gate)
        {
            SqliteConnection connection = WritableConnection();
            using SqliteTransaction transaction = connection.BeginTransaction();
            BeginErasing(connection, transaction);

            using (SqliteCommand command = connection.CreateCommand())
            {
                command.Transaction = transaction;
                command.CommandText =
                    """
                    DELETE FROM usage_minute
                    WHERE minute >= @from AND minute < @to;
                    """;
                command.Parameters.AddWithValue("@from", fromMinute);
                command.Parameters.AddWithValue("@to", untilMinute);
                removed = command.ExecuteNonQuery();
            }

            RebuildQuarters(connection, transaction, Math.Max(0, fromMinute), untilMinute);
            EraseBesideMinutes(connection, transaction, fromMinute, untilMinute);
            PruneNetworks(connection, transaction);
            EndErasing(connection, transaction);

            transaction.Commit();
            Interlocked.Increment(ref _revision);
        }

        Erased?.Invoke(this, EventArgs.Empty);
        NotesChanged?.Invoke(this, EventArgs.Empty);
        return removed;
    }

    /// <summary>
    /// Notes a period as erased, so app usage collection does not write it back.
    /// Call with the gate held.
    /// </summary>
    /// <remarks>
    /// Only up to now: a period reaching into the future has had nothing
    /// collected for its future yet, and erasing it now is not a wish never to
    /// record it. Spans older than collection can ever reach are let go.
    /// </remarks>
    private void RememberErased(SqliteConnection connection, SqliteTransaction? transaction, long fromMinute, long untilMinute)
    {
        long now = EpochMinute.From(_clock.GetUtcNow());
        long until = Math.Min(untilMinute, now + 1);

        // A span that runs on from one already noted extends it, so passing
        // over a paused minute at a time leaves one row, not one a minute.
        using SqliteCommand command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText =
            """
            DELETE FROM app_erased WHERE until_minute < @forgotten;
            UPDATE app_erased SET until_minute = MAX(until_minute, @until)
            WHERE @from < @until AND from_minute <= @from AND until_minute >= @from;
            INSERT INTO app_erased (from_minute, until_minute)
            SELECT @from, @until WHERE @from < @until AND changes() = 0;
            """;
        command.Parameters.AddWithValue("@forgotten", now - AppUsagePlan.Backfill - (24 * 60));
        command.Parameters.AddWithValue("@from", fromMinute);
        command.Parameters.AddWithValue("@until", until);
        command.ExecuteNonQuery();
    }

    /// <inheritdoc />
    public event EventHandler? Erased;

    /// <summary>How long the VACUUM after erasing everything waits for a file another reader holds.</summary>
    internal TimeSpan VacuumWait { get; set; } = TimeSpan.FromSeconds(5);

    /// <inheritdoc />
    /// <remarks>
    /// The erase is one transaction; giving the space back (VACUUM) comes after
    /// it and is best effort. A VACUUM that fails - a full disk, say - leaves
    /// the file its old size, never the history: <see cref="Erased"/> is raised
    /// whenever the erase itself was committed, so the copies beside the file
    /// are let go too.
    /// </remarks>
    public int RemoveEverything()
    {
        int gone;
        bool committed = false;

        try
        {
            gone = EraseEverything(ref committed);
        }
        finally
        {
            if (committed)
            {
                Erased?.Invoke(this, EventArgs.Empty);
                NotesChanged?.Invoke(this, EventArgs.Empty);
            }
        }

        return gone;
    }

    private int EraseEverything(ref bool committed)
    {
        int gone;

        lock (_gate)
        {
            SqliteConnection connection = WritableConnection();

            using (SqliteTransaction transaction = connection.BeginTransaction())
            {
                BeginErasing(connection, transaction);

                using (SqliteCommand readings = connection.CreateCommand())
                {
                    readings.Transaction = transaction;
                    readings.CommandText = "DELETE FROM usage_minute;";
                    gone = readings.ExecuteNonQuery();
                }

                // Notes and the networks minutes were measured on are history
                // too; plan levels and bookmarks are not, and stay.
                Execute(
                    connection,
                    transaction,
                    $"""
                    DELETE FROM coverage;
                    DELETE FROM usage_quarter;
                    DELETE FROM network;
                    DELETE FROM note;
                    DELETE FROM erase_pending;
                    DELETE FROM app_usage;
                    DELETE FROM app_minute;
                    DELETE FROM app_quarter;
                    DELETE FROM app_measured;
                    DELETE FROM system_minute;
                    DELETE FROM connection_minute;
                    DELETE FROM app;
                    DELETE FROM app_erased;
                    DELETE FROM app_state WHERE name IN ('app_detail_before', '{EraseAllKey}');
                    """);

                RememberErased(connection, transaction, long.MinValue / 2, long.MaxValue);
                EndErasing(connection, transaction);
                transaction.Commit();
            }

            committed = true;
            Interlocked.Increment(ref _revision);

            try
            {
                // Not waited on for long: another reader holding the file makes
                // VACUUM wait out the command's whole timeout, with recording
                // queued behind the store's lock all the while.
                using SqliteCommand tidy = connection.CreateCommand();
                tidy.CommandText = "VACUUM;";
                tidy.CommandTimeout = Math.Max(1, (int)Math.Ceiling(VacuumWait.TotalSeconds));
                tidy.ExecuteNonQuery();
            }
            catch (SqliteException)
            {
                // The history is gone either way; Reclaim space can try again.
            }
        }

        return gone;
    }

    /// <inheritdoc />
    public UsageExtent Extent()
    {
        lock (_gate)
        {
            using SqliteCommand command = Connection().CreateCommand();
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

    /// <inheritdoc />
    public AppUsagePosition ReadAppUsagePosition()
    {
        lock (_gate)
        {
            using SqliteCommand command = Connection().CreateCommand();
            command.CommandText = "SELECT name, value FROM app_state WHERE name IN (@cursor, @run, @missing);";
            command.Parameters.AddWithValue("@cursor", CursorKey);
            command.Parameters.AddWithValue("@run", RunStartKey);
            command.Parameters.AddWithValue("@missing", MissingKey);

            long? cursor = null;
            long? run = null;
            long? missing = null;

            using (SqliteDataReader reader = command.ExecuteReader())
            {
                while (reader.Read())
                {
                    switch (reader.GetString(0))
                    {
                        case CursorKey:
                            cursor = reader.GetInt64(1);
                            break;
                        case RunStartKey:
                            run = reader.GetInt64(1);
                            break;
                        default:
                            missing = reader.GetInt64(1);
                            break;
                    }
                }
            }

            return cursor is { } at
                ? new AppUsagePosition(at, Math.Min(run ?? at, at), missing)
                : new AppUsagePosition(null, 0);
        }
    }

    /// <inheritdoc />
    public void WriteAppUsage(IReadOnlyList<UsageWindow> windows, IReadOnlyList<AppUsageSlice> slices, AppUsagePosition position)
    {
        ArgumentNullException.ThrowIfNull(windows);
        ArgumentNullException.ThrowIfNull(slices);

        lock (_gate)
        {
            SqliteConnection connection = WritableConnection();
            using SqliteTransaction transaction = connection.BeginTransaction();

            // Read inside the transaction, under the gate, so an erase that
            // happened while this was being read from Windows is seen.
            List<(long From, long Until)> erased = ReadErased(connection, transaction);
            HashSet<long> kept = [];

            if (windows.Count > 0)
            {
                // Whatever was kept anywhere in the window goes, not only a row
                // that started where it does: an hour read again after a long gap
                // replaces the minutes kept for its first part before Windows
                // rolled them into one record (see AppUsagePlan.Start).
                using SqliteCommand clear = connection.CreateCommand();
                clear.Transaction = transaction;
                clear.CommandText =
                    """
                    DELETE FROM app_usage
                    WHERE start_minute >= @reach AND start_minute < @end AND start_minute + minutes > @start;
                    """;
                SqliteParameter reach = clear.Parameters.Add("@reach", SqliteType.Integer);
                SqliteParameter cleared = clear.Parameters.Add("@start", SqliteType.Integer);
                SqliteParameter end = clear.Parameters.Add("@end", SqliteType.Integer);

                foreach (UsageWindow window in windows)
                {
                    // A window touching anything erased is left alone whole.
                    // Keeping less than Windows counted is the side to err on.
                    if (erased.Exists(span => window.StartMinute < span.Until && window.EndMinute > span.From))
                    {
                        continue;
                    }

                    kept.Add(window.StartMinute);
                    reach.Value = window.StartMinute - WidestWindow;
                    cleared.Value = window.StartMinute;
                    end.Value = window.EndMinute;
                    clear.ExecuteNonQuery();
                }
            }

            if (slices.Count > 0)
            {
                using SqliteCommand name = connection.CreateCommand();
                name.Transaction = transaction;
                name.CommandText =
                    """
                    INSERT INTO app (attribution) VALUES (@attribution)
                    ON CONFLICT(attribution) DO UPDATE SET attribution = excluded.attribution
                    RETURNING id;
                    """;
                SqliteParameter attribution = name.Parameters.Add("@attribution", SqliteType.Text);

                using SqliteCommand usage = connection.CreateCommand();
                usage.Transaction = transaction;
                usage.CommandText =
                    """
                    INSERT INTO app_usage (start_minute, minutes, app, down, up)
                    VALUES (@start, @minutes, @app, @down, @up)
                    ON CONFLICT(start_minute, app) DO UPDATE SET
                        minutes = excluded.minutes,
                        down    = down + excluded.down,
                        up      = up + excluded.up;
                    """;
                SqliteParameter start = usage.Parameters.Add("@start", SqliteType.Integer);
                SqliteParameter minutes = usage.Parameters.Add("@minutes", SqliteType.Integer);
                SqliteParameter app = usage.Parameters.Add("@app", SqliteType.Integer);
                SqliteParameter down = usage.Parameters.Add("@down", SqliteType.Integer);
                SqliteParameter up = usage.Parameters.Add("@up", SqliteType.Integer);

                Dictionary<string, long> ids = new(StringComparer.Ordinal);

                foreach (AppUsageSlice slice in slices)
                {
                    if (slice.Minutes <= 0
                        || (slice.BytesReceived <= 0 && slice.BytesSent <= 0)
                        || !kept.Contains(slice.StartMinute))
                    {
                        continue;
                    }

                    string key = slice.AttributionId ?? string.Empty;

                    if (!ids.TryGetValue(key, out long id))
                    {
                        attribution.Value = key;
                        id = Convert.ToInt64(name.ExecuteScalar(), CultureInfo.InvariantCulture);
                        ids[key] = id;
                    }

                    start.Value = slice.StartMinute;
                    minutes.Value = slice.Minutes;
                    app.Value = id;
                    down.Value = Math.Max(0, slice.BytesReceived);
                    up.Value = Math.Max(0, slice.BytesSent);
                    usage.ExecuteNonQuery();
                }
            }

            WritePosition(connection, transaction, position);
            transaction.Commit();
        }
    }

    /// <inheritdoc />
    public void PassOver(long fromMinute, AppUsagePosition position)
    {
        lock (_gate)
        {
            SqliteConnection connection = WritableConnection();
            using SqliteTransaction transaction = connection.BeginTransaction();

            if (position.Cursor is { } until)
            {
                RememberErased(connection, transaction, fromMinute, until);
            }

            WritePosition(connection, transaction, position);
            transaction.Commit();
        }
    }

    /// <summary>Writes where collection stands. Call with the gate held, inside the transaction.</summary>
    private static void WritePosition(SqliteConnection connection, SqliteTransaction transaction, AppUsagePosition position)
    {
        if (position.Cursor is not { } cursor)
        {
            return;
        }

        using SqliteCommand state = connection.CreateCommand();
        state.Transaction = transaction;
        state.CommandText =
            """
            INSERT INTO app_state (name, value) VALUES (@cursorName, @cursor), (@runName, @run)
            ON CONFLICT(name) DO UPDATE SET value = excluded.value;
            DELETE FROM app_state WHERE name = @missingName;
            INSERT INTO app_state (name, value) SELECT @missingName, @missing WHERE @missing IS NOT NULL;
            """;
        state.Parameters.AddWithValue("@cursorName", CursorKey);
        state.Parameters.AddWithValue("@cursor", cursor);
        state.Parameters.AddWithValue("@runName", RunStartKey);
        state.Parameters.AddWithValue("@run", Math.Min(position.MinuteRunStart, cursor));
        state.Parameters.AddWithValue("@missingName", MissingKey);
        state.Parameters.AddWithValue("@missing", position.MissingSince is { } missing ? missing : DBNull.Value);
        state.ExecuteNonQuery();
    }

    private static List<(long From, long Until)> ReadErased(SqliteConnection connection, SqliteTransaction transaction)
    {
        List<(long, long)> spans = [];

        using SqliteCommand command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "SELECT from_minute, until_minute FROM app_erased;";

        using SqliteDataReader reader = command.ExecuteReader();

        while (reader.Read())
        {
            spans.Add((reader.GetInt64(0), reader.GetInt64(1)));
        }

        return spans;
    }

    /// <inheritdoc />
    public IReadOnlyList<AppUsageTotal> ReadAppTotals(DateTimeOffset from, DateTimeOffset until)
    {
        List<AppUsageTotal> totals = [];

        lock (_gate)
        {
            using SqliteCommand command = Connection().CreateCommand();
            command.CommandText =
                """
                SELECT a.attribution, SUM(u.down), SUM(u.up)
                FROM app_usage u JOIN app a ON a.id = u.app
                WHERE u.start_minute >= @from AND u.start_minute < @to
                GROUP BY u.app;
                """;
            command.Parameters.AddWithValue("@from", EpochMinute.From(from));
            command.Parameters.AddWithValue("@to", EpochMinute.From(until));

            using SqliteDataReader reader = command.ExecuteReader();

            while (reader.Read())
            {
                totals.Add(new AppUsageTotal(reader.GetString(0), reader.GetInt64(1), reader.GetInt64(2)));
            }
        }

        return totals;
    }

    /// <inheritdoc />
    public IReadOnlyList<AppUsageSlice> ReadAppSlices(long fromMinute, long untilMinute)
    {
        List<AppUsageSlice> slices = [];

        lock (_gate)
        {
            using SqliteCommand command = Connection().CreateCommand();
            command.CommandText =
                """
                SELECT u.start_minute, u.minutes, a.attribution, u.down, u.up
                FROM app_usage u JOIN app a ON a.id = u.app
                WHERE u.start_minute >= @from AND u.start_minute < @to
                ORDER BY u.start_minute;
                """;
            command.Parameters.AddWithValue("@from", fromMinute);
            command.Parameters.AddWithValue("@to", untilMinute);

            using SqliteDataReader reader = command.ExecuteReader();

            while (reader.Read())
            {
                slices.Add(new AppUsageSlice(
                    reader.GetInt64(0),
                    reader.GetInt32(1),
                    reader.GetString(2),
                    reader.GetInt64(3),
                    reader.GetInt64(4)));
            }
        }

        return slices;
    }

    /// <inheritdoc />
    public IReadOnlyList<UsageSpan> ReadMissedSpans(DateTimeOffset from, DateTimeOffset until)
    {
        List<UsageSpan> spans = [];

        lock (_gate)
        {
            // A window counts as missed only when not one of its minutes was
            // recorded. Windows' figures and Windsock's are measured differently
            // - one per network profile, the other per adapter - so a window
            // Windsock saw part of is left to what Windsock recorded, rather than
            // patched with a share of a figure measured another way. For the
            // same reason only windows wholly inside the period count: an hour
            // Windows keeps as one record cannot be cut at a custom range's edge.
            using SqliteCommand command = Connection().CreateCommand();
            command.CommandText =
                """
                SELECT s.start_minute, s.minutes, s.down, s.up
                FROM (
                    SELECT start_minute, MAX(minutes) AS minutes, SUM(down) AS down, SUM(up) AS up
                    FROM app_usage
                    WHERE start_minute >= @from AND start_minute < @to
                    GROUP BY start_minute
                    HAVING start_minute + MAX(minutes) <= @to
                ) s
                WHERE NOT EXISTS (
                    SELECT 1 FROM usage_minute m
                    WHERE m.minute >= s.start_minute AND m.minute < s.start_minute + s.minutes)
                ORDER BY s.start_minute;
                """;
            command.Parameters.AddWithValue("@from", EpochMinute.From(from));
            command.Parameters.AddWithValue("@to", EpochMinute.From(until));

            using SqliteDataReader reader = command.ExecuteReader();

            while (reader.Read())
            {
                spans.Add(new UsageSpan(
                    reader.GetInt64(0),
                    reader.GetInt32(1),
                    reader.GetInt64(2),
                    reader.GetInt64(3)));
            }
        }

        return spans;
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

            if (_connection is null)
            {
                return;
            }

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
