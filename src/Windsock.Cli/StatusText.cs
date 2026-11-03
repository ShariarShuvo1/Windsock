using System.Globalization;
using System.Text.Json;
using Windsock.Core.Formatting;
using System.Text;

namespace Windsock.Cli;

/// <summary>
/// The feed's status as lines a person reads. Plain ASCII, so it survives any
/// console code page and any redirect; a value not known is a dash, never 0.
/// </summary>
internal static class StatusText
{
    private const string Unknown = "-";

    public static void Write(TextWriter output, JsonElement status)
    {
        JsonElement rates = Member(status, "rates");
        JsonElement connection = Member(status, "connection");
        JsonElement usage = Member(status, "usage");
        JsonElement plan = Member(status, "plan");
        JsonElement system = Member(status, "system");
        JsonElement household = Member(status, "household");

        output.WriteLine(string.Concat("Windsock ", Text(status, "version") ?? Unknown, "  ", Clock(Text(status, "at"))));
        output.WriteLine(string.Concat("Down      ", Rate(rates, "downBytesPerSecond"), "    Up ", Rate(rates, "upBytesPerSecond")));
        output.WriteLine(string.Concat("Internet  ", Connection(connection)));

        if (usage.ValueKind == JsonValueKind.Object)
        {
            output.WriteLine(string.Concat("Today     ", Totals(Member(usage, "today"))));
            output.WriteLine(string.Concat("Month     ", Totals(Member(usage, "month"))));
        }
        else
        {
            output.WriteLine("Today     - (history cannot be read)");
        }

        if (plan.ValueKind == JsonValueKind.Object)
        {
            output.WriteLine(string.Concat("Plan      ", Plan(plan)));
        }

        output.WriteLine(string.Concat(
            "System    processor ", Percent(system, "processorPercent"),
            ", memory ", Percent(system, "memoryPercent"),
            ", graphics ", Percent(system, "graphicsPercent"),
            ", disk ", Percent(system, "diskPercent")));

        if (household.ValueKind == JsonValueKind.Object)
        {
            string router = Text(household, "router") is { } named ? string.Concat(" (", named, ")") : string.Empty;
            output.WriteLine(string.Concat(
                "Household down ", Rate(household, "downBytesPerSecond"),
                ", up ", Rate(household, "upBytesPerSecond"), router));
        }
    }

    /// <summary>One line for `watch`: the time, the rates and ping.</summary>
    public static string Line(JsonElement status)
    {
        JsonElement rates = Member(status, "rates");
        JsonElement connection = Member(status, "connection");

        return string.Concat(
            Clock(Text(status, "at")),
            "  down ", Rate(rates, "downBytesPerSecond").PadLeft(11),
            "  up ", Rate(rates, "upBytesPerSecond").PadLeft(11),
            "  ", Ping(connection));
    }

    private static string Connection(JsonElement connection)
    {
        string status = Text(connection, "status") ?? "checking";
        string since = Text(connection, "since") is { } at ? string.Concat(" since ", Clock(at)) : string.Empty;
        string cause = Text(connection, "cause") is { } why ? string.Concat(" (", why, ")") : string.Empty;
        string network = Text(connection, "network") is { } name ? string.Concat(" on ", name) : string.Empty;

        return string.Concat(status, since, cause, ", ", Ping(connection), network);
    }

    private static string Ping(JsonElement connection)
    {
        double? ms = Number(connection, "internetMs") ?? Number(connection, "routerMs");
        string target = Number(connection, "internetMs") is null ? "router" : "internet";

        return ms is { } known
            ? string.Concat(target, " ", known < 1 ? "<1" : Math.Round(known).ToString(CultureInfo.InvariantCulture), " ms")
            : "ping -";
    }

    private static string Plan(JsonElement plan)
    {
        string name = Text(plan, "name") ?? "Plan";
        long used = (long)(Number(plan, "usedBytes") ?? 0);

        if (Number(plan, "leftBytes") is { } left && Number(plan, "limitBytes") is { } limit)
        {
            string share = Number(plan, "usedFraction") is { } fraction ? string.Create(CultureInfo.InvariantCulture, $" ({fraction * 100:F0}% used)") : string.Empty;
            string ends = Text(plan, "cycleEnd") is { } end && DateTimeOffset.TryParse(end, CultureInfo.InvariantCulture, DateTimeStyles.None, out DateTimeOffset when)
                ? string.Concat(", resets ", when.ToString("d MMM", CultureInfo.InvariantCulture))
                : string.Empty;

            return string.Concat(name, ": ", SizeFormatter.Format((long)left), " left of ", SizeFormatter.Format((long)limit), share, ends);
        }

        return string.Concat(name, ": ", SizeFormatter.Format(used), " used");
    }

    private static string Totals(JsonElement totals)
    {
        if (totals.ValueKind != JsonValueKind.Object)
        {
            return Unknown;
        }

        long down = (long)(Number(totals, "bytesDown") ?? 0);
        long up = (long)(Number(totals, "bytesUp") ?? 0);
        return string.Concat(SizeFormatter.Format(down + up), " (down ", SizeFormatter.Format(down), ", up ", SizeFormatter.Format(up), ")");
    }

    private static string Rate(JsonElement parent, string name) =>
        Number(parent, name) is { } bytes ? RateFormatter.Format(bytes, RateFamily.Bytes, RateScale.Auto) : Unknown;

    private static string Percent(JsonElement parent, string name) =>
        Number(parent, name) is { } value ? string.Concat(Math.Round(value).ToString(CultureInfo.InvariantCulture), "%") : Unknown;

    private static string Clock(string? text) =>
        text is not null && DateTimeOffset.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.None, out DateTimeOffset at)
            ? Time(at.ToLocalTime(), seconds: true)
            : Unknown;

    /// <summary>
    /// A time on Windows' own clock, 12 or 24-hour, as the window writes it;
    /// a 24-hour time instead when the culture's words for the half of the day
    /// are not plain ASCII, which a console may not show.
    /// </summary>
    internal static string Time(DateTimeOffset at, bool seconds = false)
    {
        string written = seconds ? ClockFormat.Current.TimeWithSeconds(at) : ClockFormat.Current.Time(at);

        return Ascii.IsValid(written)
            ? written
            : at.ToString(seconds ? "HH:mm:ss" : "HH:mm", CultureInfo.InvariantCulture);
    }

    /// <summary>
    /// How much of a period was recorded: whole minutes under an hour, so a
    /// few minutes do not read as "0.0 hours", and hours to a tenth from there.
    /// </summary>
    internal static string Recorded(int minutes) => minutes switch
    {
        1 => "1 minute",
        < 60 => string.Create(CultureInfo.InvariantCulture, $"{Math.Max(0, minutes)} minutes"),
        _ => string.Create(CultureInfo.CurrentCulture, $"{minutes / 60.0:F1} hours"),
    };

    /// <summary>The date as the command line writes it, year first, then the time on Windows' clock.</summary>
    internal static string DateAndTime(DateTimeOffset at) =>
        string.Concat(at.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture), " ", Time(at));

    private static JsonElement Member(JsonElement parent, string name) =>
        parent.ValueKind == JsonValueKind.Object && parent.TryGetProperty(name, out JsonElement value) ? value : default;

    private static string? Text(JsonElement parent, string name) =>
        Member(parent, name) is { ValueKind: JsonValueKind.String } value ? value.GetString() : null;

    private static double? Number(JsonElement parent, string name) =>
        Member(parent, name) is { ValueKind: JsonValueKind.Number } value ? value.GetDouble() : null;
}
