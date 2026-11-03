using System.Globalization;

namespace Windsock.Core.SpeedTests;

/// <summary>Which kind of server a test ran against.</summary>
public enum SpeedTestBackend
{
    /// <summary>Measurement Lab's NDT7.</summary>
    MeasurementLab,

    /// <summary>A LibreSpeed server the person runs.</summary>
    LibreSpeed,
}

/// <summary>Where the latency figures of a test came from.</summary>
public enum LatencySource
{
    /// <summary>None measured.</summary>
    None,

    /// <summary>Pings to the internet address chosen in the connection settings.</summary>
    InternetTarget,

    /// <summary>The test server's own measure of its connection's round trip (NDT7's TCPInfo).</summary>
    ServerTcp,

    /// <summary>Small requests to the test server, timed (LibreSpeed's empty.php).</summary>
    ServerRequests,
}

/// <summary>A Waveform-style letter for latency under load.</summary>
public enum BloatGrade
{
    Unknown,
    APlus,
    A,
    B,
    C,
    D,
    F,
}

/// <summary>One speed test, kept for good in the connection log.</summary>
/// <param name="Id">Its row, 0 before it is kept.</param>
/// <param name="At">When it started.</param>
/// <param name="Backend">The kind of server.</param>
/// <param name="Server">The server's name or address, and its city when known.</param>
/// <param name="Scheduled">Whether it ran by itself on the schedule, rather than on a click.</param>
/// <param name="DownloadBitsPerSecond">Download speed, or nothing when that half failed.</param>
/// <param name="UploadBitsPerSecond">Upload speed, or nothing.</param>
/// <param name="IdleMs">Response time with the line quiet.</param>
/// <param name="LoadedDownMs">Response time while downloading.</param>
/// <param name="LoadedUpMs">Response time while uploading.</param>
/// <param name="IdleJitterMs">Variation in the quiet response times.</param>
/// <param name="Latency">Where the response times came from.</param>
/// <param name="Network">The network it ran on.</param>
/// <param name="Problem">What went wrong, in a sentence, when something did.</param>
public sealed record SpeedTestResult(
    long Id,
    DateTimeOffset At,
    SpeedTestBackend Backend,
    string Server,
    bool Scheduled,
    double? DownloadBitsPerSecond,
    double? UploadBitsPerSecond,
    double? IdleMs,
    double? LoadedDownMs,
    double? LoadedUpMs,
    double? IdleJitterMs,
    LatencySource Latency,
    string? Network,
    string? Problem)
{
    /// <summary>Whether the test measured anything.</summary>
    public bool Succeeded => DownloadBitsPerSecond is not null || UploadBitsPerSecond is not null;

    /// <summary>
    /// How much the response time grew under load: the busier direction's
    /// median minus the quiet median (Cloudflare AIM's loaded latency increase).
    /// </summary>
    public double? LoadedIncreaseMs
    {
        get
        {
            if (IdleMs is not { } idle)
            {
                return null;
            }

            double? loaded = (LoadedDownMs, LoadedUpMs) switch
            {
                ({ } down, { } up) => Math.Max(down, up),
                ({ } down, null) => down,
                (null, { } up) => up,
                _ => null,
            };

            return loaded is { } value ? Math.Max(0, value - idle) : null;
        }
    }

    /// <summary>The bufferbloat letter for <see cref="LoadedIncreaseMs"/>.</summary>
    public BloatGrade Bloat => SpeedGrades.Bloat(LoadedIncreaseMs);

    /// <summary>Download in megabits a second.</summary>
    public double? DownloadMbps => DownloadBitsPerSecond / 1e6;

    /// <summary>Upload in megabits a second.</summary>
    public double? UploadMbps => UploadBitsPerSecond / 1e6;
}

/// <summary>Grades for speed tests.</summary>
public static class SpeedGrades
{
    /// <summary>
    /// The letter for added latency under load, after Waveform's bufferbloat
    /// test as secondary sources describe it: A+ under 5 ms, A under 30, B
    /// under 60, C under 200, D under 400, F from 400. Waveform's own page could
    /// not be read, so this is "similar to Waveform", not Waveform's.
    /// </summary>
    public static BloatGrade Bloat(double? addedMs) => addedMs switch
    {
        null => BloatGrade.Unknown,
        < 5 => BloatGrade.APlus,
        < 30 => BloatGrade.A,
        < 60 => BloatGrade.B,
        < 200 => BloatGrade.C,
        < 400 => BloatGrade.D,
        _ => BloatGrade.F,
    };

    /// <summary>"A+", "A" ... "F", or a dash.</summary>
    public static string Letter(BloatGrade grade) => grade switch
    {
        BloatGrade.APlus => "A+",
        BloatGrade.Unknown => "—",
        _ => grade.ToString(),
    };

    /// <summary>A speed in megabits a second, as "94.3 Mbit/s", or a dash.</summary>
    public static string Mbps(double? bitsPerSecond) => bitsPerSecond is { } bits && double.IsFinite(bits)
        ? string.Create(CultureInfo.InvariantCulture, $"{bits / 1e6:0.0} Mbit/s")
        : "—";
}

/// <summary>Where speed tests are kept.</summary>
/// <remarks>Every member may throw for a file that cannot be read or written, like the outage log.</remarks>
public interface ISpeedTestStore
{
    /// <summary>Keeps a result and returns its row.</summary>
    long AddSpeedTest(SpeedTestResult result);

    /// <summary>The tests that started within a period, newest first.</summary>
    IReadOnlyList<SpeedTestResult> ReadSpeedTests(DateTimeOffset from, DateTimeOffset until);

    /// <summary>Forgets one test.</summary>
    bool DeleteSpeedTest(long id);

    /// <summary>Forgets every test.</summary>
    int ClearSpeedTests();

    /// <summary>
    /// Notes that an automatic test is starting, where deleting tests does not
    /// reach, so the day's cap holds whatever is deleted.
    /// </summary>
    void NoteScheduledRun(DateTimeOffset at);

    /// <summary>The automatic runs noted after a moment, those stamped later than now included.</summary>
    IReadOnlyList<DateTimeOffset> ReadScheduledRuns(DateTimeOffset since);

    /// <summary>Raised after a test is kept or forgotten.</summary>
    event EventHandler? SpeedTestsChanged;
}
