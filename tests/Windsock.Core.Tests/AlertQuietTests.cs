using Windsock.Core.Alerts;
using Windsock.Core.Notifications;
using Windsock.Core.Settings;
using Xunit;

namespace Windsock.Core.Tests;

/// <summary>
/// Quiet hours and the snooze, against the wall clock - midnight and both of
/// the year's clock changes included. London's: forward an hour at 01:00 GMT on
/// 29 March 2026, back an hour at 02:00 BST on 25 October 2026.
/// </summary>
public sealed class QuietHoursTests
{
    private static readonly TimeZoneInfo London = TimeZoneInfo.FindSystemTimeZoneById("GMT Standard Time");

    private static QuietHoursSettings Overnight => new() { Enabled = true, StartMinute = 22 * 60, EndMinute = 7 * 60 };

    /// <summary>A wall-clock time in London, taken where it is not ambiguous.</summary>
    private static DateTimeOffset At(int year, int month, int day, int hour, int minute)
    {
        DateTime wall = new(year, month, day, hour, minute, 0, DateTimeKind.Unspecified);
        return new DateTimeOffset(wall, London.GetUtcOffset(wall));
    }

    [Theory]
    [InlineData(23, 30, true)]
    [InlineData(0, 0, true)]
    [InlineData(6, 59, true)]
    [InlineData(7, 0, false)]
    [InlineData(12, 0, false)]
    [InlineData(21, 59, false)]
    [InlineData(22, 0, true)]
    public void AStretchOverMidnight_IsOneStretch(int hour, int minute, bool quiet)
    {
        DateTimeOffset moment = At(2026, 6, 10, hour, minute);

        Assert.Equal(quiet, QuietHours.StretchAt(Overnight, moment, London) is not null);
    }

    [Fact]
    public void AStretchBegunLastNight_EndsThisMorning()
    {
        QuietSpan span = QuietHours.StretchAt(Overnight, At(2026, 6, 10, 3, 0), London)!.Value;

        Assert.Equal(At(2026, 6, 9, 22, 0), span.Start);
        Assert.Equal(At(2026, 6, 10, 7, 0), span.End);
    }

    [Fact]
    public void AStretchWithinADay_DoesNotWrap()
    {
        QuietHoursSettings lunch = new() { Enabled = true, StartMinute = 13 * 60, EndMinute = 14 * 60 };

        Assert.NotNull(QuietHours.StretchAt(lunch, At(2026, 6, 10, 13, 30), London));
        Assert.Null(QuietHours.StretchAt(lunch, At(2026, 6, 10, 14, 0), London));
        Assert.Null(QuietHours.StretchAt(lunch, At(2026, 6, 10, 2, 0), London));
    }

    [Fact]
    public void SwitchedOff_IsNeverQuiet() =>
        Assert.Null(QuietHours.StretchAt(new QuietHoursSettings { StartMinute = 0, EndMinute = 23 * 60 }, At(2026, 6, 10, 3, 0), London));

    [Fact]
    public void TheSameStartAndEnd_IsAWholeDay()
    {
        QuietHoursSettings allDay = new() { Enabled = true, StartMinute = 9 * 60, EndMinute = 9 * 60 };

        QuietSpan span = QuietHours.StretchAt(allDay, At(2026, 6, 10, 20, 0), London)!.Value;

        Assert.Equal(TimeSpan.FromDays(1), span.End - span.Start);
    }

    [Fact]
    public void AStretchBegunOnAChosenDay_RunsIntoADayNotChosen()
    {
        // 10 June 2026 is a Wednesday; quiet hours begin only on Wednesdays.
        QuietHoursSettings wednesdays = Overnight;
        wednesdays.Days = [DayOfWeek.Wednesday];

        Assert.NotNull(QuietHours.StretchAt(wednesdays, At(2026, 6, 10, 23, 0), London));
        Assert.NotNull(QuietHours.StretchAt(wednesdays, At(2026, 6, 11, 6, 0), London));
        Assert.Null(QuietHours.StretchAt(wednesdays, At(2026, 6, 11, 23, 0), London));
        Assert.Null(QuietHours.StretchAt(wednesdays, At(2026, 6, 10, 6, 0), London));
    }

    [Fact]
    public void TheNightTheClocksGoForward_IsAnHourShorter()
    {
        QuietSpan span = QuietHours.StretchAt(Overnight, At(2026, 3, 29, 3, 0), London)!.Value;

        Assert.Equal(new DateTimeOffset(2026, 3, 28, 22, 0, 0, TimeSpan.Zero), span.Start);
        Assert.Equal(new DateTimeOffset(2026, 3, 29, 7, 0, 0, TimeSpan.FromHours(1)), span.End);
        Assert.Equal(TimeSpan.FromHours(8), span.End - span.Start);
    }

