using System.Text.Json.Serialization;
using Windsock.Core.Notifications;

namespace Windsock.Core.Settings;

/// <summary>
/// Hours in which alerts are held rather than shown, and given as one summary
/// when the hours end.
/// </summary>
/// <remarks>
/// Times are minutes past local midnight, on the wall clock, so 22:00 to 07:00
/// stays 22:00 to 07:00 either side of a change to or from summer time. A
/// stretch whose end is not after its start runs past midnight.
/// </remarks>
public sealed class QuietHoursSettings
{
    /// <summary>Minutes in a day.</summary>
    public const int Day = 24 * 60;

    /// <summary>The step the Alerts page offers times in.</summary>
    public const int Step = 15;

    /// <summary>Whether alerts are held in these hours.</summary>
    public bool Enabled { get; set; }

    /// <summary>When the quiet stretch begins, in minutes past midnight.</summary>
    public int StartMinute { get; set; } = 22 * 60;

    /// <summary>When it ends, in minutes past midnight; at or before the start means the next day.</summary>
    public int EndMinute { get; set; } = 7 * 60;

    /// <summary>
    /// The days a quiet stretch begins on. Every day unless changed; a stretch
    /// begun on Friday night still ends on Saturday morning.
    /// </summary>
    public List<DayOfWeek> Days
    {
        get => field ??= [.. Enum.GetValues<DayOfWeek>()];
        set => field = value ?? [.. Enum.GetValues<DayOfWeek>()];
    }

    /// <summary><see cref="StartMinute"/>, held to a day.</summary>
    [JsonIgnore]
    public int Start => Hold(StartMinute);

    /// <summary><see cref="EndMinute"/>, held to a day.</summary>
    [JsonIgnore]
    public int End => Hold(EndMinute);

    private static int Hold(int minute) => ((minute % Day) + Day) % Day;
}

/// <summary>What the person chose for one kind of alert beyond its switch.</summary>
public sealed class AlertChoice
{
    /// <summary>The sound it makes.</summary>
    public NoticeSound Sound { get; set; }

    /// <summary>Whether it is shown during quiet hours and a snooze rather than held.</summary>
    public bool BreaksQuiet { get; set; }

    /// <summary>Whether it is forwarded, when forwarding is set up.</summary>
    public bool Forward { get; set; } = true;
}

/// <summary>Response time or packet loss above a level for a while.</summary>
public sealed class PingAlertSettings
{
    /// <summary>The shortest stretch offered, in minutes.</summary>
    public const int ShortestMinutes = 1;

    /// <summary>The longest stretch offered, in minutes.</summary>
    public const int LongestMinutes = 60;

    /// <summary>Whether the alert is on.</summary>
    public bool Enabled { get; set; }

    /// <summary>The typical response time, in milliseconds, above which the alert fires; zero ignores response time.</summary>
    public int Milliseconds { get; set; } = 150;

    /// <summary>The share of checks unanswered, in percent, at which the alert fires; zero ignores loss.</summary>
    public int LossPercent { get; set; } = 10;

    /// <summary>How long it must last, in minutes.</summary>
    public int Minutes { get; set; } = 3;

    /// <summary>How long it must last, held to what is sensible.</summary>
    [JsonIgnore]
    public TimeSpan Window => TimeSpan.FromMinutes(Math.Clamp(Minutes, ShortestMinutes, LongestMinutes));
}

/// <summary>A download that stays slow.</summary>
public sealed class SpeedDropSettings
{
    /// <summary>The shortest stretch offered, in minutes.</summary>
    public const int ShortestMinutes = 2;

    /// <summary>The longest stretch offered, in minutes.</summary>
    public const int LongestMinutes = 60;

    /// <summary>Whether the alert is on.</summary>
    public bool Enabled { get; set; }

    /// <summary>The speed, in bytes a second, a download staying under counts as slow.</summary>
    public long BelowBytesPerSecond { get; set; } = 1_048_576;

    /// <summary>How long it must stay under, in minutes.</summary>
    public int Minutes { get; set; } = 5;

    /// <summary>How long it must stay under, held to what is sensible.</summary>
    [JsonIgnore]
    public TimeSpan Window => TimeSpan.FromMinutes(Math.Clamp(Minutes, ShortestMinutes, LongestMinutes));
}

/// <summary>An upload that stays high: a runaway backup or sync.</summary>
public sealed class UploadAlertSettings
{
    /// <summary>The shortest stretch offered, in minutes.</summary>
    public const int ShortestMinutes = 2;

    /// <summary>The longest stretch offered, in minutes.</summary>
    public const int LongestMinutes = 60;

    /// <summary>Whether the alert is on.</summary>
    public bool Enabled { get; set; }

    /// <summary>The speed, in bytes a second, an upload staying over is unusual.</summary>
    public long AboveBytesPerSecond { get; set; } = 2_097_152;

