using Microsoft.Extensions.Logging.Abstractions;
using Windsock.Core.Alerts;
using Windsock.Core.Notifications;
using Windsock.Core.Settings;
using Xunit;

namespace Windsock.Core.Tests;

/// <summary>The one place every alert passes: the record, quiet hours, the snooze, sounds, forwarding.</summary>
public sealed class AlertDeskTests : IDisposable
{
    private static readonly TimeZoneInfo London = TimeZoneInfo.FindSystemTimeZoneById("GMT Standard Time");

    private readonly string _folder = Path.Combine(Path.GetTempPath(), "windsock-alert-desk-" + Guid.NewGuid().ToString("N"));
    private readonly Clock _clock = new(new DateTimeOffset(2026, 6, 10, 12, 0, 0, TimeSpan.FromHours(1)));
    private readonly WindsockSettings _settings = new();
    private readonly Display _display = new();
    private readonly Forwarder _forwarder = new();
    private readonly SqliteAlertLog _log;

    public AlertDeskTests()
    {
        Directory.CreateDirectory(_folder);
        _log = new SqliteAlertLog(Path.Combine(_folder, "Alerts.db"));
    }

    public void Dispose()
    {
        _log.Dispose();
        Directory.Delete(_folder, recursive: true);
    }

    private AlertDesk Desk()
    {
        AlertDesk desk = new(_settings, _log, _forwarder, _clock, NullLogger<AlertDesk>.Instance);
        desk.Attach(_display);
        return desk;
    }

    private static Notice Lost(string title = "Internet connection lost") =>
        new(NoticeTopics.ConnectionLost, title, "Your internet stopped answering.") { Tag = "connection", Opens = "status" };

    [Fact]
    public void AnAlertShown_IsKeptAsShown_AndUnread()
    {
        using AlertDesk desk = Desk();

        Assert.Equal(AlertRoute.Shown, desk.Route(Lost()));

        AlertRecord kept = Assert.Single(_log.Read(AlertQuery.Everything));
        Assert.Equal(AlertOutcome.Shown, kept.Outcome);
        Assert.Equal(_clock.GetLocalNow(), kept.ShownAt);
        Assert.Equal("status", kept.Opens);
        Assert.Equal("connection", kept.Tag);
        Assert.False(kept.IsRead);
        Assert.Equal(1, _log.UnreadCount());
        Assert.Single(_display.Shown);
    }

    [Fact]
    public void WithNotificationsOff_ItIsStillKept_AsNotShown()
    {
        _settings.Notifications.Enabled = false;
        using AlertDesk desk = Desk();

        Assert.Equal(AlertRoute.NotShown, desk.Route(Lost()));

        AlertRecord kept = Assert.Single(_log.Read(AlertQuery.Everything));
        Assert.Equal(AlertOutcome.NotShown, kept.Outcome);
        Assert.Equal("Notifications were off.", kept.Detail);
        Assert.Empty(_display.Shown);
    }

    [Fact]
    public void WhenWindowsRefusesIt_TheRecordSaysWhy()
    {
        _display.Refuse = true;
        using AlertDesk desk = Desk();

        Assert.Equal(AlertRoute.NotShown, desk.Route(Lost()));
        Assert.Equal(Display.Reason, Assert.Single(_log.Read(AlertQuery.Everything)).Detail);
    }

    [Fact]
    public void TheSameAlert_AMomentLater_IsNotKeptTwice()
    {
        using AlertDesk desk = Desk();

        desk.Route(Lost());
        _clock.Advance(TimeSpan.FromSeconds(20));

        Assert.Equal(AlertRoute.Repeat, desk.Route(Lost()));
        Assert.Single(_log.Read(AlertQuery.Everything));
    }

    [Fact]
    public void TooManyAtOnce_AreKeptButNotShown()
    {
        using AlertDesk desk = Desk();

        for (int each = 0; each < NoticePacer.BurstLimit; each++)
        {
            Assert.Equal(AlertRoute.Shown, desk.Route(Lost("Lost " + each)));
        }

        Assert.Equal(AlertRoute.NotShown, desk.Route(Lost("One too many")));

        AlertRecord last = _log.Read(AlertQuery.Everything)[0];
        Assert.Equal(AlertOutcome.NotShown, last.Outcome);
        Assert.Contains("too many", last.Detail, StringComparison.Ordinal);
    }

