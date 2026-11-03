using System.Data.Common;
using System.Globalization;
using System.Net;
using Microsoft.Data.Sqlite;

namespace Windsock.Core.Home;

/// <summary>Where the devices seen on home networks are kept, so the list survives a restart.</summary>
/// <remarks>
/// Every member may throw for a file that cannot be read or written; callers
/// catch what <see cref="History.HistoryFailure.IsStorage"/> recognises and
/// carry on without the list.
/// </remarks>
public interface IHomeStore
{
    /// <summary>Every device seen on a network, most recently seen first.</summary>
    IReadOnlyList<HomeDevice> Read(string networkKey);

    /// <summary>
    /// Notes the devices one look found, and returns those seen on this network
    /// for the first time.
    /// </summary>
    IReadOnlyList<HomeDevice> Record(string networkKey, string networkName, IReadOnlyList<DeviceSighting> sightings, DateTimeOffset at);

    /// <summary>Keeps a name found for a device.</summary>
    void Name(string networkKey, MacAddress mac, string name, DeviceNameSource source, DateTimeOffset at);

    /// <summary>Gives a device a name of the person's own, or takes it away with nothing.</summary>
    void Nickname(string networkKey, MacAddress mac, string? nickname);

    /// <summary>Forgets a device, which then counts as new if it comes back.</summary>
    bool Forget(string networkKey, MacAddress mac);

    /// <summary>When a network's first look finished, or nothing before it has.</summary>
    DateTimeOffset? Baseline(string networkKey);

    /// <summary>Records that a network's first look finished: devices found after it are new.</summary>
    void SetBaseline(string networkKey, DateTimeOffset at);

    /// <summary>Keeps the router check's report for a network, as JSON.</summary>
    void WriteRouterReport(string networkKey, DateTimeOffset at, string report);

    /// <summary>The last router check's report for a network, or nothing.</summary>
    (DateTimeOffset At, string Report)? ReadRouterReport(string networkKey);

    /// <summary>Forgets the devices first seen in a period, and router reports made in it.</summary>
    int Delete(DateTimeOffset from, DateTimeOffset until);

    /// <summary>Forgets every device, network and report.</summary>
    int RemoveEverything();

    /// <summary>Raised after anything is written or removed.</summary>
    event EventHandler? Changed;
}

/// <summary>Keeps the home device list in a small SQLite file of its own, <c>Home.db</c>.</summary>
/// <remarks>
/// A file of its own, like the outage log: it is erased with history but is
/// not history, and keeping it apart leaves the history file's schema alone.
/// Hardware addresses are stored as numbers; the maker is not stored, so a
/// newer maker table names devices seen before it.
/// </remarks>
public sealed class SqliteHomeStore : IHomeStore, IDisposable
{
    /// <summary>The file's name in the data folder.</summary>
    public const string FileName = "Home.db";

    private const int SchemaVersion = 1;
    private const int Corrupt = 11;
    private const int NotADatabase = 26;

    private readonly Lock _gate = new();
    private readonly string _path;
    private SqliteConnection? _connection;
    private Exception? _openFailure;
    private bool _opened;
    private bool _disposed;

    /// <summary>
    /// A store over a file, which is opened - and created - only when first
    /// used, so nobody who never switches the home features on has one. Never
    /// throws for a file it cannot open; each call does instead.
    /// </summary>
    public SqliteHomeStore(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        _path = path;
    }

    /// <summary>The usual file, in the data folder.</summary>
    public static string DefaultPath => Path.Combine(WindsockPaths.Root, FileName);

