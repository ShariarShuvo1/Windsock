using System.Globalization;
using Microsoft.Data.Sqlite;
using Windsock.Core.SpeedTests;

namespace Windsock.Core.Connectivity;

/// <summary>
/// The rest of the connection log, beside the outages: every speed test, and
/// quarter-hour sums of the connection checks for reports over long periods.
/// </summary>
/// <remarks>
/// <para>
/// In Outages.db rather than the usage history or a file of its own. Not the
/// history: that file sets itself aside when damaged and is vacuumed and erased
/// by period from History's page, and connection evidence for a provider is a
/// different thing to keep and to erase. Not a third file: Outages.db is
/// already the connection's log, a report reads outages, tests and quality
/// together, and one connection to one file keeps the rule that
/// nothing else opens the file true.
/// </para>
/// <para>
/// Schema 2 adds the two tables, schema 3 <c>scheduled_run</c>: when each
/// automatic test was started, kept apart from the results so that deleting
/// tests cannot free the day's automatic slots. A file opened by an older
/// Windsock (schema 1 code) sees its version is at least 1 and leaves the new
/// tables alone.
/// </para>
/// </remarks>
public sealed partial class SqliteOutageStore : ISpeedTestStore, IQualityStore
{
    /// <inheritdoc />
    public event EventHandler? SpeedTestsChanged;

    private static void CreateConnectionTables(SqliteConnection connection) => Execute(
        connection,
        """
        CREATE TABLE IF NOT EXISTS speed_test (
            id           INTEGER PRIMARY KEY AUTOINCREMENT,
            at_ms        INTEGER NOT NULL,
            backend      TEXT    NOT NULL,
            server       TEXT    NOT NULL,
            scheduled    INTEGER NOT NULL,
            down_bps     REAL,
            up_bps       REAL,
            idle_ms      REAL,
            loaded_down  REAL,
            loaded_up    REAL,
            idle_jitter  REAL,
            latency_from TEXT    NOT NULL,
            network      TEXT,
            problem      TEXT
        );

        CREATE INDEX IF NOT EXISTS speed_test_at ON speed_test (at_ms);

        CREATE TABLE IF NOT EXISTS scheduled_run (
            at_ms        INTEGER NOT NULL
        );

        CREATE INDEX IF NOT EXISTS scheduled_run_at ON scheduled_run (at_ms);

        CREATE TABLE IF NOT EXISTS quality_quarter (
            start_ms     INTEGER NOT NULL,
            target       INTEGER NOT NULL,
            sent         INTEGER NOT NULL,
            answered     INTEGER NOT NULL,
            sum_ms       REAL    NOT NULL,
            worst_ms     REAL    NOT NULL,
            jitter_sum   REAL    NOT NULL,
            jitter_pairs INTEGER NOT NULL,
            PRIMARY KEY (start_ms, target)
        ) WITHOUT ROWID;
        """);

    /// <inheritdoc />
    public long AddSpeedTest(SpeedTestResult result)
    {
        ArgumentNullException.ThrowIfNull(result);

        long id;

        lock (_gate)
        {
            using SqliteCommand command = Connection().CreateCommand();
            command.CommandText =
                """
                INSERT INTO speed_test (at_ms, backend, server, scheduled, down_bps, up_bps, idle_ms,
                                        loaded_down, loaded_up, idle_jitter, latency_from, network, problem)
                VALUES (@at, @backend, @server, @scheduled, @down, @up, @idle, @loadedDown, @loadedUp, @jitter, @from, @network, @problem)
                RETURNING id;
                """;
            command.Parameters.AddWithValue("@at", result.At.ToUnixTimeMilliseconds());
            command.Parameters.AddWithValue("@backend", result.Backend.ToString());
            command.Parameters.AddWithValue("@server", result.Server);
            command.Parameters.AddWithValue("@scheduled", result.Scheduled ? 1 : 0);
            command.Parameters.AddWithValue("@down", Value(result.DownloadBitsPerSecond));
            command.Parameters.AddWithValue("@up", Value(result.UploadBitsPerSecond));
            command.Parameters.AddWithValue("@idle", Value(result.IdleMs));
            command.Parameters.AddWithValue("@loadedDown", Value(result.LoadedDownMs));
            command.Parameters.AddWithValue("@loadedUp", Value(result.LoadedUpMs));
            command.Parameters.AddWithValue("@jitter", Value(result.IdleJitterMs));
            command.Parameters.AddWithValue("@from", result.Latency.ToString());
            command.Parameters.AddWithValue("@network", (object?)result.Network ?? DBNull.Value);
            command.Parameters.AddWithValue("@problem", (object?)result.Problem ?? DBNull.Value);
            id = Convert.ToInt64(command.ExecuteScalar(), CultureInfo.InvariantCulture);
        }

        SpeedTestsChanged?.Invoke(this, EventArgs.Empty);
        return id;
    }

