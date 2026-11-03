using Windsock.Core.Navigation;
using Xunit;

namespace Windsock.Core.Tests;

public sealed class SearchIndexTests
{
    private static SearchIndex Sample()
    {
        SearchIndex index = new();

        index.AddRange(
        [
            new SearchEntry("History", "history") { Where = "Usage", Kind = SearchKind.Page, Terms = "minutes export" },
            new SearchEntry("Status", "status") { Where = "Connection", Kind = SearchKind.Page, Terms = "online offline" },
            new SearchEntry("Response time", "status") { Where = "Connection › Status", Kind = SearchKind.Reading, Terms = "ping latency" },
            new SearchEntry("Address to ping", "status") { Where = "Connection › Status", Target = "Address to ping" },
            new SearchEntry("Theme", "appearance") { Where = "Settings › Appearance", Terms = "dark light colours" },
            new SearchEntry("Delete history", "privacy") { Where = "Settings › Data and privacy", Terms = "erase clear" },
            new SearchEntry("Reverse lookups", "privacy") { Where = "Settings › Data and privacy", Terms = "dns names" },
            new SearchEntry("Notify when the network or VPN changes", "alerts") { Where = "Alerts" },
        ]);

        return index;
    }

    [Fact]
    public void AnEmptyQuery_FindsNothing()
    {
        Assert.Empty(Sample().Find(""));
        Assert.Empty(Sample().Find("   "));
        Assert.Empty(Sample().Find(null));
    }

    [Fact]
    public void ATitle_ComesBeforeAnEntryFoundOnlyByItsTerms()
    {
        IReadOnlyList<SearchEntry> found = Sample().Find("ping");

        Assert.Equal(["Address to ping", "Response time"], found.Select(each => each.Title));
    }

    [Fact]
    public void OtherWords_FindAnEntry()
    {
        Assert.Equal("Delete history", Assert.Single(Sample().Find("erase")).Title);
        Assert.Equal("Reverse lookups", Assert.Single(Sample().Find("dns")).Title);
        Assert.Equal("Theme", Assert.Single(Sample().Find("dark")).Title);
    }

    [Fact]
    public void APage_LeadsWhatIsOnIt()
    {
        IReadOnlyList<SearchEntry> found = Sample().Find("history");

        Assert.Equal("History", found[0].Title);
        Assert.Equal(SearchKind.Page, found[0].Kind);
        Assert.Contains(found, each => each.Title == "Delete history");
    }

    [Fact]
    public void EveryWordTyped_MustBeginAWord()
    {
        Assert.Equal("Notify when the network or VPN changes", Assert.Single(Sample().Find("net ch")).Title);
        Assert.Empty(Sample().Find("network erase"));
    }

    [Fact]
    public void CaseAndPunctuation_DoNotMatter() =>
        Assert.Equal("Response time", Assert.Single(Sample().Find("RESPONSE-time")).Title);

    [Fact]
    public void AWordInsideATitle_IsFoundLast()
    {
        SearchIndex index = new();
        index.Add(new SearchEntry("Downloaded", "history"));
        index.Add(new SearchEntry("Load", "processor"));

        Assert.Equal(["Load", "Downloaded"], index.Find("load").Select(each => each.Title));
    }

    [Fact]
    public void TheSameEntry_IsHeldOnce()
    {
        SearchIndex index = new();
        index.Add(new SearchEntry("Theme", "appearance"));
        index.Add(new SearchEntry("theme", "appearance"));
        index.Add(new SearchEntry("Theme", "appearance") { Target = "Theme" });

        Assert.Equal(2, index.Count);
    }

    [Fact]
    public void Find_IsCappedAtWhatWasAskedFor()
    {
        SearchIndex index = new();

        for (int each = 0; each < 20; each++)
        {
            index.Add(new SearchEntry(string.Concat("Setting ", each.ToString(System.Globalization.CultureInfo.InvariantCulture)), "general"));
        }

        Assert.Equal(5, index.Find("setting", 5).Count);
    }
}
