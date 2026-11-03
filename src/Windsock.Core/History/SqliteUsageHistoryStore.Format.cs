using System.Globalization;
using Microsoft.Data.Sqlite;

namespace Windsock.Core.History;

/// <summary>
/// Schema 6: the network each minute was measured on, quarter-hour totals kept
/// by the database itself, notes, plan levels, and the marks that keep the file
/// safe from other versions of Windsock.
/// </summary>
/// <remarks>
/// <para>
/// <b>Older versions.</b> Windsock 0.1.0 opens any newer file happily - it only
/// checks that the schema is at least its own - and its erase knows nothing of
/// the tables added since, so before this schema a downgrade, an erase and an
/// upgrade brought the erased period back as Windows' figures and per-app
/// history. 0.1.0 cannot be changed, but it runs the file's triggers like any
/// SQLite client. So deleting a minute without saying so (see
/// <see cref="ErasingKey"/>) leaves the minute in <c>erase_pending</c>, and
/// clearing the record of watching (which only "erase everything" does) leaves
/// the moment in <c>app_state</c>; the next run of this version or a later one
/// erases everything else for that time before anything reads it
/// (<see cref="SettleOlderWriters"/>). This only protects files a 0.2.0 or later
/// has opened, since that is what puts the triggers there.
/// </para>
/// <para>
/// <b>Newer versions.</b> From this schema on, the file says the oldest schema
/// that may write it (<see cref="FloorKey"/>). A version older than that reads
/// the file and never writes it (<see cref="HistoryFileState.NewerVersion"/>), so
/// a future change of the kind that bit 0.1.0 cannot be undone by this version.
/// A newer schema that leaves the floor alone - an index, a column nothing
/// erases by - is written by this version as usual.
/// </para>
/// </remarks>
public sealed partial class SqliteUsageHistoryStore
{
    /// <summary>The application id in the file's header: "WSCK".</summary>
    internal const int ApplicationId = 0x5753434B;

    /// <summary>The oldest schema a Windsock must know to write a file this version writes.</summary>
    internal const int FormatFloor = 6;

    internal const string FloorKey = "format_floor";

    /// <summary>
    /// Set inside this version's own erase transactions, so the triggers leave
    /// the minutes it deletes alone; it does everything they would itself.
    /// </summary>
    internal const string ErasingKey = "erasing";

    /// <summary>The minute an older version erased everything up to.</summary>
    internal const string EraseAllKey = "erase_all_pending";

    private const string MarkPrefix = "mark:";
    private const string NewerMessage =
        "The history file was written by a newer version of Windsock, so this version only reads it.";

    private bool _newer;
    private int _fileSchema;

    /// <inheritdoc />
    public HistoryFileState State =>
        _connection is null ? HistoryFileState.Unavailable
        : _newer ? HistoryFileState.NewerVersion
        : HistoryFileState.Ready;

    /// <inheritdoc />
    public int FileSchema => _fileSchema;

    /// <inheritdoc />
    public long Revision => Interlocked.Read(ref _revision);

    private long _revision;

