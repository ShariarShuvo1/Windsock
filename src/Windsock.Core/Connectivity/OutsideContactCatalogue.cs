using Windsock.Core.Lookups;
using Windsock.Core.Settings;

namespace Windsock.Core.Connectivity;

/// <summary>One thing the connection tools may send beyond this PC, as the privacy page lists it.</summary>
/// <param name="Key">A stable name for it.</param>
/// <param name="Title">What the person would call it.</param>
/// <param name="Description">One line: what is sent, to whom, and when.</param>
/// <param name="Destination">Who would be contacted with the current settings.</param>
/// <param name="IsAllowed">Whether its switch is on.</param>
/// <param name="Allow">Sets its switch.</param>
/// <param name="FullEditionOnly">Whether the Store edition leaves it out.</param>
/// <param name="OffOnly">
/// Whether a list may only switch it off: the M-Lab agreement is given on the
/// speed test page, beside what it agrees to, and a LibreSpeed server needs its
/// address typed there. <paramref name="Allow"/> then ignores a request to switch on.
/// </param>
public sealed record OutsideContact(
    string Key,
    string Title,
    string Description,
    Func<WindsockSettings, string> Destination,
    Func<WindsockSettings, bool> IsAllowed,
    Action<WindsockSettings, bool> Allow,
    bool FullEditionOnly,
    bool OffOnly = false)
{
    /// <summary>Whether a list may switch it on as well as off.</summary>
    public bool CanAllowHere => !OffOnly;
}

/// <summary>
/// The outside contacts the connection tools add, each with its switch and a
/// line to show. All start off. Route tracing and reverse lookups, which the
/// map owns, are the privacy page's own and are not repeated here; the
/// hop-by-hop table uses the route tracing switch.
/// </summary>
public static class OutsideContactCatalogue
{
    /// <summary>The public address lookup.</summary>
    public static OutsideContact PublicAddress { get; } = new(
        "public-address",
        "Public address lookup",
        "Asks the service you pick for the address the internet sees. Sent only when you press Look up; the service learns your public address.",
        settings => PublicAddressLookup.Describe(settings.ConnectionTools),
        settings => settings.ConnectionTools.PublicAddressLookups,
        (settings, on) => settings.ConnectionTools.PublicAddressLookups = on,
        FullEditionOnly: false);

    /// <summary>Speed tests against Measurement Lab.</summary>
    public static OutsideContact MeasurementLab { get; } = new(
        "speed-test-mlab",
        "Speed tests with M-Lab",
        "Measures your speed against Measurement Lab's nearest server. M-Lab publishes every result with your public address and keeps it for good.",
        _ => "locate.measurementlab.net and its nearest NDT7 server",
        settings => settings.ConnectionTools.MeasurementLabAgreed,
        (settings, on) =>
        {
            if (!on)
            {
                settings.ConnectionTools.MeasurementLabConsentAt = null;

                if (settings.ConnectionTools.SpeedTestServer == SpeedTestServer.MeasurementLab)
                {
                    settings.ConnectionTools.ScheduledSpeedTests = false;
                }
            }
        },
        FullEditionOnly: false,
        OffOnly: true);

    /// <summary>Speed tests against the person's own LibreSpeed server.</summary>
    public static OutsideContact LibreSpeed { get; } = new(
        "speed-test-librespeed",
        "Speed tests with your LibreSpeed server",
        "Measures your speed against a LibreSpeed server you run, at the address you typed. Nothing goes anywhere else.",
        settings => SpeedTests.LibreSpeedClient.ParseRoot(settings.ConnectionTools.LibreSpeedUrl)?.Host ?? "no server set",
        settings => settings.ConnectionTools.SpeedTestServer == SpeedTestServer.LibreSpeed
            && SpeedTests.LibreSpeedClient.ParseRoot(settings.ConnectionTools.LibreSpeedUrl) is not null,
        (settings, on) =>
        {
            if (!on)
            {
                settings.ConnectionTools.LibreSpeedUrl = string.Empty;
            }
        },
        FullEditionOnly: false,
        OffOnly: true);

    /// <summary>Automatic speed tests.</summary>
    public static OutsideContact ScheduledSpeedTests { get; } = new(
        "speed-test-schedule",
        "Automatic speed tests",
        "Runs up to four speed tests a day at random times, against the server above, skipping metered connections unless you allow them.",
        settings => settings.ConnectionTools.SpeedTestServer == SpeedTestServer.LibreSpeed ? "your LibreSpeed server" : "Measurement Lab",
        settings => settings.ConnectionTools.ScheduledSpeedTests,
        (settings, on) => settings.ConnectionTools.ScheduledSpeedTests = on,
        FullEditionOnly: false);

    /// <summary>The NAT test.</summary>
    public static OutsideContact Stun { get; } = new(
        "nat-test",
        "NAT test",
        "Asks a STUN server how your router and provider translate addresses. Sent only when you press Test; the server learns your public address and port.",
        settings =>
        {
            (string host, int port) = StunNatTest.ServerFor(settings.ConnectionTools.StunServer, settings.ConnectionTools.StunCustomServer);
            return host.Length == 0 ? "no server set" : string.Concat(host, ":", port.ToString(System.Globalization.CultureInfo.InvariantCulture));
        },
        settings => settings.ConnectionTools.StunTest,
        (settings, on) => settings.ConnectionTools.StunTest = on,
        FullEditionOnly: false);

    /// <summary>DNS timing.</summary>
    public static OutsideContact DnsTiming { get; } = new(
        "dns-timing",
        "DNS timing",
        "Times your DNS servers with a few queries for the root zone, which names no website. Sent only when you press Time, to the servers this PC already uses.",
        _ => "the DNS servers this PC uses",
        settings => settings.ConnectionTools.DnsTiming,
        (settings, on) => settings.ConnectionTools.DnsTiming = on,
        FullEditionOnly: false);

    /// <summary>Every contact the connection tools add, in the order a page lists them.</summary>
    public static IReadOnlyList<OutsideContact> All { get; } =
        [PublicAddress, MeasurementLab, LibreSpeed, ScheduledSpeedTests, Stun, DnsTiming];
}
