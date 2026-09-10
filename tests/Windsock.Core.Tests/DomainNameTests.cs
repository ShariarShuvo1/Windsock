using Windsock.Core.Networking;
using Xunit;

namespace Windsock.Core.Tests;

public sealed class DomainNameTests
{
    [Theory]
    [InlineData("mail.google.com", "google.com")]
    [InlineData("lhr48s09-in-f14.1e100.net", "1e100.net")]
    [InlineData("s3.ap-south-1.amazonaws.com", "amazonaws.com")]
    [InlineData("a.b.c.example.org", "example.org")]
    public void AHostName_FoldsToItsSite(string host, string expected) =>
        Assert.Equal(expected, DomainName.Group(host));

    [Theory]
    [InlineData("www.bbc.co.uk", "bbc.co.uk")]
    [InlineData("shop.example.com.au", "example.com.au")]
    [InlineData("a.b.university.ac.uk", "university.ac.uk")]
    public void ACountrySuffix_KeepsThreeLabels(string host, string expected) =>
        Assert.Equal(expected, DomainName.Group(host));

    [Theory]
    [InlineData("example.com")]
    [InlineData("localhost")]
    [InlineData("example.co")]
    public void AShortName_IsAlreadyItsOwnSite(string host) =>
        Assert.Equal(host, DomainName.Group(host));

    [Theory]
    [InlineData("142.250.187.14")]
    [InlineData("2606:4700:4700::1111")]
    [InlineData("127.0.0.1")]
    public void AnAddress_IsItsOwnGroup(string address)
    {
        Assert.Equal(address, DomainName.Group(address));
    }

    [Fact]
    public void ATrailingDot_DoesNotMakeAnEmptyLabel() =>
        Assert.Equal("google.com", DomainName.Group("mail.google.com."));

    [Fact]
    public void CaseDoesNotSplitASite() =>
        Assert.Equal(DomainName.Group("MAIL.Google.COM"), DomainName.Group("mail.google.com"));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void NothingToFold_IsNothing(string? host) =>
        Assert.Equal(string.Empty, DomainName.Group(host));
}