    private static void MigrateFormat(SqliteConnection connection, SqliteTransaction transaction)
    {
        // A network is stored once and referred to by number, like an app. Its
        // key is Windows' id for it; the person's own name for it and whether
        // it is their hotspot are choices, kept in the settings, not history.
        Execute(
            connection,
            transaction,
            """
            CREATE TABLE IF NOT EXISTS network (
                id      INTEGER PRIMARY KEY,
                key     TEXT NOT NULL UNIQUE,
                name    TEXT NOT NULL,
                kind    TEXT NOT NULL,
                metered INTEGER
            );
            """);

        // The network a minute was measured on. Added as a column, so a minute
        // and its network can never be parted, an erase takes both, and an older
        // version still writes minutes it knows nothing about (as unknown).
        if (!HasColumn(connection, transaction, "usage_minute", "network"))
        {
            Execute(connection, transaction, "ALTER TABLE usage_minute ADD COLUMN network INTEGER REFERENCES network(id);");
        }

        // The part of a minute that stayed on the local network - a copy from a
        // NAS, a cast to the TV - which no internet plan counts. Only the kernel
        // trace can tell it apart, so it is NULL, "not known", unless that fills
        // it in (WriteLocalTraffic); everything reading it falls back to the
        // minute's whole figure then. Reserved here so the feature needs no
        // schema of its own.
        if (!HasColumn(connection, transaction, "usage_minute", "lan_down"))
        {
            Execute(
                connection,
                transaction,
                """
                ALTER TABLE usage_minute ADD COLUMN lan_down INTEGER;
                ALTER TABLE usage_minute ADD COLUMN lan_up INTEGER;
                """);
        }

        Execute(
            connection,
            transaction,
            """
            CREATE TABLE IF NOT EXISTS usage_quarter (
                quarter     INTEGER NOT NULL,
                network     INTEGER NOT NULL,
                down        INTEGER NOT NULL,
                up          INTEGER NOT NULL,
                peak_down   INTEGER NOT NULL,
                peak_up     INTEGER NOT NULL,
                minutes     INTEGER NOT NULL,
                lan_down    INTEGER NOT NULL DEFAULT 0,
                lan_up      INTEGER NOT NULL DEFAULT 0,
                lan_minutes INTEGER NOT NULL DEFAULT 0,
                PRIMARY KEY (quarter, network)
            ) WITHOUT ROWID;

            CREATE TABLE IF NOT EXISTS note (
                id           INTEGER PRIMARY KEY,
                from_minute  INTEGER NOT NULL,
                until_minute INTEGER NOT NULL,
                text         TEXT NOT NULL,
                created      INTEGER NOT NULL
            );

            CREATE INDEX IF NOT EXISTS note_by_time ON note (from_minute);

            CREATE TABLE IF NOT EXISTS plan_level (
                plan  TEXT NOT NULL,
                cycle INTEGER NOT NULL,
                level INTEGER NOT NULL,
                at    INTEGER NOT NULL,
                PRIMARY KEY (plan, cycle, level)
            ) WITHOUT ROWID;

            CREATE TABLE IF NOT EXISTS erase_pending (
                minute INTEGER PRIMARY KEY
            ) WITHOUT ROWID;

            DELETE FROM usage_quarter;
            INSERT INTO usage_quarter (quarter, network, down, up, peak_down, peak_up, minutes, lan_down, lan_up, lan_minutes)
            SELECT minute / 15, COALESCE(network, 0), SUM(down), SUM(up), MAX(peak_down), MAX(peak_up), COUNT(*),
                   SUM(COALESCE(lan_down, 0)), SUM(COALESCE(lan_up, 0)), COUNT(lan_down)
            FROM usage_minute
            GROUP BY minute / 15, COALESCE(network, 0);
            """);

        // Quarter-hour totals, kept by the database rather than by this code, so
        // that a minute written by any version - 0.1.0 included - is counted.
        // An insert and a change keep them exactly; peaks are worked out again
        // from the quarter's fifteen minutes, since a maximum cannot be taken
        // back. A delete this version did not announce is only noted: older
        // versions never read the totals, and the next run of this one settles
        // the note before anything does (SettleOlderWriters).
        Execute(
            connection,
            transaction,
            """
            CREATE TRIGGER IF NOT EXISTS usage_minute_added AFTER INSERT ON usage_minute
            BEGIN
                INSERT INTO usage_quarter (quarter, network, down, up, peak_down, peak_up, minutes, lan_down, lan_up, lan_minutes)
                VALUES (NEW.minute / 15, COALESCE(NEW.network, 0), NEW.down, NEW.up, NEW.peak_down, NEW.peak_up, 1,
                        COALESCE(NEW.lan_down, 0), COALESCE(NEW.lan_up, 0), NEW.lan_down IS NOT NULL)
                ON CONFLICT (quarter, network) DO UPDATE SET
                    down        = down + excluded.down,
                    up          = up + excluded.up,
                    peak_down   = MAX(peak_down, excluded.peak_down),
                    peak_up     = MAX(peak_up, excluded.peak_up),
                    minutes     = minutes + 1,
                    lan_down    = lan_down + excluded.lan_down,
                    lan_up      = lan_up + excluded.lan_up,
                    lan_minutes = lan_minutes + excluded.lan_minutes;
            END;

            CREATE TRIGGER IF NOT EXISTS usage_minute_changed AFTER UPDATE ON usage_minute
            BEGIN
                UPDATE usage_quarter SET
                    down        = down - OLD.down,
                    up          = up - OLD.up,
                    minutes     = minutes - 1,
                    lan_down    = lan_down - COALESCE(OLD.lan_down, 0),
                    lan_up      = lan_up - COALESCE(OLD.lan_up, 0),
                    lan_minutes = lan_minutes - (OLD.lan_down IS NOT NULL)
                WHERE quarter = OLD.minute / 15 AND network = COALESCE(OLD.network, 0);

                INSERT INTO usage_quarter (quarter, network, down, up, peak_down, peak_up, minutes, lan_down, lan_up, lan_minutes)
                VALUES (NEW.minute / 15, COALESCE(NEW.network, 0), NEW.down, NEW.up, 0, 0, 1,
                        COALESCE(NEW.lan_down, 0), COALESCE(NEW.lan_up, 0), NEW.lan_down IS NOT NULL)
                ON CONFLICT (quarter, network) DO UPDATE SET
                    down        = down + excluded.down,
                    up          = up + excluded.up,
                    minutes     = minutes + 1,
                    lan_down    = lan_down + excluded.lan_down,
                    lan_up      = lan_up + excluded.lan_up,
                    lan_minutes = lan_minutes + excluded.lan_minutes;

                DELETE FROM usage_quarter WHERE quarter = OLD.minute / 15 AND minutes <= 0;

                UPDATE usage_quarter SET
                    peak_down = (SELECT COALESCE(MAX(m.peak_down), 0) FROM usage_minute m
                                 WHERE m.minute >= usage_quarter.quarter * 15 AND m.minute < usage_quarter.quarter * 15 + 15
                                   AND COALESCE(m.network, 0) = usage_quarter.network),
                    peak_up   = (SELECT COALESCE(MAX(m.peak_up), 0) FROM usage_minute m
                                 WHERE m.minute >= usage_quarter.quarter * 15 AND m.minute < usage_quarter.quarter * 15 + 15
                                   AND COALESCE(m.network, 0) = usage_quarter.network)
                WHERE quarter = NEW.minute / 15;
            END;

            CREATE TRIGGER IF NOT EXISTS usage_minute_removed AFTER DELETE ON usage_minute
            WHEN NOT EXISTS (SELECT 1 FROM app_state WHERE name = 'erasing')
            BEGIN
                INSERT OR IGNORE INTO erase_pending (minute) VALUES (OLD.minute);
            END;

            CREATE TRIGGER IF NOT EXISTS coverage_removed AFTER DELETE ON coverage
            WHEN NOT EXISTS (SELECT 1 FROM app_state WHERE name = 'erasing')
            BEGIN
                INSERT INTO app_state (name, value)
                VALUES ('erase_all_pending', CAST(strftime('%s', 'now') AS INTEGER) / 60 + 1)
                ON CONFLICT (name) DO UPDATE SET value = MAX(value, excluded.value);
            END;
            """);

        Execute(connection, transaction, $"PRAGMA application_id={ApplicationId};");

        using SqliteCommand floor = connection.CreateCommand();
        floor.Transaction = transaction;
        floor.CommandText =
            """
            INSERT INTO app_state (name, value) VALUES (@name, @floor)
            ON CONFLICT (name) DO UPDATE SET value = MAX(value, excluded.value);
            """;
        floor.Parameters.AddWithValue("@name", FloorKey);
        floor.Parameters.AddWithValue("@floor", FormatFloor);
        floor.ExecuteNonQuery();
    }