    [Fact]
    public void TheNightTheClocksGoBack_IsAnHourLonger()
    {
        QuietSpan span = QuietHours.StretchAt(Overnight, new DateTimeOffset(2026, 10, 25, 4, 0, 0, TimeSpan.Zero), London)!.Value;

        Assert.Equal(TimeSpan.FromHours(10), span.End - span.Start);
        Assert.Equal(new DateTimeOffset(2026, 10, 25, 7, 0, 0, TimeSpan.Zero), span.End);
    }

    [Fact]
    public void AnEndTheClockSkips_IsWhereTheClockLands()
    {
        // 01:30 does not happen on 29 March: the clock goes from 01:00 GMT to 02:00 BST.
        QuietHoursSettings small = new() { Enabled = true, StartMinute = 30, EndMinute = 90 };

        QuietSpan span = QuietHours.Stretch(small, new DateOnly(2026, 3, 29), London)!.Value;

        Assert.Equal(new DateTimeOffset(2026, 3, 29, 0, 30, 0, TimeSpan.Zero), span.Start);
        Assert.Equal(new DateTimeOffset(2026, 3, 29, 1, 0, 0, TimeSpan.Zero), span.End);
    }

    [Fact]
    public void AnEndTheClockPassesTwice_IsTheFirstPassing_AndTheRepeatIsNotQuiet()
    {
        // 01:30 happens twice on 25 October: at 00:30 UTC (BST), then 01:30 UTC (GMT).
        QuietHoursSettings small = new() { Enabled = true, StartMinute = 30, EndMinute = 90 };

        QuietSpan span = QuietHours.Stretch(small, new DateOnly(2026, 10, 25), London)!.Value;

        Assert.Equal(new DateTimeOffset(2026, 10, 25, 0, 30, 0, TimeSpan.FromHours(1)), span.Start);
        Assert.Equal(new DateTimeOffset(2026, 10, 25, 1, 30, 0, TimeSpan.FromHours(1)), span.End);

        // 01:15 on the second pass is 01:15 UTC, after the stretch ended.
        Assert.Null(QuietHours.StretchAt(small, new DateTimeOffset(2026, 10, 25, 1, 15, 0, TimeSpan.Zero), London));
    }

    [Fact]
    public void HeldUntil_IsTheLaterOfQuietHoursAndTheSnooze()
    {
        DateTimeOffset now = At(2026, 6, 10, 23, 0);

        Assert.Equal(At(2026, 6, 11, 7, 0), QuietHours.HeldUntil(Overnight, now.AddHours(1), now, London));
        Assert.Equal(At(2026, 6, 11, 9, 0), QuietHours.HeldUntil(Overnight, At(2026, 6, 11, 9, 0), now, London));
        Assert.Equal(now.AddHours(1), QuietHours.HeldUntil(new QuietHoursSettings(), now.AddHours(1), now, London));
    }

    [Fact]
    public void ASnoozeThatEnded_HoldsNothing()
    {
        DateTimeOffset now = At(2026, 6, 10, 12, 0);

        Assert.Null(QuietHours.HeldUntil(Overnight, now.AddMinutes(-1), now, London));
    }

    [Fact]
    public void UntilTomorrow_InTheSmallHours_MeansThisMorning()
    {
        Assert.Equal(At(2026, 6, 10, 7, 0), QuietHours.Morning(new QuietHoursSettings(), At(2026, 6, 10, 1, 0), London));
        Assert.Equal(At(2026, 6, 11, 7, 0), QuietHours.Morning(new QuietHoursSettings(), At(2026, 6, 10, 9, 0), London));
    }

    [Fact]
    public void UntilTomorrow_FollowsQuietHoursEnd_WhenTheyAreOn()
    {
        QuietHoursSettings quiet = Overnight;
        quiet.EndMinute = (6 * 60) + 30;

        Assert.Equal(At(2026, 6, 11, 6, 30), QuietHours.Morning(quiet, At(2026, 6, 10, 21, 0), London));
    }
}

public sealed class QuietSummaryTests
{
    private static readonly TimeZoneInfo Utc = TimeZoneInfo.Utc;

    private static AlertRecord Held(int minute, string topic, string title) =>
        new(minute, new DateTimeOffset(2026, 6, 10, 2, minute, 0, TimeSpan.Zero), topic, title, "message") { Outcome = AlertOutcome.Held };

    [Fact]
    public void Nothing_IsNoSummary() => Assert.Null(QuietSummary.Compose([], Utc));

    [Fact]
    public void EachHeldAlert_IsNamedWithItsTime_WhenTheyFit()
    {
        Notice summary = QuietSummary.Compose(
            [Held(14, NoticeTopics.ConnectionLost, "Internet connection lost"), Held(20, NoticeTopics.ConnectionRestored, "Back online")],
            Utc)!;

        Assert.Equal("2 alerts while notifications were held", summary.Title);
        Assert.Contains("Internet connection lost", summary.Message, StringComparison.Ordinal);
        Assert.Contains("Back online", summary.Message, StringComparison.Ordinal);
        Assert.True(summary.Message.IndexOf("lost", StringComparison.Ordinal) < summary.Message.IndexOf("Back", StringComparison.Ordinal));
        Assert.Equal(QuietSummary.Opens, summary.Opens);
        Assert.Equal(NoticeTopics.QuietSummary, summary.Topic);
    }

