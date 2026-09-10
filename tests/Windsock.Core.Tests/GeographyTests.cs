using System.Net;
using Windsock.Core.Geography;
using Xunit;

namespace Windsock.Core.Tests;

public sealed class MapProjectionTests
{
    [Fact]
    public void PutsTheOriginWhereTheEquatorMeetsTheMeridian()
    {
        (double x, double y) = MapProjection.Project(0, 0);

        Assert.Equal(0, x, 6);
        Assert.Equal(0, y, 6);
    }

    [Fact]
    public void NorthIsUpAndEastIsRight()
    {
        Assert.True(MapProjection.Project(50, 0).Y > 0);
        Assert.True(MapProjection.Project(-50, 0).Y < 0);
        Assert.True(MapProjection.Project(0, 50).X > 0);
        Assert.True(MapProjection.Project(0, -50).X < 0);
    }

    [Fact]
    public void ParallelsGetShorterTowardsThePoles()
    {
        double equator = MapProjection.Project(0, 180).X;
        double middle = MapProjection.Project(45, 180).X;
        double top = MapProjection.Project(85, 180).X;

        Assert.True(middle < equator, "45 degrees north is not narrower than the equator");
        Assert.True(top < middle, "85 degrees north is not narrower than 45");
        Assert.True(MapProjection.Project(90, 180).X > equator * 0.5);
    }

    [Fact]
    public void AgreesWithThePublishedTableAtItsOwnSamples()
    {
        double equator = MapProjection.Project(0, 180).X;

        Assert.Equal(0.9216, MapProjection.Project(40, 180).X / equator, 4);
        Assert.Equal(0.8679, MapProjection.Project(50, 180).X / equator, 4);
    }

    [Fact]
    public void TheMapIsWiderThanItIsTall()
    {
        (double width, double height) = MapProjection.Extent();

        Assert.Equal(width / height, MapProjection.Aspect, 6);
        Assert.InRange(MapProjection.Aspect, 1.9, 2.0);
    }

    [Fact]
    public void NowhereRunsOffTheMap()
    {
        (double width, double height) = MapProjection.Extent();

        for (int latitude = -90; latitude <= 90; latitude++)
        {
            for (int longitude = -180; longitude <= 180; longitude += 10)
            {
                (double x, double y) = MapProjection.Project(latitude, longitude);

                Assert.InRange(x, -width / 2 - 0.001, (width / 2) + 0.001);
                Assert.InRange(y, -height / 2 - 0.001, (height / 2) + 0.001);
            }
        }
    }
}

public sealed class WorldMapTests
{
    private static WorldMap? Map() =>
        File.Exists(IpLocations.Beside("world.map")) ? WorldMap.Read(IpLocations.Beside("world.map")) : null;

    [Fact]
    public void ReadsTheWholeWorld()
    {
        if (Map() is not { } map)
        {
            return;
        }

        Assert.InRange(map.Countries.Count, 150, 300);
        Assert.All(map.Countries, country => Assert.NotEmpty(country.Rings));
        Assert.All(map.Countries, country => Assert.NotEmpty(country.Name));
    }

    [Fact]
    public void KnowsWhichCountryAPointIsIn()
    {
        if (Map() is not { } map)
        {
            return;
        }

        Assert.Equal("France", map.At(2.35, 48.85)?.Name);          // Paris
        Assert.Equal("Japan", map.At(139.69, 35.68)?.Name);         // Tokyo
        Assert.Equal("Brazil", map.At(-46.63, -23.55)?.Name);       // Sao Paulo
        Assert.Equal("Australia", map.At(151.2, -33.87)?.Name);     // Sydney

        // The middle of the Pacific is nobody's.
        Assert.Null(map.At(-140, 0));
    }

    [Fact]
    public void FindsACountryByItsCode()
    {
        if (Map() is not { } map)
        {
            return;
        }

        Assert.Equal("Germany", map.Find("DE")?.Name);
        Assert.Equal("Germany", map.Find("de")?.Name);
        Assert.Null(map.Find(""));
        Assert.Null(map.Find("ZZ"));
    }

    [Fact]
    public void EveryCountryFitsInsideTheBoxItClaims()
    {
        if (Map() is not { } map)
        {
            return;
        }

        foreach (WorldCountry country in map.Countries)
        {
            foreach (double[] ring in country.Rings)
            {
                for (int at = 0; at < ring.Length; at += 2)
                {
                    Assert.True(
                        country.Around(ring[at], ring[at + 1]),
                        $"{country.Name} has a point outside its own bounds");
                }
            }
        }
    }

    [Fact]
    public void WritesShorterNamesOnTheMapThanInASentence()
    {
        if (Map() is not { } map)
        {
            return;
        }

        Assert.All(map.Countries, country => Assert.NotEmpty(country.Label));
        Assert.All(map.Countries, country => Assert.True(
            country.Label.Length <= country.Name.Length + 4,
            $"{country.Name} is labelled with something far longer than itself"));

        Assert.Equal("United States", map.Find("US")?.Label);
        Assert.Equal("Dem. Rep. Congo", map.Find("CD")?.Label);
        Assert.Equal("Bangladesh", map.Find("BD")?.Label);
    }

    [Fact]
    public void LabelsSitInsideTheCountriesTheyName()
    {
        if (Map() is not { } map)
        {
            return;
        }
        int inside = map.Countries.Count(
            country => country.Holds(country.LabelLongitude, country.LabelLatitude));

        Assert.True(inside > map.Countries.Count * 0.75, $"only {inside} labels landed in their country");
    }
}

