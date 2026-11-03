namespace Windsock.Core.Formatting;

/// <summary>The Wi-Fi link, in the few figures the meter shows.</summary>
/// <param name="Percent">Signal quality, 0 to 100.</param>
/// <param name="Bars">Bars, 0 to 4, as Windows draws them.</param>
/// <param name="Band">The band, such as "5 GHz", or empty.</param>
public readonly record struct WifiFigures(double? Percent, int? Bars, string Band);

/// <summary>
/// What the meter knows besides the network and the machine: response times,
/// history's totals, addresses, a plan, Wi-Fi, values read from JSON, and the
/// scale temperatures are written in.
/// </summary>
/// <remarks>
/// Plain figures rather than the monitors' own types, so the readout depends
/// on none of them: the meter fills these from whichever monitors exist, and a
/// figure nobody could fill stays empty and shows as a dash.
/// </remarks>
public sealed record MeterExtras
{
    /// <summary>Nothing known.</summary>
    public static MeterExtras None { get; } = new();

    /// <summary>The router's last response time, in milliseconds.</summary>
    public double? RouterMilliseconds { get; init; }

    /// <summary>The internet address's last response time, in milliseconds.</summary>
    public double? InternetMilliseconds { get; init; }

    /// <summary>Whether an internet address is being pinged at all.</summary>
    public bool InternetChecked { get; init; }

    /// <summary>Bytes moved today, both ways.</summary>
    public long? TodayBytes { get; init; }

    /// <summary>Bytes moved this month, both ways.</summary>
    public long? MonthBytes { get; init; }

    /// <summary>This PC's address on its network.</summary>
    public string? LocalAddress { get; init; }

    /// <summary>The address the internet sees.</summary>
    public string? PublicAddress { get; init; }

    /// <summary>The country the public address belongs to, as two letters.</summary>
    public string? PublicCountry { get; init; }

    /// <summary>What is left of the plan in use: "41 GB left · 17 days".</summary>
    public string? PlanLeft { get; init; }

    /// <summary>How much of the plan's allowance is used, in percent, past 100 when over; nothing without one.</summary>
    public double? PlanUsedPercent { get; init; }

    /// <summary>The Wi-Fi link, when the PC is on one.</summary>
    public WifiFigures? Wifi { get; init; }

    /// <summary>Values read from JSON, by <see cref="MeterReadout.FeedKey"/>.</summary>
    public IReadOnlyDictionary<string, Meter.FieldReading>? Feeds { get; init; }

    /// <summary>The scale temperatures are written in.</summary>
    public TemperatureUnit Temperature { get; init; }
}