    [Fact]
    public void ManyHeld_NameTheMostSerious_AndCountTheRest()
    {
        Notice summary = QuietSummary.Compose(
            [
                Held(1, NoticeTopics.NetworkChanged, "Switched to Home"),
                Held(2, NoticeTopics.ConnectionLost, "Internet connection lost"),
                Held(3, NoticeTopics.NetworkChanged, "Switched to Office"),
                Held(4, NoticeTopics.PingHigh, "Slow responses from the internet"),
                Held(5, NoticeTopics.NetworkChanged, "Switched to Cafe"),
            ],
            Utc)!;

        Assert.Equal("5 alerts while notifications were held", summary.Title);
        Assert.Contains("Internet connection lost", summary.Message, StringComparison.Ordinal);
        Assert.Contains("Slow responses", summary.Message, StringComparison.Ordinal);
        Assert.Contains("and 3 more", summary.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("Cafe", summary.Message, StringComparison.Ordinal);
    }
}

public sealed class AlertWordsTests
{
    private static readonly DateTimeOffset Now = new(2026, 6, 10, 15, 0, 0, TimeSpan.FromHours(1));

    [Fact]
    public void WhatBecameOfAnAlert_IsSaidPlainly()
    {
        AlertRecord shown = new(1, Now, "t", "Title", "Message") { Outcome = AlertOutcome.Shown };

        Assert.Equal(string.Empty, AlertWords.Outcome(shown, Now));
        Assert.StartsWith("Held until", AlertWords.Outcome(shown with { Outcome = AlertOutcome.Held }, Now), StringComparison.Ordinal);
        Assert.Equal("Not shown. Notifications were off.", AlertWords.Outcome(shown with { Outcome = AlertOutcome.NotShown, Detail = "Notifications were off." }, Now));
        Assert.StartsWith("Held, then shown in the summary at ", AlertWords.Outcome(shown with { Outcome = AlertOutcome.Summarised, ShownAt = Now }, Now), StringComparison.Ordinal);
    }

    [Fact]
    public void Forwarding_IsSaidOnlyWhenItHadAPart()
    {
        AlertRecord record = new(1, Now, "t", "Title", "Message");

        Assert.Equal(string.Empty, AlertWords.Forwarding(record));
        Assert.Equal("Forwarded", AlertWords.Forwarding(record with { Forward = ForwardOutcome.Sent }));
        Assert.Equal("Not forwarded: ntfy.sh answered 404.", AlertWords.Forwarding(record with { Forward = ForwardOutcome.Failed, ForwardDetail = "ntfy.sh answered 404." }));
    }

    [Fact]
    public void Yesterday_IsSaidSo()
    {
        Assert.StartsWith("Yesterday ", AlertWords.When(Now.AddDays(-1), Now), StringComparison.Ordinal);
        Assert.DoesNotContain("Yesterday", AlertWords.When(Now.AddHours(-1), Now), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(0, "")]
    [InlineData(3, "3")]
    [InlineData(150, "99+")]
    public void TheBadge_CountsUnreadAndStopsAt99(int unread, string badge) =>
        Assert.Equal(badge, AlertWords.Badge(unread));
}

public sealed class NoticePacerHoldingTests
{
    private static Notice Say(string message) => new("topic", "Title", message);

    [Fact]
    public void AHeldNotice_DoesNotUseUpTheBurst()
    {
        NoticePacer pacer = new(new Clock());

        for (int each = 0; each < NoticePacer.BurstLimit * 2; each++)
        {
            Assert.Equal(PaceVerdict.Admitted, pacer.Pace(Say("Held " + each), holding: true));
        }

        Assert.Equal(PaceVerdict.Admitted, pacer.Pace(Say("Shown")));
    }

    [Fact]
    public void AHeldNotice_IsStillNotRepeated()
    {
        NoticePacer pacer = new(new Clock());

        Assert.Equal(PaceVerdict.Admitted, pacer.Pace(Say("Lost"), holding: true));
        Assert.Equal(PaceVerdict.Repeat, pacer.Pace(Say("Lost"), holding: true));
        Assert.Equal(PaceVerdict.Repeat, pacer.Pace(Say("Lost")));
    }

    [Fact]
    public void ABurst_SaysSo()
    {
        NoticePacer pacer = new(new Clock());

        for (int each = 0; each < NoticePacer.BurstLimit; each++)
        {
            pacer.Pace(Say("Message " + each));
        }

        Assert.Equal(PaceVerdict.Burst, pacer.Pace(Say("One more")));
    }

    private sealed class Clock : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => new(2026, 10, 5, 12, 0, 0, TimeSpan.Zero);
    }
}