public sealed class IpLocationsTests
{
    private static IpLocations? Table() => IpLocations.Open();

    [Fact]
    public void HoldsTheWholeInternet()
    {
        using IpLocations? places = Table();

        if (places is null)
        {
            return;
        }

        Assert.True(places.Ranges > 5_000_000, $"only {places.Ranges} ranges");
        Assert.True(places.Places > 100_000, $"only {places.Places} places");
        Assert.InRange(places.Countries, 150, 300);
    }

    [Theory]
    [InlineData("8.8.8.8", "US")]
    [InlineData("1.1.1.1", null)]
    [InlineData("208.67.222.222", "US")]
    [InlineData("212.58.244.22", "GB")]
    [InlineData("2001:4860:4860::8888", null)]
    public void FindsWhereAnAddressIs(string address, string? country)
    {
        using IpLocations? places = Table();

        if (places is null)
        {
            return;
        }

        WorldPlace? found = places.Find(address);

        Assert.NotNull(found);
        Assert.InRange(found.Value.Latitude, -90, 90);
        Assert.InRange(found.Value.Longitude, -180, 180);

        if (country is not null)
        {
            Assert.Equal(country, found.Value.Country);
        }
    }

    [Theory]
    [InlineData("127.0.0.1")]
    [InlineData("::1")]
    [InlineData("192.168.0.1")]
    [InlineData("10.4.5.6")]
    [InlineData("172.16.9.9")]
    [InlineData("169.254.1.1")]
    [InlineData("0.0.0.0")]
    [InlineData("not an address")]
    [InlineData(null)]
    public void SaysNothingAboutSomewhereThatIsNotOnTheInternet(string? address)
    {
        using IpLocations? places = Table();

        if (places is null)
        {
            return;
        }

        Assert.Null(places.Find(address));
    }

    [Fact]
    public void AnAddressAndTheOneAfterItUsuallyAgree()
    {
        using IpLocations? places = Table();

        if (places is null)
        {
            return;
        }
        WorldPlace? first = places.Find("8.8.8.8");
        WorldPlace? next = places.Find("8.8.8.9");

        Assert.Equal(first, next);
    }

    [Fact]
    public void TheFourFamiliesOfTheSameAddressAgree()
    {
        using IpLocations? places = Table();

        if (places is null)
        {
            return;
        }

        // An IPv4 address wearing an IPv6 coat is the same machine.
        WorldPlace? plain = places.Find(IPAddress.Parse("8.8.8.8"));
        WorldPlace? dressed = places.Find(IPAddress.Parse("::ffff:8.8.8.8"));

        Assert.Equal(plain, dressed);
    }

    [Fact]
    public void EveryPublicAddressLandsSomewhere()
    {
        using IpLocations? places = Table();

        if (places is null)
        {
            return;
        }
        Random dice = new(20260907);
        int found = 0;
        int asked = 0;

        for (int which = 0; which < 1000; which++)
        {
            byte[] bytes = new byte[4];
            dice.NextBytes(bytes);

            IPAddress address = new(bytes);
            uint plain = ((uint)bytes[0] << 24) | ((uint)bytes[1] << 16) | ((uint)bytes[2] << 8) | bytes[3];

            // Skip the ones that are nobody's on purpose.
            if (bytes[0] is 0 or 10 or 127 or >= 224 || (plain >> 20) == 0xAC1 || (plain >> 16) == 0xC0A8)
            {
                continue;
            }

            asked++;

            if (places.Find(address) is not null)
            {
                found++;
            }
        }

        Assert.True(found > asked * 0.98, $"only {found} of {asked} addresses landed anywhere");
    }

    [Fact]
    public void LooksThingsUpFasterThanTheGraphRedraws()
    {
        using IpLocations? places = Table();

        if (places is null)
        {
            return;
        }

        Random dice = new(1);
        byte[] bytes = new byte[4];
        for (int which = 0; which < 200; which++)
        {
            dice.NextBytes(bytes);
            _ = places.Find(new IPAddress(bytes));
        }

        System.Diagnostics.Stopwatch clock = System.Diagnostics.Stopwatch.StartNew();

        for (int which = 0; which < 5000; which++)
        {
            dice.NextBytes(bytes);
            _ = places.Find(new IPAddress(bytes));
        }

        double each = clock.Elapsed.TotalMilliseconds / 5000;

        // A map of a busy machine asks a few hundred times a second at most.
        Assert.True(each < 0.2, $"a lookup took {each:N3}ms");
    }

    [Fact]
    public void KnowsHowFarApartTwoPlacesAre()
    {
        WorldPlace london = new(51.5074, -0.1278, "London", "GB");
        WorldPlace york = new(40.7128, -74.0060, "New York", "US");

        Assert.InRange(london.From(york), 5560, 5590);
        Assert.Equal(0, london.From(london), 6);
    }

    [Fact]
    public void KnowsWhatLatencyRulesOut()
    {
        Assert.Equal(100, WorldPlace.Reachable(TimeSpan.FromMilliseconds(1)));
        Assert.Equal(1200, WorldPlace.Reachable(TimeSpan.FromMilliseconds(12)));

        WorldPlace london = new(51.5074, -0.1278, "London", "GB");
        WorldPlace york = new(40.7128, -74.0060, "New York", "US");
        Assert.True(london.From(york) > WorldPlace.Reachable(TimeSpan.FromMilliseconds(12)));
        Assert.True(london.From(york) < WorldPlace.Reachable(TimeSpan.FromMilliseconds(80)));
    }
}
