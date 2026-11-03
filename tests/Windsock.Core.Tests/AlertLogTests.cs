using Windsock.Core.Alerts;
using Windsock.Core.Connectivity;
using Windsock.Core.Notifications;
using Xunit;

namespace Windsock.Core.Tests;

public sealed class SqliteAlertLogTests : IDisposable
{
    private static readonly DateTimeOffset Noon = new(2026, 6, 10, 12, 0, 0, TimeSpan.Zero);

    private readonly string _folder = Path.Combine(Path.GetTempPath(), "windsock-alert-log-" + Guid.NewGuid().ToString("N"));
    private readonly string _path;

    public SqliteAlertLogTests()
    {
        Directory.CreateDirectory(_folder);
        _path = Path.Combine(_folder, "Alerts.db");
    }

    public void Dispose() => Directory.Delete(_folder, recursive: true);

    private static AlertRecord Alert(int minutes, string topic = NoticeTopics.PingHigh, AlertOutcome outcome = AlertOutcome.Shown, string? tag = null) =>
        new(0, Noon.AddMinutes(minutes), topic, "Title " + minutes, "Message " + minutes) { Outcome = outcome, Tag = tag };

    [Fact]
    public void Alerts_ComeBackNewestFirst_WithEverythingKept()
    {
        using SqliteAlertLog log = new(_path);

        log.Add(Alert(0) with { Opens = "status", Detail = "why", ShownAt = Noon, Forward = ForwardOutcome.Sent });
        log.Add(Alert(5));

        IReadOnlyList<AlertRecord> read = log.Read(AlertQuery.Everything);

        Assert.Equal(["Title 5", "Title 0"], read.Select(each => each.Title));

        AlertRecord first = read[1];
        Assert.Equal(Noon, first.At);
        Assert.Equal("status", first.Opens);
        Assert.Equal("why", first.Detail);
        Assert.Equal(Noon, first.ShownAt);
        Assert.Equal(ForwardOutcome.Sent, first.Forward);
        Assert.Equal(AlertLevel.Warning, first.Level);
    }

    [Fact]
    public void AQuery_FiltersAndPages()
    {
        using SqliteAlertLog log = new(_path);

        for (int each = 0; each < 10; each++)
        {
            log.Add(Alert(each, each % 2 == 0 ? NoticeTopics.PingHigh : NoticeTopics.DriveFull));
        }

        log.Add(Alert(20, NoticeTopics.NetworkChanged, AlertOutcome.Event));

        Assert.Equal(10, log.Count(AlertQuery.Everything));
        Assert.Equal(11, log.Count(AlertQuery.Everything with { IncludeEvents = true }));
        Assert.Equal(5, log.Count(AlertQuery.Everything with { Topics = [NoticeTopics.DriveFull] }));
        Assert.Equal(3, log.Count(new AlertQuery(Noon.AddMinutes(2), Noon.AddMinutes(5))));

        IReadOnlyList<AlertRecord> page = log.Read(AlertQuery.Everything with { Skip = 2, Take = 3 });
        Assert.Equal(["Title 7", "Title 6", "Title 5"], page.Select(each => each.Title));
    }

    [Fact]
    public void Reading_IsMarked_AndEventsAreNeverUnread()
    {
        using SqliteAlertLog log = new(_path);

        long first = log.Add(Alert(0));
        log.Add(Alert(1));
        log.Add(Alert(2, NoticeTopics.NetworkChanged, AlertOutcome.Event));

        Assert.Equal(2, log.UnreadCount());

        log.MarkRead([first]);
        Assert.Equal(1, log.UnreadCount());
        Assert.Single(log.Read(AlertQuery.Everything with { OnlyUnread = true }));

        log.MarkAllRead();
        Assert.Equal(0, log.UnreadCount());
    }

    [Fact]
    public void HeldAlerts_AreFound_AndTheirOutcomeChanged()
    {
        using SqliteAlertLog log = new(_path);

        long held = log.Add(Alert(0, outcome: AlertOutcome.Held));
        log.Add(Alert(1));

        Assert.Equal(held, Assert.Single(log.Held()).Id);

        log.SetOutcome([held], AlertOutcome.Summarised, string.Empty, Noon.AddHours(7));

        Assert.Empty(log.Held());
        Assert.Equal(Noon.AddHours(7), log.Read(AlertQuery.Everything).Single(each => each.Id == held).ShownAt);
    }

    [Fact]
    public void LastRaised_MatchesKindAndTag_AndIgnoresEvents()
    {
        using SqliteAlertLog log = new(_path);

        log.Add(Alert(0, NoticeTopics.DriveFull, tag: "C:"));
        log.Add(Alert(10, NoticeTopics.DriveFull, tag: "D:"));
        log.Add(Alert(20, NoticeTopics.DriveFull, AlertOutcome.Event, tag: "C:"));
        log.Add(Alert(30, NoticeTopics.PingHigh));

        Assert.Equal(Noon, log.LastRaised(NoticeTopics.DriveFull, "C:"));
        Assert.Equal(Noon.AddMinutes(30), log.LastRaised(NoticeTopics.PingHigh, null));
        Assert.Null(log.LastRaised(NoticeTopics.DriveFull, "E:"));
    }