    /// <summary>
    /// Every device on every network, with each network's name, read from a
    /// file opened read-only, for the command line: nothing is created or
    /// changed, and a running Windsock is not disturbed. Empty when there is no
    /// file. Throws a storage failure when it cannot be read.
    /// </summary>
    public static IReadOnlyList<(string NetworkName, HomeDevice Device)> ReadAllReadOnly(string path)
    {
        if (!File.Exists(path))
        {
            return [];
        }

        SqliteConnectionStringBuilder builder = new()
        {
            DataSource = path,
            Mode = SqliteOpenMode.ReadOnly,
            Pooling = false,
            DefaultTimeout = 5,
        };

        using SqliteConnection connection = new(builder.ConnectionString);
        connection.Open();
        Execute(connection, "PRAGMA query_only=ON;");

        List<(string, HomeDevice)> devices = [];
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText =
            """
            SELECT d.network, COALESCE(n.name, ''), d.mac, d.first_seen, d.last_seen, d.address, d.address6,
                   d.name, d.name_from, d.nickname, d.is_router
            FROM device d LEFT JOIN home_network n ON n.key = d.network
            ORDER BY d.network, d.last_seen DESC;
            """;

        using SqliteDataReader reader = command.ExecuteReader();

        while (reader.Read())
        {
            devices.Add((reader.GetString(1), new HomeDevice(
                reader.GetString(0),
                new MacAddress(unchecked((ulong)reader.GetInt64(2))),
                DateTimeOffset.FromUnixTimeSeconds(reader.GetInt64(3)).ToLocalTime(),
                DateTimeOffset.FromUnixTimeSeconds(reader.GetInt64(4)).ToLocalTime(),
                Address(reader, 5),
                Address(reader, 6),
                reader.IsDBNull(7) ? null : reader.GetString(7),
                reader.IsDBNull(8) || !Enum.TryParse(reader.GetString(8), out DeviceNameSource source) ? DeviceNameSource.None : source,
                reader.IsDBNull(9) ? null : reader.GetString(9),
                reader.GetInt64(10) != 0)));
        }

        return devices;
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
            $"""
            CREATE TABLE IF NOT EXISTS device (
                network    TEXT    NOT NULL,
                mac        INTEGER NOT NULL,
                first_seen INTEGER NOT NULL,
                last_seen  INTEGER NOT NULL,
                address    TEXT,
                address6   TEXT,
                name       TEXT,
                name_from  TEXT,
                named_at   INTEGER,
                nickname   TEXT,
                is_router  INTEGER NOT NULL DEFAULT 0,
                PRIMARY KEY (network, mac)
            ) WITHOUT ROWID;

            CREATE INDEX IF NOT EXISTS device_first_seen ON device (first_seen);

            CREATE TABLE IF NOT EXISTS home_network (
                key         TEXT PRIMARY KEY,
                name        TEXT NOT NULL,
                baseline_at INTEGER
            ) WITHOUT ROWID;

            CREATE TABLE IF NOT EXISTS router_report (
                network    TEXT PRIMARY KEY,
                checked_at INTEGER NOT NULL,
                report     TEXT    NOT NULL
            ) WITHOUT ROWID;

            PRAGMA user_version={SchemaVersion};
            """);
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

    /// <summary>
    /// Whether the file was never made: then a read has nothing to find and is
    /// answered without making it, so looking at the page with the features off
    /// leaves no file behind. Called with the gate held.
    /// </summary>
    private bool Untouched => !_opened && !_disposed && !File.Exists(_path);

    /// <summary>The open connection, opening it the first time. Called with the gate held.</summary>
    private SqliteConnection Connection()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        if (!_opened)
        {
            _opened = true;

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

        return _connection
            ?? throw new InvalidOperationException("The home device list could not be opened.", _openFailure);
    }

    /// <inheritdoc />
    public IReadOnlyList<HomeDevice> Read(string networkKey)
    {
        ArgumentNullException.ThrowIfNull(networkKey);

        lock (_gate)
        {
            return Untouched ? [] : ReadLocked(networkKey, null);
        }
    }

