using System.Globalization;
using System.Text;

namespace Windsock.Core.Connectivity;

/// <summary>Writes the outage log as CSV.</summary>
public static class OutageCsv
{
    private const string UtcPattern = "yyyy-MM-ddTHH:mm:ss'Z'";
    private const string LocalPattern = "yyyy-MM-ddTHH:mm:sszzz";

    /// <summary>The header row, and the order columns are written in.</summary>
    public static string Header { get; } = string.Join(
        ',',
        "start_utc",
        "start_local",
        "end_utc",
        "end_local",
        "duration_seconds",
        "cause",
        "network",
        "interrupted");

    /// <summary>Writes outages as CSV, header first, in the order given.</summary>
    /// <remarks>
    /// An outage still in progress has empty end and duration cells rather than a
    /// made-up end. A network name is text somebody else chose, so it is quoted when
    /// it needs to be and never left able to start a spreadsheet formula.
    /// </remarks>
    public static void Write(TextWriter writer, IEnumerable<Outage> outages)
    {
        ArgumentNullException.ThrowIfNull(writer);
        ArgumentNullException.ThrowIfNull(outages);

        writer.Write(Header);
        writer.Write('\n');

        StringBuilder line = new(160);

        foreach (Outage outage in outages)
        {
            line.Clear()
                .Append(Utc(outage.Start)).Append(',')
                .Append(Local(outage.Start)).Append(',')
                .Append(outage.End is { } endUtc ? Utc(endUtc) : string.Empty).Append(',')
                .Append(outage.End is { } endLocal ? Local(endLocal) : string.Empty).Append(',')
                .Append(outage.Duration is { } duration
                    ? ((long)Math.Round(duration.TotalSeconds)).ToString(CultureInfo.InvariantCulture)
                    : string.Empty).Append(',')
                .Append(Cause(outage.Cause)).Append(',')
                .Append(Field(outage.Network)).Append(',')
                .Append(outage.Interrupted ? "yes" : "no");

            writer.Write(line);
            writer.Write('\n');
        }
    }

    /// <summary>A cause as the CSV spells it.</summary>
    public static string Cause(OutageCause cause) => cause switch
    {
        OutageCause.NoNetwork => "no_network",
        OutageCause.Router => "router",
        OutageCause.Provider => "provider",
        OutageCause.SignIn => "sign_in",
        _ => "unknown",
    };

    /// <summary>A text cell, quoted when needed and defused if it could be read as a formula.</summary>
    public static string Field(string? value)
    {
        if (string.IsNullOrEmpty(value))
        {
            return string.Empty;
        }

        if (value[0] is '=' or '+' or '-' or '@' or '\t' or '\r')
        {
            value = string.Concat("'", value);
        }

        return value.AsSpan().IndexOfAny(",\"\r\n") >= 0
            ? string.Concat("\"", value.Replace("\"", "\"\"", StringComparison.Ordinal), "\"")
            : value;
    }

    private static string Utc(DateTimeOffset moment) =>
        moment.ToUniversalTime().ToString(UtcPattern, CultureInfo.InvariantCulture);

    private static string Local(DateTimeOffset moment) =>
        moment.ToLocalTime().ToString(LocalPattern, CultureInfo.InvariantCulture);
}
