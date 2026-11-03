using System.Data.Common;
using System.Globalization;
using System.Text;
using Microsoft.Data.Sqlite;
using Windsock.Core.Insight;
using Windsock.Core.Networking;

namespace Windsock.Core.Timeline;

/// <summary>Keeps the connection timeline, each day's places and each service's days.</summary>
/// <remarks>
/// Every member may throw for a file that cannot be read or written; callers
/// catch what <see cref="History.HistoryFailure.IsStorage"/> recognises and carry on
/// without the timeline.
/// </remarks>
public interface ITimelineStore : IServiceTrafficStore
{
    /// <summary>
    /// Keeps flows, joining each to the conversation it continues - the same
    /// app, address and port with no gap over five minutes - or starting a new
    /// one, and adds them to their days' places.
    /// </summary>
    void Record(IReadOnlyList<TimelineFlow> flows);

    /// <summary>Adds service hosts' bytes to their days.</summary>
    void AddServiceDays(IReadOnlyList<ServiceDayBytes> days);

    /// <summary>The conversations matching a query, the latest first.</summary>
    IReadOnlyList<TimelineEntry> Search(TimelineQuery query);

    /// <summary>How many conversations match a query, ignoring its paging.</summary>
    int Count(TimelineQuery query);

    /// <summary>Each day's places in a span of local days, the busiest first within a day.</summary>
    IReadOnlyList<PlaceDay> ReadPlaces(DateOnly from, DateOnly until);

    /// <summary>Forgets conversations last active before a moment, and days of places before its day.</summary>
    int Prune(DateTimeOffset before);

    /// <summary>Forgets everything in a period, as erasing history does.</summary>
    int Delete(DateTimeOffset from, DateTimeOffset until);

    /// <summary>Forgets everything.</summary>
    int RemoveEverything();

    /// <summary>
    /// Forgets every conversation and every day's places, keeping only the
    /// service hosts' daily totals, which name no place: what switching the
    /// timeline off offers to delete.
    /// </summary>
    int ForgetConversations();

    /// <summary>Raised after anything is kept, pruned or erased.</summary>
    event EventHandler? Changed;
}

/// <summary>Keeps the timeline in a SQLite file of its own, apart from usage history.</summary>
/// <remarks>
/// <para>
/// A separate file on purpose, like the outage log: it is the full edition's
/// alone, it holds what is closest to browsing history in anything Windsock
/// keeps, and it is pruned by its own retention. Erasing history erases it too.
/// </para>
/// <para>
/// Days are local days, as <c>yyyymmdd</c> numbers, fixed when the bytes are
/// kept. Times are Unix milliseconds.
/// </para>
/// </remarks>
public sealed class SqliteTimelineStore : ITimelineStore, IErasedWithHistory, IDisposable
{
    private const int SchemaVersion = 1;
    private const int Corrupt = 11;
    private const int NotADatabase = 26;

    /// <summary>The longest pause that still continues a conversation.</summary>
    internal static readonly TimeSpan Continues = TimeSpan.FromMinutes(5);

    /// <summary>How long service days are kept, whatever the timeline's retention: they name no place.</summary>
    internal static readonly TimeSpan ServiceDaysKept = TimeSpan.FromDays(400);

    /// <summary>
    /// The most conversations kept, whatever the retention: past it the
    /// oldest go first, so a torrent client cannot grow the file without end
    /// or make every search slower.
    /// </summary>
    internal const int MostConversations = 250_000;

    /// <summary>How many new conversations may be kept before the budget is checked again.</summary>
    private const int TrimEvery = 5_000;

    private readonly int _mostConversations;
    private int _addedSinceTrim;

    private readonly Lock _gate = new();
    private readonly string _path;
    private SqliteConnection? _connection;
    private Exception? _openFailure;
    private readonly Dictionary<(long App, string? Address, int Port), (long Row, long LastMs)> _open = [];
    private readonly Dictionary<string, long> _apps = new(StringComparer.OrdinalIgnoreCase);
    private bool _disposed;

