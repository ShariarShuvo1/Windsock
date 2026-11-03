namespace Windsock.Core.Docking;

/// <summary>How the meter's readings are laid out on one particular bar.</summary>
public enum MeterArrangement
{
    /// <summary>The rows and columns the reader set.</summary>
    AsSet,

    /// <summary>
    /// Every reading on one line, for a bar too thin to stack the rows that
    /// were asked for: the Small taskbar, mostly.
    /// </summary>
    OneLine,

    /// <summary>
    /// Every reading in one column, for a bar down the side of the screen,
    /// which has length to spare and no width.
    /// </summary>
    Stacked,
}

/// <summary>
/// Decides how the readings are laid out to fit the bar they are on.
/// </summary>
public static class MeterLayout
{
    /// <summary>
    /// The least the figures are shrunk to fit a narrow bar down the side,
    /// as a share of the size they were asked for. Below this they would no
    /// longer be legible, so they keep this size and lose an edge instead.
    /// </summary>
    public const double SmallestShrink = 0.7;

    /// <summary>
    /// Picks the arrangement for a bar.
    /// </summary>
    /// <param name="edge">The bar's edge, or nothing for a meter on no bar at all.</param>
    /// <param name="rows">Rows the reader asked for.</param>
    /// <param name="columns">Columns the reader asked for.</param>
    /// <param name="room">Thickness the meter has across the bar, in device-independent pixels.</param>
    /// <param name="lineHeight">Height one row of readings needs, in device-independent pixels.</param>
    /// <remarks>
    /// The count is of places, not of readings: a grid with one reading and an
    /// empty place beside it is still two columns wide, which a side bar has
    /// no room for, and laid out again the empty place is dropped.
    /// </remarks>
    public static MeterArrangement Choose(
        TaskbarEdge? edge,
        int rows,
        int columns,
        double room,
        double lineHeight)
    {
        if (rows * columns <= 1 || edge is null)
        {
            return MeterArrangement.AsSet;
        }

        if (edge is TaskbarEdge.Left or TaskbarEdge.Right)
        {
            return MeterArrangement.Stacked;
        }

        bool fits = lineHeight <= 0 || rows * lineHeight <= room + 0.5;

        return rows > 1 && !fits ? MeterArrangement.OneLine : MeterArrangement.AsSet;
    }

    /// <summary>The rows and columns an arrangement draws.</summary>
    public static (int Rows, int Columns) Shape(
        MeterArrangement arrangement,
        int rows,
        int columns,
        int places) => arrangement switch
        {
            MeterArrangement.OneLine => (1, Math.Max(1, places)),
            MeterArrangement.Stacked => (Math.Max(1, places), 1),
            _ => (Math.Max(1, rows), Math.Max(1, columns)),
        };

    /// <summary>
    /// Which of the reader's places each place on the bar shows, as indices
    /// into the row-by-row list of places.
    /// </summary>
    /// <remarks>
    /// Laid out again, the readings go column by column, so a column the
    /// reader built as a pair - download over upload, processor over memory -
    /// stays a pair on one line or down one side. Places with nothing in them
    /// mean "leave a gap here" in a grid; in a line they would only be a hole,
    /// so they are left out.
    /// </remarks>
    public static int[] Order(MeterArrangement arrangement, int rows, int columns, IReadOnlyList<bool> filled)
    {
        ArgumentNullException.ThrowIfNull(filled);

        int across = Math.Max(1, columns);
        int down = Math.Max(1, rows);

        if (arrangement == MeterArrangement.AsSet)
        {
            int[] every = new int[across * down];

            for (int each = 0; each < every.Length; each++)
            {
                every[each] = each;
            }

            return every;
        }

        List<int> kept = new(across * down);

        for (int column = 0; column < across; column++)
        {
            for (int row = 0; row < down; row++)
            {
                int index = (row * across) + column;

                if (index < filled.Count && filled[index])
                {
                    kept.Add(index);
                }
            }
        }

        return kept.Count > 0 ? [.. kept] : [0];
    }

    /// <summary>
    /// How much the figures are shrunk so a stacked meter fits across a bar
    /// down the side of the screen.
    /// </summary>
    /// <param name="wanted">Width the readings take at the size asked for.</param>
    /// <param name="room">Width the bar leaves them.</param>
    public static double Shrink(double wanted, double room)
    {
        if (!double.IsFinite(wanted) || !double.IsFinite(room) || wanted <= 0 || room <= 0 || wanted <= room)
        {
            return 1;
        }

        return Math.Max(SmallestShrink, Math.Floor(room / wanted * 100) / 100);
    }
}
