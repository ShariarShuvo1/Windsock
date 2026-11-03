using System.Globalization;
using Windsock.Core.Connectivity;
using Windsock.Core.Formatting;
using Windsock.Core.Notifications;
using Windsock.Core.Plans;

namespace Windsock.Core.Alerts;

/// <summary>The window sections the alerts open when clicked.</summary>
/// <remarks>
/// A section the window does not have opens the window where it was, so a name
/// given here before its page exists is harmless.
/// </remarks>
public static class AlertSections
{
    /// <summary>The Alerts page and its list.</summary>
    public const string Alerts = "alerts";

    /// <summary>Connection › Status.</summary>
    public const string Status = "status";

    /// <summary>Network › Overview.</summary>
    public const string Overview = "overview";

    /// <summary>Network › Apps.</summary>
    public const string Apps = "apps";

    /// <summary>Network › Adapters and Wi-Fi.</summary>
    public const string Adapters = "adapters";

    /// <summary>Usage › Data plan.</summary>
    public const string Plan = "plan";

    /// <summary>System › Processor.</summary>
    public const string Processor = "processor";

    /// <summary>System › Graphics.</summary>
    public const string Graphics = "graphics";

    /// <summary>System › Storage.</summary>
    public const string Storage = "storage";

    /// <summary>Settings › About, where an update is installed.</summary>
    public const string About = "about";
}

/// <summary>What each of the alerts added in 0.2.0 says, in plain English.</summary>
public static class AlertNotices
{
    private static CultureInfo Culture => CultureInfo.CurrentCulture;

    /// <summary>Responses slow, or pings going missing.</summary>
    public static Notice Ping(PingAlarm alarm)
    {
        ArgumentNullException.ThrowIfNull(alarm);

        string whose = alarm.Target == PingTarget.Router ? "your router" : "the internet";
        string stretch = OutageWords.Spoken(alarm.Window);

        string title = alarm.Slow
            ? string.Concat("Slow responses from ", whose)
            : string.Concat("Pings to ", whose, " going missing");

        List<string> said = [];

        if (alarm.Slow && alarm.TypicalMs is { } ms)
        {
            said.Add(string.Create(Culture, $"Typical response time has been {ms:F0} ms for the last {stretch}."));
        }

        if (alarm.Lossy || alarm.Loss >= 0.01)
        {
            said.Add(string.Create(Culture, $"{alarm.Loss:P0} of checks went unanswered{(alarm.Slow ? string.Empty : string.Concat(" in the last ", stretch))}."));
        }

        if (alarm.Target == PingTarget.Router)
        {
            said.Add("The trouble is between this PC and the router, often Wi-Fi.");
        }

        return new Notice(NoticeTopics.PingHigh, title, string.Join(' ', said))
        {
            Tag = NoticeTopics.PingHigh,
            Opens = AlertSections.Status,
        };
    }

    /// <summary>A download that stayed slow.</summary>
    public static Notice SpeedDrop(SpeedDropAlarm alarm, long level, RateFamily family)
    {
        ArgumentNullException.ThrowIfNull(alarm);

        string message = string.Concat(
            "A download that reached ",
            RateFormatter.Format(alarm.PeakBytesPerSecond, family, RateScale.Auto),
            " has been going at about ",
            RateFormatter.Format(alarm.TypicalBytesPerSecond, family, RateScale.Auto),
            " for ",
            OutageWords.Spoken(alarm.Window),
            ", under the ",
            RateFormatter.Format(level, family, RateScale.Auto),
            " you set.");

        return new Notice(NoticeTopics.SpeedDrop, "Download has slowed down", message)
        {
            Tag = NoticeTopics.SpeedDrop,
            Opens = AlertSections.Overview,
        };
    }

    /// <summary>An upload that stayed high.</summary>
    public static Notice Upload(UploadAlarm alarm, RateFamily family)
    {
        ArgumentNullException.ThrowIfNull(alarm);

        string message = string.Concat(
            "This PC has been uploading at about ",
            RateFormatter.Format(alarm.TypicalBytesPerSecond, family, RateScale.Auto),
            " for ",
            OutageWords.Spoken(alarm.Window),
            ", ",
            SizeFormatter.Format(alarm.Sent),
            " in all. A backup or sync may be running away; the Apps page shows which app.");

        return new Notice(NoticeTopics.UploadHigh, "Unusual upload", message)
        {
            Tag = NoticeTopics.UploadHigh,
            Opens = AlertSections.Apps,
        };
    }

