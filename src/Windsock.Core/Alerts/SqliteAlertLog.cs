using System.Data.Common;
using System.Globalization;
using Microsoft.Data.Sqlite;

namespace Windsock.Core.Alerts;

/// <summary>
/// The record of every alert raised, the events the charts mark, the networks
/// remembered for the change alerts, and the little state alerts keep between
/// runs.
/// </summary>
/// <remarks>
/// Every member may throw for a file that cannot be read or written; callers
/// catch what <see cref="History.HistoryFailure.IsStorage"/> recognises and carry
/// on without the record. <see cref="Changed"/> is raised on the writing thread.
/// </remarks>
public interface IAlertLog
{
    /// <summary>Raised after anything is written, marked or erased.</summary>
    event EventHandler? Changed;

    /// <summary>Keeps an alert or an event, and returns its row.</summary>
    long Add(AlertRecord record);

    /// <summary>Notes what became of alerts: shown in a summary, or not shown after all.</summary>
    void SetOutcome(IReadOnlyCollection<long> ids, AlertOutcome outcome, string detail, DateTimeOffset? shownAt);

    /// <summary>Notes whether an alert was forwarded.</summary>
    void SetForward(long id, ForwardOutcome outcome, string detail);

    /// <summary>The alerts in a stretch, newest first.</summary>
    IReadOnlyList<AlertRecord> Read(AlertQuery query);

    /// <summary>How many alerts a query would return, ignoring its paging.</summary>
    int Count(AlertQuery query);

    /// <summary>How many alerts the person has not seen in the list.</summary>
    int UnreadCount();

    /// <summary>The alerts held for quiet hours or a snooze, oldest first.</summary>
    IReadOnlyList<AlertRecord> Held();

    /// <summary>Marks alerts as seen.</summary>
    void MarkRead(IReadOnlyCollection<long> ids);

    /// <summary>Marks every alert as seen.</summary>
    void MarkAllRead();

    /// <summary>When an alert of a kind about a thing was last raised, or nothing; events do not count.</summary>
    DateTimeOffset? LastRaised(string topic, string? tag);

    /// <summary>
    /// When an alert of a kind about a thing was last told, as noted by
    /// <see cref="NoteTold"/>: kept apart from the list, so pruning or clearing
    /// the list never makes "drive wearing out" news again.
    /// </summary>
    DateTimeOffset? LastTold(string topic, string? tag);

    /// <summary>Notes that an alert of a kind about a thing was told now.</summary>
    void NoteTold(string topic, string? tag, DateTimeOffset at);

    /// <summary>Forgets what was told: everything deleted at once.</summary>
    int ForgetTold();

    /// <summary>
    /// Marks every alert still waiting to be forwarded as not forwarded, with a
    /// reason: those a run that ended was still sending.
    /// </summary>
    int AbandonForwards(string detail);

    /// <summary>Forgets the alerts and events in a stretch.</summary>
    int Forget(DateTimeOffset from, DateTimeOffset until);

    /// <summary>Forgets every alert and event.</summary>
    int Clear();

    /// <summary>Forgets alerts and events older than a moment.</summary>
    int Prune(DateTimeOffset before);

    /// <summary>How a network looked the last time, or nothing for one not seen.</summary>
    NetworkPrint? KnownPrint(string key);

    /// <summary>Remembers how a network looks now.</summary>
    void KeepPrint(NetworkPrint print);

    /// <summary>Forgets every network remembered.</summary>
    int ForgetPrints();

    /// <summary>A value kept between runs, or nothing.</summary>
    string? ReadState(string key);

    /// <summary>Keeps a value between runs; nothing removes it.</summary>
    void WriteState(string key, string? value);
}

/// <summary>Keeps the record of alerts in a small SQLite file of its own.</summary>
/// <remarks>
/// A separate file for the same reasons as the outage log: the history store
/// sets a damaged file aside and vacuums on erase, and neither is safe with a
/// second connection holding the file.
/// </remarks>
public sealed class SqliteAlertLog : IAlertLog, IDisposable
{
    private const int SchemaVersion = 2;
    private const int Corrupt = 11;
    private const int NotADatabase = 26;
    private const string HeldOutcome = nameof(AlertOutcome.Held);
    private const string EventOutcome = nameof(AlertOutcome.Event);

    private const string Columns =
        "id, at_ms, topic, tag, title, message, opens, outcome, detail, shown_ms, is_read, forward, forward_detail";

