using Windsock.Core.Formatting;
using Xunit;

namespace Windsock.Core.Tests;

public sealed class ClockTimeTests
{
    [Theory]
    [InlineData("14:30", 14, 30)]
    [InlineData("09:05", 9, 5)]
    [InlineData("9:5", 9, 5)]
    [InlineData("0:00", 0, 0)]
    [InlineData("23:59", 23, 59)]
    [InlineData("09.30", 9, 30)]
    public void ATimeWithASeparator_IsRead(string text, int hour, int minute)
    {
        Assert.True(ClockTime.TryParse(text, out TimeOnly time));
        Assert.Equal(new TimeOnly(hour, minute), time);
    }

    [Theory]
    [InlineData("9", 9, 0)]
    [InlineData("14", 14, 0)]
    [InlineData("930", 9, 30)]
    [InlineData("1430", 14, 30)]
    [InlineData("0930", 9, 30)]
    [InlineData("0", 0, 0)]
    public void DigitsWithoutASeparator_AreRead(string text, int hour, int minute)
    {
        Assert.True(ClockTime.TryParse(text, out TimeOnly time));
        Assert.Equal(new TimeOnly(hour, minute), time);
    }

    [Theory]
    [InlineData("24:00")]
    [InlineData("23:60")]
    [InlineData("2500")]
    [InlineData("1275")]
    public void ATimeThatDoesNotExist_IsRejected(string text) =>
        Assert.False(ClockTime.TryParse(text, out _));

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(null)]
    [InlineData("half nine")]
    [InlineData("14:")]
    [InlineData(":30")]
    [InlineData("1:2:3")]
    [InlineData("-1:00")]
    [InlineData("12345")]
    public void AnythingElse_IsRejectedRatherThanGuessedAt(string? text) =>
        Assert.False(ClockTime.TryParse(text, out _));

    [Theory]
    [InlineData(0, 0, "00:00")]
    [InlineData(9, 5, "09:05")]
    [InlineData(23, 59, "23:59")]
    public void ATimeIsWrittenWithBothDigits(int hour, int minute, string expected) =>
        Assert.Equal(expected, ClockTime.Format(new TimeOnly(hour, minute)));

    [Fact]
    public void WhatIsWritten_CanBeReadBack()
    {
        for (int hour = 0; hour < 24; hour++)
        {
            for (int minute = 0; minute < 60; minute += 7)
            {
                TimeOnly original = new(hour, minute);

                Assert.True(ClockTime.TryParse(ClockTime.Format(original), out TimeOnly read));
                Assert.Equal(original, read);
            }
        }
    }

    [Theory]
    [InlineData(0, 0, "12:00", "AM")]
    [InlineData(9, 5, "09:05", "AM")]
    [InlineData(11, 59, "11:59", "AM")]
    [InlineData(12, 0, "12:00", "PM")]
    [InlineData(13, 30, "01:30", "PM")]
    [InlineData(23, 59, "11:59", "PM")]
    public void ATimeIsWrittenOnATwelveHourClock(int hour, int minute, string clock, string half)
    {
        TimeOnly time = new(hour, minute);
        Assert.Equal(clock, ClockTime.FormatClock(time));
        Assert.Equal(half, ClockTime.Meridiem(time));
    }

    [Theory]
    [InlineData("2:30", false, 2, 30)]
    [InlineData("2:30", true, 14, 30)]
    [InlineData("230", true, 14, 30)]
    [InlineData("12:00", false, 0, 0)]
    [InlineData("12:00", true, 12, 0)]
    [InlineData("11:59", true, 23, 59)]
    public void ATimeTypedOnAClock_TakesTheHalfFromTheButton(
        string text,
        bool afternoon,
        int hour,
        int minute)
    {
        Assert.True(ClockTime.TryParseClock(text, afternoon, out TimeOnly time));
        Assert.Equal(new TimeOnly(hour, minute), time);
    }

    [Theory]
    [InlineData("1430", false, 14, 30)]
    [InlineData("23:00", false, 23, 0)]
    [InlineData("0", false, 0, 0)]
    public void AnHourPastTwelve_CarriesItsOwnHalf(string text, bool afternoon, int hour, int minute)
    {
        Assert.True(ClockTime.TryParseClock(text, afternoon, out TimeOnly time));
        Assert.Equal(new TimeOnly(hour, minute), time);
    }

    [Theory]
    [InlineData("24:00")]
    [InlineData("13:70")]
    [InlineData("noon")]
    [InlineData("")]
    public void AClockTimeThatIsNotOne_IsRejected(string text) =>
        Assert.False(ClockTime.TryParseClock(text, afternoon: false, out _));

    [Theory]
    [InlineData(9, 30, true, 21, 30)]
    [InlineData(21, 30, false, 9, 30)]
    [InlineData(0, 15, true, 12, 15)]
    [InlineData(12, 15, false, 0, 15)]
    public void FlippingTheHalf_KeepsTheHourOnTheClock(
        int hour,
        int minute,
        bool afternoon,
        int expectedHour,
        int expectedMinute)
    {
        TimeOnly moved = ClockTime.WithMeridiem(new TimeOnly(hour, minute), afternoon);

        Assert.Equal(new TimeOnly(expectedHour, expectedMinute), moved);
    }

    [Fact]
    public void EveryMinuteOfTheDay_SurvivesTheClockRoundTrip()
    {
        for (int hour = 0; hour < 24; hour++)
        {
            for (int minute = 0; minute < 60; minute += 7)
            {
                TimeOnly original = new(hour, minute);
                string written = ClockTime.FormatClock(original);
                bool afternoon = ClockTime.IsAfternoon(original);

                Assert.True(ClockTime.TryParseClock(written, afternoon, out TimeOnly read));
                Assert.Equal(original, read);
            }
        }
    }
}