    /// <summary>A part of the machine past its level.</summary>
    public static Notice Hardware(HardwareAlarm alarm, TemperatureUnit unit)
    {
        ArgumentNullException.ThrowIfNull(alarm);

        string degrees = TemperatureFormatter.Format(alarm.Value, unit);
        string level = TemperatureFormatter.Format(alarm.Level, unit);

        return alarm.Problem switch
        {
            HardwareProblem.GraphicsHot => new Notice(
                NoticeTopics.GraphicsHot,
                "Graphics adapter is hot",
                string.Concat(alarm.Subject, " has stayed at ", degrees, ", past the ", level, " you set."))
            {
                Tag = alarm.Tag,
                Opens = AlertSections.Graphics,
            },
            HardwareProblem.ProcessorHot => new Notice(
                NoticeTopics.ProcessorHot,
                "Processor is hot",
                string.Concat("The processor has stayed at ", degrees, ", past the ", level, " you set."))
            {
                Tag = alarm.Tag,
                Opens = AlertSections.Processor,
            },
            HardwareProblem.DriveHot => new Notice(
                NoticeTopics.DriveHot,
                "Drive is hot",
                string.Concat(alarm.Subject, " has stayed at ", degrees, ", past the ", level, " you set."))
            {
                Tag = alarm.Tag,
                Opens = AlertSections.Storage,
            },
            HardwareProblem.DriveFull => new Notice(
                NoticeTopics.DriveFull,
                string.Concat(alarm.Subject, " is nearly full"),
                string.Create(Culture, $"It is {alarm.Value:F0}% full{(alarm.Free is { } free ? string.Concat(", with ", SizeFormatter.Format(free), " free") : string.Empty)}."))
            {
                Tag = alarm.Tag,
                Opens = AlertSections.Storage,
            },
            _ => new Notice(
                NoticeTopics.DriveWear,
                "Drive is wearing out",
                string.Create(Culture, $"{alarm.Subject} has used {alarm.Value:F0}% of its rated life. Keep a backup of what is on it."))
            {
                Tag = alarm.Tag,
                Opens = AlertSections.Storage,
            },
        };
    }

    /// <summary>Something about a known network changed.</summary>
    public static Notice NetworkChange(NetworkPrintDifference change)
    {
        ArgumentNullException.ThrowIfNull(change);

        return change.Change switch
        {
            NetworkPrintChange.Address => new Notice(
                NoticeTopics.AddressChanged,
                string.Concat("New address on ", change.Network),
                string.Concat("This PC's address there changed from ", change.Was, " to ", change.Now, "."))
            {
                Tag = string.Concat("address:", change.Network),
                Opens = AlertSections.Adapters,
                Quiet = true,
            },
            NetworkPrintChange.Dns => new Notice(
                NoticeTopics.DnsChanged,
                string.Concat("DNS servers changed on ", change.Network),
                string.Concat(
                    "Names are now looked up through ", change.Now, " instead of ", change.Was,
                    ". If you did not change this, a program, a VPN or the router did."))
            {
                Tag = string.Concat("dns:", change.Network),
                Opens = AlertSections.Adapters,
            },
            _ => new Notice(
                NoticeTopics.RouterChanged,
                string.Concat("Different router on ", change.Network),
                string.Concat(
                    "The router answering there is ", change.Now, ", not ", change.Was,
                    " as before. A new router does this; so does a network pretending to be yours."))
            {
                Tag = string.Concat("router:", change.Network),
                Opens = AlertSections.Adapters,
            },
        };
    }

    /// <summary>The system proxy changed.</summary>
    public static Notice Proxy(ProxyChange change)
    {
        ArgumentNullException.ThrowIfNull(change);

        string now = ProxyJudge.Describe(change.Now);
        string was = ProxyJudge.Describe(change.Was);
        string first = now == "no proxy"
            ? string.Concat("This PC no longer uses ", was, ".")
            : string.Concat("This PC now uses ", now, ", where it used ", was, ".");

        string who = change.WhileAway
            ? " It changed while Windsock was not running. If you did not change it, a program did."
            : " Windows' proxy settings were not open, so a program made the change.";

        return new Notice(
            NoticeTopics.ProxyChanged,
            "Proxy settings changed",
            string.Concat(first, who))
        {
            Tag = NoticeTopics.ProxyChanged,
            Opens = AlertSections.Alerts,
        };
    }