    private static bool HasColumn(SqliteConnection connection, SqliteTransaction transaction, string table, string column)
    {
        using SqliteCommand command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "SELECT COUNT(*) FROM pragma_table_info(@table) WHERE name = @column;";
        command.Parameters.AddWithValue("@table", table);
        command.Parameters.AddWithValue("@column", column);
        return Convert.ToInt64(command.ExecuteScalar(), CultureInfo.InvariantCulture) > 0;
    }

    /// <summary>
    /// Works out whether this version may write the file, and settles anything
    /// an older version left behind.
    /// </summary>
    /// <remarks>
    /// Both read tables, which a file damaged past its first page cannot
    /// answer. Such a file still opened, and every call on it fails as a
    /// storage failure, as it did before; it is not set aside for this.
    /// </remarks>
    private void Inspect(SqliteConnection connection)
    {
        try
        {
            _fileSchema = (int)Math.Min(int.MaxValue, ReadVersion(connection));

            if (_fileSchema > SchemaVersion)
            {
                using SqliteCommand floor = connection.CreateCommand();
                floor.CommandText = "SELECT value FROM app_state WHERE name = @name;";
                floor.Parameters.AddWithValue("@name", FloorKey);
                object? found = floor.ExecuteScalar();

                // No floor in a file newer than this one cannot come from a
                // Windsock that kept the rule, so it is not trusted to be safe.
                long required = found is null or DBNull ? long.MaxValue : Convert.ToInt64(found, CultureInfo.InvariantCulture);
                _newer = required > SchemaVersion;
            }

            if (!_newer)
            {
                SettleOlderWriters(connection);
            }
        }
        catch (SqliteException)
        {
        }
    }

