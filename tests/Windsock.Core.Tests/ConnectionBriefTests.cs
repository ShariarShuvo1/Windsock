using Windsock.Core.Connectivity;
using Xunit;

namespace Windsock.Core.Tests;

public sealed class ConnectionBriefTests
{
    private static readonly DateTimeOffset Noon = new(2026, 10, 5, 12, 0, 0, TimeSpan.Zero);

    private static ConnectionState Online => new(ConnectionStatus.Online, null, Noon);

    private static ConnectionSample Sample(
        WindowsReach reach = WindowsReach.Internet,
        double? router = 4,
        double? internet = null,
        bool internetChecked = false) => new(
        Noon,
        new ConnectionFacts(reach, new NetworkIdentity("Home", NetworkKind.WiFi), "192.168.1.1", VpnActive: false),
        router is { } r ? TimeSpan.FromMilliseconds(r) : null,
        internet is { } i ? TimeSpan.FromMilliseconds(i) : null,
        internetChecked);

    [Fact]
    public void Online_SaysHowFastTheRouterAnswered()
    {
        Assert.Equal("Online · 4 ms", ConnectionBrief.Of(Online, Sample()));
    }

    /// <summary>The internet's answer is the one that says the internet is there, so it wins where there is one.</summary>
    [Fact]
    public void Online_PrefersTheInternetAnswer()
    {
        Assert.Equal("Online · 23 ms", ConnectionBrief.Of(Online, Sample(internet: 23, internetChecked: true)));
    }

    [Fact]
    public void Online_WithNoAnswerTimed_SaysOnlyOnline()
    {
        Assert.Equal("Online", ConnectionBrief.Of(Online, Sample(router: null)));
        Assert.Equal("Online", ConnectionBrief.Of(Online, null));
    }

    [Fact]
    public void AnAnswerUnderAMillisecond_IsNotCalledNought()
    {
        Assert.Equal("Online · <1 ms", ConnectionBrief.Of(Online, Sample(router: 0.3)));
    }

    [Fact]
    public void Offline_SaysOffline()
    {
        ConnectionState offline = new(ConnectionStatus.Offline, OutageCause.Router, Noon);

        Assert.Equal("Offline", ConnectionBrief.Of(offline, Sample(router: null)));
    }

    /// <summary>A captive portal is neither: the network is there, the internet is behind a page.</summary>
    [Fact]
    public void ASignInPage_SaysSoWhateverTheState()
    {
        Assert.Equal("Sign-in needed", ConnectionBrief.Of(Online, Sample(WindowsReach.SignInRequired)));
    }

    /// <summary>Nothing settled is nothing said, rather than a word nobody measured.</summary>
    [Fact]
    public void Checking_SaysNothing()
    {
        Assert.Equal(string.Empty, ConnectionBrief.Of(ConnectionState.Checking, Sample()));
    }
}