    [Fact]
    public void DuringQuietHours_AnAlertIsHeld_AndShownAsOneSummaryWhenTheyEnd()
    {
        _clock.Now = new DateTimeOffset(2026, 6, 10, 23, 0, 0, TimeSpan.FromHours(1));
        _settings.Alerts.Quiet = new QuietHoursSettings { Enabled = true, StartMinute = 22 * 60, EndMinute = 7 * 60 };
        using AlertDesk desk = Desk();

        Assert.Equal(AlertRoute.Held, desk.Route(Lost()));
        _clock.Advance(TimeSpan.FromMinutes(5));
        Assert.Equal(AlertRoute.Held, desk.Route(new Notice(NoticeTopics.ConnectionRestored, "Back online", "It came back.")));

        Assert.Empty(_display.Shown);
        Assert.Equal(2, desk.HeldCount);
        Assert.Equal(new DateTimeOffset(2026, 6, 11, 7, 0, 0, TimeSpan.FromHours(1)), desk.HeldUntil);

        desk.Release();
        Assert.Empty(_display.Shown);

        _clock.Now = new DateTimeOffset(2026, 6, 11, 7, 0, 0, TimeSpan.FromHours(1));
        desk.Release();

        Notice summary = Assert.Single(_display.Shown);
        Assert.Equal(NoticeTopics.QuietSummary, summary.Topic);
        Assert.Equal(0, desk.HeldCount);
        Assert.All(_log.Read(AlertQuery.Everything), each => Assert.Equal(AlertOutcome.Summarised, each.Outcome));
        Assert.Empty(_log.Held());
    }

    [Fact]
    public void AKindThatBreaksQuiet_IsShownDuringQuietHours()
    {
        _clock.Now = new DateTimeOffset(2026, 6, 10, 23, 0, 0, TimeSpan.FromHours(1));
        _settings.Alerts.Quiet = new QuietHoursSettings { Enabled = true };
        _settings.Alerts.Choices = new() { [NoticeTopics.ConnectionLost] = new AlertChoice { BreaksQuiet = true } };
        using AlertDesk desk = Desk();

        Assert.Equal(AlertRoute.Shown, desk.Route(Lost()));
    }

    [Fact]
    public void ASnooze_HoldsAlerts_AndSurvivesARestart()
    {
        using (AlertDesk desk = Desk())
        {
            desk.Snooze(TimeSpan.FromHours(1));
            Assert.Equal(AlertRoute.Held, desk.Route(Lost()));
        }

        using AlertDesk again = Desk();

        Assert.Equal(_clock.GetLocalNow().AddHours(1), again.SnoozedUntil);
        Assert.Equal(1, again.HeldCount);

        again.EndSnooze();

        Assert.Single(_display.Shown);
        Assert.Null(again.SnoozedUntil);
        Assert.Equal(AlertOutcome.Summarised, Assert.Single(_log.Read(AlertQuery.Everything)).Outcome);
    }

    [Fact]
    public void HeldAlerts_FromARunThatEnded_AreShownByTheNext()
    {
        using (AlertDesk desk = Desk())
        {
            desk.Snooze(TimeSpan.FromMinutes(30));
            desk.Route(Lost());
        }

        _clock.Advance(TimeSpan.FromHours(1));
        using AlertDesk again = Desk();
        again.Release();

        Assert.Equal(NoticeTopics.QuietSummary, Assert.Single(_display.Shown).Topic);
    }

    [Fact]
    public void TheSoundChosenForAKind_GoesWithIt()
    {
        _settings.Alerts.Choices = new() { [NoticeTopics.ConnectionLost] = new AlertChoice { Sound = NoticeSound.Reminder } };
        using AlertDesk desk = Desk();

        desk.Route(Lost());

        Assert.Equal(NoticeSound.Reminder, Assert.Single(_display.Shown).Sound);
    }

