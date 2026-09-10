using Windsock.Core.Hardware;
using Xunit;

namespace Windsock.Core.Tests;

/// <summary>
/// The contract the thermal zone keeps, on a machine that may or may not have one.
/// </summary>
public sealed class ThermalZoneTests
{
    private const double TooCold = 5;
    private const double TooHot = 125;

    [Fact]
    public void Reading_IsEitherNothingOrAPlausibleTemperature()
    {
        using PerformanceQuery? query = PerformanceQuery.Open();

        if (query is null)
        {
            return;
        }

        ThermalZone zone = new();
        zone.Register(query);
        query.Collect();
        query.Collect();

        double? celsius = zone.Read(query);

        if (celsius is null)
        {
            return;
        }
        Assert.InRange(celsius.Value, TooCold, TooHot);
    }

    [Fact]
    public void WithNoZoneDeclared_ReadsNothingRatherThanZero()
    {
        using PerformanceQuery? query = PerformanceQuery.Open();

        if (query is null)
        {
            return;
        }

        ThermalZone zone = new();
        zone.Register(query);
        query.Collect();
        query.Collect();

        if (!zone.IsPresent)
        {
            Assert.Null(zone.Read(query));
        }
    }

    [Fact]
    public void ReadingTwice_DoesNotThrow()
    {
        using PerformanceQuery? query = PerformanceQuery.Open();

        if (query is null)
        {
            return;
        }

        ThermalZone zone = new();
        zone.Register(query);

        for (int i = 0; i < 3; i++)
        {
            query.Collect();
            _ = zone.Read(query);
        }
    }

    [Fact]
    public void Register_RequiresAQuery()
    {
        ThermalZone zone = new();

        Assert.Throws<ArgumentNullException>(() => zone.Register(null!));
    }

    [Fact]
    public void BeforeRegistering_ThereIsNoZoneAndNoReading()
    {
        using PerformanceQuery? query = PerformanceQuery.Open();

        if (query is null)
        {
            return;
        }

        ThermalZone zone = new();

        Assert.False(zone.IsPresent);
        Assert.Null(zone.Read(query));
    }
}