    /// <inheritdoc />
    public IReadOnlyList<SpeedTestResult> ReadSpeedTests(DateTimeOffset from, DateTimeOffset until)
    {
        List<SpeedTestResult> tests = [];

        lock (_gate)
        {
            using SqliteCommand command = Connection().CreateCommand();
            command.CommandText =
                """
                SELECT id, at_ms, backend, server, scheduled, down_bps, up_bps, idle_ms, loaded_down, loaded_up,
                       idle_jitter, latency_from, network, problem
                FROM speed_test
                WHERE at_ms >= @from AND at_ms < @until
                ORDER BY at_ms DESC;
                """;
            command.Parameters.AddWithValue("@from", from.ToUnixTimeMilliseconds());
            command.Parameters.AddWithValue("@until", until.ToUnixTimeMilliseconds());

            using SqliteDataReader reader = command.ExecuteReader();

            while (reader.Read())
            {
                tests.Add(new SpeedTestResult(
                    reader.GetInt64(0),
                    DateTimeOffset.FromUnixTimeMilliseconds(reader.GetInt64(1)).ToLocalTime(),
                    Enum.TryParse(reader.GetString(2), out SpeedTestBackend backend) ? backend : SpeedTestBackend.MeasurementLab,
                    reader.GetString(3),
                    reader.GetInt64(4) != 0,
                    Number(reader, 5),
                    Number(reader, 6),
                    Number(reader, 7),
                    Number(reader, 8),
                    Number(reader, 9),
                    Number(reader, 10),
                    Enum.TryParse(reader.GetString(11), out LatencySource source) ? source : LatencySource.None,
                    reader.IsDBNull(12) ? null : reader.GetString(12),
                    reader.IsDBNull(13) ? null : reader.GetString(13)));
            }
        }

        return tests;
    }

    /// <inheritdoc />
    public bool DeleteSpeedTest(long id)
    {
        int gone;

        lock (_gate)
        {
            using SqliteCommand command = Connection().CreateCommand();
            command.CommandText = "DELETE FROM speed_test WHERE id = @id;";
            command.Parameters.AddWithValue("@id", id);
            gone = command.ExecuteNonQuery();
        }

        if (gone > 0)
        {
            SpeedTestsChanged?.Invoke(this, EventArgs.Empty);
        }

        return gone > 0;
    }

    /// <inheritdoc />
    public int ClearSpeedTests()
    {
        int gone;

        lock (_gate)
        {
            using SqliteCommand command = Connection().CreateCommand();
            command.CommandText = "DELETE FROM speed_test;";
            gone = command.ExecuteNonQuery();
        }

        SpeedTestsChanged?.Invoke(this, EventArgs.Empty);
        return gone;
    }

    /// <inheritdoc />
    public void NoteScheduledRun(DateTimeOffset at)
    {
        lock (_gate)
        {
            using SqliteCommand command = Connection().CreateCommand();

            // Runs older than two days can never count again. One stamped later
            // than now is kept whatever happens: the clock was set back.
            command.CommandText =
                """
                DELETE FROM scheduled_run WHERE at_ms < @old;
                INSERT INTO scheduled_run (at_ms) VALUES (@at);
                """;
            command.Parameters.AddWithValue("@old", (at - TimeSpan.FromDays(2)).ToUnixTimeMilliseconds());
            command.Parameters.AddWithValue("@at", at.ToUnixTimeMilliseconds());
            command.ExecuteNonQuery();
        }
    }

    /// <inheritdoc />
    public IReadOnlyList<DateTimeOffset> ReadScheduledRuns(DateTimeOffset since)
    {
        List<DateTimeOffset> runs = [];

        lock (_gate)
        {
            using SqliteCommand command = Connection().CreateCommand();
            command.CommandText = "SELECT at_ms FROM scheduled_run WHERE at_ms > @since ORDER BY at_ms;";
            command.Parameters.AddWithValue("@since", since.ToUnixTimeMilliseconds());

            using SqliteDataReader reader = command.ExecuteReader();

            while (reader.Read())
            {
                runs.Add(DateTimeOffset.FromUnixTimeMilliseconds(reader.GetInt64(0)).ToLocalTime());
            }
        }

        return runs;
    }