    private readonly Lock _gate = new();
    private readonly SqliteConnection? _connection;
    private readonly Exception? _openFailure;
    private bool _disposed;

    /// <summary>Opens, creating the file as needed. Never throws for a file it cannot open.</summary>
    public SqliteAlertLog(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);

        try
        {
            string? directory = Path.GetDirectoryName(path);

            if (!string.IsNullOrEmpty(directory))
            {
                Directory.CreateDirectory(directory);
            }

            try
            {
                _connection = Connect(path);
            }
            catch (SqliteException ex) when (ex.SqliteErrorCode is Corrupt or NotADatabase)
            {
                SetAside(path);
                _connection = Connect(path);
            }
        }
        catch (Exception ex) when (ex is DbException or IOException or UnauthorizedAccessException)
        {
            _openFailure = ex;
        }
    }

    /// <inheritdoc />
    public event EventHandler? Changed;

    /// <summary>Whether the file could be opened.</summary>
    public bool IsOpen => _connection is not null;

    /// <inheritdoc />
    public long Add(AlertRecord record)
    {
        ArgumentNullException.ThrowIfNull(record);

        long id;

        lock (_gate)
        {
            using SqliteCommand command = Connection().CreateCommand();
            command.CommandText =
                """
                INSERT INTO alert (at_ms, topic, tag, title, message, opens, outcome, detail, shown_ms, is_read, forward, forward_detail)
                VALUES (@at, @topic, @tag, @title, @message, @opens, @outcome, @detail, @shown, @read, @forward, @forwardDetail)
                RETURNING id;
                """;
            command.Parameters.AddWithValue("@at", record.At.ToUnixTimeMilliseconds());
            command.Parameters.AddWithValue("@topic", record.Topic);
            command.Parameters.AddWithValue("@tag", (object?)record.Tag ?? DBNull.Value);
            command.Parameters.AddWithValue("@title", record.Title);
            command.Parameters.AddWithValue("@message", record.Message);
            command.Parameters.AddWithValue("@opens", (object?)record.Opens ?? DBNull.Value);
            command.Parameters.AddWithValue("@outcome", record.Outcome.ToString());
            command.Parameters.AddWithValue("@detail", record.Detail);
            command.Parameters.AddWithValue("@shown", record.ShownAt is { } shown ? shown.ToUnixTimeMilliseconds() : DBNull.Value);
            command.Parameters.AddWithValue("@read", record.IsRead || !record.IsAlert ? 1 : 0);
            command.Parameters.AddWithValue("@forward", record.Forward.ToString());
            command.Parameters.AddWithValue("@forwardDetail", record.ForwardDetail);
            id = Convert.ToInt64(command.ExecuteScalar(), CultureInfo.InvariantCulture);
        }

        Changed?.Invoke(this, EventArgs.Empty);
        return id;
    }

    /// <inheritdoc />
    public void SetOutcome(IReadOnlyCollection<long> ids, AlertOutcome outcome, string detail, DateTimeOffset? shownAt)
    {
        ArgumentNullException.ThrowIfNull(ids);

        if (ids.Count == 0)
        {
            return;
        }

        lock (_gate)
        {
            SqliteConnection connection = Connection();
            using SqliteTransaction transaction = connection.BeginTransaction();
            using SqliteCommand command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = "UPDATE alert SET outcome = @outcome, detail = @detail, shown_ms = COALESCE(@shown, shown_ms) WHERE id = @id;";
            command.Parameters.AddWithValue("@outcome", outcome.ToString());
            command.Parameters.AddWithValue("@detail", detail ?? string.Empty);
            command.Parameters.AddWithValue("@shown", shownAt is { } shown ? shown.ToUnixTimeMilliseconds() : DBNull.Value);
            SqliteParameter id = command.Parameters.Add("@id", SqliteType.Integer);

            foreach (long each in ids)
            {
                id.Value = each;
                command.ExecuteNonQuery();
            }

            transaction.Commit();
        }

        Changed?.Invoke(this, EventArgs.Empty);
    }

    /// <inheritdoc />
    public void SetForward(long id, ForwardOutcome outcome, string detail)
    {
        lock (_gate)
        {
            using SqliteCommand command = Connection().CreateCommand();
            command.CommandText = "UPDATE alert SET forward = @forward, forward_detail = @detail WHERE id = @id;";
            command.Parameters.AddWithValue("@forward", outcome.ToString());
            command.Parameters.AddWithValue("@detail", detail ?? string.Empty);
            command.Parameters.AddWithValue("@id", id);
            command.ExecuteNonQuery();
        }

        Changed?.Invoke(this, EventArgs.Empty);
    }

    /// <inheritdoc />
    public IReadOnlyList<AlertRecord> Read(AlertQuery query)
    {
        ArgumentNullException.ThrowIfNull(query);

        lock (_gate)
        {
            using SqliteCommand command = Connection().CreateCommand();
            command.CommandText = string.Concat(
                "SELECT ", Columns, " FROM alert ", Where(command, query),
                " ORDER BY at_ms DESC, id DESC LIMIT @take OFFSET @skip;");
            command.Parameters.AddWithValue("@take", Math.Max(0, query.Take));
            command.Parameters.AddWithValue("@skip", Math.Max(0, query.Skip));

            return ReadAll(command);
        }
    }

    /// <inheritdoc />
    public int Count(AlertQuery query)
    {
        ArgumentNullException.ThrowIfNull(query);

        lock (_gate)
        {
            using SqliteCommand command = Connection().CreateCommand();
            command.CommandText = string.Concat("SELECT COUNT(*) FROM alert ", Where(command, query), ";");
            return Convert.ToInt32(command.ExecuteScalar(), CultureInfo.InvariantCulture);
        }
    }

    /// <inheritdoc />
    public int UnreadCount()
    {
        lock (_gate)
        {
            using SqliteCommand command = Connection().CreateCommand();
            command.CommandText = "SELECT COUNT(*) FROM alert WHERE is_read = 0 AND outcome <> @event;";
            command.Parameters.AddWithValue("@event", EventOutcome);
            return Convert.ToInt32(command.ExecuteScalar(), CultureInfo.InvariantCulture);
        }
    }

    /// <inheritdoc />
    public IReadOnlyList<AlertRecord> Held()
    {
        lock (_gate)
        {
            using SqliteCommand command = Connection().CreateCommand();
            command.CommandText = string.Concat("SELECT ", Columns, " FROM alert WHERE outcome = @held ORDER BY at_ms, id;");
            command.Parameters.AddWithValue("@held", HeldOutcome);
            return ReadAll(command);
        }
    }

    /// <inheritdoc />
    public void MarkRead(IReadOnlyCollection<long> ids)
    {
        ArgumentNullException.ThrowIfNull(ids);

        if (ids.Count == 0)
        {
            return;
        }

        lock (_gate)
        {
            SqliteConnection connection = Connection();
            using SqliteTransaction transaction = connection.BeginTransaction();
            using SqliteCommand command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = "UPDATE alert SET is_read = 1 WHERE id = @id;";
            SqliteParameter id = command.Parameters.Add("@id", SqliteType.Integer);

            foreach (long each in ids)
            {
                id.Value = each;
                command.ExecuteNonQuery();
            }

            transaction.Commit();
        }

        Changed?.Invoke(this, EventArgs.Empty);
    }

    /// <inheritdoc />
    public void MarkAllRead()
    {
        int marked;

        lock (_gate)
        {
            using SqliteCommand command = Connection().CreateCommand();
            command.CommandText = "UPDATE alert SET is_read = 1 WHERE is_read = 0;";
            marked = command.ExecuteNonQuery();
        }

        if (marked > 0)
        {
            Changed?.Invoke(this, EventArgs.Empty);
        }
    }

    /// <inheritdoc />
    public DateTimeOffset? LastRaised(string topic, string? tag)
    {
        ArgumentNullException.ThrowIfNull(topic);

        lock (_gate)
        {
            using SqliteCommand command = Connection().CreateCommand();
            command.CommandText =
                """
                SELECT MAX(at_ms) FROM alert
                WHERE topic = @topic AND COALESCE(tag, '') = @tag AND outcome <> @event;
                """;
            command.Parameters.AddWithValue("@topic", topic);
            command.Parameters.AddWithValue("@tag", tag ?? string.Empty);
            command.Parameters.AddWithValue("@event", EventOutcome);

            object? last = command.ExecuteScalar();

            return last is null or DBNull
                ? null
                : DateTimeOffset.FromUnixTimeMilliseconds(Convert.ToInt64(last, CultureInfo.InvariantCulture)).ToLocalTime();
        }
    }

    /// <inheritdoc />
    public DateTimeOffset? LastTold(string topic, string? tag)
    {
        ArgumentNullException.ThrowIfNull(topic);

        lock (_gate)
        {
            using SqliteCommand command = Connection().CreateCommand();
            command.CommandText = "SELECT at_ms FROM told WHERE topic = @topic AND tag = @tag;";
            command.Parameters.AddWithValue("@topic", topic);
            command.Parameters.AddWithValue("@tag", tag ?? string.Empty);

            object? last = command.ExecuteScalar();

            return last is null or DBNull
                ? null
                : DateTimeOffset.FromUnixTimeMilliseconds(Convert.ToInt64(last, CultureInfo.InvariantCulture)).ToLocalTime();
        }
    }

    /// <inheritdoc />
    public void NoteTold(string topic, string? tag, DateTimeOffset at)
    {
        ArgumentNullException.ThrowIfNull(topic);

        lock (_gate)
        {
            using SqliteCommand command = Connection().CreateCommand();
            command.CommandText =
                """
                INSERT INTO told (topic, tag, at_ms) VALUES (@topic, @tag, @at)
                ON CONFLICT (topic, tag) DO UPDATE SET at_ms = MAX(at_ms, excluded.at_ms);
                """;
            command.Parameters.AddWithValue("@topic", topic);
            command.Parameters.AddWithValue("@tag", tag ?? string.Empty);
            command.Parameters.AddWithValue("@at", at.ToUnixTimeMilliseconds());
            command.ExecuteNonQuery();
        }
    }

    /// <inheritdoc />
    public int ForgetTold()
    {
        lock (_gate)
        {
            using SqliteCommand command = Connection().CreateCommand();
            command.CommandText = "DELETE FROM told;";
            return command.ExecuteNonQuery();
        }
    }

    /// <inheritdoc />
    public int AbandonForwards(string detail)
    {
        int marked;

        lock (_gate)
        {
            using SqliteCommand command = Connection().CreateCommand();
            command.CommandText = "UPDATE alert SET forward = @failed, forward_detail = @detail WHERE forward = @waiting;";
            command.Parameters.AddWithValue("@failed", nameof(ForwardOutcome.Failed));
            command.Parameters.AddWithValue("@waiting", nameof(ForwardOutcome.Waiting));
            command.Parameters.AddWithValue("@detail", detail ?? string.Empty);
            marked = command.ExecuteNonQuery();
        }

        if (marked > 0)
        {
            Changed?.Invoke(this, EventArgs.Empty);
        }

        return marked;
    }

    /// <inheritdoc />
    public int Forget(DateTimeOffset from, DateTimeOffset until)
    {
        int gone;

        lock (_gate)
        {
            using SqliteCommand command = Connection().CreateCommand();
            command.CommandText = "DELETE FROM alert WHERE at_ms >= @from AND at_ms < @until;";
            command.Parameters.AddWithValue("@from", from.ToUnixTimeMilliseconds());
            command.Parameters.AddWithValue("@until", until.ToUnixTimeMilliseconds());
            gone = command.ExecuteNonQuery();
        }

        Changed?.Invoke(this, EventArgs.Empty);
        return gone;
    }

    /// <inheritdoc />
    public int Clear()
    {
        int gone;

        lock (_gate)
        {
            using SqliteCommand command = Connection().CreateCommand();
            command.CommandText = "DELETE FROM alert;";
            gone = command.ExecuteNonQuery();
        }

        Changed?.Invoke(this, EventArgs.Empty);
        return gone;
    }

    /// <inheritdoc />
    public int Prune(DateTimeOffset before)
    {
        int gone;

        lock (_gate)
        {
            using SqliteCommand command = Connection().CreateCommand();

            // A held alert is kept until it is shown, however old: deferred, not lost.
            command.CommandText = "DELETE FROM alert WHERE at_ms < @before AND outcome <> @held;";
            command.Parameters.AddWithValue("@before", before.ToUnixTimeMilliseconds());
            command.Parameters.AddWithValue("@held", HeldOutcome);
            gone = command.ExecuteNonQuery();
        }

        if (gone > 0)
        {
            Changed?.Invoke(this, EventArgs.Empty);
        }

        return gone;
    }

    /// <inheritdoc />
    public NetworkPrint? KnownPrint(string key)
    {
        ArgumentNullException.ThrowIfNull(key);

        lock (_gate)
        {
            using SqliteCommand command = Connection().CreateCommand();
            command.CommandText = "SELECT key, name, address, dns, gateway, router_mac, seen_ms FROM network_print WHERE key = @key;";
            command.Parameters.AddWithValue("@key", key);

            using SqliteDataReader reader = command.ExecuteReader();

            if (!reader.Read())
            {
                return null;
            }

            string dns = reader.GetString(3);

            return new NetworkPrint(
                reader.GetString(0),
                reader.GetString(1),
                reader.IsDBNull(2) ? null : reader.GetString(2),
                dns.Length == 0 ? [] : dns.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries),
                reader.IsDBNull(4) ? null : reader.GetString(4),
                reader.IsDBNull(5) ? null : reader.GetString(5))
            {
                Seen = DateTimeOffset.FromUnixTimeMilliseconds(reader.GetInt64(6)).ToLocalTime(),
            };
        }
    }

    /// <inheritdoc />
    public void KeepPrint(NetworkPrint print)
    {
        ArgumentNullException.ThrowIfNull(print);

        lock (_gate)
        {
            using SqliteCommand command = Connection().CreateCommand();
            command.CommandText =
                """
                INSERT INTO network_print (key, name, address, dns, gateway, router_mac, seen_ms)
                VALUES (@key, @name, @address, @dns, @gateway, @mac, @seen)
                ON CONFLICT (key) DO UPDATE SET
                    name = excluded.name, address = excluded.address, dns = excluded.dns,
                    gateway = excluded.gateway, router_mac = excluded.router_mac, seen_ms = excluded.seen_ms;
                """;
            command.Parameters.AddWithValue("@key", print.Key);
            command.Parameters.AddWithValue("@name", print.Name);
            command.Parameters.AddWithValue("@address", (object?)print.Address ?? DBNull.Value);
            command.Parameters.AddWithValue("@dns", string.Join(',', print.Dns));
            command.Parameters.AddWithValue("@gateway", (object?)print.Gateway ?? DBNull.Value);
            command.Parameters.AddWithValue("@mac", (object?)print.RouterMac ?? DBNull.Value);
            command.Parameters.AddWithValue("@seen", print.Seen.ToUnixTimeMilliseconds());
            command.ExecuteNonQuery();
        }
    }

    /// <inheritdoc />
    public int ForgetPrints()
    {
        lock (_gate)
        {
            using SqliteCommand command = Connection().CreateCommand();
            command.CommandText = "DELETE FROM network_print;";
            return command.ExecuteNonQuery();
        }
    }

    /// <inheritdoc />
    public string? ReadState(string key)
    {
        ArgumentNullException.ThrowIfNull(key);

        lock (_gate)
        {
            using SqliteCommand command = Connection().CreateCommand();
            command.CommandText = "SELECT value FROM state WHERE key = @key;";
            command.Parameters.AddWithValue("@key", key);
            return command.ExecuteScalar() as string;
        }
    }

    /// <inheritdoc />
    public void WriteState(string key, string? value)
    {
        ArgumentNullException.ThrowIfNull(key);

        lock (_gate)
        {
            using SqliteCommand command = Connection().CreateCommand();

            if (value is null)
            {
                command.CommandText = "DELETE FROM state WHERE key = @key;";
            }
            else
            {
                command.CommandText = "INSERT INTO state (key, value) VALUES (@key, @value) ON CONFLICT (key) DO UPDATE SET value = excluded.value;";
                command.Parameters.AddWithValue("@value", value);
            }

            command.Parameters.AddWithValue("@key", key);
            command.ExecuteNonQuery();
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

    private static string Where(SqliteCommand command, AlertQuery query)
    {
        List<string> conditions = ["at_ms >= @from", "at_ms < @until"];
        command.Parameters.AddWithValue("@from", query.From.ToUnixTimeMilliseconds());
        command.Parameters.AddWithValue("@until", query.Until.ToUnixTimeMilliseconds());

        if (!query.IncludeEvents)
        {
            conditions.Add("outcome <> @event");
            command.Parameters.AddWithValue("@event", EventOutcome);
        }

        if (query.OnlyUnread)
        {
            conditions.Add("is_read = 0");
        }

        if (query.Topics.Count > 0)
        {
            List<string> names = [];

            for (int index = 0; index < query.Topics.Count; index++)
            {
                string name = string.Create(CultureInfo.InvariantCulture, $"@topic{index}");
                names.Add(name);
                command.Parameters.AddWithValue(name, query.Topics[index]);
            }

            conditions.Add(string.Concat("topic IN (", string.Join(", ", names), ")"));
        }

        return string.Concat("WHERE ", string.Join(" AND ", conditions));
    }

    private static List<AlertRecord> ReadAll(SqliteCommand command)
    {
        List<AlertRecord> records = [];
        using SqliteDataReader reader = command.ExecuteReader();

        while (reader.Read())
        {
            records.Add(new AlertRecord(
                reader.GetInt64(0),
                DateTimeOffset.FromUnixTimeMilliseconds(reader.GetInt64(1)).ToLocalTime(),
                reader.GetString(2),
                reader.GetString(4),
                reader.GetString(5))
            {
                Tag = reader.IsDBNull(3) ? null : reader.GetString(3),
                Opens = reader.IsDBNull(6) ? null : reader.GetString(6),
                Outcome = Enum.TryParse(reader.GetString(7), out AlertOutcome outcome) ? outcome : AlertOutcome.Shown,
                Detail = reader.GetString(8),
                ShownAt = reader.IsDBNull(9) ? null : DateTimeOffset.FromUnixTimeMilliseconds(reader.GetInt64(9)).ToLocalTime(),
                IsRead = reader.GetInt64(10) != 0,
                Forward = Enum.TryParse(reader.GetString(11), out ForwardOutcome forward) ? forward : ForwardOutcome.None,
                ForwardDetail = reader.GetString(12),
            });
        }

        return records;
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
            Migrate(connection);
            return connection;
        }
        catch
        {
            connection.Dispose();
            throw;
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

        Execute(
            connection,
            """
            CREATE TABLE IF NOT EXISTS alert (
                id              INTEGER PRIMARY KEY AUTOINCREMENT,
                at_ms           INTEGER NOT NULL,
                topic           TEXT    NOT NULL,
                tag             TEXT,
                title           TEXT    NOT NULL,
                message         TEXT    NOT NULL,
                opens           TEXT,
                outcome         TEXT    NOT NULL,
                detail          TEXT    NOT NULL DEFAULT '',
                shown_ms        INTEGER,
                is_read         INTEGER NOT NULL DEFAULT 0,
                forward         TEXT    NOT NULL DEFAULT 'None',
                forward_detail  TEXT    NOT NULL DEFAULT ''
            );

            CREATE INDEX IF NOT EXISTS alert_at ON alert (at_ms);
            CREATE INDEX IF NOT EXISTS alert_topic ON alert (topic, tag, at_ms);
            CREATE INDEX IF NOT EXISTS alert_held ON alert (outcome) WHERE outcome = 'Held';

            CREATE TABLE IF NOT EXISTS network_print (
                key         TEXT    PRIMARY KEY,
                name        TEXT    NOT NULL,
                address     TEXT,
                dns         TEXT    NOT NULL,
                gateway     TEXT,
                router_mac  TEXT,
                seen_ms     INTEGER NOT NULL
            );

            CREATE TABLE IF NOT EXISTS state (
                key     TEXT PRIMARY KEY,
                value   TEXT NOT NULL
            );

            CREATE TABLE IF NOT EXISTS told (
                topic   TEXT    NOT NULL,
                tag     TEXT    NOT NULL,
                at_ms   INTEGER NOT NULL,
                PRIMARY KEY (topic, tag)
            );

            INSERT OR IGNORE INTO told (topic, tag, at_ms)
            SELECT topic, COALESCE(tag, ''), MAX(at_ms) FROM alert
            WHERE outcome <> 'Event'
            GROUP BY topic, COALESCE(tag, '');
            """);

        Execute(connection, $"PRAGMA user_version={SchemaVersion};");
    }

    private static void SetAside(string path)
    {
        string stamp = DateTime.UtcNow.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture);

        try
        {
            foreach (string suffix in new[] { "", "-wal", "-shm" })
            {
                if (File.Exists(path + suffix))
                {
                    File.Move(path + suffix, $"{path}.{stamp}.corrupt{suffix}", overwrite: true);
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
    }

    private static void Execute(SqliteConnection connection, string sql)
    {
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = sql;
        command.ExecuteNonQuery();
    }

    private SqliteConnection Connection()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        return _connection
            ?? throw new InvalidOperationException("The record of alerts could not be opened.", _openFailure);
    }
}
