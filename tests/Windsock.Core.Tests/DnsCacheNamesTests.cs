using Windsock.Core.Processes;
using Xunit;

namespace Windsock.Core.Tests;

public sealed class DnsCacheNamesTests
{
    [Fact]
    public void AnAddress_IsNamedByTheNameThatWasAskedFor()
    {
        IReadOnlyDictionary<string, string> names = DnsCacheNames.Index(
        [
            new DnsCacheEntry("www.example.com", 5, "example.cdn.net"),
            new DnsCacheEntry("www.example.com", 1, "93.184.216.34"),
            new DnsCacheEntry("ipv6.example.com.", 28, "2606:2800:220:1:248:1893:25c8:1946"),
        ]);

        Assert.Equal("www.example.com", names["93.184.216.34"]);
        Assert.Equal("ipv6.example.com", names["2606:2800:220:1:248:1893:25c8:1946"]);
    }

    [Fact]
    public void AReverseAnswer_IsUsedOnlyWhereNoForwardOneExists()
    {
        IReadOnlyDictionary<string, string> names = DnsCacheNames.Index(
        [
            new DnsCacheEntry("34.216.184.93.in-addr.arpa", 12, "provider-host.example.net"),
            new DnsCacheEntry("www.example.com", 1, "93.184.216.34"),
            new DnsCacheEntry("229.170.231.82.in-addr.arpa", 12, "82-231-170-229.subs.proxad.net."),
        ]);

        Assert.Equal("www.example.com", names["93.184.216.34"]);
        Assert.Equal("82-231-170-229.subs.proxad.net", names["82.231.170.229"]);
    }

    [Fact]
    public void ThisMachine_AndNonsense_AreNotNamed()
    {
        IReadOnlyDictionary<string, string> names = DnsCacheNames.Index(
        [
            new DnsCacheEntry("localhost", 1, "127.0.0.1"),
            new DnsCacheEntry("me.example.com", 28, "::1"),
            new DnsCacheEntry("broken.example.com", 1, "not an address"),
            new DnsCacheEntry("1.2.3.4", 1, "1.2.3.4"),
        ]);

        Assert.Empty(names);
    }

    [Theory]
    [InlineData("4.3.2.1.in-addr.arpa", "1.2.3.4")]
    [InlineData("1.0.0.0.0.0.0.0.0.0.0.0.0.0.0.0.0.0.0.0.0.0.0.0.8.b.d.0.1.0.0.2.ip6.arpa", "2001:db8::1")]
    [InlineData("3.2.1.in-addr.arpa", null)]
    [InlineData("example.com", null)]
    public void ReverseNames_GiveBackTheirAddress(string name, string? address) =>
        Assert.Equal(address, DnsCacheNames.FromReverse(name));

    [Fact]
    public async Task HostNames_ComeFromTheCache_WithoutAskingAnyResolver()
    {
        StubCache cache = new([new DnsCacheEntry("api.example.com", 1, "198.51.100.7")]);
        HostNameCache names = new(cache, askResolver: false);
        TaskCompletionSource resolved = new(TaskCreationOptions.RunContinuationsAsynchronously);
        names.Resolved += (_, _) => resolved.TrySetResult();

        // The first ask starts a reading of the cache; the name follows it.
        string? first = names.Find("198.51.100.7");

        if (first is null)
        {
            await resolved.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
        }

        Assert.Equal("api.example.com", names.Find("198.51.100.7"));

        // An address the cache cannot name stays unnamed rather than being
        // looked up, when looking up is not allowed.
        Assert.Null(names.Find("192.0.2.1"));
        Assert.Null(names.Find("127.0.0.1"));
    }

    [Fact]
    public void TheCacheOnThisMachine_CanBeRead()
    {
        IReadOnlyList<DnsCacheEntry> entries = new WmiDnsCacheSource().Read();

        // An empty cache is possible; a reading that throws is not.
        Assert.All(entries, entry => Assert.False(string.IsNullOrWhiteSpace(entry.Entry)));
    }

    [Fact]
    public void AProcessesPath_IsReadWithTheLeastAccess()
    {
        ProcessIdentity identity = new ProcessIdentityResolver().Resolve(Environment.ProcessId);

        Assert.Equal(Environment.ProcessPath, identity.FilePath, StringComparer.OrdinalIgnoreCase);
    }

    private sealed class StubCache(IReadOnlyList<DnsCacheEntry> entries) : IDnsCacheSource
    {
        public IReadOnlyList<DnsCacheEntry> Read() => entries;
    }
}