    /// <summary>The open connection, for a call that writes. Call with the gate held.</summary>
    private SqliteConnection WritableConnection()
    {
        SqliteConnection connection = Connection();

        if (_newer)
        {
            throw new HistoryReadOnlyException(NewerMessage);
        }

        return connection;
    }

    /// <summary>Says, inside a transaction, that the deletes to follow are this version's own.</summary>
    private static void BeginErasing(SqliteConnection connection, SqliteTransaction transaction) =>
        Execute(connection, transaction, $"INSERT INTO app_state (name, value) VALUES ('{ErasingKey}', 1) ON CONFLICT (name) DO NOTHING;");

    private static void EndErasing(SqliteConnection connection, SqliteTransaction transaction) =>
        Execute(connection, transaction, $"DELETE FROM app_state WHERE name = '{ErasingKey}';");

    /// <summary>
    /// Finishes erases an older version made: the minutes it deleted take
    /// everything else of their time with them, and the quarter-hour totals are
    /// worked out again where it took minutes out from under them.
    /// </summary>
    private void SettleOlderWriters(SqliteConnection connection)
    {
        lock (_gate)
        {
            using SqliteTransaction transaction = connection.BeginTransaction();
            long? everythingBefore = null;

            using (SqliteCommand all = connection.CreateCommand())
            {
                all.Transaction = transaction;
                all.CommandText = "SELECT value FROM app_state WHERE name = @name;";
                all.Parameters.AddWithValue("@name", EraseAllKey);
                object? found = all.ExecuteScalar();
                everythingBefore = found is null or DBNull ? null : Convert.ToInt64(found, CultureInfo.InvariantCulture);
            }

            List<long> minutes = [];

            using (SqliteCommand pending = connection.CreateCommand())
            {
                pending.Transaction = transaction;
                pending.CommandText = "SELECT minute FROM erase_pending ORDER BY minute;";
                using SqliteDataReader reader = pending.ExecuteReader();

                while (reader.Read())
                {
                    minutes.Add(reader.GetInt64(0));
                }
            }

            if (everythingBefore is null && minutes.Count == 0)
            {
                return;
            }

            BeginErasing(connection, transaction);

            if (everythingBefore is { } before)
            {
                RebuildQuarters(connection, transaction, 0, before);
                EraseBesideMinutes(connection, transaction, long.MinValue / 2, before);
            }

            foreach ((long from, long until) in Joined(connection, transaction, Runs(minutes)))
            {
                RebuildQuarters(connection, transaction, from, until);
                EraseBesideMinutes(connection, transaction, from, until);
            }

            Execute(connection, transaction, $"DELETE FROM erase_pending; DELETE FROM app_state WHERE name = '{EraseAllKey}';");
            PruneNetworks(connection, transaction);
            EndErasing(connection, transaction);
            transaction.Commit();
            Interlocked.Increment(ref _revision);
            SettledOlderErase = true;
        }
    }