    /// <inheritdoc />
    public void AddQuality(IReadOnlyList<QualityQuarter> quarters)
    {
        ArgumentNullException.ThrowIfNull(quarters);

        if (quarters.Count == 0)
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
                INSERT INTO quality_quarter (start_ms, target, sent, answered, sum_ms, worst_ms, jitter_sum, jitter_pairs)
                VALUES (@start, @target, @sent, @answered, @sum, @worst, @jitter, @pairs)
                ON CONFLICT (start_ms, target) DO UPDATE SET
                    sent = sent + excluded.sent,
                    answered = answered + excluded.answered,
                    sum_ms = sum_ms + excluded.sum_ms,
                    worst_ms = MAX(worst_ms, excluded.worst_ms),
                    jitter_sum = jitter_sum + excluded.jitter_sum,
                    jitter_pairs = jitter_pairs + excluded.jitter_pairs;
                """;

            SqliteParameter start = command.Parameters.Add("@start", SqliteType.Integer);
            SqliteParameter target = command.Parameters.Add("@target", SqliteType.Integer);
            SqliteParameter sent = command.Parameters.Add("@sent", SqliteType.Integer);
            SqliteParameter answered = command.Parameters.Add("@answered", SqliteType.Integer);
            SqliteParameter sum = command.Parameters.Add("@sum", SqliteType.Real);
            SqliteParameter worst = command.Parameters.Add("@worst", SqliteType.Real);
            SqliteParameter jitter = command.Parameters.Add("@jitter", SqliteType.Real);
            SqliteParameter pairs = command.Parameters.Add("@pairs", SqliteType.Integer);

            foreach (QualityQuarter quarter in quarters)
            {
                start.Value = QualityQuarter.QuarterOf(quarter.Start).ToUnixTimeMilliseconds();
                target.Value = (int)quarter.Target;
                sent.Value = quarter.Sent;
                answered.Value = quarter.Answered;
                sum.Value = quarter.SumMs;
                worst.Value = quarter.WorstMs;
                jitter.Value = quarter.JitterSumMs;
                pairs.Value = quarter.JitterPairs;
                command.ExecuteNonQuery();
            }

            transaction.Commit();
        }
    }

    /// <inheritdoc />
    public IReadOnlyList<QualityQuarter> ReadQuality(DateTimeOffset from, DateTimeOffset until)
    {
        List<QualityQuarter> quarters = [];

        lock (_gate)
        {
            using SqliteCommand command = Connection().CreateCommand();
            command.CommandText =
                """
                SELECT start_ms, target, sent, answered, sum_ms, worst_ms, jitter_sum, jitter_pairs
                FROM quality_quarter
                WHERE start_ms >= @from AND start_ms < @until
                ORDER BY start_ms, target;
                """;
            command.Parameters.AddWithValue("@from", from.ToUnixTimeMilliseconds());
            command.Parameters.AddWithValue("@until", until.ToUnixTimeMilliseconds());

            using SqliteDataReader reader = command.ExecuteReader();

            while (reader.Read())
            {
                quarters.Add(new QualityQuarter(
                    DateTimeOffset.FromUnixTimeMilliseconds(reader.GetInt64(0)).ToLocalTime(),
                    reader.GetInt64(1) == 0 ? CheckTarget.Router : CheckTarget.Internet,
                    reader.GetInt32(2),
                    reader.GetInt32(3),
                    reader.GetDouble(4),
                    reader.GetDouble(5),
                    reader.GetDouble(6),
                    reader.GetInt32(7)));
            }
        }

        return quarters;
    }

    /// <inheritdoc />
    public int ClearQuality()
    {
        lock (_gate)
        {
            using SqliteCommand command = Connection().CreateCommand();
            command.CommandText = "DELETE FROM quality_quarter;";
            return command.ExecuteNonQuery();
        }
    }

    private static object Value(double? value) => value is { } number && double.IsFinite(number) ? number : DBNull.Value;

    private static double? Number(SqliteDataReader reader, int column) => reader.IsDBNull(column) ? null : reader.GetDouble(column);
}
