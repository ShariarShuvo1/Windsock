using System.Globalization;
using Microsoft.Data.Sqlite;
using Windsock.Core.Networking;

namespace Windsock.Core.History;

/// <summary>Quarter-hour totals and networks; see <see cref="IUsageAggregates"/>.</summary>
public sealed partial class SqliteUsageHistoryStore
{
    /// <summary>
    /// Whole quarters inside a range, as [first, last) quarter numbers; the two
    /// are equal, and every minute an edge minute, when no whole quarter fits.
    /// </summary>
    private static (long First, long Last) WholeQuarters(long fromMinute, long untilMinute)
    {
        long first = FloorDiv(fromMinute + Quarter - 1, Quarter);
        long last = FloorDiv(untilMinute, Quarter);
        return first < last ? (first, last) : (FloorDiv(fromMinute, Quarter), FloorDiv(fromMinute, Quarter));
    }

    /// <summary>
    /// The SQL for a range's rows: whole quarters from their totals and the
    /// minutes at either edge, each as (start, network, down, up, peak_down,
    /// peak_up, minutes, lan_down, lan_up, lan_minutes). Bind with
    /// <see cref="BindRange"/>.
    /// </summary>
    /// <remarks>
    /// Each edge is its own plain range, so SQLite seeks to it. Written as one
    /// range with "outside the whole quarters" as an OR, it walked every minute
    /// of the range instead: 35 ms a year, measured, on every read.
    /// </remarks>
    private const string RangeRows =
        """
        SELECT quarter * 15 AS start, network, down, up, peak_down, peak_up, minutes, lan_down, lan_up, lan_minutes
        FROM usage_quarter
        WHERE quarter >= @firstQuarter AND quarter < @lastQuarter
        UNION ALL
        SELECT minute AS start, COALESCE(network, 0), down, up, peak_down, peak_up, 1,
               COALESCE(lan_down, 0), COALESCE(lan_up, 0), lan_down IS NOT NULL
        FROM usage_minute
        WHERE minute >= @from AND minute < MIN(@to, @firstQuarter * 15)
        UNION ALL
        SELECT minute AS start, COALESCE(network, 0), down, up, peak_down, peak_up, 1,
               COALESCE(lan_down, 0), COALESCE(lan_up, 0), lan_down IS NOT NULL
        FROM usage_minute
        WHERE minute >= MAX(@from, @lastQuarter * 15) AND minute < @to
        """;

    private static void BindRange(SqliteCommand command, long fromMinute, long untilMinute)
    {
        (long first, long last) = WholeQuarters(fromMinute, untilMinute);
        command.Parameters.AddWithValue("@firstQuarter", first);
        command.Parameters.AddWithValue("@lastQuarter", last);
        command.Parameters.AddWithValue("@from", fromMinute);
        command.Parameters.AddWithValue("@to", untilMinute);
    }

    /// <inheritdoc />
    public IReadOnlyList<UsageQuarter> ReadQuarters(long fromMinute, long untilMinute)
    {
        List<UsageQuarter> rows = [];

        if (untilMinute <= fromMinute)
        {
            return rows;
        }

        lock (_gate)
        {
            using SqliteCommand command = Connection().CreateCommand();
            command.CommandText = RangeRows + ";";
            BindRange(command, fromMinute, untilMinute);

            using SqliteDataReader reader = command.ExecuteReader();

            while (reader.Read())
            {
                rows.Add(new UsageQuarter(
                    reader.GetInt64(0),
                    reader.GetInt64(1),
                    reader.GetInt64(2),
                    reader.GetInt64(3),
                    reader.GetInt64(4),
                    reader.GetInt64(5),
                    reader.GetInt32(6),
                    reader.GetInt64(7),
                    reader.GetInt64(8),
                    reader.GetInt32(9)));
            }
        }

        return rows;
    }

