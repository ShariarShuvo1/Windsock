namespace Windsock.Core.Formatting;

/// <summary>
/// Chooses the value that sits at the top of a live chart.
/// </summary>
public static class ChartScale
{
    private static readonly double[] Mantissas = [1, 2, 5, 10, 20, 50, 100, 200, 500, 1024];

    /// <summary>
    /// Rounds <paramref name="peak"/> up to a readable axis maximum.
    /// </summary>
    public static double NiceMaximum(double peak, double floor)
    {
        double lowerBound = Math.Max(floor, 1);

        if (peak <= 0 || double.IsNaN(peak) || double.IsInfinity(peak))
        {
            return lowerBound;
        }
        double magnitude = Math.Pow(1024, Math.Floor(Math.Log(peak, 1024)));
        double mantissa = peak / magnitude;
        double rounded = 1024 * magnitude;

        foreach (double candidate in Mantissas)
        {
            if (mantissa <= candidate)
            {
                rounded = candidate * magnitude;
                break;
            }
        }

        return Math.Max(rounded, lowerBound);
    }
}