    /// <inheritdoc />
    public bool SettledOlderErase { get; private set; }

    /// <summary>
    /// Runs of erased minutes joined across the time between them when no
    /// minute is left in it. 0.1.0 erases one unbroken range, so time inside it
    /// with nothing recorded - the PC off, Windsock closed - was erased too, and
    /// Windows' figures and notes for that time must go with it; a minute still
    /// there between two runs means they were two erases.
    /// </summary>
    private static List<(long From, long Until)> Joined(SqliteConnection connection, SqliteTransaction transaction, IEnumerable<(long From, long Until)> runs)
    {
        List<(long From, long Until)> joined = [];

        using SqliteCommand between = connection.CreateCommand();
        between.Transaction = transaction;
        between.CommandText = "SELECT EXISTS (SELECT 1 FROM usage_minute WHERE minute >= @from AND minute < @until);";
        SqliteParameter from = between.Parameters.Add("@from", SqliteType.Integer);
        SqliteParameter until = between.Parameters.Add("@until", SqliteType.Integer);

        foreach ((long From, long Until) run in runs)
        {
            if (joined.Count > 0)
            {
                (long From, long Until) last = joined[^1];
                from.Value = last.Until;
                until.Value = run.From;

                if (Convert.ToInt64(between.ExecuteScalar(), CultureInfo.InvariantCulture) == 0)
                {
                    joined[^1] = (last.From, run.Until);
                    continue;
                }
            }

            joined.Add(run);
        }

        return joined;
    }

    /// <summary>Sorted minutes as unbroken runs, each end exclusive.</summary>
    internal static IEnumerable<(long From, long Until)> Runs(IReadOnlyList<long> sorted)
    {
        int at = 0;

        while (at < sorted.Count)
        {
            long from = sorted[at];
            long until = from + 1;
            at++;

            while (at < sorted.Count && sorted[at] <= until)
            {
                until = Math.Max(until, sorted[at] + 1);
                at++;
            }

            yield return (from, until);
        }
    }

    /// <summary>
    /// Takes a period out of everything history keeps beside the network
    /// minutes themselves: Windows' figures, each app's measured minutes, the
    /// machine and the connection, and the notes; and remembers it as erased.
    /// Call with the gate held, inside the erase's transaction.
    /// </summary>
    private void EraseBesideMinutes(SqliteConnection connection, SqliteTransaction transaction, long fromMinute, long untilMinute)
    {
        // A period erased is erased whoever counted it. Leaving the figures
        // Windows gave behind would bring the period straight back as time
        // Windsock missed. Every window touching the period goes, an hour
        // that began before it too: one record cannot be cut in two, and
        // keeping less than was counted is the side to err on.
        using (SqliteCommand apps = connection.CreateCommand())
        {
            apps.Transaction = transaction;
            apps.CommandText =
                """
                DELETE FROM app_usage
                WHERE start_minute >= @from - 1440 AND start_minute < @to AND start_minute + minutes > @from;
                """;
            apps.Parameters.AddWithValue("@from", fromMinute);
            apps.Parameters.AddWithValue("@to", untilMinute);
            apps.ExecuteNonQuery();
        }

        EraseAppDetail(connection, transaction, fromMinute, untilMinute);
        EraseMachine(connection, transaction, fromMinute, untilMinute);
        TrimNotes(connection, transaction, fromMinute, untilMinute);
        RememberErased(connection, transaction, fromMinute, untilMinute);
    }