    /// <inheritdoc />
    public IReadOnlyList<NetworkRecord> ReadNetworks()
    {
        Dictionary<long, (long First, long Last, long Down, long Up, long Minutes)> totals = [];
        List<NetworkRecord> networks = [];

        lock (_gate)
        {
            SqliteConnection connection = Connection();

            using (SqliteCommand sums = connection.CreateCommand())
            {
                sums.CommandText =
                    """
                    SELECT network, MIN(quarter), MAX(quarter), SUM(down), SUM(up), SUM(minutes)
                    FROM usage_quarter WHERE network <> 0 GROUP BY network;
                    """;
                using SqliteDataReader reader = sums.ExecuteReader();

                while (reader.Read())
                {
                    totals[reader.GetInt64(0)] = (reader.GetInt64(1), reader.GetInt64(2), reader.GetInt64(3), reader.GetInt64(4), reader.GetInt64(5));
                }
            }

            using SqliteCommand names = connection.CreateCommand();
            names.CommandText = "SELECT id, key, name, kind, metered FROM network ORDER BY id;";
            using SqliteDataReader rows = names.ExecuteReader();

            while (rows.Read())
            {
                long id = rows.GetInt64(0);
                bool known = totals.TryGetValue(id, out (long First, long Last, long Down, long Up, long Minutes) total);

                // The first and last minutes are the quarters' bounds, which is
                // as near as the totals go; a page wanting the exact minute reads it.
                networks.Add(new NetworkRecord(
                    id,
                    rows.GetString(1),
                    rows.GetString(2),
                    NetworkTag.KindFrom(rows.GetString(3)),
                    rows.IsDBNull(4) ? null : rows.GetInt64(4) != 0,
                    known ? total.First * Quarter : null,
                    known ? (total.Last * Quarter) + Quarter - 1 : null,
                    known ? total.Down : 0,
                    known ? total.Up : 0,
                    known ? total.Minutes : 0));
            }
        }

        return networks;
    }

    /// <inheritdoc />
    public IReadOnlyDictionary<string, long> ReadNetworkIds()
    {
        Dictionary<string, long> ids = new(StringComparer.Ordinal);

        lock (_gate)
        {
            using SqliteCommand command = Connection().CreateCommand();
            command.CommandText = "SELECT key, id FROM network;";
            using SqliteDataReader reader = command.ExecuteReader();

            while (reader.Read())
            {
                ids[reader.GetString(0)] = reader.GetInt64(1);
            }
        }

        return ids;
    }

    /// <inheritdoc />
    public IReadOnlyDictionary<long, NetworkTag> ReadNetworkTags()
    {
        lock (_gate)
        {
            return NetworkTags(Connection());
        }
    }

    /// <inheritdoc />
    public PeakMoment? ReadPeak(long fromMinute, long untilMinute, bool upload)
    {
        if (untilMinute <= fromMinute)
        {
            return null;
        }

        string column = upload ? "peak_up" : "peak_down";

        lock (_gate)
        {
            SqliteConnection connection = Connection();
            long start;

            // The quarter, or edge minute, holding the fastest second first;
            // then the minute inside it, so a year costs a quarter's read.
            using (SqliteCommand best = connection.CreateCommand())
            {
                best.CommandText = string.Concat(
                    "SELECT start, ", column, " FROM (", RangeRows, ") ORDER BY ", column, " DESC, start LIMIT 1;");
                BindRange(best, fromMinute, untilMinute);

                using SqliteDataReader reader = best.ExecuteReader();

                if (!reader.Read() || reader.GetInt64(1) <= 0)
                {
                    return null;
                }

                start = reader.GetInt64(0);
            }

            using SqliteCommand minute = connection.CreateCommand();
            minute.CommandText = string.Concat(
                "SELECT minute, ", column, " FROM usage_minute WHERE minute >= @start AND minute < @end ORDER BY ",
                column, " DESC, minute LIMIT 1;");
            minute.Parameters.AddWithValue("@start", Math.Max(fromMinute, start));
            minute.Parameters.AddWithValue("@end", Math.Min(untilMinute, start - (start % Quarter) + Quarter));

            using SqliteDataReader found = minute.ExecuteReader();
            return found.Read() ? new PeakMoment(found.GetInt64(0), found.GetInt64(1)) : null;
        }
    }

    /// <summary>
    /// Works out the quarter-hour totals touching a range again from the
    /// minutes. Call with the gate held, inside a transaction.
    /// </summary>
    private static void RebuildQuarters(SqliteConnection connection, SqliteTransaction transaction, long fromMinute, long untilMinute)
    {
        if (untilMinute <= fromMinute)
        {
            return;
        }

        using SqliteCommand command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText =
            """
            DELETE FROM usage_quarter WHERE quarter >= @low AND quarter < @high;
            INSERT INTO usage_quarter (quarter, network, down, up, peak_down, peak_up, minutes, lan_down, lan_up, lan_minutes)
            SELECT minute / 15, COALESCE(network, 0), SUM(down), SUM(up), MAX(peak_down), MAX(peak_up), COUNT(*),
                   SUM(COALESCE(lan_down, 0)), SUM(COALESCE(lan_up, 0)), COUNT(lan_down)
            FROM usage_minute
            WHERE minute >= @low * 15 AND minute < @high * 15
            GROUP BY minute / 15, COALESCE(network, 0);
            """;
        command.Parameters.AddWithValue("@low", Math.Max(0, FloorDiv(fromMinute, Quarter)));
        command.Parameters.AddWithValue("@high", Math.Max(0, FloorDiv(untilMinute + Quarter - 1, Quarter)));
        command.ExecuteNonQuery();
    }

