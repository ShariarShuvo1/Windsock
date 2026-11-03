using Windsock.Core.Navigation;
using Xunit;

namespace Windsock.Core.Tests;

public sealed class PageLinkTests
{
    [Theory]
    [InlineData("history", true)]
    [InlineData("connection-status", true)]
    [InlineData("History", false)]
    [InlineData("his tory", false)]
    [InlineData("../history", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    [InlineData("abcdefghijklmnopqrstuvwxyzabcdefg", false)]
    public void IsName_TakesLowercaseLettersAndHyphensOnly(string? name, bool expected) =>
        Assert.Equal(expected, PageLink.IsName(name));

    [Fact]
    public void FromArguments_ReadsTheNameAfterTheSwitch() =>
        Assert.Equal("outages", PageLink.FromArguments(["--background", "--open", "outages"]));

    [Theory]
    [InlineData("--open=processor")]
    [InlineData("--open:processor")]
    [InlineData("--OPEN=processor")]
    public void FromArguments_ReadsTheJoinedForms(string argument) =>
        Assert.Equal("processor", PageLink.FromArguments([argument]));

    [Fact]
    public void FromArguments_RefusesWhatCannotBeAPage()
    {
        Assert.Null(PageLink.FromArguments(["--open", "C:\\Windows"]));
        Assert.Null(PageLink.FromArguments(["--open=Memory"]));
        Assert.Null(PageLink.FromArguments(["--open"]));
        Assert.Null(PageLink.FromArguments(["history"]));
        Assert.Null(PageLink.FromArguments(null));
    }

    [Fact]
    public void Arguments_IsWhatFromArgumentsReads()
    {
        string[] line = PageLink.Arguments("alerts").Split(' ');

        Assert.Equal("alerts", PageLink.FromArguments(line));
    }

    [Fact]
    public void Arguments_RefusesANameThatCannotBeAPage() =>
        Assert.Throws<ArgumentException>(() => PageLink.Arguments("Not a page"));
}
