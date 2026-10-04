using System.Globalization;
using Microsoft.Data.Sqlite;
using Windsock.Core.Apps;

namespace Windsock.Core.History;

/// <summary>Each app's minutes as Windsock measured them; see <see cref="IAppDetailStore"/>.</summary>
public sealed partial class SqliteUsageHistoryStore
{
    private const string DetailBeforeKey = "app_detail_before";
    private const int Quarter = 15;
    private const long CompactionStep = 7 * 24 * 60;

    private static void MigrateAppDetail(SqliteConnection connection) =>
        // app_minute is the detail and app_quarter its quarter-hour totals,
        // written in the same transaction; app_measured holds the stretches
        // measured whole, until_minute exclusive, so a minute with nothing
        // moving still counts as measured. All three name apps through app,
        // which Windows' figures use too, so one app is one row either way.
        Execute(
            connection,
            """
            CREATE TABLE IF NOT EXISTS app_minute (
                minute INTEGER NOT NULL,
                app    INTEGER NOT NULL REFERENCES app(id),
                down   INTEGER NOT NULL,
                up     INTEGER NOT NULL,
                PRIMARY KEY (minute, app)
            ) WITHOUT ROWID;

            CREATE TABLE IF NOT EXISTS app_quarter (
                quarter INTEGER NOT NULL,
                app     INTEGER NOT NULL REFERENCES app(id),
                down    INTEGER NOT NULL,
                up      INTEGER NOT NULL,
                PRIMARY KEY (quarter, app)
            ) WITHOUT ROWID;

            CREATE TABLE IF NOT EXISTS app_measured (
                start_minute INTEGER PRIMARY KEY,
                until_minute INTEGER NOT NULL
            ) WITHOUT ROWID;
            """);

    /// <inheritdoc />
    public void WriteMeasuredMinute(long minute, IReadOnlyList<AppBytes> apps)
    {
        ArgumentNullException.ThrowIfNull(apps);

        lock (_gate)
        {
            SqliteConnection connection = Connection();
            using SqliteTransaction transaction = connection.BeginTransaction();

            if (ReadErased(connection, transaction).Exists(span => minute >= span.From && minute < span.Until))
            {
                return;
            }

            using (SqliteCommand known = connection.CreateCommand())
            {
                known.Transaction = transaction;
                known.CommandText =
                    "SELECT 1 FROM app_measured WHERE start_minute <= @minute AND until_minute > @minute LIMIT 1;";
                known.Parameters.AddWithValue("@minute", minute);

                if (known.ExecuteScalar() is not null)
                {
                    return;
                }
            }

            using (SqliteCommand measured = connection.CreateCommand())
            {
                // Runs on from the stretch that ends where this minute starts,
                // so an unbroken session is one row.
                measured.Transaction = transaction;
                measured.CommandText =
                    """
                    UPDATE app_measured SET until_minute = @until WHERE until_minute = @from;
                    INSERT INTO app_measured (start_minute, until_minute)
                    SELECT @from, @until WHERE changes() = 0;
                    """;
                measured.Parameters.AddWithValue("@from", minute);
                measured.Parameters.AddWithValue("@until", minute + 1);
                measured.ExecuteNonQuery();
            }

            if (apps.Count > 0)
            {
                using SqliteCommand name = NameCommand(connection, transaction, out SqliteParameter attribution);

                using SqliteCommand row = connection.CreateCommand();
                row.Transaction = transaction;
                row.CommandText =
                    """
                    INSERT INTO app_minute (minute, app, down, up) VALUES (@minute, @app, @down, @up)
                    ON CONFLICT(minute, app) DO NOTHING;
                    """;
                row.Parameters.AddWithValue("@minute", minute);
                SqliteParameter app = row.Parameters.Add("@app", SqliteType.Integer);
                SqliteParameter down = row.Parameters.Add("@down", SqliteType.Integer);
                SqliteParameter up = row.Parameters.Add("@up", SqliteType.Integer);

                using SqliteCommand quarter = connection.CreateCommand();
                quarter.Transaction = transaction;
                quarter.CommandText =
                    """
                    INSERT INTO app_quarter (quarter, app, down, up) VALUES (@quarter, @app, @down, @up)
                    ON CONFLICT(quarter, app) DO UPDATE SET down = down + excluded.down, up = up + excluded.up;
                    """;
                quarter.Parameters.AddWithValue("@quarter", FloorDiv(minute, Quarter));
                SqliteParameter quarterApp = quarter.Parameters.Add("@app", SqliteType.Integer);
                SqliteParameter quarterDown = quarter.Parameters.Add("@down", SqliteType.Integer);
                SqliteParameter quarterUp = quarter.Parameters.Add("@up", SqliteType.Integer);

                foreach (AppBytes each in Merged(apps))
                {
                    attribution.Value = each.AttributionId;
                    long id = Convert.ToInt64(name.ExecuteScalar(), CultureInfo.InvariantCulture);

                    app.Value = id;
                    down.Value = each.BytesReceived;
                    up.Value = each.BytesSent;

                    // The quarter takes the minute only when the minute was new.
                    if (row.ExecuteNonQuery() == 1)
                    {
                        quarterApp.Value = id;
                        quarterDown.Value = each.BytesReceived;
                        quarterUp.Value = each.BytesSent;
                        quarter.ExecuteNonQuery();
                    }
                }
            }

            transaction.Commit();
        }
    }