    [Fact]
    public void ACooldown_SpansRestarts()
    {
        Notice full = new(NoticeTopics.DriveFull, "Drive C: is nearly full", "It is 95% full.") { Tag = "drive-full:Drive C:" };

        using (AlertDesk desk = Desk())
        {
            Assert.Equal(AlertRoute.Shown, desk.Route(full));
        }

        _clock.Advance(TimeSpan.FromHours(3));

        using (AlertDesk again = Desk())
        {
            Assert.Equal(AlertRoute.Repeat, again.Route(full));
            Assert.Equal(AlertRoute.Shown, again.Route(full with { Tag = "drive-full:Drive D:" }));
        }

        _clock.Advance(TimeSpan.FromDays(1));

        using AlertDesk later = Desk();
        Assert.Equal(AlertRoute.Shown, later.Route(full));
    }

    [Fact]
    public void ACooldown_SurvivesTheListBeingClearedOrPruned()
    {
        Notice wear = new(NoticeTopics.DriveWear, "Drive is wearing out", "It has used 85% of its rated life.") { Tag = "drive-wear:0:SSD:80" };
        using AlertDesk desk = Desk();

        Assert.Equal(AlertRoute.Shown, desk.Route(wear));

        _log.Clear();
        _clock.Advance(TimeSpan.FromMinutes(5));
        Assert.Equal(AlertRoute.Repeat, desk.Route(wear));

        _clock.Advance(TimeSpan.FromDays(400));
        _log.Prune(_clock.GetLocalNow());
        Assert.Equal(AlertRoute.Repeat, desk.Route(wear));

        using SqliteAlertLog reopened = new(Path.Combine(_folder, "Alerts.db"));
        Assert.NotNull(reopened.LastTold(NoticeTopics.DriveWear, "drive-wear:0:SSD:80"));
    }

    [Fact]
    public void BackOnline_WithoutALostThatWasTold_IsDropped()
    {
        using AlertDesk desk = Desk();
        Notice back = new(NoticeTopics.ConnectionRestored, "Back online", "It came back.");

        Assert.Equal(AlertRoute.Repeat, desk.Route(back));
        Assert.Empty(_log.Read(AlertQuery.Everything));
        Assert.Empty(_display.Shown);
    }

    [Fact]
    public void BackOnline_FollowsWhatBecameOfLost()
    {
        Notice back = new(NoticeTopics.ConnectionRestored, "Back online", "It came back.");

        using (AlertDesk desk = Desk())
        {
            Assert.Equal(AlertRoute.Shown, desk.Route(Lost()));
            Assert.Equal(AlertRoute.Shown, desk.Route(back));

            // A second back for the same outage has nothing to follow.
            Assert.Equal(AlertRoute.Repeat, desk.Route(back with { Message = "Again." }));
        }

        _settings.Notifications.Enabled = false;
        _clock.Advance(TimeSpan.FromMinutes(5));

        using AlertDesk off = Desk();
        Assert.Equal(AlertRoute.NotShown, off.Route(Lost("Lost again")));
        Assert.Equal(AlertRoute.NotShown, off.Route(back with { Message = "Back again." }));
        Assert.Equal(AlertOutcome.NotShown, _log.Read(AlertQuery.Everything)[0].Outcome);
    }

    [Fact]
    public void ClearingDuringQuietHours_EmptiesTheHeldCount()
    {
        _clock.Now = new DateTimeOffset(2026, 6, 10, 23, 0, 0, TimeSpan.FromHours(1));
        _settings.Alerts.Quiet = new QuietHoursSettings { Enabled = true };
        using AlertDesk desk = Desk();

        desk.Route(Lost());
        Assert.Equal(1, desk.HeldCount);

        _log.Clear();
        desk.Refresh();

        Assert.Equal(0, desk.HeldCount);
    }

    [Fact]
    public void ForwardsARunLeftWaiting_AreMarkedNotForwarded()
    {
        long id = _log.Add(new AlertRecord(0, _clock.GetLocalNow(), NoticeTopics.UploadHigh, "Unusual upload", "x") { Forward = ForwardOutcome.Waiting });

        using AlertDesk desk = Desk();

        AlertRecord read = _log.Read(AlertQuery.Everything).Single(each => each.Id == id);
        Assert.Equal(ForwardOutcome.Failed, read.Forward);
        Assert.Contains("closed", read.ForwardDetail, StringComparison.Ordinal);
    }