    private List<HomeDevice> ReadLocked(string networkKey, long? firstSeenAtLeast)
    {
        List<HomeDevice> devices = [];
        using SqliteCommand command = Connection().CreateCommand();
        command.CommandText =
            """
            SELECT mac, first_seen, last_seen, address, address6, name, name_from, nickname, is_router
            FROM device
            WHERE network = @network AND (@since IS NULL OR first_seen >= @since)
            ORDER BY last_seen DESC, mac;
            """;
        command.Parameters.AddWithValue("@network", networkKey);
        command.Parameters.AddWithValue("@since", firstSeenAtLeast is { } since ? since : DBNull.Value);

        using SqliteDataReader reader = command.ExecuteReader();

        while (reader.Read())
        {
            devices.Add(new HomeDevice(
                networkKey,
                new MacAddress(unchecked((ulong)reader.GetInt64(0))),
                DateTimeOffset.FromUnixTimeSeconds(reader.GetInt64(1)).ToLocalTime(),
                DateTimeOffset.FromUnixTimeSeconds(reader.GetInt64(2)).ToLocalTime(),
                Address(reader, 3),
                Address(reader, 4),
                reader.IsDBNull(5) ? null : reader.GetString(5),
                reader.IsDBNull(6) || !Enum.TryParse(reader.GetString(6), out DeviceNameSource source) ? DeviceNameSource.None : source,
                reader.IsDBNull(7) ? null : reader.GetString(7),
                reader.GetInt64(8) != 0));
        }

        return devices;
    }

    private static IPAddress? Address(SqliteDataReader reader, int column) =>
        !reader.IsDBNull(column) && IPAddress.TryParse(reader.GetString(column), out IPAddress? address) ? address : null;

    /// <inheritdoc />
    public IReadOnlyList<HomeDevice> Record(string networkKey, string networkName, IReadOnlyList<DeviceSighting> sightings, DateTimeOffset at)
    {
        ArgumentNullException.ThrowIfNull(networkKey);
        ArgumentNullException.ThrowIfNull(networkName);
        ArgumentNullException.ThrowIfNull(sightings);

        long now = at.ToUnixTimeSeconds();
        List<HomeDevice> added = [];

        lock (_gate)
        {
            SqliteConnection connection = Connection();
            using SqliteTransaction transaction = connection.BeginTransaction();

            using (SqliteCommand network = connection.CreateCommand())
            {
                network.Transaction = transaction;
                network.CommandText =
                    """
                    INSERT INTO home_network (key, name) VALUES (@key, @name)
                    ON CONFLICT (key) DO UPDATE SET name = excluded.name;
                    """;
                network.Parameters.AddWithValue("@key", networkKey);
                network.Parameters.AddWithValue("@name", networkName);
                network.ExecuteNonQuery();
            }

            HashSet<long> known = [];

            using (SqliteCommand existing = connection.CreateCommand())
            {
                existing.Transaction = transaction;
                existing.CommandText = "SELECT mac FROM device WHERE network = @network;";
                existing.Parameters.AddWithValue("@network", networkKey);
                using SqliteDataReader reader = existing.ExecuteReader();

                while (reader.Read())
                {
                    known.Add(reader.GetInt64(0));
                }
            }

            using SqliteCommand upsert = connection.CreateCommand();
            upsert.Transaction = transaction;
            upsert.CommandText =
                """
                INSERT INTO device (network, mac, first_seen, last_seen, address, address6, is_router)
                VALUES (@network, @mac, @now, @now, @address, @address6, @router)
                ON CONFLICT (network, mac) DO UPDATE SET
                    last_seen = MAX(last_seen, excluded.last_seen),
                    address   = COALESCE(excluded.address, address),
                    address6  = COALESCE(excluded.address6, address6),
                    is_router = MAX(is_router, excluded.is_router);
                """;
            SqliteParameter mac = upsert.Parameters.Add("@mac", SqliteType.Integer);
            SqliteParameter address = upsert.Parameters.Add("@address", SqliteType.Text);
            SqliteParameter address6 = upsert.Parameters.Add("@address6", SqliteType.Text);
            SqliteParameter router = upsert.Parameters.Add("@router", SqliteType.Integer);
            upsert.Parameters.AddWithValue("@network", networkKey);
            upsert.Parameters.AddWithValue("@now", now);

            HashSet<MacAddress> fresh = [];

            foreach (DeviceSighting sighting in sightings)
            {
                long number = unchecked((long)sighting.Mac.Value);
                mac.Value = number;
                address.Value = sighting.Address?.ToString() ?? (object)DBNull.Value;
                address6.Value = sighting.Address6?.ToString() ?? (object)DBNull.Value;
                router.Value = sighting.IsRouter ? 1 : 0;
                upsert.ExecuteNonQuery();

                if (known.Add(number))
                {
                    fresh.Add(sighting.Mac);
                }
            }

            transaction.Commit();

            if (fresh.Count > 0)
            {
                added.AddRange(ReadLocked(networkKey, now).Where(each => fresh.Contains(each.Mac)));
            }
        }

        Changed?.Invoke(this, EventArgs.Empty);
        return added;
    }