    /// <summary>One entry an app, nothing negative, nothing empty.</summary>
    private static IEnumerable<AppBytes> Merged(IReadOnlyList<AppBytes> apps)
    {
        Dictionary<string, (long Down, long Up)> merged = new(StringComparer.Ordinal);

        foreach (AppBytes each in apps)
        {
            string key = each.AttributionId ?? AppAttribution.UnattributedId;
            (long down, long up) = merged.GetValueOrDefault(key);
            merged[key] = (down + Math.Max(0, each.BytesReceived), up + Math.Max(0, each.BytesSent));
        }

        foreach ((string key, (long down, long up)) in merged)
        {
            if (down > 0 || up > 0)
            {
                yield return new AppBytes(key, down, up);
            }
        }
    }

    private static SqliteCommand NameCommand(SqliteConnection connection, SqliteTransaction transaction, out SqliteParameter attribution)
    {
        SqliteCommand name = connection.CreateCommand();
        name.Transaction = transaction;
        name.CommandText =
            """
            INSERT INTO app (attribution) VALUES (@attribution)
            ON CONFLICT(attribution) DO UPDATE SET attribution = excluded.attribution
            RETURNING id;
            """;
        attribution = name.Parameters.Add("@attribution", SqliteType.Text);
        return name;
    }

    /// <inheritdoc />
    public IReadOnlyList<UsageWindow> ReadMeasured(long fromMinute, long untilMinute)
    {
        List<UsageWindow> windows = [];

        if (untilMinute <= fromMinute)
        {
            return windows;
        }

        lock (_gate)
        {
            using SqliteCommand command = Connection().CreateCommand();
            command.CommandText =
                """
                SELECT start_minute, until_minute FROM app_measured
                WHERE until_minute > @from AND start_minute < @to
                ORDER BY start_minute;
                """;
            command.Parameters.AddWithValue("@from", fromMinute);
            command.Parameters.AddWithValue("@to", untilMinute);

            using SqliteDataReader reader = command.ExecuteReader();

            while (reader.Read())
            {
                long start = Math.Max(fromMinute, reader.GetInt64(0));
                long until = Math.Min(untilMinute, reader.GetInt64(1));

                if (until > start)
                {
                    windows.Add(new UsageWindow(start, (int)Math.Min(int.MaxValue, until - start)));
                }
            }
        }

        return windows;
    }

