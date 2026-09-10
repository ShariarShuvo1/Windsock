using System.Net;
using Windsock.Core.Processes;
using Xunit;

namespace Windsock.Core.Tests;

public sealed class EndpointKeyTests
{
    [Theory]
    [InlineData("142.250.187.14", 443)]
    [InlineData("10.0.0.1", 53)]
    [InlineData("0.0.0.0", 0)]
    [InlineData("255.255.255.255", 65535)]
    public void AnIpv4Endpoint_SurvivesTheRoundTrip(string address, int port)
    {
        EndpointKey key = EndpointKey.From(IPAddress.Parse(address), port);

        Assert.Equal(address, key.Address());
        Assert.Equal(port, key.Port);
        Assert.False(key.IsIpv6);
    }

    [Theory]
    [InlineData("2606:4700:4700::1111", 443)]
    [InlineData("fe80::1", 5353)]
    [InlineData("::", 0)]
    public void AnIpv6Endpoint_SurvivesTheRoundTrip(string address, int port)
    {
        EndpointKey key = EndpointKey.From(IPAddress.Parse(address), port);

        Assert.Equal(address, key.Address());
        Assert.Equal(port, key.Port);
        Assert.True(key.IsIpv6);
    }

    [Fact]
    public void TheSameEndpoint_IsTheSameKey()
    {
        EndpointKey first = EndpointKey.From(IPAddress.Parse("1.1.1.1"), 443);
        EndpointKey again = EndpointKey.From(IPAddress.Parse("1.1.1.1"), 443);

        Assert.Equal(first, again);
        Assert.Equal(first.GetHashCode(), again.GetHashCode());
    }

    [Fact]
    public void APortApart_IsADifferentEndpoint()
    {
        EndpointKey https = EndpointKey.From(IPAddress.Parse("1.1.1.1"), 443);
        EndpointKey http = EndpointKey.From(IPAddress.Parse("1.1.1.1"), 80);

        Assert.NotEqual(https, http);
    }

    [Fact]
    public void TheTwoZeroAddresses_AreNotTheSameKey()
    {
        EndpointKey four = EndpointKey.From(IPAddress.Any, 0);
        EndpointKey six = EndpointKey.From(IPAddress.IPv6Any, 0);

        Assert.NotEqual(four, six);
    }

    [Fact]
    public void NoAddressAtAll_SaysSo()
    {
        EndpointKey nothing = EndpointKey.From(null, 443);

        Assert.False(nothing.HasAddress);
    }

    [Fact]
    public void AnAddress_SaysItHasOne()
    {
        Assert.True(EndpointKey.From(IPAddress.Parse("8.8.8.8"), 53).HasAddress);
    }
}
