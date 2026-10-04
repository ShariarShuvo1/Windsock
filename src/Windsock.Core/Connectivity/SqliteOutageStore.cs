using System.Data.Common;
using System.Globalization;
using Microsoft.Data.Sqlite;

namespace Windsock.Core.Connectivity;

/// <summary>Where outages are kept, so the log survives a restart.</summary>
/// <remarks>
/// Every member may throw for a file that cannot be read or written; callers
/// catch what <see cref="History.HistoryFailure.IsStorage"/> recognises and carry on
/// without the log.
/// </remarks>
public interface IOutageStore
{
    /// <summary>Records an outage that has just begun, and returns its row.</summary>
    long Begin(Outage outage);

    /// <summary>Notes that an outage was still going at a moment, in case Windsock never sees it end.</summary>
    void Touch(long id, DateTimeOffset stillOut);

    /// <summary>Records how an outage ended.</summary>
    void Finish(long id, DateTimeOffset finishedAt, bool interrupted);

    /// <summary>
    /// Closes outages a previous run never saw end - the PC lost power, or
    /// Windsock was ended - at the last moment each was known to be going.
    /// </summary>
    int CloseUnfinished();

    /// <summary>The outages that overlap a period, newest first.</summary>
    IReadOnlyList<Outage> Read(DateTimeOffset from, DateTimeOffset until);

    /// <summary>Forgets every outage.</summary>
    int Clear();

    /// <summary>Raised after anything is written or cleared.</summary>
    event EventHandler? Changed;
}

/// <summary>Keeps the outage log in a small SQLite file of its own.</summary>
/// <remarks>
/// A separate file from the usage history on purpose. The history store sets a
/// damaged file aside and vacuums on erase, and both are simpler to reason about
/// when nothing else holds the file open.
/// </remarks>
public sealed class SqliteOutageStore : IOutageStore, IDisposable
{
    private const int SchemaVersion = 1;
    private const int Corrupt = 11;
    private const int NotADatabase = 26;

    private readonly Lock _gate = new();
    private readonly SqliteConnection? _connection;
    private readonly Exception? _openFailure;
    private bool _disposed;

    /// <summary>Opens, creating the file as needed. Never throws for a file it cannot open.</summary>
    public SqliteOutageStore(string path)
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
            CREATE TABLE IF NOT EXISTS outage (
                id          INTEGER PRIMARY KEY AUTOINCREMENT,
                start_ms    INTEGER NOT NULL,
                end_ms      INTEGER,
                seen_ms     INTEGER NOT NULL,
                cause       TEXT    NOT NULL,
                network     TEXT    NOT NULL,
                interrupted INTEGER NOT NULL DEFAULT 0
            );

            CREATE INDEX IF NOT EXISTS outage_start ON outage (start_ms);
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
            ?? throw new InvalidOperationException("The outage log could not be opened.", _openFailure);
    }

    /// <inheritdoc />
    public long Begin(Outage outage)
    {
        ArgumentNullException.ThrowIfNull(outage);

        long id;

        lock (_gate)
        {
            using SqliteCommand command = Connection().CreateCommand();
            command.CommandText =
                """
                INSERT INTO outage (start_ms, end_ms, seen_ms, cause, network, interrupted)
                VALUES (@start, @end, @start, @cause, @network, @interrupted)
                RETURNING id;
                """;
            command.Parameters.AddWithValue("@start", outage.Start.ToUnixTimeMilliseconds());
            command.Parameters.AddWithValue("@end", outage.End is { } end ? end.ToUnixTimeMilliseconds() : DBNull.Value);
            command.Parameters.AddWithValue("@cause", outage.Cause.ToString());
            command.Parameters.AddWithValue("@network", outage.Network);
            command.Parameters.AddWithValue("@interrupted", outage.Interrupted ? 1 : 0);
            id = Convert.ToInt64(command.ExecuteScalar(), CultureInfo.InvariantCulture);
        }

        Changed?.Invoke(this, EventArgs.Empty);
        return id;
    }

    /// <inheritdoc />
    public void Touch(long id, DateTimeOffset stillOut)
    {
        lock (_gate)
        {
            using SqliteCommand command = Connection().CreateCommand();
            command.CommandText = "UPDATE outage SET seen_ms = MAX(seen_ms, @seen) WHERE id = @id AND end_ms IS NULL;";
            command.Parameters.AddWithValue("@seen", stillOut.ToUnixTimeMilliseconds());
            command.Parameters.AddWithValue("@id", id);
            command.ExecuteNonQuery();
        }
    }

    /// <inheritdoc />
    public void Finish(long id, DateTimeOffset finishedAt, bool interrupted)
    {
        lock (_gate)
        {
            using SqliteCommand command = Connection().CreateCommand();
            command.CommandText =
                """
                UPDATE outage
                SET end_ms = MAX(start_ms, @end), seen_ms = MAX(seen_ms, @end), interrupted = @interrupted
                WHERE id = @id;
                """;
            command.Parameters.AddWithValue("@end", finishedAt.ToUnixTimeMilliseconds());
            command.Parameters.AddWithValue("@interrupted", interrupted ? 1 : 0);
            command.Parameters.AddWithValue("@id", id);
            command.ExecuteNonQuery();
        }

        Changed?.Invoke(this, EventArgs.Empty);
    }

    /// <inheritdoc />
    public int CloseUnfinished()
    {
        int closed;

        lock (_gate)
        {
            using SqliteCommand command = Connection().CreateCommand();
            command.CommandText = "UPDATE outage SET end_ms = seen_ms, interrupted = 1 WHERE end_ms IS NULL;";
            closed = command.ExecuteNonQuery();
        }

        if (closed > 0)
        {
            Changed?.Invoke(this, EventArgs.Empty);
        }

        return closed;
    }

    /// <inheritdoc />
    public IReadOnlyList<Outage> Read(DateTimeOffset from, DateTimeOffset until)
    {
        List<Outage> outages = [];

        lock (_gate)
        {
            using SqliteCommand command = Connection().CreateCommand();
            command.CommandText =
                """
                SELECT id, start_ms, end_ms, cause, network, interrupted
                FROM outage
                WHERE start_ms < @until AND COALESCE(end_ms, seen_ms) >= @from
                ORDER BY start_ms DESC;
                """;
            command.Parameters.AddWithValue("@from", from.ToUnixTimeMilliseconds());
            command.Parameters.AddWithValue("@until", until.ToUnixTimeMilliseconds());

            using SqliteDataReader reader = command.ExecuteReader();

            while (reader.Read())
            {
                DateTimeOffset? end = reader.IsDBNull(2)
                    ? null
                    : DateTimeOffset.FromUnixTimeMilliseconds(reader.GetInt64(2)).ToLocalTime();

                outages.Add(new Outage(
                    DateTimeOffset.FromUnixTimeMilliseconds(reader.GetInt64(1)).ToLocalTime(),
                    end,
                    Enum.TryParse(reader.GetString(3), out OutageCause cause) ? cause : OutageCause.Unknown,
                    reader.GetString(4))
                {
                    Id = reader.GetInt64(0),
                    Interrupted = reader.GetInt64(5) != 0,
                });
            }
        }

        return outages;
    }

    /// <inheritdoc />
    public int Clear()
    {
        int gone;

        lock (_gate)
        {
            using SqliteCommand command = Connection().CreateCommand();
            command.CommandText = "DELETE FROM outage;";
            gone = command.ExecuteNonQuery();
        }

        Changed?.Invoke(this, EventArgs.Empty);
        return gone;
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