    /// <summary>How long it must stay over, in minutes.</summary>
    public int Minutes { get; set; } = 10;

    /// <summary>How long it must stay over, held to what is sensible.</summary>
    [JsonIgnore]
    public TimeSpan Window => TimeSpan.FromMinutes(Math.Clamp(Minutes, ShortestMinutes, LongestMinutes));
}

/// <summary>Temperatures, a drive filling up, and a drive wearing out.</summary>
/// <remarks>
/// All off until asked for: on, they keep the machine read every few seconds
/// while nothing else needs it.
/// </remarks>
public sealed class HardwareAlertSettings
{
    /// <summary>The lowest temperature offered, in degrees.</summary>
    public const int CoolestLevel = 40;

    /// <summary>The highest temperature offered, in degrees.</summary>
    public const int HottestLevel = 110;

    /// <summary>Whether a hot graphics adapter is an alert.</summary>
    public bool GraphicsHot { get; set; }

    /// <summary>The graphics temperature that counts as hot, in degrees Celsius.</summary>
    public int GraphicsCelsius { get; set; } = 85;

    /// <summary>Whether a hot drive is an alert.</summary>
    public bool DriveHot { get; set; }

    /// <summary>The drive temperature that counts as hot, in degrees Celsius.</summary>
    public int DriveCelsius { get; set; } = 65;

    /// <summary>Whether a nearly full drive is an alert.</summary>
    public bool DriveFull { get; set; }

    /// <summary>How full, in percent, counts as nearly full.</summary>
    public int DriveFullPercent { get; set; } = 90;

    /// <summary>Whether a worn drive is an alert.</summary>
    public bool DriveWear { get; set; }

    /// <summary>The share of its rated life a drive has used, in percent, that is worth a warning.</summary>
    public int DriveWearPercent { get; set; } = 80;

    /// <summary>Whether a hot processor is an alert (full edition, with PawnIO, as administrator).</summary>
    public bool ProcessorHot { get; set; }

    /// <summary>The processor temperature that counts as hot, in degrees Celsius.</summary>
    public int ProcessorCelsius { get; set; } = 90;

    /// <summary>
    /// Whether any of these is switched on. What keeps the machine read is a
    /// narrower question - the processor's alert counts only where its
    /// temperature can be read - and the hardware alerts' source answers it.
    /// </summary>
    [JsonIgnore]
    public bool AnyOn => GraphicsHot || DriveHot || DriveFull || DriveWear || ProcessorHot;
}

/// <summary>Changes to the network or the proxy that someone else may have made.</summary>
public sealed class ChangeAlertSettings
{
    /// <summary>Whether this PC's address changing on a known network is an alert.</summary>
    public bool Address { get; set; }

    /// <summary>Whether the DNS servers changing on a known network is an alert.</summary>
    public bool Dns { get; set; }

    /// <summary>Whether a different router on a known network is an alert.</summary>
    public bool Router { get; set; }

    /// <summary>Whether the system proxy changing is an alert.</summary>
    public bool Proxy { get; set; }

    /// <summary>Whether any of the network ones is on, which is what keeps the adapters read.</summary>
    [JsonIgnore]
    public bool AnyNetwork => Address || Dns || Router;
}

/// <summary>A summary on unlocking the screen.</summary>
public sealed class AwayAlertSettings
{
    /// <summary>The shortest absence offered, in minutes.</summary>
    public const int ShortestMinutes = 1;

    /// <summary>The longest offered.</summary>
    public const int LongestMinutes = 24 * 60;

    /// <summary>Whether it is sent.</summary>
    public bool Enabled { get; set; }

    /// <summary>How long the screen must have been locked for a summary, in minutes.</summary>
    public int MinimumMinutes { get; set; } = 15;

    /// <summary>The absence that earns a summary, held to what is sensible.</summary>
    [JsonIgnore]
    public TimeSpan Minimum => TimeSpan.FromMinutes(Math.Clamp(MinimumMinutes, ShortestMinutes, LongestMinutes));
}

/// <summary>Where forwarded alerts go.</summary>
public enum ForwardKind
{
    /// <summary>A topic on an ntfy server, which pushes to a phone.</summary>
    Ntfy,

    /// <summary>Any address that takes a JSON post.</summary>
    Webhook,
}

/// <summary>
/// Sending each alert to a webhook or an ntfy topic the person set up. Off
/// until set up; the only thing it sends is the alert itself.
/// </summary>
public sealed class ForwardSettings
{
    /// <summary>The ntfy server people use unless they run their own.</summary>
    public const string PublicNtfy = "https://ntfy.sh";

    /// <summary>Whether alerts are forwarded.</summary>
    public bool Enabled { get; set; }

    /// <summary>Where they go.</summary>
    public ForwardKind To { get; set; } = ForwardKind.Ntfy;

