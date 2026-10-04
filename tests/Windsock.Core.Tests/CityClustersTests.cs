using Windsock.Core.Geography;
using Xunit;

namespace Windsock.Core.Tests;

public sealed class CityClustersTests
{
    // As the shipped place table answered for real addresses on 2026-09-13.
    private static readonly WorldPlace Dhaka = new(23.7806, 90.4038, "Dhaka", "BD");
    private static readonly WorldPlace Gulshan = new(23.7916, 90.4038, "Dhaka (Gulshan)", "BD");
    private static readonly WorldPlace Cantonment = new(23.7834, 90.3928, "Dhaka (Cantonment)", "BD");
    private static readonly WorldPlace NewYork = new(40.7138, -74.0062, "New York", "US");
    private static readonly WorldPlace Newark = new(40.7358, -74.1710, "Newark", "US");
    private static readonly WorldPlace Teterboro = new(40.8649, -74.0556, "Teterboro", "US");
    private static readonly WorldPlace Toronto = new(43.6528, -79.3841, "Toronto", "CA");
    private static readonly WorldPlace Montreal = new(45.5013, -73.5667, "Montreal", "CA");
    private static readonly WorldPlace Sydney = new(-33.8691, 151.2094, "Sydney", "AU");

    [Fact]
    public void DistrictsTheTableNamesApart_AreOneCity()
    {
        CityClusters cities = new();

        City first = cities.Join(Dhaka);

        Assert.Same(first, cities.Join(Gulshan));
        Assert.Same(first, cities.Join(Cantonment));
        Assert.Single(cities.Cities);
        Assert.Equal("Dhaka, BD", first.Name);
    }

    [Fact]
    public void SuburbsOfOneMetro_AreOneCity()
    {
        CityClusters cities = new();

        City first = cities.Join(NewYork);

        Assert.Same(first, cities.Join(Newark));
        Assert.Same(first, cities.Join(Teterboro));
    }

    [Fact]
    public void CitiesHundredsOfKilometresApart_StayApart()
    {
        CityClusters cities = new();

        Assert.NotSame(cities.Join(Toronto), cities.Join(Montreal));
        Assert.Equal(2, cities.Cities.Count);
    }

    [Fact]
    public void ABorder_IsNeverDrawnOver()
    {
        CityClusters cities = new();

        // Windsor sits across the river from Detroit, a couple of kilometres away.
        City detroit = cities.Join(new WorldPlace(42.3314, -83.0458, "Detroit", "US"));
        City windsor = cities.Join(new WorldPlace(42.3149, -83.0364, "Windsor", "CA"));

        Assert.NotSame(detroit, windsor);
    }

    [Fact]
    public void ACity_IsCalledWhatMostOfItsPlacesCallIt_AndDrawnThere()
    {
        CityClusters cities = new();

        City city = cities.Join(Teterboro);
        cities.Join(NewYork);
        cities.Join(NewYork);

        Assert.Equal("New York, US", city.Name);
        Assert.Equal(NewYork, city.Place);
    }

    [Fact]
    public void ATie_StaysWithTheNameThatCameFirst()
    {
        CityClusters cities = new();

        City city = cities.Join(Newark);
        cities.Join(NewYork);

        Assert.Equal("Newark, US", city.Name);
        Assert.Equal(Newark, city.Place);
    }

    [Fact]
    public void TheKey_DoesNotMoveAsPlacesJoin()
    {
        CityClusters cities = new();

        City city = cities.Join(Teterboro);
        string key = city.Key;

        cities.Join(NewYork);
        cities.Join(NewYork);

        Assert.Equal(key, city.Key);
    }

    [Fact]
    public void TheSameOrder_MakesTheSameCities()
    {
        WorldPlace[] seen = [Gulshan, Sydney, NewYork, Dhaka, Teterboro, Cantonment];

        CityClusters once = new();
        CityClusters again = new();

        string[] first = [.. seen.Select(place => once.Join(place).Key)];
        string[] second = [.. seen.Select(place => again.Join(place).Key)];

        Assert.Equal(first, second);
        Assert.Equal(3, once.Cities.Count);
    }

    [Fact]
    public void DistanceIsMeasuredFromWhereTheCityStarted_SoItCannotCreep()
    {
        CityClusters cities = new();

        // A line of places 30 km apart along a meridian: each is in reach of the
        // one before it, but the third is 60 km from the first.
        City start = cities.Join(new WorldPlace(10.00, 20, "A", "XX"));
        City second = cities.Join(new WorldPlace(10.27, 20, "B", "XX"));
        City third = cities.Join(new WorldPlace(10.54, 20, "C", "XX"));

        Assert.Same(start, second);
        Assert.NotSame(start, third);
    }

    [Fact]
    public void APlaceWithNoCity_IsCalledByItsCountry()
    {
        CityClusters cities = new();

        City city = cities.Join(new WorldPlace(23.7, 90.3, string.Empty, "BD"));

        Assert.Equal("BD", city.Name);
    }

    [Theory]
    [InlineData("Dhaka (Gulshan)", "Dhaka")]
    [InlineData("Dhaka", "Dhaka")]
    [InlineData("  Sydney  ", "Sydney")]
    [InlineData("(Somewhere)", "(Somewhere)")]
    [InlineData("", "")]
    public void BaseName_DropsTheDistrictInBrackets(string name, string expected) =>
        Assert.Equal(expected, CityClusters.BaseName(name));
}
