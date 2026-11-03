using System.Globalization;
using Windsock.Core.Connectivity;
using Windsock.Core.Formatting;
using Windsock.Core.Meter;
using Xunit;

namespace Windsock.Core.Tests;

/// <summary>
/// The hover card's sentences, and the minute it draws: one value a second,
/// the fastest of each, newest last.
/// </summary>
public sealed class CardWordsTests
{
    private static readonly ConnectionFacts Home = new(WindowsReach.Internet, null, "192.168.0.1", VpnActive: false);
    private static readonly ConnectionState Online = new(ConnectionStatus.Online, null, null);

    private static ConnectionSample Sample(double? router, double? internet, bool asked = true, WindowsReach reach = WindowsReach.Internet) =>
        new(
            DateTimeOffset.UnixEpoch,
            Home with { Reach = reach },
            router is { } r ? TimeSpan.FromMilliseconds(r) : null,
            internet is { } i ? TimeSpan.FromMilliseconds(i) : null,
            asked);

    [Fact]
    public void Online_NamesBothAnswers()
    {
        Assert.Equal("Online · router 2 ms · internet 14 ms", CardWords.Connection(Online, Sample(2.2, 13.6)));
    }

    [Fact]
    public void Online_UnderAMillisecond_SaysSo()
    {
        Assert.Equal("Online · router <1 ms", CardWords.Connection(Online, Sample(0.4, null, asked: false)));
    }

    [Fact]
    public void Online_AnInternetNobodyAskedAbout_IsLeftOut()
    {
        // The default checks only the router; the internet line would be a
        // guess, so it is not drawn.
        Assert.Equal("Online · router 3 ms", CardWords.Connection(Online, Sample(3, 20, asked: false)));
    }

    [Fact]
    public void Online_WithNothingMeasured_IsJustOnline()
    {
        Assert.Equal("Online", CardWords.Connection(Online, null));
    }

    [Fact]
    public void Offline_SaysWhyAndSince()
    {
        DateTimeOffset since = new(2026, 10, 5, 21, 4, 0, TimeZoneInfo.Local.GetUtcOffset(new DateTime(2026, 10, 5, 21, 4, 0, DateTimeKind.Local)));
        ConnectionState state = new(ConnectionStatus.Offline, OutageCause.Router, since);

        string expected = string.Concat("Router not answering since ", ClockFormat.Time(since.ToLocalTime()));

        Assert.Equal(expected, CardWords.Connection(state, Sample(null, null)));
    }

    [Fact]
    public void Offline_WithoutAStart_IsTheHeadlineAlone()
    {
        Assert.Equal("Internet unreachable", CardWords.Connection(new ConnectionState(ConnectionStatus.Offline, null, null), null));
    }

    [Fact]
    public void SignIn_WinsOverEverything()
    {
        Assert.Equal("Sign-in needed", CardWords.Connection(Online, Sample(2, null, reach: WindowsReach.SignInRequired)));
    }

    [Fact]
    public void Checking_SaysItIsChecking()
    {
        Assert.Equal("Checking the connection", CardWords.Connection(ConnectionState.Checking, null));
    }

    [Fact]
    public void Wifi_LeavesOutWhatIsNotKnown()
    {
        string full = CardWords.Wifi(78.4, "5 GHz", "Wi-Fi 6", 866.2);
        string some = CardWords.Wifi(null, "2.4 GHz", null, 0);

        Assert.Equal(string.Create(CultureInfo.CurrentCulture, $"{78:0}% · 5 GHz · Wi-Fi 6 · {866:0} Mbps"), full);
        Assert.Equal("2.4 GHz", some);
        Assert.Equal("Connected", CardWords.Wifi(null, null, "", null));
    }

    [Fact]
    public void Busiest_MostFirst_NothingThatMovedNothing()
    {
        IReadOnlyList<BusyApp> busiest = CardWords.Busiest(
            [new("Edge", 10), new("Steam", 900), new("Idle", 0), new("", 50), new("Teams", 300), new("Brave", 300)],
            3);

        Assert.Equal(["Steam", "Brave", "Teams"], busiest.Select(app => app.Name));
    }

    [Fact]
    public void Busiest_NoneAsked_IsEmpty()
    {
        Assert.Empty(CardWords.Busiest([new("Edge", 10)], 0));
        Assert.Empty(CardWords.Busiest([new("Edge", 10)], -2));
    }

    [Fact]
    public void Seconds_TakesTheFastestOfEachSecond()
    {
        double[] samples = [1, 5, 2, 8, 3, 3, 9, 1];
        double[] into = new double[60];

        int count = CardWords.Seconds(samples, TimeSpan.FromMilliseconds(500), into);

        Assert.Equal(4, count);
        Assert.Equal([5, 8, 3, 9], into[..4]);
    }

    [Fact]
    public void Seconds_AnOddSampleOut_StartsTheMinute()
    {
        // Counted from the newest, so a half-second left over is the oldest.
        double[] samples = [7, 1, 2, 3, 4];
        double[] into = new double[60];

        int count = CardWords.Seconds(samples, TimeSpan.FromMilliseconds(500), into);

        Assert.Equal(3, count);
        Assert.Equal([7, 2, 4], into[..3]);
    }

    [Fact]
    public void Seconds_KeepsOnlyTheNewestThatFit()
    {
        double[] samples = [.. Enumerable.Range(0, 100).Select(each => (double)each)];
        double[] into = new double[60];

        int count = CardWords.Seconds(samples, TimeSpan.FromSeconds(1), into);

        Assert.Equal(60, count);
        Assert.Equal(40, into[0]);
        Assert.Equal(99, into[59]);
    }

    [Fact]
    public void Seconds_ASecondWithNoReading_IsAGap()
    {
        double[] samples = [double.NaN, double.NaN, 4, double.NaN];
        double[] into = new double[60];

        int count = CardWords.Seconds(samples, TimeSpan.FromMilliseconds(500), into);

        Assert.Equal(2, count);
        Assert.True(double.IsNaN(into[0]));
        Assert.Equal(4, into[1]);
    }

    [Fact]
    public void Seconds_NothingToDo_IsNothing()
    {
        double[] into = new double[60];

        Assert.Equal(0, CardWords.Seconds([], TimeSpan.FromSeconds(1), into));
        Assert.Equal(0, CardWords.Seconds([1, 2], TimeSpan.Zero, into));
        Assert.Equal(0, CardWords.Seconds([1, 2], TimeSpan.FromSeconds(1), []));
    }
}
