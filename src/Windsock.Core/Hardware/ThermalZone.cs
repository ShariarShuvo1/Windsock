namespace Windsock.Core.Hardware;

internal sealed class ThermalZone
{
    private const string Counter = "thermal";

    private const string Precise = @"\Thermal Zone Information(*)\High Precision Temperature";

    private const string Coarse = @"\Thermal Zone Information(*)\Temperature";

    private const double Coldest = 5;
    private const double Hottest = 125;
    private const double Kelvin = 273.15;
    private bool _tenths;
    private bool _present;

    /// <summary>Whether this machine declares a thermal zone at all.</summary>
    public bool IsPresent => _present;

    /// <summary>Adds the counter this reads to the shared query.</summary>
    public void Register(PerformanceQuery query)
    {
        ArgumentNullException.ThrowIfNull(query);

        _tenths = query.Add(Counter, Precise, wildcard: true);
        _present = _tenths || query.Add(Counter, Coarse, wildcard: true);
    }

    /// <summary>
    /// The warmest zone the firmware reports, or nothing where it reports none.
    /// </summary>
    public double? Read(PerformanceQuery query)
    {
        ArgumentNullException.ThrowIfNull(query);

        if (!_present)
        {
            return null;
        }

        double? warmest = null;

        foreach (PerformanceQuery.Reading reading in query.Values(Counter))
        {
            double celsius = (_tenths ? reading.Value / 10 : reading.Value) - Kelvin;

            if (celsius is < Coldest or > Hottest)
            {
                continue;
            }

            if (warmest is null || celsius > warmest)
            {
                warmest = celsius;
            }
        }

        return warmest;
    }
}