    /// <summary>
    /// Forgets networks no minute is left on, so erasing the only time spent on
    /// a network takes its name out of history too. Call inside a transaction,
    /// with the quarter-hour totals of whatever was erased already worked out
    /// again: every minute is in them, so they are what is asked.
    /// </summary>
    private static void PruneNetworks(SqliteConnection connection, SqliteTransaction transaction) =>
        Execute(
            connection,
            transaction,
            """
            DELETE FROM network WHERE id NOT IN (SELECT DISTINCT network FROM usage_quarter);
            """);

    /// <summary>
    /// The row for each network the minutes name, made or brought up to date.
    /// Call with the gate held, inside the write's transaction.
    /// </summary>
    private static Dictionary<string, long> NetworkIds(SqliteConnection connection, SqliteTransaction transaction, IReadOnlyList<UsageMinute> minutes)
    {
        Dictionary<string, long> ids = new(StringComparer.Ordinal);
        SqliteCommand? upsert = null;

        try
        {
            foreach (UsageMinute row in minutes)
            {
                if (row.Network is not { } network || ids.ContainsKey(network.Key))
                {
                    continue;
                }

                if (upsert is null)
                {
                    upsert = connection.CreateCommand();
                    upsert.Transaction = transaction;
                    upsert.CommandText =
                        """
                        INSERT INTO network (key, name, kind, metered) VALUES (@key, @name, @kind, @metered)
                        ON CONFLICT (key) DO UPDATE SET
                            name    = excluded.name,
                            kind    = excluded.kind,
                            metered = COALESCE(excluded.metered, metered)
                        RETURNING id;
                        """;
                    upsert.Parameters.Add("@key", SqliteType.Text);
                    upsert.Parameters.Add("@name", SqliteType.Text);
                    upsert.Parameters.Add("@kind", SqliteType.Text);
                    upsert.Parameters.Add("@metered", SqliteType.Integer);
                }

                upsert.Parameters["@key"].Value = network.Key;
                upsert.Parameters["@name"].Value = network.Name;
                upsert.Parameters["@kind"].Value = NetworkTag.KindName(network.Kind);
                upsert.Parameters["@metered"].Value = network.Metered is { } metered ? (metered ? 1 : 0) : DBNull.Value;
                ids[network.Key] = Convert.ToInt64(upsert.ExecuteScalar(), CultureInfo.InvariantCulture);
            }
        }
        finally
        {
            upsert?.Dispose();
        }

        return ids;
    }

    /// <summary>The network rows by id, for reading minutes back with their networks.</summary>
    private static Dictionary<long, NetworkTag> NetworkTags(SqliteConnection connection)
    {
        Dictionary<long, NetworkTag> tags = [];
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = "SELECT id, key, name, kind, metered FROM network;";
        using SqliteDataReader reader = command.ExecuteReader();

        while (reader.Read())
        {
            tags[reader.GetInt64(0)] = new NetworkTag(
                reader.GetString(1),
                reader.GetString(2),
                NetworkTag.KindFrom(reader.GetString(3)),
                reader.IsDBNull(4) ? null : reader.GetInt64(4) != 0);
        }

        return tags;
    }

    /// <inheritdoc />
    public IReadOnlyList<UsageMinute> ReadMinuteRows(long fromMinute, long untilMinute)
    {
        List<UsageMinute> rows = [];

        if (untilMinute <= fromMinute)
        {
            return rows;
        }

        lock (_gate)
        {
            SqliteConnection connection = Connection();
            Dictionary<long, NetworkTag> tags = NetworkTags(connection);

            using SqliteCommand command = connection.CreateCommand();
            command.CommandText =
                """
                SELECT minute, down, up, peak_down, peak_up, network, lan_down, lan_up FROM usage_minute
                WHERE minute >= @from AND minute < @to ORDER BY minute;
                """;
            command.Parameters.AddWithValue("@from", fromMinute);
            command.Parameters.AddWithValue("@to", untilMinute);

            using SqliteDataReader reader = command.ExecuteReader();

            while (reader.Read())
            {
                rows.Add(new UsageMinute(reader.GetInt64(0), reader.GetInt64(1), reader.GetInt64(2), reader.GetInt64(3), reader.GetInt64(4))
                {
                    Network = reader.IsDBNull(5) ? null : tags.GetValueOrDefault(reader.GetInt64(5)),
                    LocalDown = reader.IsDBNull(6) ? null : reader.GetInt64(6),
                    LocalUp = reader.IsDBNull(7) ? null : reader.GetInt64(7),
                });
            }
        }

        return rows;
    }
}