    /// <inheritdoc />
    public IReadOnlyList<AppUsageTotal> ReadMeasuredTotals(long fromMinute, long untilMinute)
    {
        List<AppUsageTotal> totals = [];

        if (untilMinute <= fromMinute)
        {
            return totals;
        }

        // Whole quarters inside the range come from their totals; what is left
        // at either end, from minutes.
        long firstQuarter = FloorDiv(fromMinute + Quarter - 1, Quarter);
        long lastQuarter = FloorDiv(untilMinute, Quarter);

        if (firstQuarter >= lastQuarter)
        {
            firstQuarter = lastQuarter = FloorDiv(fromMinute, Quarter);
        }

        lock (_gate)
        {
            using SqliteCommand command = Connection().CreateCommand();
            command.CommandText =
                """
                SELECT a.attribution, SUM(t.down), SUM(t.up)
                FROM (
                    SELECT app, down, up FROM app_quarter
                    WHERE quarter >= @firstQuarter AND quarter < @lastQuarter
                    UNION ALL
                    SELECT app, down, up FROM app_minute
                    WHERE minute >= @from AND minute < @to
                      AND (minute < @firstQuarter * 15 OR minute >= @lastQuarter * 15)
                ) t
                JOIN app a ON a.id = t.app
                GROUP BY t.app;
                """;
            command.Parameters.AddWithValue("@firstQuarter", firstQuarter);
            command.Parameters.AddWithValue("@lastQuarter", lastQuarter);
            command.Parameters.AddWithValue("@from", fromMinute);
            command.Parameters.AddWithValue("@to", untilMinute);

            using SqliteDataReader reader = command.ExecuteReader();

            while (reader.Read())
            {
                totals.Add(new AppUsageTotal(reader.GetString(0), reader.GetInt64(1), reader.GetInt64(2)));
            }
        }

        return totals;
    }

    /// <inheritdoc />
    public int CompactAppDetail(long beforeMinute)
    {
        int removed = 0;
        long? oldest;

        lock (_gate)
        {
            using SqliteCommand first = Connection().CreateCommand();
            first.CommandText = "SELECT MIN(minute) FROM app_minute;";
            object? found = first.ExecuteScalar();
            oldest = found is null or DBNull ? null : Convert.ToInt64(found, CultureInfo.InvariantCulture);
        }

        // A week at a time, each in its own transaction and turn at the gate, so
        // a year of detail going does not hold up the minute being recorded.
        for (long from = oldest ?? beforeMinute; from < beforeMinute; from += CompactionStep)
        {
            lock (_gate)
            {
                using SqliteCommand command = Connection().CreateCommand();
                command.CommandText = "DELETE FROM app_minute WHERE minute >= @from AND minute < @to;";
                command.Parameters.AddWithValue("@from", from);
                command.Parameters.AddWithValue("@to", Math.Min(beforeMinute, from + CompactionStep));
                removed += command.ExecuteNonQuery();
            }
        }

        lock (_gate)
        {
            using SqliteCommand mark = Connection().CreateCommand();
            mark.CommandText =
                """
                INSERT INTO app_state (name, value) VALUES (@name, @before)
                ON CONFLICT(name) DO UPDATE SET value = MAX(value, excluded.value);
                """;
            mark.Parameters.AddWithValue("@name", DetailBeforeKey);
            mark.Parameters.AddWithValue("@before", beforeMinute);
            mark.ExecuteNonQuery();
        }

        return removed;
    }

    /// <inheritdoc />
    public long? ReadAppDetailCompactedBefore()
    {
        lock (_gate)
        {
            using SqliteCommand command = Connection().CreateCommand();
            command.CommandText = "SELECT value FROM app_state WHERE name = @name;";
            command.Parameters.AddWithValue("@name", DetailBeforeKey);
            object? found = command.ExecuteScalar();
            return found is null or DBNull ? null : Convert.ToInt64(found, CultureInfo.InvariantCulture);
        }
    }