    /// <inheritdoc />
    public void BackupTo(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);

        string? directory = Path.GetDirectoryName(path);

        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        DeleteCopy(path);
        SqliteConnectionStringBuilder builder = new()
        {
            DataSource = path,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Pooling = false,
        };

        bool good = false;

        try
        {
            using SqliteConnection copy = new(builder.ConnectionString);
            copy.Open();

            // Under the gate, so no write, erase or VACUUM is part way through.
            lock (_gate)
            {
                Connection().BackupDatabase(copy);
            }

            // The copy carries the original's write-ahead mode in its header;
            // a backup is one file, so it is turned back.
            Execute(copy, "PRAGMA journal_mode=DELETE;");

            using SqliteCommand check = copy.CreateCommand();
            check.CommandText = "PRAGMA quick_check;";
            string? verdict = check.ExecuteScalar() as string;

            if (!string.Equals(verdict, "ok", StringComparison.OrdinalIgnoreCase))
            {
                throw new SqliteException("The backup copy did not pass SQLite's check: " + verdict, 11);
            }

            good = true;
        }
        finally
        {
            if (!good)
            {
                DeleteCopy(path);
            }
        }
    }

    private static void DeleteCopy(string path)
    {
        foreach (string suffix in new[] { "", "-wal", "-shm", "-journal" })
        {
            if (File.Exists(path + suffix))
            {
                File.Delete(path + suffix);
            }
        }
    }

    /// <inheritdoc />
    public long? ReadMark(string name)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);

        lock (_gate)
        {
            using SqliteCommand command = Connection().CreateCommand();
            command.CommandText = "SELECT value FROM app_state WHERE name = @name;";
            command.Parameters.AddWithValue("@name", MarkPrefix + name);
            object? found = command.ExecuteScalar();
            return found is null or DBNull ? null : Convert.ToInt64(found, CultureInfo.InvariantCulture);
        }
    }

    /// <inheritdoc />
    public void WriteMark(string name, long value)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);

        lock (_gate)
        {
            using SqliteCommand command = WritableConnection().CreateCommand();
            command.CommandText =
                """
                INSERT INTO app_state (name, value) VALUES (@name, @value)
                ON CONFLICT (name) DO UPDATE SET value = excluded.value;
                """;
            command.Parameters.AddWithValue("@name", MarkPrefix + name);
            command.Parameters.AddWithValue("@value", value);
            command.ExecuteNonQuery();
        }
    }

    /// <inheritdoc />
    public IReadOnlySet<int> ReadCrossedLevels(string planId, long cycleStartMinute)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(planId);
        HashSet<int> levels = [];

        lock (_gate)
        {
            using SqliteCommand command = Connection().CreateCommand();
            command.CommandText = "SELECT level FROM plan_level WHERE plan = @plan AND cycle = @cycle;";
            command.Parameters.AddWithValue("@plan", planId);
            command.Parameters.AddWithValue("@cycle", cycleStartMinute);
            using SqliteDataReader reader = command.ExecuteReader();

            while (reader.Read())
            {
                levels.Add(reader.GetInt32(0));
            }
        }

        return levels;
    }

    /// <inheritdoc />
    public bool MarkCrossed(string planId, long cycleStartMinute, int level, long atMinute)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(planId);

        lock (_gate)
        {
            using SqliteCommand command = WritableConnection().CreateCommand();
            command.CommandText =
                """
                INSERT INTO plan_level (plan, cycle, level, at) VALUES (@plan, @cycle, @level, @at)
                ON CONFLICT (plan, cycle, level) DO NOTHING;
                """;
            command.Parameters.AddWithValue("@plan", planId);
            command.Parameters.AddWithValue("@cycle", cycleStartMinute);
            command.Parameters.AddWithValue("@level", level);
            command.Parameters.AddWithValue("@at", atMinute);
            return command.ExecuteNonQuery() == 1;
        }
    }
}