    [Fact]
    public void Forgetting_AndPruning_TakeOnlyWhatTheySay_AndPruningKeepsWhatIsHeld()
    {
        using SqliteAlertLog log = new(_path);

        log.Add(Alert(0));
        log.Add(Alert(1, outcome: AlertOutcome.Held));
        log.Add(Alert(60));
        log.Add(Alert(120));

        Assert.Equal(1, log.Forget(Noon.AddMinutes(59), Noon.AddMinutes(61)));
        Assert.Equal(1, log.Prune(Noon.AddMinutes(30)));

        Assert.Equal(["Title 120", "Title 1"], log.Read(AlertQuery.Everything).Select(each => each.Title));

        Assert.Equal(2, log.Clear());
        Assert.Empty(log.Read(AlertQuery.Everything));
    }

    [Fact]
    public void ANetwork_IsRemembered_AcrossOpenings()
    {
        NetworkPrint print = new("net-1", "Home", "192.168.1.20", ["1.1.1.1", "9.9.9.9"], "192.168.1.1", "AA-BB-CC-DD-EE-FF")
        {
            Seen = Noon,
        };

        using (SqliteAlertLog log = new(_path))
        {
            Assert.Null(log.KnownPrint("net-1"));
            log.KeepPrint(print);
            log.KeepPrint(print with { Address = "192.168.1.21" });
        }

        using SqliteAlertLog again = new(_path);
        NetworkPrint known = again.KnownPrint("net-1")!;

        Assert.Equal("192.168.1.21", known.Address);
        Assert.Equal(["1.1.1.1", "9.9.9.9"], known.Dns);
        Assert.Equal("AA-BB-CC-DD-EE-FF", known.RouterMac);
        Assert.Equal(Noon, known.Seen);

        Assert.Equal(1, again.ForgetPrints());
        Assert.Null(again.KnownPrint("net-1"));
    }

    [Fact]
    public void State_IsKept_AndRemoved()
    {
        using SqliteAlertLog log = new(_path);

        log.WriteState("snoozed_until", "2026-06-10T13:00:00+00:00");
        Assert.Equal("2026-06-10T13:00:00+00:00", log.ReadState("snoozed_until"));

        log.WriteState("snoozed_until", null);
        Assert.Null(log.ReadState("snoozed_until"));
    }

    [Fact]
    public void ADamagedFile_IsSetAside_AndANewOneStarted()
    {
        File.WriteAllText(_path, "this is not a database, not even close to one, and long enough to be read");

        using SqliteAlertLog log = new(_path);

        Assert.True(log.IsOpen);
        log.Add(Alert(0));
        Assert.Single(log.Read(AlertQuery.Everything));
        Assert.Single(Directory.GetFiles(_folder, "Alerts.db.*.corrupt"));
    }
}

public sealed class EventTimelineTests
{
    private static readonly DateTimeOffset Noon = new(2026, 6, 10, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void EachHappening_IsMarkedOnce_InTimeOrder()
    {
        Outage outage = new(Noon.AddMinutes(10), Noon.AddMinutes(14), OutageCause.Unknown, "Home (Wi-Fi)") { Id = 7 };

        AlertRecord[] records =
        [
            new(1, Noon.AddMinutes(10), NoticeTopics.ConnectionLost, "Internet connection lost", "Gone."),
            new(2, Noon.AddMinutes(14), NoticeTopics.ConnectionRestored, "Back online", "Back."),
            new(3, Noon.AddMinutes(2), NoticeTopics.NetworkChanged, "Switched to Home", "Was Office.") { Outcome = AlertOutcome.Event },
            new(4, Noon.AddMinutes(3), NoticeTopics.NetworkChanged, "Switched to Home", "Was Office."),
            new(5, Noon.AddMinutes(30), NoticeTopics.UploadHigh, "Unusual upload", "2 MB/s."),
            new(6, Noon.AddMinutes(31), NoticeTopics.VpnChanged, "VPN disconnected", "Off.") { Outcome = AlertOutcome.Event },
        ];

        IReadOnlyList<TimelineMarker> marks = EventTimeline.Merge([outage], records);

        Assert.Equal(
            [MarkerKind.NetworkChange, MarkerKind.Outage, MarkerKind.Alert, MarkerKind.VpnChange],
            marks.Select(each => each.Kind));

        TimelineMarker gone = marks[1];
        Assert.Equal(Noon.AddMinutes(14), gone.Until);
        Assert.Equal(7, gone.OutageId);
        Assert.Equal(AlertLevel.Problem, gone.Level);
        Assert.Contains("4 minutes", gone.Detail, StringComparison.Ordinal);

        Assert.Equal(5, marks[2].AlertId);
        Assert.Equal(AlertLevel.Warning, marks[2].Level);
    }
}