    [Fact]
    public void Forwarding_TakesOnlyWhatIsSetUp()
    {
        using AlertDesk desk = Desk();
        desk.Route(Lost("Not forwarded: off"));

        _settings.Alerts.Forward.Enabled = true;
        _forwarder.IsReady = true;
        desk.Route(Lost("Forwarded"));

        _settings.Alerts.Choices = new() { [NoticeTopics.ConnectionLost] = new AlertChoice { Forward = false } };
        desk.Route(Lost("Not forwarded: this kind is not"));

        AlertRecord sent = Assert.Single(_forwarder.Taken);
        Assert.Equal("Forwarded", sent.Title);
        Assert.True(sent.Id > 0);
        Assert.Equal(ForwardOutcome.Waiting, _log.Read(AlertQuery.Everything).Single(each => each.Title == "Forwarded").Forward);
    }

    [Fact]
    public void ForwardingIgnoresQuietHours_ButNotFloodControl()
    {
        _settings.Alerts.Forward.Enabled = true;
        _forwarder.IsReady = true;
        using AlertDesk desk = Desk();

        desk.Snooze(TimeSpan.FromHours(1));
        desk.Route(Lost());
        desk.Route(Lost());

        Assert.Single(_forwarder.Taken);
    }

    [Fact]
    public void Forwarding_IsKeptInTheRecord_AndTakenUpByTheNextRun()
    {
        using (AlertDesk desk = Desk())
        {
            Assert.True(desk.SaveForwarding(new ForwardSettings { Enabled = true, NtfyTopic = "home-pc", SenderName = "Desk" }));
        }

        WindsockSettings fresh = new();
        using AlertDesk again = new(fresh, _log, _forwarder, _clock, NullLogger<AlertDesk>.Instance);
        again.Attach(_display);

        Assert.True(fresh.Alerts.Forward.Enabled);
        Assert.Equal("home-pc", fresh.Alerts.Forward.NtfyTopic);
        Assert.Equal("Desk", fresh.Alerts.Forward.SenderName);
    }

    [Fact]
    public void AnEvent_IsKeptForTheCharts_ButIsNoAlert()
    {
        using AlertDesk desk = Desk();

        desk.Note(NoticeTopics.NetworkChanged, "Switched to Home", "This PC was on Office before.");

        Assert.Empty(_log.Read(AlertQuery.Everything));
        AlertRecord kept = Assert.Single(_log.Read(AlertQuery.Everything with { IncludeEvents = true }));
        Assert.Equal(AlertOutcome.Event, kept.Outcome);
        Assert.Equal(0, _log.UnreadCount());
        Assert.Empty(_display.Shown);
    }

    [Fact]
    public void ARecordThatCannotOpen_StillLetsAlertsThrough()
    {
        string blocked = Path.Combine(_folder, "blocked");
        File.WriteAllText(blocked, "a file where the folder should be");

        using SqliteAlertLog broken = new(Path.Combine(blocked, "Alerts.db"));
        using AlertDesk desk = new(_settings, broken, _forwarder, _clock, NullLogger<AlertDesk>.Instance);
        desk.Attach(_display);

        Assert.False(broken.IsOpen);
        Assert.Equal(AlertRoute.Shown, desk.Route(Lost()));
    }

    private sealed class Display : INoticeDisplay
    {
        public const string Reason = "Windows has notifications turned off for Windsock.";

        public List<Notice> Shown { get; } = [];

        public bool Refuse { get; set; }

        public string WhyNotShown => Refuse ? Reason : string.Empty;

        public bool Show(Notice notice)
        {
            if (Refuse)
            {
                return false;
            }

            Shown.Add(notice);
            return true;
        }

        public void Post(Action action)
        {
            // Timers in a test are left alone; the test releases by hand.
        }
    }

    private sealed class Forwarder : IAlertForwarder
    {
        public bool IsReady { get; set; }

        public List<AlertRecord> Taken { get; } = [];

        public void Forward(AlertRecord record) => Taken.Add(record);
    }

    private sealed class Clock(DateTimeOffset now) : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = now;

        public override TimeZoneInfo LocalTimeZone => London;

        public override DateTimeOffset GetUtcNow() => Now.ToUniversalTime();

        public void Advance(TimeSpan by) => Now += by;
    }
}