    /// <summary>A data plan passed one of its levels.</summary>
    /// <remarks>
    /// A level already passed when Windsock first looked - while it was closed,
    /// or before the plan was made - is said without a sound.
    /// </remarks>
    public static Notice Plan(PlanLevelCrossing crossing)
    {
        ArgumentNullException.ThrowIfNull(crossing);

        string name = string.IsNullOrWhiteSpace(crossing.Plan.Name) ? "Data plan" : crossing.Plan.Name.Trim();
        string title = crossing.Level >= 100
            ? string.Concat(name, ": allowance used up")
            : string.Create(Culture, $"{name}: {crossing.Level}% used");

        List<string> said = [];

        if (PlanWords.Left(crossing.Status) is { } left)
        {
            said.Add(string.Concat(left, "."));
        }

        if (PlanWords.Forecast(crossing.Status) is { } forecast)
        {
            said.Add(forecast);
        }

        return new Notice(NoticeTopics.PlanLevel, title, string.Join(' ', said))
        {
            Tag = string.Concat("plan:", crossing.Plan.Id),
            Opens = AlertSections.Plan,
            Quiet = crossing.FirstLook,
        };
    }

    /// <summary>A newer Windsock can be installed.</summary>
    /// <param name="version">The version.</param>
    /// <param name="downloaded">Whether it is downloaded and waits only for a restart.</param>
    public static Notice Update(string version, bool downloaded)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(version);

        return downloaded
            ? new Notice(
                NoticeTopics.UpdateReady,
                string.Concat("Windsock ", version, " is ready to install"),
                "It is installed when Windsock restarts. Restart it from Settings › About.")
            {
                Tag = string.Concat("update:", version),
                Opens = AlertSections.About,
                Quiet = true,
            }
            : new Notice(
                NoticeTopics.UpdateReady,
                string.Concat("Windsock ", version, " is available"),
                "Download and install it from Settings › About.")
            {
                Tag = string.Concat("update:", version),
                Opens = AlertSections.About,
                Quiet = true,
            };
    }

    /// <summary>What happened while the screen was locked.</summary>
    /// <param name="report">What moved, and for how long the screen was locked.</param>
    /// <param name="outages">The outages that overlapped the time away.</param>
    /// <param name="alerts">How many alerts were raised meanwhile.</param>
    public static Notice Away(AwayReport report, IReadOnlyList<Outage> outages, int alerts)
    {
        ArgumentNullException.ThrowIfNull(report);
        ArgumentNullException.ThrowIfNull(outages);

        List<string> said =
        [
            string.Concat("Locked for ", OutageWords.Spoken(report.Duration), "."),
            string.Concat(SizeFormatter.Format(report.Received), " down, ", SizeFormatter.Format(report.Sent), " up."),
        ];

        if (outages.Count == 0)
        {
            said.Add("No outages.");
        }
        else
        {
            TimeSpan offline = TimeSpan.Zero;

            foreach (Outage outage in outages)
            {
                DateTimeOffset start = outage.Start < report.Since ? report.Since : outage.Start;
                DateTimeOffset end = outage.End is { } ended && ended < report.Until ? ended : report.Until;

                if (end > start)
                {
                    offline += end - start;
                }
            }

            said.Add(outages.Count == 1
                ? string.Concat("1 outage, ", OutageWords.Spoken(offline), " offline.")
                : string.Create(Culture, $"{outages.Count} outages, {OutageWords.Spoken(offline)} offline."));
        }

        said.Add(alerts switch
        {
            0 => "No alerts.",
            1 => "1 alert.",
            _ => string.Create(Culture, $"{alerts} alerts."),
        });

        return new Notice(NoticeTopics.AwaySummary, "While you were away", string.Join(' ', said))
        {
            Tag = NoticeTopics.AwaySummary,
            Opens = AlertSections.Alerts,
            Quiet = true,
        };
    }
}
