using System.Globalization;
using Windsock.Core.Formatting;
using Xunit;

namespace Windsock.Core.Tests;

public sealed class ClockFormatTests
{
    private static readonly DateTime Evening = new(2026, 10, 5, 21, 4, 31, DateTimeKind.Local);

    private static DateTimeFormatInfo Culture(string name) =>
        CultureInfo.GetCultureInfo(name, predefinedOnly: false).DateTimeFormat;

    [Theory]
    [InlineData("HH:mm", true)]
    [InlineData("H:mm", true)]
    [InlineData("h:mm tt", false)]
    [InlineData("hh:mm", false)]
    [InlineData("tt h:mm", false)]
    [InlineData("HH'h'mm", true)]
    [InlineData("H.mm 'Uhr'", true)]
    [InlineData("", false)]
    [InlineData(null, false)]
    public void Uses24Hour_ReadsThePattern(string? pattern, bool expected) =>
        Assert.Equal(expected, ClockPatterns.Uses24Hour(pattern));

    [Fact]
    public void TwelveHourClock_WritesTheHalfOfTheDay()
    {
        DateTimeFormatInfo format = (DateTimeFormatInfo)Culture("en-US").Clone();
        format.ShortTimePattern = "h:mm tt";
        format.LongTimePattern = "h:mm:ss tt";

        ClockPatterns clock = new(format);

        Assert.False(clock.Is24Hour);
        Assert.Equal("9:04 PM", clock.Time(Evening));
        Assert.Equal("9:04:31 PM", clock.TimeWithSeconds(Evening));
        Assert.Equal("9 PM", clock.Hour(Evening));
    }

    [Fact]
    public void TwentyFourHourClock_NeverWritesAmOrPm()
    {
        // en-US with Windows' "Time format: 24-hour" chosen, which is how most
        // people meet this: the locale stays and only the patterns change.
        DateTimeFormatInfo format = (DateTimeFormatInfo)Culture("en-US").Clone();
        format.ShortTimePattern = "HH:mm";
        format.LongTimePattern = "HH:mm:ss";

        ClockPatterns clock = new(format);

        Assert.True(clock.Is24Hour);
        Assert.Equal("21:04", clock.Time(Evening));
        Assert.Equal("21:04:31", clock.TimeWithSeconds(Evening));
        Assert.Equal("21:00", clock.Hour(Evening.Date.AddHours(21)));
        Assert.DoesNotContain("PM", clock.Day(Evening), StringComparison.Ordinal);
    }

    [Fact]
    public void Day_PutsTheWeekdayAndDateBeforeTheTime()
    {
        DateTimeFormatInfo format = (DateTimeFormatInfo)Culture("en-GB").Clone();
        format.ShortTimePattern = "HH:mm";

        Assert.Equal("Mon 5 Oct, 21:04", new ClockPatterns(format).Day(Evening));
    }

    [Fact]
    public void ACultureWithoutAnHour_FallsBackToAPlainClockOfTheSameKind()
    {
        DateTimeFormatInfo format = (DateTimeFormatInfo)CultureInfo.InvariantCulture.DateTimeFormat.Clone();
        format.ShortTimePattern = "HH:mm";
        format.LongTimePattern = "mm:ss";

        ClockPatterns clock = new(format);

        Assert.Equal("HH:mm:ss", clock.LongTime);
    }

    [Fact]
    public void Current_FollowsTheRegionSettings()
    {
        // Whatever this machine is set to, the patterns are its own and agree
        // with themselves about which clock they are on.
        ClockPatterns current = ClockFormat.Current;

        Assert.Equal(ClockPatterns.Uses24Hour(current.ShortTime), current.Is24Hour);
        Assert.Equal(current.Is24Hour, !current.Time(Evening).Contains(current.Format.PMDesignator, StringComparison.Ordinal) || current.Format.PMDesignator.Length == 0);
    }

    [Fact]
    public void TheSameChoices_WriteAlike()
    {
        DateTimeFormatInfo format = (DateTimeFormatInfo)Culture("en-US").Clone();

        Assert.True(new ClockPatterns(format).WritesLike(new ClockPatterns((DateTimeFormatInfo)format.Clone())));
    }

    [Theory]
    [InlineData("AM")]
    [InlineData("PM")]
    [InlineData("time")]
    [InlineData("date")]
    [InlineData("short date")]
    public void AChangeOfWordsOrSeparatorsAlone_IsAChange(string what)
    {
        // Windows lets "AM" and "PM", and the separators, be changed with the
        // patterns left as they were: every list on screen still has to be
        // written again.
        DateTimeFormatInfo before = (DateTimeFormatInfo)Culture("en-US").Clone();
        before.ShortTimePattern = "h:mm tt";
        DateTimeFormatInfo after = (DateTimeFormatInfo)before.Clone();

        switch (what)
        {
            case "AM":
                after.AMDesignator = "a.m.";
                break;
            case "PM":
                after.PMDesignator = "p.m.";
                break;
            case "time":
                after.TimeSeparator = ".";
                break;
            case "date":
                after.DateSeparator = "-";
                break;
            default:
                after.ShortDatePattern = "yyyy-MM-dd";
                break;
        }

        ClockPatterns was = new(before);
        ClockPatterns now = new(after);

        Assert.Equal(was.ShortTime, now.ShortTime);
        Assert.False(was.WritesLike(now));
    }

    [Fact]
    public void ChangingTheFormatAfterwards_DoesNotChangeWhatWasRead()
    {
        DateTimeFormatInfo format = (DateTimeFormatInfo)Culture("en-US").Clone();
        ClockPatterns was = new(format);
        ClockPatterns copy = new((DateTimeFormatInfo)format.Clone());

        format.PMDesignator = "p.m.";

        Assert.True(was.WritesLike(copy));
    }

    [Fact]
    public void Refresh_KeepsThePatternsWhenNothingChanged()
    {
        string before = ClockFormat.Current.ShortTime;

        ClockFormat.Refresh();

        Assert.Equal(before, ClockFormat.Current.ShortTime);
    }
}
