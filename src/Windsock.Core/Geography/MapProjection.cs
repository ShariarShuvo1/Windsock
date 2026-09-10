namespace Windsock.Core.Geography;

/// <summary>
/// Flattens the earth onto a rectangle, the way an atlas does.
/// </summary>
public static class MapProjection
{
    private static readonly double[] Lengths =
    [
        1.0000, 0.9986, 0.9954, 0.9900, 0.9822, 0.9730, 0.9600, 0.9427, 0.9216,
        0.8962, 0.8679, 0.8350, 0.7986, 0.7597, 0.7186, 0.6732, 0.6213, 0.5722, 0.5322,
    ];

    private static readonly double[] Heights =
    [
        0.0000, 0.0620, 0.1240, 0.1860, 0.2480, 0.3100, 0.3720, 0.4340, 0.4958,
        0.5571, 0.6176, 0.6769, 0.7346, 0.7903, 0.8435, 0.8936, 0.9394, 0.9761, 1.0000,
    ];

    private const double Across = 0.8487;
    private const double Down = 1.3523;

    /// <summary>How much wider the map is than it is tall.</summary>
    public static double Aspect => Across * Math.PI / Down;

    /// <summary>
    /// Puts a point on the map, in units where the equator is 2π·0.8487 long.
    /// </summary>
    public static (double X, double Y) Project(double latitude, double longitude)
    {
        double north = Math.Clamp(latitude, -90, 90);
        double east = Math.Clamp(longitude, -180, 180);
        double away = Math.Abs(north);

        double length = Sample(Lengths, away);
        double height = Sample(Heights, away);

        return (Across * length * east * Math.PI / 180, Down * height * Math.Sign(north));
    }

    /// <summary>
    /// Works out which point on the earth landed somewhere on the map.
    /// </summary>
    public static (double Latitude, double Longitude) Unproject(double x, double y)
    {
        double height = Math.Clamp(Math.Abs(y) / Down, 0, 1);

        double low = 0;
        double high = 90;

        for (int step = 0; step < 40; step++)
        {
            double middle = (low + high) / 2;

            if (Sample(Heights, middle) < height)
            {
                low = middle;
            }
            else
            {
                high = middle;
            }
        }

        double away = (low + high) / 2;
        double length = Sample(Lengths, away);

        return (
            away * Math.Sign(y),
            Math.Clamp(x / (Across * length) * 180 / Math.PI, -180, 180));
    }

    /// <summary>The width and height of the whole map in projected units.</summary>
    public static (double Width, double Height) Extent() =>
        (2 * Across * Math.PI, 2 * Down);

    private static double Sample(double[] table, double degrees)
    {
        double at = degrees / 5;
        int step = Math.Min((int)at, table.Length - 2);
        double part = at - step;

        double before = table[Math.Max(0, step - 1)];
        double here = table[step];
        double next = table[step + 1];
        double after = table[Math.Min(table.Length - 1, step + 2)];

        return 0.5 * ((2 * here)
            + ((next - before) * part)
            + (((2 * before) - (5 * here) + (4 * next) - after) * part * part)
            + ((-before + (3 * here) - (3 * next) + after) * part * part * part));
    }
}