    /// <inheritdoc />
    public void Name(string networkKey, MacAddress mac, string name, DeviceNameSource source, DateTimeOffset at)
    {
        ArgumentNullException.ThrowIfNull(networkKey);
        ArgumentException.ThrowIfNullOrWhiteSpace(name);

        lock (_gate)
        {
            using SqliteCommand command = Connection().CreateCommand();
            command.CommandText =
                "UPDATE device SET name = @name, name_from = @from, named_at = @at WHERE network = @network AND mac = @mac;";
            command.Parameters.AddWithValue("@name", name);
            command.Parameters.AddWithValue("@from", source.ToString());
            command.Parameters.AddWithValue("@at", at.ToUnixTimeSeconds());
            command.Parameters.AddWithValue("@network", networkKey);
            command.Parameters.AddWithValue("@mac", unchecked((long)mac.Value));
            command.ExecuteNonQuery();
        }

        Changed?.Invoke(this, EventArgs.Empty);
    }

    /// <inheritdoc />
    public void Nickname(string networkKey, MacAddress mac, string? nickname)
    {
        ArgumentNullException.ThrowIfNull(networkKey);

        string? kept = string.IsNullOrWhiteSpace(nickname) ? null : nickname.Trim();

        lock (_gate)
        {
            if (Untouched)
            {
                return;
            }

            using SqliteCommand command = Connection().CreateCommand();
            command.CommandText = "UPDATE device SET nickname = @nickname WHERE network = @network AND mac = @mac;";
            command.Parameters.AddWithValue("@nickname", kept is null ? DBNull.Value : kept);
            command.Parameters.AddWithValue("@network", networkKey);
            command.Parameters.AddWithValue("@mac", unchecked((long)mac.Value));
            command.ExecuteNonQuery();
        }

        Changed?.Invoke(this, EventArgs.Empty);
    }

    /// <inheritdoc />
    public bool Forget(string networkKey, MacAddress mac)
    {
        ArgumentNullException.ThrowIfNull(networkKey);

        int gone;

        lock (_gate)
        {
            if (Untouched)
            {
                return false;
            }

            using SqliteCommand command = Connection().CreateCommand();
            command.CommandText = "DELETE FROM device WHERE network = @network AND mac = @mac;";
            command.Parameters.AddWithValue("@network", networkKey);
            command.Parameters.AddWithValue("@mac", unchecked((long)mac.Value));
            gone = command.ExecuteNonQuery();
        }

        Changed?.Invoke(this, EventArgs.Empty);
        return gone > 0;
    }

    /// <inheritdoc />
    public DateTimeOffset? Baseline(string networkKey)
    {
        ArgumentNullException.ThrowIfNull(networkKey);

        lock (_gate)
        {
            if (Untouched)
            {
                return null;
            }

            using SqliteCommand command = Connection().CreateCommand();
            command.CommandText = "SELECT baseline_at FROM home_network WHERE key = @key;";
            command.Parameters.AddWithValue("@key", networkKey);
            object? value = command.ExecuteScalar();

            return value is long seconds ? DateTimeOffset.FromUnixTimeSeconds(seconds).ToLocalTime() : null;
        }
    }