    /// <summary>
    /// A store kept in a file, opened - and created - the first time anything
    /// is kept or asked for, so a PC that never runs the trace never has one.
    /// </summary>
    public SqliteTimelineStore(string path)
        : this(path, MostConversations)
    {
    }

    internal SqliteTimelineStore(string path, int mostConversations)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        ArgumentOutOfRangeException.ThrowIfLessThan(mostConversations, 1);
        _path = path;
        _mostConversations = mostConversations;
    }

    /// <summary>Whether the file exists. Reading, pruning and erasing never create it; only keeping does.</summary>
    public bool Exists
    {
        get
        {
            lock (_gate)
            {
                return _connection is not null || File.Exists(_path);
            }
        }
    }

    /// <summary>Opens the file, setting a damaged one aside. Call with the gate held; never throws.</summary>
    private void Open()
    {
        try
        {
            string? directory = Path.GetDirectoryName(_path);

            if (!string.IsNullOrEmpty(directory))
            {
                Directory.CreateDirectory(directory);
            }

            try
            {
                _connection = Connect(_path);
            }
            catch (SqliteException ex) when (ex.SqliteErrorCode is Corrupt or NotADatabase)
            {
                SetAside(_path);
                _connection = Connect(_path);
            }
        }
        catch (Exception ex) when (ex is DbException or IOException or UnauthorizedAccessException)
        {
            _openFailure = ex;
        }
    }

    /// <inheritdoc />
    public event EventHandler? Changed;

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

            // Before the first table: space freed by pruning goes back to the
            // disk a little at a time rather than needing a VACUUM.
            Execute(connection, "PRAGMA auto_vacuum=INCREMENTAL;");

            // A deleted row is overwritten, not just unlinked, so an erase or a
            // prune leaves nothing for a file viewer to find.
            Execute(connection, "PRAGMA secure_delete=ON;");
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
            CREATE TABLE IF NOT EXISTS app (
                id          INTEGER PRIMARY KEY,
                attribution TEXT    NOT NULL UNIQUE COLLATE NOCASE,
                name        TEXT
            );

            CREATE TABLE IF NOT EXISTS flow (
                id           INTEGER PRIMARY KEY,
                app          INTEGER NOT NULL REFERENCES app(id),
                address      TEXT,
                port         INTEGER NOT NULL,
                host         TEXT,
                country      TEXT,
                organisation TEXT,
                local        INTEGER NOT NULL,
                first_ms     INTEGER NOT NULL,
                last_ms      INTEGER NOT NULL,
                received     INTEGER NOT NULL,
                sent         INTEGER NOT NULL,
                packets      INTEGER NOT NULL
            );

            CREATE INDEX IF NOT EXISTS flow_last ON flow (last_ms);
            CREATE INDEX IF NOT EXISTS flow_app ON flow (app, last_ms);
            CREATE INDEX IF NOT EXISTS flow_address ON flow (address);

            CREATE TABLE IF NOT EXISTS place_day (
                day           INTEGER NOT NULL,
                country       TEXT    NOT NULL,
                site          TEXT    NOT NULL,
                received      INTEGER NOT NULL,
                sent          INTEGER NOT NULL,
                conversations INTEGER NOT NULL,
                PRIMARY KEY (day, country, site)
            ) WITHOUT ROWID;

            CREATE TABLE IF NOT EXISTS service_day (
                day      INTEGER NOT NULL,
                service  TEXT    NOT NULL,
                received INTEGER NOT NULL,
                sent     INTEGER NOT NULL,
                PRIMARY KEY (day, service)
            ) WITHOUT ROWID;
            """);

        Execute(connection, $"PRAGMA user_version={SchemaVersion};");
    }

    /// <inheritdoc />
    public void Record(IReadOnlyList<TimelineFlow> flows)
    {
        ArgumentNullException.ThrowIfNull(flows);

        if (flows.Count == 0)
        {
            return;
        }

        lock (_gate)
        {
            SqliteConnection connection = Connection();
            using SqliteTransaction transaction = connection.BeginTransaction();

            using SqliteCommand extend = connection.CreateCommand();
            extend.Transaction = transaction;
            extend.CommandText =
                """
                UPDATE flow SET
                    last_ms = MAX(last_ms, @last),
                    received = received + @received,
                    sent = sent + @sent,
                    packets = packets + @packets,
                    host = COALESCE(host, @host),
                    country = COALESCE(country, @country),
                    organisation = COALESCE(organisation, @organisation)
                WHERE id = @id;
                """;

            using SqliteCommand insert = connection.CreateCommand();
            insert.Transaction = transaction;
            insert.CommandText =
                """
                INSERT INTO flow (app, address, port, host, country, organisation, local, first_ms, last_ms, received, sent, packets)
                VALUES (@app, @address, @port, @host, @country, @organisation, @local, @first, @last, @received, @sent, @packets)
                RETURNING id;
                """;

            using SqliteCommand place = connection.CreateCommand();
            place.Transaction = transaction;
            place.CommandText =
                """
                INSERT INTO place_day (day, country, site, received, sent, conversations)
                VALUES (@day, @country, @site, @received, @sent, @new)
                ON CONFLICT (day, country, site) DO UPDATE SET
                    received = received + excluded.received,
                    sent = sent + excluded.sent,
                    conversations = conversations + excluded.conversations;
                """;

            foreach (TimelineFlow flow in flows)
            {
                long app = AppRow(connection, transaction, flow.AttributionId, flow.AppName);
                long firstMs = flow.First.ToUnixTimeMilliseconds();
                long lastMs = Math.Max(firstMs, flow.Last.ToUnixTimeMilliseconds());
                var key = (app, flow.Address, flow.Port);
                bool isNew;

                if (_open.TryGetValue(key, out (long Row, long LastMs) open)
                    && firstMs - open.LastMs <= Continues.TotalMilliseconds
                    && firstMs >= open.LastMs - Continues.TotalMilliseconds)
                {
                    Bind(extend, flow, lastMs);
                    extend.Parameters.AddWithValue("@id", open.Row);
                    extend.ExecuteNonQuery();
                    extend.Parameters.Clear();
                    _open[key] = (open.Row, Math.Max(open.LastMs, lastMs));
                    isNew = false;
                }
                else
                {
                    Bind(insert, flow, lastMs);
                    insert.Parameters.AddWithValue("@app", app);
                    insert.Parameters.AddWithValue("@address", (object?)flow.Address ?? DBNull.Value);
                    insert.Parameters.AddWithValue("@port", flow.Port);
                    insert.Parameters.AddWithValue("@local", flow.IsLocal ? 1 : 0);
                    insert.Parameters.AddWithValue("@first", firstMs);
                    long row = Convert.ToInt64(insert.ExecuteScalar(), CultureInfo.InvariantCulture);
                    insert.Parameters.Clear();
                    _open[key] = (row, lastMs);
                    _addedSinceTrim++;
                    isNew = true;
                }

                if (flow.Address is not null)
                {
                    place.Parameters.AddWithValue("@day", DayNumber(DateOnly.FromDateTime(flow.Last.ToLocalTime().DateTime)));
                    place.Parameters.AddWithValue("@country", flow.Country ?? string.Empty);
                    place.Parameters.AddWithValue("@site", SiteOf(flow));
                    place.Parameters.AddWithValue("@received", flow.Received);
                    place.Parameters.AddWithValue("@sent", flow.Sent);
                    place.Parameters.AddWithValue("@new", isNew ? 1 : 0);
                    place.ExecuteNonQuery();
                    place.Parameters.Clear();
                }
            }

            transaction.Commit();
            ForgetClosed();

            if (_addedSinceTrim >= TrimEvery && Trim(connection) is > 0 and int trimmed)
            {
                Forget();
                Settle(connection, trimmed);
            }
        }

        Changed?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>What a flow counts as among a day's places: its host's site, else who runs the network, else the address.</summary>
    internal static string SiteOf(TimelineFlow flow)
    {
        if (!string.IsNullOrWhiteSpace(flow.Host))
        {
            return DomainName.Group(flow.Host);
        }

        return !string.IsNullOrWhiteSpace(flow.Organisation) ? flow.Organisation : flow.Address ?? string.Empty;
    }

    private static void Bind(SqliteCommand command, TimelineFlow flow, long lastMs)
    {
        command.Parameters.AddWithValue("@last", lastMs);
        command.Parameters.AddWithValue("@received", flow.Received);
        command.Parameters.AddWithValue("@sent", flow.Sent);
        command.Parameters.AddWithValue("@packets", flow.Packets);
        command.Parameters.AddWithValue("@host", (object?)flow.Host ?? DBNull.Value);
        command.Parameters.AddWithValue("@country", (object?)flow.Country ?? DBNull.Value);
        command.Parameters.AddWithValue("@organisation", (object?)flow.Organisation ?? DBNull.Value);
    }

    /// <summary>Lets go of conversations idle long enough that nothing can continue them.</summary>
    private void ForgetClosed()
    {
        if (_open.Count < 4096)
        {
            return;
        }

        long newest = _open.Values.Max(each => each.LastMs);
        long cutoff = newest - (long)(2 * Continues.TotalMilliseconds);

        foreach (var key in _open.Where(each => each.Value.LastMs < cutoff).Select(each => each.Key).ToList())
        {
            _open.Remove(key);
        }
    }

    private long AppRow(SqliteConnection connection, SqliteTransaction transaction, string attribution, string? name)
    {
        if (_apps.TryGetValue(attribution, out long known))
        {
            return known;
        }

        using SqliteCommand command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText =
            """
            INSERT INTO app (attribution, name) VALUES (@attribution, @name)
            ON CONFLICT (attribution) DO UPDATE SET name = COALESCE(excluded.name, name)
            RETURNING id;
            """;
        command.Parameters.AddWithValue("@attribution", attribution);
        command.Parameters.AddWithValue("@name", (object?)name ?? DBNull.Value);

        long id = Convert.ToInt64(command.ExecuteScalar(), CultureInfo.InvariantCulture);
        _apps[attribution] = id;
        return id;
    }

    /// <inheritdoc />
    public void AddServiceDays(IReadOnlyList<ServiceDayBytes> days)
    {
        ArgumentNullException.ThrowIfNull(days);

        if (days.Count == 0)
        {
            return;
        }

        lock (_gate)
        {
            SqliteConnection connection = Connection();
            using SqliteTransaction transaction = connection.BeginTransaction();
            using SqliteCommand command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText =
                """
                INSERT INTO service_day (day, service, received, sent) VALUES (@day, @service, @received, @sent)
                ON CONFLICT (day, service) DO UPDATE SET
                    received = received + excluded.received,
                    sent = sent + excluded.sent;
                """;

            foreach (ServiceDayBytes day in days)
            {
                if (day.Received <= 0 && day.Sent <= 0)
                {
                    continue;
                }

                command.Parameters.AddWithValue("@day", DayNumber(day.Day));
                command.Parameters.AddWithValue("@service", day.ServiceKey);
                command.Parameters.AddWithValue("@received", Math.Max(0, day.Received));
                command.Parameters.AddWithValue("@sent", Math.Max(0, day.Sent));
                command.ExecuteNonQuery();
                command.Parameters.Clear();
            }

            transaction.Commit();
        }
    }

    /// <inheritdoc />
    public IReadOnlyList<ServiceBytes> ReadServiceTotals(DateTimeOffset from, DateTimeOffset until)
    {
        List<ServiceBytes> totals = [];

        lock (_gate)
        {
            if (Existing() is not { } connection)
            {
                return totals;
            }

            using SqliteCommand command = connection.CreateCommand();
            command.CommandText =
                """
                SELECT service, SUM(received), SUM(sent) FROM service_day
                WHERE day >= @from AND day <= @until
                GROUP BY service
                ORDER BY SUM(received) + SUM(sent) DESC;
                """;
            command.Parameters.AddWithValue("@from", DayNumber(DateOnly.FromDateTime(from.ToLocalTime().DateTime)));
            command.Parameters.AddWithValue("@until", DayNumber(DateOnly.FromDateTime(until.ToLocalTime().DateTime)));

            using SqliteDataReader reader = command.ExecuteReader();

            while (reader.Read())
            {
                totals.Add(new ServiceBytes(reader.GetString(0), reader.GetInt64(1), reader.GetInt64(2)));
            }
        }

        return totals;
    }

    /// <inheritdoc />
    public IReadOnlyList<TimelineEntry> Search(TimelineQuery query)
    {
        ArgumentNullException.ThrowIfNull(query);

        List<TimelineEntry> entries = [];

        lock (_gate)
        {
            if (Existing() is not { } connection)
            {
                return entries;
            }

            using SqliteCommand command = connection.CreateCommand();
            command.CommandText = string.Concat(
                """
                SELECT flow.id, app.attribution, app.name, flow.address, flow.port, flow.host, flow.country,
                       flow.organisation, flow.local, flow.first_ms, flow.last_ms, flow.received, flow.sent
                FROM flow JOIN app ON app.id = flow.app
                """,
                Where(command, query),
                " ORDER BY flow.last_ms DESC, flow.id DESC LIMIT @limit OFFSET @offset;");
            command.Parameters.AddWithValue("@limit", Math.Clamp(query.Limit, 1, 10_000));
            command.Parameters.AddWithValue("@offset", Math.Max(0, query.Offset));

            using SqliteDataReader reader = command.ExecuteReader();

            while (reader.Read())
            {
                entries.Add(new TimelineEntry(
                    reader.GetInt64(0),
                    reader.GetString(1),
                    reader.IsDBNull(2) ? null : reader.GetString(2),
                    reader.IsDBNull(3) ? null : reader.GetString(3),
                    reader.GetInt32(4),
                    reader.IsDBNull(5) ? null : reader.GetString(5),
                    reader.IsDBNull(6) ? null : reader.GetString(6),
                    reader.IsDBNull(7) ? null : reader.GetString(7),
                    reader.GetInt64(8) != 0,
                    DateTimeOffset.FromUnixTimeMilliseconds(reader.GetInt64(9)).ToLocalTime(),
                    DateTimeOffset.FromUnixTimeMilliseconds(reader.GetInt64(10)).ToLocalTime(),
                    reader.GetInt64(11),
                    reader.GetInt64(12)));
            }
        }

        return entries;
    }

    /// <inheritdoc />
    public int Count(TimelineQuery query)
    {
        ArgumentNullException.ThrowIfNull(query);

        lock (_gate)
        {
            if (Existing() is not { } connection)
            {
                return 0;
            }

            using SqliteCommand command = connection.CreateCommand();
            command.CommandText = string.Concat("SELECT COUNT(*) FROM flow JOIN app ON app.id = flow.app", Where(command, query), ";");
            return Convert.ToInt32(command.ExecuteScalar(), CultureInfo.InvariantCulture);
        }
    }

    /// <summary>The WHERE clause for a query, its parameters added to the command.</summary>
    private static string Where(SqliteCommand command, TimelineQuery query)
    {
        StringBuilder where = new(" WHERE flow.last_ms >= @from AND flow.first_ms < @until");
        command.Parameters.AddWithValue("@from", query.From.ToUnixTimeMilliseconds());
        command.Parameters.AddWithValue("@until", query.Until.ToUnixTimeMilliseconds());

        if (!string.IsNullOrWhiteSpace(query.AttributionId))
        {
            where.Append(" AND app.attribution = @attribution");
            command.Parameters.AddWithValue("@attribution", query.AttributionId);
        }

        if (!string.IsNullOrWhiteSpace(query.Text))
        {
            where.Append(
                """
                 AND (app.attribution LIKE @text ESCAPE '\' OR app.name LIKE @text ESCAPE '\'
                      OR flow.address LIKE @text ESCAPE '\' OR flow.host LIKE @text ESCAPE '\'
                      OR flow.country LIKE @text ESCAPE '\' OR flow.organisation LIKE @text ESCAPE '\')
                """);
            command.Parameters.AddWithValue("@text", string.Concat("%", Escape(query.Text.Trim()), "%"));
        }

        return where.ToString();
    }

    private static string Escape(string text) =>
        text.Replace("\\", "\\\\", StringComparison.Ordinal)
            .Replace("%", "\\%", StringComparison.Ordinal)
            .Replace("_", "\\_", StringComparison.Ordinal);

    /// <inheritdoc />
    public IReadOnlyList<PlaceDay> ReadPlaces(DateOnly from, DateOnly until)
    {
        List<PlaceDay> places = [];

        lock (_gate)
        {
            if (Existing() is not { } connection)
            {
                return places;
            }

            using SqliteCommand command = connection.CreateCommand();
            command.CommandText =
                """
                SELECT day, country, site, received, sent, conversations FROM place_day
                WHERE day >= @from AND day <= @until
                ORDER BY day DESC, received + sent DESC;
                """;
            command.Parameters.AddWithValue("@from", DayNumber(from));
            command.Parameters.AddWithValue("@until", DayNumber(until));

            using SqliteDataReader reader = command.ExecuteReader();

            while (reader.Read())
            {
                places.Add(new PlaceDay(
                    FromDayNumber(reader.GetInt32(0)),
                    reader.GetString(1),
                    reader.GetString(2),
                    reader.GetInt64(3),
                    reader.GetInt64(4),
                    reader.GetInt64(5)));
            }
        }

        return places;
    }

    /// <inheritdoc />
    public int Prune(DateTimeOffset before)
    {
        int gone;

        lock (_gate)
        {
            if (Existing() is not { } connection)
            {
                return 0;
            }

            using SqliteTransaction transaction = connection.BeginTransaction();
            using SqliteCommand command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText =
                """
                DELETE FROM flow WHERE last_ms < @before;
                DELETE FROM place_day WHERE day < @day;
                DELETE FROM service_day WHERE day < @serviceDay;
                DELETE FROM app WHERE id NOT IN (SELECT DISTINCT app FROM flow);
                """;
            command.Parameters.AddWithValue("@before", before.ToUnixTimeMilliseconds());
            command.Parameters.AddWithValue("@day", DayNumber(DateOnly.FromDateTime(before.ToLocalTime().DateTime)));
            command.Parameters.AddWithValue("@serviceDay", DayNumber(DateOnly.FromDateTime((before - ServiceDaysKept).ToLocalTime().DateTime)));
            gone = command.ExecuteNonQuery();
            transaction.Commit();

            gone += Trim(connection);
            Forget();
            Settle(connection, gone);
        }

        if (gone > 0)
        {
            Changed?.Invoke(this, EventArgs.Empty);
        }

        return gone;
    }

    /// <inheritdoc />
    public int Delete(DateTimeOffset from, DateTimeOffset until)
    {
        int gone;

        lock (_gate)
        {
            if (Existing() is not { } connection)
            {
                return 0;
            }

            using SqliteTransaction transaction = connection.BeginTransaction();
            using SqliteCommand command = connection.CreateCommand();
            command.Transaction = transaction;

            // A conversation that touches the period goes whole: it cannot be
            // cut in two, and what it moved in the period is in its totals.
            command.CommandText =
                """
                DELETE FROM flow WHERE last_ms >= @from AND first_ms < @until;
                DELETE FROM place_day WHERE day >= @fromDay AND day <= @untilDay;
                DELETE FROM service_day WHERE day >= @fromDay AND day <= @untilDay;
                DELETE FROM app WHERE id NOT IN (SELECT DISTINCT app FROM flow);
                """;
            command.Parameters.AddWithValue("@from", from.ToUnixTimeMilliseconds());
            command.Parameters.AddWithValue("@until", until.ToUnixTimeMilliseconds());
            command.Parameters.AddWithValue("@fromDay", DayNumber(DateOnly.FromDateTime(from.ToLocalTime().DateTime)));
            command.Parameters.AddWithValue("@untilDay", DayNumber(DateOnly.FromDateTime(until.AddTicks(-1).ToLocalTime().DateTime)));
            gone = command.ExecuteNonQuery();
            transaction.Commit();

            Forget();
            Settle(connection, gone);
        }

        Changed?.Invoke(this, EventArgs.Empty);
        return gone;
    }

    /// <inheritdoc />
    public int RemoveEverything()
    {
        return Clear(
            """
            DELETE FROM flow;
            DELETE FROM place_day;
            DELETE FROM service_day;
            DELETE FROM app;
            """);
    }

    /// <inheritdoc />
    public int ForgetConversations() => Clear(
        """
        DELETE FROM flow;
        DELETE FROM place_day;
        DELETE FROM app;
        """);

    private int Clear(string sql)
    {
        int gone;

        lock (_gate)
        {
            if (Existing() is not { } connection)
            {
                return 0;
            }

            using SqliteTransaction transaction = connection.BeginTransaction();
            using SqliteCommand command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = sql;
            gone = command.ExecuteNonQuery();
            transaction.Commit();

            Forget();
            Execute(connection, "VACUUM;");
            Execute(connection, "PRAGMA wal_checkpoint(TRUNCATE);");
        }

        Changed?.Invoke(this, EventArgs.Empty);
        return gone;
    }

    /// <summary>
    /// Deletes the oldest conversations past the budget. Call with the gate
    /// held, outside a transaction.
    /// </summary>
    private int Trim(SqliteConnection connection)
    {
        _addedSinceTrim = 0;

        using SqliteCommand command = connection.CreateCommand();
        command.CommandText =
            """
            DELETE FROM flow WHERE id IN (
                SELECT id FROM flow ORDER BY last_ms ASC, id ASC
                LIMIT MAX(0, (SELECT COUNT(*) FROM flow) - @most));
            """;
        command.Parameters.AddWithValue("@most", _mostConversations);
        int gone = command.ExecuteNonQuery();

        if (gone > 0)
        {
            Execute(connection, "DELETE FROM app WHERE id NOT IN (SELECT DISTINCT app FROM flow);");
        }

        return gone;
    }

    /// <summary>
    /// After rows were deleted: frees their pages and moves the write-ahead
    /// log's copies into the file and empties it, so what was deleted is in
    /// neither. Call with the gate held.
    /// </summary>
    private static void Settle(SqliteConnection connection, int gone)
    {
        if (gone <= 0)
        {
            return;
        }

        Execute(connection, "PRAGMA incremental_vacuum;");
        Execute(connection, "PRAGMA wal_checkpoint(TRUNCATE);");
    }

    /// <summary>Drops what is remembered of rows that may no longer exist. Call with the gate held.</summary>
    private void Forget()
    {
        _open.Clear();
        _apps.Clear();
    }

    /// <summary>A local day as <c>yyyymmdd</c>.</summary>
    internal static int DayNumber(DateOnly day) => (day.Year * 10_000) + (day.Month * 100) + day.Day;

    internal static DateOnly FromDayNumber(int number) => new(number / 10_000, number / 100 % 100, number % 100);

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

    /// <summary>
    /// The connection when the file is already there, or null: reading,
    /// pruning and erasing never create it. Call with the gate held.
    /// </summary>
    private SqliteConnection? Existing() =>
        _connection is null && !File.Exists(_path) ? null : Connection();

    /// <summary>The open connection, opened on first use. Call with the gate held.</summary>
    private SqliteConnection Connection()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        if (_connection is null && _openFailure is null)
        {
            Open();
        }

        return _connection
            ?? throw new InvalidOperationException("The connection timeline could not be opened.", _openFailure);
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
            _connection?.Dispose();
        }
    }
}
