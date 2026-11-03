using Microsoft.Data.Sqlite;

namespace Windsock.Core.Tests;

/// <summary>
/// History files as earlier versions left them, written with their own DDL, so
/// migrating them in place is tested against what is really on people's disks.
/// </summary>
internal static class OlderHistoryFiles
{
    /// <summary>Windsock 0.1.0's file: schema 1, minutes and coverage only.</summary>
    public const string Schema1 =
        """
        CREATE TABLE usage_minute (
            minute    INTEGER PRIMARY KEY,
            down      INTEGER NOT NULL,
            up        INTEGER NOT NULL,
            peak_down INTEGER NOT NULL,
            peak_up   INTEGER NOT NULL
        ) WITHOUT ROWID;

        CREATE TABLE coverage (
            start_minute INTEGER PRIMARY KEY,
            end_minute   INTEGER NOT NULL
        ) WITHOUT ROWID;

        PRAGMA user_version=1;
        """;

    /// <summary>The schema the dev branch used before this one: 5.</summary>
    public const string Schema5 =
        """
        CREATE TABLE usage_minute (
            minute    INTEGER PRIMARY KEY,
            down      INTEGER NOT NULL,
            up        INTEGER NOT NULL,
            peak_down INTEGER NOT NULL,
            peak_up   INTEGER NOT NULL
        ) WITHOUT ROWID;

        CREATE TABLE coverage (
            start_minute INTEGER PRIMARY KEY,
            end_minute   INTEGER NOT NULL
        ) WITHOUT ROWID;

        CREATE TABLE app (
            id          INTEGER PRIMARY KEY,
            attribution TEXT NOT NULL UNIQUE
        );

        CREATE TABLE app_usage (
            start_minute INTEGER NOT NULL,
            minutes      INTEGER NOT NULL,
            app          INTEGER NOT NULL REFERENCES app(id),
            down         INTEGER NOT NULL,
            up           INTEGER NOT NULL,
            PRIMARY KEY (start_minute, app)
        ) WITHOUT ROWID;

        CREATE INDEX app_usage_by_app ON app_usage (app, start_minute);

        CREATE TABLE app_state (
            name  TEXT PRIMARY KEY,
            value INTEGER NOT NULL
        ) WITHOUT ROWID;

        CREATE TABLE app_erased (
            from_minute  INTEGER NOT NULL,
            until_minute INTEGER NOT NULL
        );

        CREATE TABLE app_minute (
            minute INTEGER NOT NULL,
            app    INTEGER NOT NULL REFERENCES app(id),
            down   INTEGER NOT NULL,
            up     INTEGER NOT NULL,
            PRIMARY KEY (minute, app)
        ) WITHOUT ROWID;

        CREATE TABLE app_quarter (
            quarter INTEGER NOT NULL,
            app     INTEGER NOT NULL REFERENCES app(id),
            down    INTEGER NOT NULL,
            up      INTEGER NOT NULL,
            PRIMARY KEY (quarter, app)
        ) WITHOUT ROWID;

        CREATE TABLE app_measured (
            start_minute INTEGER PRIMARY KEY,
            until_minute INTEGER NOT NULL
        ) WITHOUT ROWID;

        CREATE TABLE system_minute (
            minute       INTEGER PRIMARY KEY,
            samples      INTEGER NOT NULL,
            cpu_sum      INTEGER NOT NULL,
            cpu_peak     INTEGER NOT NULL,
            heat_sum     INTEGER NOT NULL,
            heat_samples INTEGER NOT NULL,
            heat_peak    INTEGER,
            gpu_sum      INTEGER NOT NULL,
            gpu_peak     INTEGER NOT NULL,
            gpu_heat     INTEGER,
            mem_sum      INTEGER NOT NULL,
            mem_peak     INTEGER NOT NULL,
            mem_total    INTEGER NOT NULL,
            disk_read    INTEGER NOT NULL,
            disk_write   INTEGER NOT NULL
        ) WITHOUT ROWID;

        CREATE TABLE connection_minute (
            minute            INTEGER PRIMARY KEY,
            checks            INTEGER NOT NULL,
            offline           INTEGER NOT NULL,
            router_tried      INTEGER NOT NULL,
            router_answered   INTEGER NOT NULL,
            router_sum        INTEGER NOT NULL,
            router_peak       INTEGER NOT NULL,
            internet_tried    INTEGER NOT NULL,
            internet_answered INTEGER NOT NULL,
            internet_sum      INTEGER NOT NULL,
            internet_peak     INTEGER NOT NULL
        ) WITHOUT ROWID;

        PRAGMA user_version=5;
        """;

    /// <summary>Makes a file from DDL and then runs anything else against it.</summary>
    public static void Create(string path, string ddl, params string[] then)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);

        using SqliteConnection connection = Open(path);
        Run(connection, ddl);

        foreach (string sql in then)
        {
            Run(connection, sql);
        }
    }

    /// <summary>Runs SQL against a file the way another program - an older Windsock - would.</summary>
    public static void Run(string path, params string[] statements)
    {
        using SqliteConnection connection = Open(path);

        foreach (string sql in statements)
        {
            Run(connection, sql);
        }
    }

    /// <summary>
    /// Holds a read transaction open on a file, as another program reading it
    /// would, until disposed.
    /// </summary>
    public static IDisposable HoldRead(string path)
    {
        SqliteConnection connection = Open(path);
        SqliteTransaction transaction = connection.BeginTransaction(deferred: true);

        using (SqliteCommand read = connection.CreateCommand())
        {
            read.Transaction = transaction;
            read.CommandText = "SELECT COUNT(*) FROM sqlite_master;";
            read.ExecuteScalar();
        }

        return new Held(connection, transaction);
    }

    private sealed class Held(SqliteConnection connection, SqliteTransaction transaction) : IDisposable
    {
        public void Dispose()
        {
            transaction.Dispose();
            connection.Dispose();
        }
    }

    /// <summary>Asks a file one thing directly.</summary>
    public static object? Ask(string path, string sql)
    {
        using SqliteConnection connection = Open(path);
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = sql;
        return command.ExecuteScalar();
    }

    private static SqliteConnection Open(string path)
    {
        SqliteConnection connection = new(new SqliteConnectionStringBuilder
        {
            DataSource = path,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Pooling = false,
        }.ConnectionString);

        connection.Open();
        return connection;
    }

    private static void Run(SqliteConnection connection, string sql)
    {
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = sql;
        command.ExecuteNonQuery();
    }
}