    /// <inheritdoc />
    public void SetBaseline(string networkKey, DateTimeOffset at)
    {
        ArgumentNullException.ThrowIfNull(networkKey);

        lock (_gate)
        {
            using SqliteCommand command = Connection().CreateCommand();
            command.CommandText =
                """
                INSERT INTO home_network (key, name, baseline_at) VALUES (@key, '', @at)
                ON CONFLICT (key) DO UPDATE SET baseline_at = COALESCE(baseline_at, excluded.baseline_at);
                """;
            command.Parameters.AddWithValue("@key", networkKey);
            command.Parameters.AddWithValue("@at", at.ToUnixTimeSeconds());
            command.ExecuteNonQuery();
        }
    }

    /// <inheritdoc />
    public void WriteRouterReport(string networkKey, DateTimeOffset at, string report)
    {
        ArgumentNullException.ThrowIfNull(networkKey);
        ArgumentNullException.ThrowIfNull(report);

        lock (_gate)
        {
            using SqliteCommand command = Connection().CreateCommand();
            command.CommandText =
                """
                INSERT INTO router_report (network, checked_at, report) VALUES (@network, @at, @report)
                ON CONFLICT (network) DO UPDATE SET checked_at = excluded.checked_at, report = excluded.report;
                """;
            command.Parameters.AddWithValue("@network", networkKey);
            command.Parameters.AddWithValue("@at", at.ToUnixTimeSeconds());
            command.Parameters.AddWithValue("@report", report);
            command.ExecuteNonQuery();
        }

        Changed?.Invoke(this, EventArgs.Empty);
    }

    /// <inheritdoc />
    public (DateTimeOffset At, string Report)? ReadRouterReport(string networkKey)
    {
        ArgumentNullException.ThrowIfNull(networkKey);

        lock (_gate)
        {
            if (Untouched)
            {
                return null;
            }

            using SqliteCommand command = Connection().CreateCommand();
            command.CommandText = "SELECT checked_at, report FROM router_report WHERE network = @network;";
            command.Parameters.AddWithValue("@network", networkKey);
            using SqliteDataReader reader = command.ExecuteReader();

            return reader.Read()
                ? (DateTimeOffset.FromUnixTimeSeconds(reader.GetInt64(0)).ToLocalTime(), reader.GetString(1))
                : null;
        }
    }

    /// <inheritdoc />
    public int Delete(DateTimeOffset from, DateTimeOffset until)
    {
        int gone;

        lock (_gate)
        {
            if (Untouched)
            {
                return 0;
            }

            SqliteConnection connection = Connection();
            using SqliteTransaction transaction = connection.BeginTransaction();
            using SqliteCommand command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText =
                """
                DELETE FROM device WHERE first_seen >= @from AND first_seen < @until;
                DELETE FROM router_report WHERE checked_at >= @from AND checked_at < @until;
                """;
            command.Parameters.AddWithValue("@from", from.ToUnixTimeSeconds());
            command.Parameters.AddWithValue("@until", until.ToUnixTimeSeconds());
            gone = command.ExecuteNonQuery();
            transaction.Commit();
        }

        Changed?.Invoke(this, EventArgs.Empty);
        return gone;
    }

    /// <inheritdoc />
    public int RemoveEverything()
    {
        int gone;

        lock (_gate)
        {
            if (Untouched)
            {
                return 0;
            }

            SqliteConnection connection = Connection();
            using SqliteTransaction transaction = connection.BeginTransaction();
            using SqliteCommand command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = "DELETE FROM device; DELETE FROM home_network; DELETE FROM router_report;";
            gone = command.ExecuteNonQuery();
            transaction.Commit();
        }

        Changed?.Invoke(this, EventArgs.Empty);
        return gone;
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;

        lock (_gate)
        {
            _connection?.Dispose();
        }
    }
}
