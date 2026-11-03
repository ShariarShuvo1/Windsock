using System.Text.Json.Serialization;

namespace Windsock.Core.Settings;

/// <summary>Where the public address is asked for.</summary>
public enum PublicAddressService
{
    /// <summary>ipify, at api.ipify.org and api6.ipify.org.</summary>
    Ipify,

    /// <summary>icanhazip.com, run by Cloudflare.</summary>
    Icanhazip,

    /// <summary>An address the person typed, which answers with the address as plain text.</summary>
    Custom,
}

/// <summary>Which server a speed test measures against.</summary>
public enum SpeedTestServer
{
    /// <summary>Measurement Lab's NDT7, which publishes every result with the tester's address.</summary>
    MeasurementLab,

    /// <summary>A LibreSpeed server the person runs, at an address they typed.</summary>
    LibreSpeed,
}

/// <summary>Which STUN server the NAT test asks.</summary>
public enum StunServerChoice
{
    /// <summary>stun.nextcloud.com:3478, which can report how the NAT maps and filters.</summary>
    Nextcloud,

    /// <summary>stun.cloudflare.com:3478.</summary>
    Cloudflare,

    /// <summary>stun.l.google.com:19302.</summary>
    Google,

    /// <summary>A server the person typed.</summary>
    Custom,
}

/// <summary>
/// The connection tools that reach beyond this PC - the public address, speed
/// tests, the NAT test and DNS timing - and the plan speed the speed tests are
/// held against.
/// </summary>
/// <remarks>
/// Every switch here starts off. Each is the standing permission for one kind
/// of outside contact, and nothing is sent until it is on and the person, or a
/// schedule they set, asks. The privacy page lists them through the
/// outside contact catalogue in Connectivity.
/// </remarks>
public sealed class ConnectionToolsSettings
{
    /// <summary>
    /// The most automatic speed tests in a day: M-Lab asks integrations for no
    /// more than four a day per device, and the same cap serves a LibreSpeed
    /// server, whose owner pays for the bandwidth.
    /// </summary>
    public const int MostScheduledTestsADay = 4;

    /// <summary>Whether the public address may be looked up when asked.</summary>
    public bool PublicAddressLookups { get; set; }

    /// <summary>Which service answers the public address lookup.</summary>
    public PublicAddressService PublicAddressService { get; set; } = PublicAddressService.Ipify;

    /// <summary>The address asked when <see cref="PublicAddressService"/> is <see cref="PublicAddressService.Custom"/>.</summary>
    public string PublicAddressCustomUrl { get; set; } = string.Empty;

    /// <summary>Whether the DNS servers in use may be timed when asked.</summary>
    public bool DnsTiming { get; set; }

    /// <summary>Whether the NAT test may ask a STUN server when asked.</summary>
    public bool StunTest { get; set; }

    /// <summary>Which STUN server the NAT test asks.</summary>
    public StunServerChoice StunServer { get; set; } = StunServerChoice.Nextcloud;

    /// <summary>The server asked when <see cref="StunServer"/> is <see cref="StunServerChoice.Custom"/>, as host or host:port.</summary>
    public string StunCustomServer { get; set; } = string.Empty;

    /// <summary>Which server speed tests measure against.</summary>
    public SpeedTestServer SpeedTestServer { get; set; } = SpeedTestServer.MeasurementLab;

    /// <summary>The LibreSpeed server's address, such as https://speed.example.lan/.</summary>
    public string LibreSpeedUrl { get; set; } = string.Empty;

    /// <summary>
    /// When the person agreed that M-Lab publishes each test with this PC's
    /// public address and keeps it for good, or nothing when they have not.
    /// No M-Lab test runs without it.
    /// </summary>
    public DateTimeOffset? MeasurementLabConsentAt { get; set; }

    /// <summary>Whether speed tests also run by themselves, a few times a day at random.</summary>
    public bool ScheduledSpeedTests { get; set; }

    /// <summary>How many automatic tests a day to aim for, at most <see cref="MostScheduledTestsADay"/>.</summary>
    public int ScheduledTestsADay { get; set; } = MostScheduledTestsADay;

    /// <summary>Whether automatic tests also run on a connection Windows treats as metered.</summary>
    public bool ScheduledTestsOnMetered { get; set; }

    /// <summary>The download speed the internet plan promises, in megabits a second, or zero when not given.</summary>
    public double PlanDownloadMbps { get; set; }

    /// <summary>The upload speed the plan promises, in megabits a second, or zero when not given.</summary>
    public double PlanUploadMbps { get; set; }

    /// <summary>Whether M-Lab tests may run: the person agreed.</summary>
    [JsonIgnore]
    public bool MeasurementLabAgreed => MeasurementLabConsentAt is not null;

    /// <summary>The automatic tests a day, held to between one and <see cref="MostScheduledTestsADay"/>.</summary>
    [JsonIgnore]
    public int TestsADay => Math.Clamp(ScheduledTestsADay, 1, MostScheduledTestsADay);

    /// <summary>
    /// A copy for another PC: every choice, but not the agreement to have M-Lab
    /// publish this PC's address, nor the schedule that would act on it. Both
    /// are asked again on the machine they would apply to.
    /// </summary>
    public ConnectionToolsSettings ForAnotherMachine() => new()
    {
        PublicAddressLookups = PublicAddressLookups,
        PublicAddressService = PublicAddressService,
        PublicAddressCustomUrl = PublicAddressCustomUrl,
        DnsTiming = DnsTiming,
        StunTest = StunTest,
        StunServer = StunServer,
        StunCustomServer = StunCustomServer,
        SpeedTestServer = SpeedTestServer,
        LibreSpeedUrl = LibreSpeedUrl,
        ScheduledTestsADay = ScheduledTestsADay,
        ScheduledTestsOnMetered = ScheduledTestsOnMetered,
        PlanDownloadMbps = PlanDownloadMbps,
        PlanUploadMbps = PlanUploadMbps,
    };
}