    /// <summary>The webhook's address, for <see cref="ForwardKind.Webhook"/>.</summary>
    public string WebhookUrl { get; set; } = string.Empty;

    /// <summary>The ntfy server, for <see cref="ForwardKind.Ntfy"/>.</summary>
    public string NtfyServer { get; set; } = PublicNtfy;

    /// <summary>The ntfy topic.</summary>
    public string NtfyTopic { get; set; } = string.Empty;

    /// <summary>An ntfy access token, for a protected topic; empty for none.</summary>
    public string NtfyToken { get; set; } = string.Empty;

    /// <summary>
    /// A name for this PC, sent with each alert so several PCs can share one
    /// destination; empty sends none.
    /// </summary>
    public string SenderName { get; set; } = string.Empty;
}

/// <summary>
/// Every alert's choices that are not kept with the feature that raises it:
/// quiet hours, sounds, the new alerts' switches and levels, the record's
/// length, and forwarding.
/// </summary>
/// <remarks>
/// The connection's own two switches stay in <see cref="ConnectionSettings"/>
/// and the plans' levels on each <see cref="Plans.DataPlan"/>, where they have
/// always been; the Alerts page shows them beside these.
/// </remarks>
public sealed class AlertSettings
{
    /// <summary>The record lengths the Alerts page offers, in days.</summary>
    public static IReadOnlyList<int> KeepChoices { get; } = [7, 30, 90, 365];

    /// <summary>Quiet hours.</summary>
    public QuietHoursSettings Quiet
    {
        get => field ??= new QuietHoursSettings();
        set => field = value ?? new QuietHoursSettings();
    }

    /// <summary>
    /// Sound, quiet hours and forwarding per kind of alert, by its topic. A kind
    /// not listed takes the defaults. Replace rather than change in place.
    /// </summary>
    public Dictionary<string, AlertChoice> Choices
    {
        get => field ??= new Dictionary<string, AlertChoice>(StringComparer.Ordinal);
        set => field = value is null
            ? new Dictionary<string, AlertChoice>(StringComparer.Ordinal)
            : new Dictionary<string, AlertChoice>(value, StringComparer.Ordinal);
    }

    /// <summary>Response time or loss.</summary>
    public PingAlertSettings Ping
    {
        get => field ??= new PingAlertSettings();
        set => field = value ?? new PingAlertSettings();
    }

    /// <summary>A slow download.</summary>
    public SpeedDropSettings SpeedDrop
    {
        get => field ??= new SpeedDropSettings();
        set => field = value ?? new SpeedDropSettings();
    }

    /// <summary>A high upload.</summary>
    public UploadAlertSettings Upload
    {
        get => field ??= new UploadAlertSettings();
        set => field = value ?? new UploadAlertSettings();
    }

    /// <summary>The machine's temperatures and drives.</summary>
    public HardwareAlertSettings Hardware
    {
        get => field ??= new HardwareAlertSettings();
        set => field = value ?? new HardwareAlertSettings();
    }

    /// <summary>Address, DNS, router and proxy changes.</summary>
    public ChangeAlertSettings Changes
    {
        get => field ??= new ChangeAlertSettings();
        set => field = value ?? new ChangeAlertSettings();
    }

    /// <summary>The summary on unlocking.</summary>
    public AwayAlertSettings Away
    {
        get => field ??= new AwayAlertSettings();
        set => field = value ?? new AwayAlertSettings();
    }

    /// <summary>Forwarding.</summary>
    /// <remarks>
    /// Never in the settings file, so never in an exported one: a webhook's
    /// address and an ntfy topic are secrets, and the name given this PC belongs
    /// to it. The alert desk keeps it in the record of alerts beside the file
    /// (see <see cref="Alerts.AlertDesk.SaveForwarding"/>) and puts it here as it starts.
    /// </remarks>
    [JsonIgnore]
    public ForwardSettings Forward
    {
        get => field ??= new ForwardSettings();
        set => field = value ?? new ForwardSettings();
    }

    /// <summary>Whether a data plan passing one of its levels is an alert. The levels are each plan's own.</summary>
    public bool PlanLevels { get; set; } = true;

    /// <summary>Whether a newer version being ready is an alert (full edition).</summary>
    public bool UpdateReady { get; set; } = true;

    /// <summary>How many days the record of alerts is kept.</summary>
    public int KeepDays { get; set; } = 90;

    /// <summary><see cref="KeepDays"/>, held to what is sensible.</summary>
    [JsonIgnore]
    public TimeSpan Keep => TimeSpan.FromDays(Math.Clamp(KeepDays, 1, 3650));

    /// <summary>The choices for one kind of alert: its own, or the defaults.</summary>
    public AlertChoice ChoiceFor(string topic) =>
        topic is not null && Choices.TryGetValue(topic, out AlertChoice? choice) && choice is not null
            ? choice
            : new AlertChoice();
}