    /// <inheritdoc />
    public void Reclaim()
    {
        lock (_gate)
        {
            SqliteConnection connection = Connection();
            Execute(connection, "VACUUM;");
            Execute(connection, "PRAGMA wal_checkpoint(TRUNCATE);");
        }
    }

    /// <inheritdoc />
    public long FileBytes
    {
        get
        {
            long total = 0;

            foreach (string suffix in new[] { "", "-wal" })
            {
                try
                {
                    FileInfo file = new(_path + suffix);
                    total += file.Exists ? file.Length : 0;
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                }
            }

            return total;
        }
    }

    /// <summary>
    /// Takes a period out of the measured detail. Call with the gate held,
    /// inside the erase's transaction.
    /// </summary>
    /// <remarks>
    /// The minutes go, then every quarter touching the period is added up again
    /// from the minutes left. Where old minutes were compacted away there is
    /// nothing to add up, so those quarters go whole - as a Windows hour touching
    /// an erased period does, and for the same reason.
    /// </remarks>
    private static void EraseAppDetail(SqliteConnection connection, SqliteTransaction transaction, long fromMinute, long untilMinute)
    {
        if (untilMinute <= fromMinute)
        {
            return;
        }

        using (SqliteCommand command = connection.CreateCommand())
        {
            command.Transaction = transaction;
            command.CommandText =
                """
                DELETE FROM app_minute WHERE minute >= @from AND minute < @to;
                DELETE FROM app_quarter WHERE quarter >= @low AND quarter < @high;
                INSERT INTO app_quarter (quarter, app, down, up)
                SELECT minute / 15, app, SUM(down), SUM(up) FROM app_minute
                WHERE minute >= @low * 15 AND minute < @high * 15
                GROUP BY minute / 15, app;
                """;
            command.Parameters.AddWithValue("@from", fromMinute);
            command.Parameters.AddWithValue("@to", untilMinute);
            command.Parameters.AddWithValue("@low", FloorDiv(fromMinute, Quarter));
            command.Parameters.AddWithValue("@high", FloorDiv(untilMinute + Quarter - 1, Quarter));
            command.ExecuteNonQuery();
        }

        // Measured stretches are cut around the period.
        List<(long Start, long Until)> touched = [];

        using (SqliteCommand read = connection.CreateCommand())
        {
            read.Transaction = transaction;
            read.CommandText = "SELECT start_minute, until_minute FROM app_measured WHERE until_minute > @from AND start_minute < @to;";
            read.Parameters.AddWithValue("@from", fromMinute);
            read.Parameters.AddWithValue("@to", untilMinute);

            using SqliteDataReader reader = read.ExecuteReader();

            while (reader.Read())
            {
                touched.Add((reader.GetInt64(0), reader.GetInt64(1)));
            }
        }

        using SqliteCommand remove = connection.CreateCommand();
        remove.Transaction = transaction;
        remove.CommandText = "DELETE FROM app_measured WHERE start_minute = @start;";
        SqliteParameter removed = remove.Parameters.Add("@start", SqliteType.Integer);

        using SqliteCommand add = connection.CreateCommand();
        add.Transaction = transaction;
        add.CommandText = "INSERT INTO app_measured (start_minute, until_minute) VALUES (@start, @until);";
        SqliteParameter start = add.Parameters.Add("@start", SqliteType.Integer);
        SqliteParameter until = add.Parameters.Add("@until", SqliteType.Integer);

        foreach ((long spanStart, long spanUntil) in touched)
        {
            removed.Value = spanStart;
            remove.ExecuteNonQuery();

            if (spanStart < fromMinute)
            {
                start.Value = spanStart;
                until.Value = fromMinute;
                add.ExecuteNonQuery();
            }

            if (spanUntil > untilMinute)
            {
                start.Value = untilMinute;
                until.Value = spanUntil;
                add.ExecuteNonQuery();
            }
        }
    }

    private static long FloorDiv(long value, long by) =>
        (value / by) - ((value % by != 0 && (value < 0) != (by < 0)) ? 1 : 0);
}
