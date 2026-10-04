using Microsoft.Data.Sqlite;

namespace Windsock.Core.History;

/// <summary>The machine's and the connection's minutes; see <see cref="IMachineHistoryStore"/>.</summary>
public sealed partial class SqliteUsageHistoryStore
{
    private static void MigrateMachine(SqliteConnection connection) =>
        // Sums and peaks rather than averages, so a minute written in two goes
        // (a restart inside it) adds up, and any bucket's average is exact.
        Execute(
            connection,
            """
            CREATE TABLE IF NOT EXISTS system_minute (
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

            CREATE TABLE IF NOT EXISTS connection_minute (
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
            """);

    /// <inheritdoc />
    public void WriteSystem(IReadOnlyList<SystemMinute> minutes)
    {
        ArgumentNullException.ThrowIfNull(minutes);

        if (minutes.Count == 0)
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
                INSERT INTO system_minute (minute, samples, cpu_sum, cpu_peak, heat_sum, heat_samples, heat_peak,
                                           gpu_sum, gpu_peak, gpu_heat, mem_sum, mem_peak, mem_total, disk_read, disk_write)
                VALUES (@minute, @samples, @cpuSum, @cpuPeak, @heatSum, @heatSamples, @heatPeak,
                        @gpuSum, @gpuPeak, @gpuHeat, @memSum, @memPeak, @memTotal, @diskRead, @diskWrite)
                ON CONFLICT(minute) DO UPDATE SET
                    samples      = samples + excluded.samples,
                    cpu_sum      = cpu_sum + excluded.cpu_sum,
                    cpu_peak     = MAX(cpu_peak, excluded.cpu_peak),
                    heat_sum     = heat_sum + excluded.heat_sum,
                    heat_samples = heat_samples + excluded.heat_samples,
                    heat_peak    = MAX(COALESCE(heat_peak, excluded.heat_peak), COALESCE(excluded.heat_peak, heat_peak)),
                    gpu_sum      = gpu_sum + excluded.gpu_sum,
                    gpu_peak     = MAX(gpu_peak, excluded.gpu_peak),
                    gpu_heat     = MAX(COALESCE(gpu_heat, excluded.gpu_heat), COALESCE(excluded.gpu_heat, gpu_heat)),
                    mem_sum      = mem_sum + excluded.mem_sum,
                    mem_peak     = MAX(mem_peak, excluded.mem_peak),
                    mem_total    = MAX(mem_total, excluded.mem_total),
                    disk_read    = disk_read + excluded.disk_read,
                    disk_write   = disk_write + excluded.disk_write;
                """;

            string[] names =
            [
                "@minute", "@samples", "@cpuSum", "@cpuPeak", "@heatSum", "@heatSamples", "@heatPeak",
                "@gpuSum", "@gpuPeak", "@gpuHeat", "@memSum", "@memPeak", "@memTotal", "@diskRead", "@diskWrite",
            ];

            SqliteParameter[] values = [.. names.Select(name => command.Parameters.Add(name, SqliteType.Integer))];
            List<(long From, long Until)> erased = ReadErased(connection, transaction);

            foreach (SystemMinute row in minutes)
            {
                if (IsErased(erased, row.Minute))
                {
                    continue;
                }

                values[0].Value = row.Minute;
                values[1].Value = row.Samples;
                values[2].Value = row.ProcessorSum;
                values[3].Value = row.ProcessorPeak;
                values[4].Value = row.ProcessorHeatSum;
                values[5].Value = row.ProcessorHeatSamples;
                values[6].Value = row.ProcessorHeatPeak is { } heat ? heat : DBNull.Value;
                values[7].Value = row.GraphicsSum;
                values[8].Value = row.GraphicsPeak;
                values[9].Value = row.GraphicsHeatPeak is { } gpuHeat ? gpuHeat : DBNull.Value;
                values[10].Value = row.MemorySum;
                values[11].Value = row.MemoryPeak;
                values[12].Value = row.MemoryTotal;
                values[13].Value = row.DiskRead;
                values[14].Value = row.DiskWrite;
                command.ExecuteNonQuery();
            }

            transaction.Commit();
        }
    }

    /// <inheritdoc />
    public void WriteConnection(IReadOnlyList<ConnectionMinute> minutes)
    {
        ArgumentNullException.ThrowIfNull(minutes);

        if (minutes.Count == 0)
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
                INSERT INTO connection_minute (minute, checks, offline, router_tried, router_answered, router_sum, router_peak,
                                               internet_tried, internet_answered, internet_sum, internet_peak)
                VALUES (@minute, @checks, @offline, @routerTried, @routerAnswered, @routerSum, @routerPeak,
                        @internetTried, @internetAnswered, @internetSum, @internetPeak)
                ON CONFLICT(minute) DO UPDATE SET
                    checks            = checks + excluded.checks,
                    offline           = offline + excluded.offline,
                    router_tried      = router_tried + excluded.router_tried,
                    router_answered   = router_answered + excluded.router_answered,
                    router_sum        = router_sum + excluded.router_sum,
                    router_peak       = MAX(router_peak, excluded.router_peak),
                    internet_tried    = internet_tried + excluded.internet_tried,
                    internet_answered = internet_answered + excluded.internet_answered,
                    internet_sum      = internet_sum + excluded.internet_sum,
                    internet_peak     = MAX(internet_peak, excluded.internet_peak);
                """;

            string[] names =
            [
                "@minute", "@checks", "@offline", "@routerTried", "@routerAnswered", "@routerSum", "@routerPeak",
                "@internetTried", "@internetAnswered", "@internetSum", "@internetPeak",
            ];

            SqliteParameter[] values = [.. names.Select(name => command.Parameters.Add(name, SqliteType.Integer))];
            List<(long From, long Until)> erased = ReadErased(connection, transaction);

            foreach (ConnectionMinute row in minutes)
            {
                if (IsErased(erased, row.Minute))
                {
                    continue;
                }

                values[0].Value = row.Minute;
                values[1].Value = row.Checks;
                values[2].Value = row.Offline;
                values[3].Value = row.RouterTried;
                values[4].Value = row.RouterAnswered;
                values[5].Value = row.RouterSum;
                values[6].Value = row.RouterPeak;
                values[7].Value = row.InternetTried;
                values[8].Value = row.InternetAnswered;
                values[9].Value = row.InternetSum;
                values[10].Value = row.InternetPeak;
                command.ExecuteNonQuery();
            }

            transaction.Commit();
        }
    }

    /// <inheritdoc />
    public IReadOnlyList<SystemBucket> ReadSystem(DateTimeOffset from, DateTimeOffset until, UsageGranularity granularity)
    {
        List<SystemBucket> buckets = [];

        lock (_gate)
        {
            using SqliteCommand command = Connection().CreateCommand();
            command.CommandText = string.Concat(
                "SELECT ",
                BucketExpression(granularity),
                """
                 AS bucket, COUNT(*), SUM(samples), SUM(cpu_sum), MAX(cpu_peak), SUM(heat_sum), SUM(heat_samples),
                       MAX(heat_peak), SUM(gpu_sum), MAX(gpu_peak), MAX(gpu_heat), SUM(mem_sum), MAX(mem_peak),
                       MAX(mem_total), SUM(disk_read), SUM(disk_write)
                FROM system_minute
                WHERE minute >= @from AND minute < @to
                GROUP BY bucket
                ORDER BY bucket;
                """);
            Bound(command, from, until, granularity);

            using SqliteDataReader reader = command.ExecuteReader();

            while (reader.Read())
            {
                long samples = Math.Max(1, reader.GetInt64(2));
                long heatSamples = reader.GetInt64(6);

                buckets.Add(new SystemBucket(
                    BucketStart(reader, granularity),
                    reader.GetInt32(1),
                    reader.GetInt64(3) / 10.0 / samples,
                    reader.GetInt64(4) / 10.0,
                    heatSamples > 0 ? reader.GetInt64(5) / 10.0 / heatSamples : null,
                    reader.IsDBNull(7) ? null : reader.GetInt64(7) / 10.0,
                    reader.GetInt64(8) / 10.0 / samples,
                    reader.GetInt64(9) / 10.0,
                    reader.IsDBNull(10) ? null : reader.GetInt64(10) / 10.0,
                    reader.GetInt64(11) / samples,
                    reader.GetInt64(12),
                    reader.GetInt64(13),
                    reader.GetInt64(14),
                    reader.GetInt64(15)));
            }
        }

        return buckets;
    }

    /// <inheritdoc />
    public IReadOnlyList<ConnectionBucket> ReadConnection(DateTimeOffset from, DateTimeOffset until, UsageGranularity granularity)
    {
        List<ConnectionBucket> buckets = [];

        lock (_gate)
        {
            using SqliteCommand command = Connection().CreateCommand();
            command.CommandText = string.Concat(
                "SELECT ",
                BucketExpression(granularity),
                """
                 AS bucket, COUNT(*), SUM(checks), SUM(offline), SUM(router_tried), SUM(router_answered),
                       SUM(router_sum), MAX(router_peak), SUM(internet_tried), SUM(internet_answered),
                       SUM(internet_sum), MAX(internet_peak)
                FROM connection_minute
                WHERE minute >= @from AND minute < @to
                GROUP BY bucket
                ORDER BY bucket;
                """);
            Bound(command, from, until, granularity);

            using SqliteDataReader reader = command.ExecuteReader();

            while (reader.Read())
            {
                long routerTried = reader.GetInt64(4);
                long routerAnswered = reader.GetInt64(5);
                long internetTried = reader.GetInt64(8);
                long internetAnswered = reader.GetInt64(9);

                buckets.Add(new ConnectionBucket(
                    BucketStart(reader, granularity),
                    reader.GetInt32(1),
                    reader.GetInt32(2),
                    reader.GetInt32(3),
                    routerAnswered > 0 ? reader.GetInt64(6) / 10.0 / routerAnswered : null,
                    routerAnswered > 0 ? reader.GetInt64(7) / 10.0 : null,
                    routerTried > 0 ? 1 - ((double)routerAnswered / routerTried) : null,
                    internetAnswered > 0 ? reader.GetInt64(10) / 10.0 / internetAnswered : null,
                    internetAnswered > 0 ? reader.GetInt64(11) / 10.0 : null,
                    internetTried > 0 ? 1 - ((double)internetAnswered / internetTried) : null));
            }
        }

        return buckets;
    }

    /// <summary>
    /// Whether a minute lies in an erased period. The minute being gathered as
    /// somebody erases would otherwise be written straight back.
    /// </summary>
    private static bool IsErased(List<(long From, long Until)> erased, long minute) =>
        erased.Exists(span => minute >= span.From && minute < span.Until);

    private static string BucketExpression(UsageGranularity granularity) =>
        granularity == UsageGranularity.Minute
            ? "minute"
            : "strftime(@format, minute * 60, 'unixepoch', 'localtime')";

    private static void Bound(SqliteCommand command, DateTimeOffset from, DateTimeOffset until, UsageGranularity granularity)
    {
        command.Parameters.AddWithValue("@from", EpochMinute.From(from));
        command.Parameters.AddWithValue("@to", EpochMinute.From(until));

        if (granularity != UsageGranularity.Minute)
        {
            command.Parameters.AddWithValue("@format", granularity == UsageGranularity.Hour ? HourFormat : DayFormat);
        }
    }

    private static DateTimeOffset BucketStart(SqliteDataReader reader, UsageGranularity granularity) =>
        granularity == UsageGranularity.Minute
            ? EpochMinute.ToLocal(reader.GetInt64(0))
            : LocalStart(reader.GetString(0));

    /// <summary>Takes a period out of the machine's and the connection's minutes. Call inside the erase's transaction.</summary>
    private static void EraseMachine(SqliteConnection connection, SqliteTransaction transaction, long fromMinute, long untilMinute)
    {
        using SqliteCommand command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText =
            """
            DELETE FROM system_minute WHERE minute >= @from AND minute < @to;
            DELETE FROM connection_minute WHERE minute >= @from AND minute < @to;
            """;
        command.Parameters.AddWithValue("@from", fromMinute);
        command.Parameters.AddWithValue("@to", untilMinute);
        command.ExecuteNonQuery();
    }
}
