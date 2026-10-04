using System.Diagnostics;
using System.Net;
using Windsock.Core.Geography;
using Xunit;

namespace Windsock.Core.Tests;

public sealed class HomeGuessTests
{
    private static TimeZoneInfo Zone(string id) => TimeZoneInfo.FindSystemTimeZoneById(id);

    [Theory]
    [InlineData("Bangladesh Standard Time", null, "Dhaka")]
    [InlineData("India Standard Time", null, "Kolkata")]
    [InlineData("GMT Standard Time", null, "London")]
    [InlineData("Eastern Standard Time", null, "New York")]
    [InlineData("W. Europe Standard Time", "NL", "Amsterdam")]
    [InlineData("UTC", null, null)]
    public void AZone_IsNamedForItsCity(string zone, string? region, string? city) =>
        Assert.Equal(city, HomeGuess.ZoneCity(Zone(zone), region));

    [Theory]
    [InlineData("Bangladesh Standard Time", "US", "BD")]
    [InlineData("GMT Standard Time", "US", "GB")]
    [InlineData("Eastern Standard Time", "US", "US")]
    [InlineData("India Standard Time", "US", "IN")]
    [InlineData("Tokyo Standard Time", null, "JP")]
    public void TheClock_PutsHomeInItsCountry_WhateverWindowsRegionSays(string zone, string? region, string country)
    {
        using IpLocations? places = IpLocations.Open();

        if (places is null)
        {
            return;
        }

        var clock = Stopwatch.StartNew();
        WorldPlace? home = HomeGuess.ZonePlace(places, Zone(zone), region);

        Assert.NotNull(home);
        Assert.Equal(country, home.Value.Country);
        Assert.True(clock.Elapsed < TimeSpan.FromSeconds(5), $"took {clock.Elapsed}");
    }

    [Fact]
    public void AnAddressInAnotherCountry_DoesNotMoveHome()
    {
        using IpLocations? places = IpLocations.Open();

        if (places is null || places.Find("77.88.8.8") is not { Country: "RU" })
        {
            return;
        }

        // A VPN, or a guess from the far end: Dhaka's clock, a Moscow address.
        WorldPlace? home = HomeGuess.Estimate(places, Zone("Bangladesh Standard Time"), "US", [IPAddress.Parse("77.88.8.8")]);

        Assert.NotNull(home);
        Assert.Equal("BD", home.Value.Country);
    }

    [Fact]
    public void AHopInTheSameCountry_SharpensHome_AndOneAbroadDoesNot()
    {
        WorldPlace guess = new(23.8, 90.4, "Dhaka", "BD");
        WorldPlace near = new(22.3, 91.8, "Chittagong", "BD");
        WorldPlace far = new(55.7, 37.6, "Moscow", "RU");

        Assert.Equal(near, HomeGuess.Refine(guess, near));
        Assert.Equal(guess, HomeGuess.Refine(guess, far));
        Assert.Equal(far, HomeGuess.Refine(null, far));
        Assert.Null(HomeGuess.Refine(null, null));
    }

    [Theory]
    [InlineData("8.8.8.8", true)]
    [InlineData("2a02:6b8::feed:0ff", true)]
    [InlineData("192.168.0.101", false)]
    [InlineData("10.0.0.1", false)]
    [InlineData("100.64.1.1", false)]
    [InlineData("169.254.3.3", false)]
    [InlineData("fe80::6cd2:9267:831b:ae56", false)]
    [InlineData("fd12:3456::1", false)]
    [InlineData("2001:0:4136:e378::1", false)]
    [InlineData("::1", false)]
    [InlineData("239.1.1.1", false)]
    public void OnlyAddressesTheInternetRoutes_SayWhereTheMachineIs(string address, bool routed) =>
        Assert.Equal(routed, HomeGuess.IsPublic(IPAddress.Parse(address)));
}
