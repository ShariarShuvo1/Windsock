using System.Globalization;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Windsock.Core.Security;

/// <summary>How a reputation lookup went.</summary>
public enum ReputationOutcome
{
    /// <summary>VirusTotal knows the file and gave its engines' verdicts.</summary>
    Known,

    /// <summary>VirusTotal has never seen the file - not the same as clean.</summary>
    Unknown,

    /// <summary>No key is set, so nothing was sent.</summary>
    NoKey,

    /// <summary>VirusTotal refused the key.</summary>
    BadKey,

    /// <summary>VirusTotal said the key's allowance is used up.</summary>
    OverQuota,

    /// <summary>
    /// Windsock held the request back to stay inside the free allowance
    /// (4 a minute, 500 a day); nothing was sent.
    /// </summary>
    HeldBack,

    /// <summary>The file could not be read to hash it; nothing was sent.</summary>
    Unreadable,

    /// <summary>The request failed - no connection, a timeout, an answer that made no sense.</summary>
    Failed,
}

/// <summary>What VirusTotal said about one file.</summary>
/// <param name="Sha256">The file's SHA-256, lower-case hex: the only thing sent.</param>
/// <param name="Outcome">How the lookup went.</param>
/// <param name="Malicious">Engines that called it malicious.</param>
/// <param name="Suspicious">Engines that called it suspicious.</param>
/// <param name="Harmless">Engines that called it harmless.</param>
/// <param name="Undetected">Engines that found nothing.</param>
/// <param name="AnalysedAt">When VirusTotal last analysed it.</param>
/// <param name="ThreatLabel">VirusTotal's suggested label for what it is, when engines agree on one.</param>
/// <param name="Detail">A sentence on why the lookup did not give verdicts, when it did not.</param>
/// <param name="CheckedAt">When Windsock asked.</param>
public sealed record ReputationReport(
    string Sha256,
    ReputationOutcome Outcome,
    int Malicious,
    int Suspicious,
    int Harmless,
    int Undetected,
    DateTimeOffset? AnalysedAt,
    string? ThreatLabel,
    string? Detail,
    DateTimeOffset CheckedAt)
{
    /// <summary>Engines that gave a verdict.</summary>
    public int Engines => Malicious + Suspicious + Harmless + Undetected;

    /// <summary>The file's page on VirusTotal, for the person to read in full.</summary>
    public Uri Page => new("https://www.virustotal.com/gui/file/" + Sha256);

    /// <summary>The verdict in a sentence.</summary>
    public string Text => Outcome switch
    {
        ReputationOutcome.Known when Malicious + Suspicious == 0 => string.Format(
            CultureInfo.CurrentCulture,
            "None of {0} engines flag it.",
            Engines),
        ReputationOutcome.Known => string.Format(
            CultureInfo.CurrentCulture,
            "{0} of {1} engines flag it{2}. A few flags are often false alarms.",
            Malicious + Suspicious,
            Engines,
            ThreatLabel is null ? string.Empty : " (" + ThreatLabel + ")"),
        ReputationOutcome.Unknown => "VirusTotal has never seen this file. That is not the same as clean.",
        _ => Detail ?? "The lookup did not complete.",
    };

    /// <summary>A report that sent nothing, or failed, with why.</summary>
    public static ReputationReport Without(string sha256, ReputationOutcome outcome, string detail, DateTimeOffset at) =>
        new(sha256, outcome, 0, 0, 0, 0, null, null, detail, at);
}

/// <summary>Reads VirusTotal API v3 answers for <c>GET /api/v3/files/{hash}</c>.</summary>
public static class VirusTotalAnswers
{
    /// <summary>The verdicts in a 200 answer's <c>data.attributes</c>; null when the body is not that shape.</summary>
    public static ReputationReport? Read(string sha256, string json, DateTimeOffset at)
    {
        ArgumentNullException.ThrowIfNull(json);

        try
        {
            using JsonDocument document = JsonDocument.Parse(json);

            // Every value is checked for its kind before it is read: reading a
            // value of another kind throws InvalidOperationException, which a
            // JsonException catch never sees.
            if (document.RootElement.ValueKind != JsonValueKind.Object
                || !document.RootElement.TryGetProperty("data", out JsonElement data)
                || data.ValueKind != JsonValueKind.Object
                || !data.TryGetProperty("attributes", out JsonElement attributes)
                || attributes.ValueKind != JsonValueKind.Object)
            {
                return null;
            }

            int malicious = 0, suspicious = 0, harmless = 0, undetected = 0;

            if (attributes.TryGetProperty("last_analysis_stats", out JsonElement stats) && stats.ValueKind == JsonValueKind.Object)
            {
                malicious = Count(stats, "malicious");
                suspicious = Count(stats, "suspicious");
                harmless = Count(stats, "harmless");
                undetected = Count(stats, "undetected");
            }

            DateTimeOffset? analysed = attributes.TryGetProperty("last_analysis_date", out JsonElement date)
                && date.ValueKind == JsonValueKind.Number
                && date.TryGetInt64(out long seconds)
                && seconds is > 0 and < MaxUnixSeconds
                    ? DateTimeOffset.FromUnixTimeSeconds(seconds)
                    : null;

            string? label = attributes.TryGetProperty("popular_threat_classification", out JsonElement classification)
                && classification.ValueKind == JsonValueKind.Object
                && classification.TryGetProperty("suggested_threat_label", out JsonElement suggested)
                && suggested.ValueKind == JsonValueKind.String
                    ? suggested.GetString()
                    : null;

            return new ReputationReport(sha256, ReputationOutcome.Known, malicious, suspicious, harmless, undetected, analysed, label, null, at);
        }
        catch (Exception ex) when (BlocklistFormats.IsMalformed(ex))
        {
            return null;
        }
    }

    /// <summary>Unix seconds for the year 9999, the last a date can hold.</summary>
    private const long MaxUnixSeconds = 253_402_300_799;

    private static int Count(JsonElement stats, string name) =>
        stats.TryGetProperty(name, out JsonElement value)
        && value.ValueKind == JsonValueKind.Number
        && value.TryGetInt32(out int count)
            ? Math.Max(0, count)
            : 0;
}

/// <summary>
/// Keeps lookups inside VirusTotal's free allowance: 4 a minute and 500 a day,
/// and backs off when VirusTotal says the allowance is used.
/// </summary>
public sealed class RequestAllowance
{
    /// <summary>Requests allowed in any minute.</summary>
    public const int PerMinute = 4;

    /// <summary>Requests allowed in a day (UTC).</summary>
    public const int PerDay = 500;

    private readonly TimeProvider _clock;
    private readonly string? _path;
    private readonly Queue<DateTimeOffset> _lastMinute = new();
    private readonly Lock _gate = new();
    private DateOnly _day;
    private int _today;
    private DateTimeOffset _pausedUntil = DateTimeOffset.MinValue;
    private int _refusals;

    /// <summary>
    /// Keeps count in memory, and when <paramref name="path"/> is given in a
    /// file too, so a restart does not hand out a second day's allowance.
    /// </summary>
    public RequestAllowance(TimeProvider clock, string? path = null)
    {
        ArgumentNullException.ThrowIfNull(clock);
        _clock = clock;
        _path = path;
        ReadCount();
    }

    /// <summary>Requests made today (UTC).</summary>
    public int UsedToday
    {
        get
        {
            lock (_gate)
            {
                Roll(_clock.GetUtcNow());
                return _today;
            }
        }
    }

    /// <summary>Takes one request if the allowance has room; otherwise says how long until it does.</summary>
    public bool TryTake(out TimeSpan wait)
    {
        lock (_gate)
        {
            DateTimeOffset now = _clock.GetUtcNow();
            Roll(now);

            while (_lastMinute.Count > 0 && now - _lastMinute.Peek() >= TimeSpan.FromMinutes(1))
            {
                _lastMinute.Dequeue();
            }

            if (now < _pausedUntil)
            {
                wait = _pausedUntil - now;
                return false;
            }

            if (_today >= PerDay)
            {
                wait = new DateTimeOffset(_day.AddDays(1), TimeOnly.MinValue, TimeSpan.Zero) - now;
                return false;
            }

            if (_lastMinute.Count >= PerMinute)
            {
                wait = TimeSpan.FromMinutes(1) - (now - _lastMinute.Peek());
                return false;
            }

            _lastMinute.Enqueue(now);
            _today++;
            WriteCount();
            wait = TimeSpan.Zero;
            return true;
        }
    }

    /// <summary>Forgets the count kept on disk, with the key it was for.</summary>
    public void Forget()
    {
        lock (_gate)
        {
            _today = 0;

            try
            {
                if (_path is not null)
                {
                    File.Delete(_path);
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
            }
        }
    }

    /// <summary>Reads the day and its count, if kept. Call from the constructor only.</summary>
    private void ReadCount()
    {
        if (_path is null || !File.Exists(_path))
        {
            return;
        }

        try
        {
            string[] parts = File.ReadAllText(_path, Encoding.UTF8).Trim().Split(' ');

            if (parts.Length == 2
                && DateOnly.TryParseExact(parts[0], "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out DateOnly day)
                && int.TryParse(parts[1], NumberStyles.None, CultureInfo.InvariantCulture, out int count))
            {
                _day = day;
                _today = Math.Clamp(count, 0, PerDay);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
    }

    /// <summary>Keeps the day and its count. Call with the gate held.</summary>
    private void WriteCount()
    {
        if (_path is null)
        {
            return;
        }

        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
            File.WriteAllText(
                _path + ".tmp",
                string.Create(CultureInfo.InvariantCulture, $"{_day:yyyy-MM-dd} {_today}"),
                new UTF8Encoding(false));
            File.Move(_path + ".tmp", _path, overwrite: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
    }

    /// <summary>
    /// VirusTotal answered "quota exceeded": pause a minute, and an hour if it
    /// says so again straight after.
    /// </summary>
    public void Refused()
    {
        lock (_gate)
        {
            _refusals++;
            _pausedUntil = _clock.GetUtcNow() + (_refusals > 1 ? TimeSpan.FromHours(1) : TimeSpan.FromMinutes(1));
        }
    }

    /// <summary>A request went through, so the next refusal starts the pauses again.</summary>
    public void Accepted()
    {
        lock (_gate)
        {
            _refusals = 0;
        }
    }

    private void Roll(DateTimeOffset now)
    {
        DateOnly day = DateOnly.FromDateTime(now.UtcDateTime);

        if (day != _day)
        {
            _day = day;
            _today = 0;
        }
    }
}

/// <summary>
/// Looks a program up on VirusTotal by its SHA-256, with the person's own key,
/// only when they ask.
/// </summary>
/// <remarks>
/// <para>
/// The hash is the only thing sent - never the file, its name or its path -
/// in one HTTPS request to <c>www.virustotal.com</c> carrying the key. Nothing
/// is looked up by itself. Answers are kept for a week (an unknown file for a
/// day) so a program is not looked up twice in a row.
/// </para>
/// <para>
/// VirusTotal's public API is for non-commercial use, 4 requests a minute and
/// 500 a day, and a key may not be shared; the person brings their own.
/// Whether a free, open-source program using people's own keys meets the
/// "non-commercial" clause is a reading of VirusTotal's terms nobody here has
/// had confirmed.
/// </para>
/// </remarks>
public sealed class ReputationService : IDisposable
{
    private static readonly Uri Api = new("https://www.virustotal.com/api/v3/files/");
    private static readonly TimeSpan KeepKnown = TimeSpan.FromDays(7);
    private static readonly TimeSpan KeepUnknown = TimeSpan.FromDays(1);
    private static readonly JsonSerializerOptions CacheFormat = new() { WriteIndented = false };

    private readonly ApiKeyVault _vault;
    private readonly HttpClient _http;
    private readonly TimeProvider _clock;
    private readonly string? _cachePath;
    private readonly RequestAllowance _allowance;
    private readonly Dictionary<string, ReputationReport> _cache = new(StringComparer.OrdinalIgnoreCase);
    private readonly Lock _gate = new();
    private bool _loaded;

    /// <summary>The most of an answer kept in memory; VirusTotal's run to tens of kilobytes.</summary>
    private const long MostBytes = 2L * 1024 * 1024;

    public ReputationService(ApiKeyVault vault)
        : this(vault, new HttpClient(new SocketsHttpHandler { PooledConnectionLifetime = TimeSpan.FromMinutes(5) }), TimeProvider.System, Insight.InsightPaths.Reputation)
    {
    }

    internal ReputationService(ApiKeyVault vault, HttpClient http, TimeProvider clock, string? cachePath)
    {
        ArgumentNullException.ThrowIfNull(vault);
        ArgumentNullException.ThrowIfNull(http);
        ArgumentNullException.ThrowIfNull(clock);

        _vault = vault;
        _http = http;
        _clock = clock;
        _cachePath = cachePath;
        _allowance = new RequestAllowance(
            clock,
            cachePath is null ? null : Path.Combine(Path.GetDirectoryName(cachePath) ?? string.Empty, "virustotal-usage.txt"));
        _http.Timeout = TimeSpan.FromSeconds(20);

        // The answer is buffered whole (SendAsync's default), and never past this.
        _http.MaxResponseContentBufferSize = MostBytes;
        _http.DefaultRequestHeaders.UserAgent.ParseAdd(Blocklists.UserAgent);
    }

    /// <summary>Whether a key is set, which is the standing permission to look up when asked.</summary>
    public bool HasKey => _vault.HasKey;

    /// <summary>Requests made today, against the free allowance of 500.</summary>
    public int UsedToday => _allowance.UsedToday;

    /// <summary>Keeps the person's key. False when it is not a key's shape or could not be protected.</summary>
    public bool SetKey(string key) => ApiKeyVault.IsWellFormed(key) && _vault.Store(key);

    /// <summary>Forgets the key, every answer kept and the day's count.</summary>
    public void ClearKey()
    {
        _vault.Clear();
        _allowance.Forget();

        lock (_gate)
        {
            _cache.Clear();
            _loaded = true;
        }

        try
        {
            if (_cachePath is not null)
            {
                File.Delete(_cachePath);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
    }

    /// <summary>A file's SHA-256, lower-case hex, read without locking the file against its program.</summary>
    public static async Task<string?> HashAsync(string path, CancellationToken token)
    {
        try
        {
            await using FileStream stream = new(path, new FileStreamOptions
            {
                Mode = FileMode.Open,
                Access = FileAccess.Read,
                Share = FileShare.ReadWrite | FileShare.Delete,
                Options = FileOptions.SequentialScan | FileOptions.Asynchronous,
                BufferSize = 1 << 16,
            });

            byte[] hash = await SHA256.HashDataAsync(stream, token).ConfigureAwait(false);
            return Convert.ToHexStringLower(hash);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            return null;
        }
    }

    /// <summary>The answer kept for a hash, if it is still fresh.</summary>
    public ReputationReport? Cached(string sha256)
    {
        lock (_gate)
        {
            LoadCache();
            return _cache.TryGetValue(sha256, out ReputationReport? report) && IsFresh(report, _clock.GetUtcNow()) ? report : null;
        }
    }

    /// <summary>Hashes a program and looks the hash up. The person asked; this is the only way a lookup happens.</summary>
    public async Task<ReputationReport> LookUpFileAsync(string path, CancellationToken token)
    {
        string? sha = await HashAsync(path, token).ConfigureAwait(false);

        return sha is null
            ? ReputationReport.Without(string.Empty, ReputationOutcome.Unreadable, "The file could not be read, so nothing was sent.", _clock.GetUtcNow())
            : await LookUpAsync(sha, token).ConfigureAwait(false);
    }

    /// <summary>Looks a SHA-256 up, from the kept answers when fresh, otherwise on VirusTotal.</summary>
    public async Task<ReputationReport> LookUpAsync(string sha256, CancellationToken token)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sha256);

        string sha = sha256.Trim().ToLowerInvariant();
        DateTimeOffset now = _clock.GetUtcNow();

        if (sha.Length != 64 || !sha.All(Uri.IsHexDigit))
        {
            return ReputationReport.Without(sha, ReputationOutcome.Failed, "That is not a SHA-256.", now);
        }

        if (Cached(sha) is { } kept)
        {
            return kept;
        }

        string? key = _vault.Read();

        if (string.IsNullOrWhiteSpace(key))
        {
            return ReputationReport.Without(sha, ReputationOutcome.NoKey, "Add your VirusTotal key to look programs up. Nothing was sent.", now);
        }

        if (!_allowance.TryTake(out TimeSpan wait))
        {
            return ReputationReport.Without(
                sha,
                ReputationOutcome.HeldBack,
                string.Format(CultureInfo.CurrentCulture, "Held back to stay inside VirusTotal's free allowance; try again in {0:N0} seconds. Nothing was sent.", Math.Ceiling(wait.TotalSeconds)),
                now);
        }

        ReputationReport report;

        if (!ApiKeyVault.IsWellFormed(key))
        {
            return ReputationReport.Without(sha, ReputationOutcome.BadKey, "The key kept is not shaped like a VirusTotal key; add it again. Nothing was sent.", now);
        }

        try
        {
            using HttpRequestMessage request = new(HttpMethod.Get, new Uri(Api, sha));
            request.Headers.Add("x-apikey", key.Trim());

            using HttpResponseMessage response = await _http.SendAsync(request, token).ConfigureAwait(false);
            string body = await response.Content.ReadAsStringAsync(token).ConfigureAwait(false);

            report = response.StatusCode switch
            {
                HttpStatusCode.OK => VirusTotalAnswers.Read(sha, body, now)
                    ?? ReputationReport.Without(sha, ReputationOutcome.Failed, "VirusTotal's answer could not be read.", now),
                HttpStatusCode.NotFound => new ReputationReport(sha, ReputationOutcome.Unknown, 0, 0, 0, 0, null, null, null, now),
                HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden => ReputationReport.Without(sha, ReputationOutcome.BadKey, "VirusTotal did not accept the key.", now),
                HttpStatusCode.TooManyRequests => ReputationReport.Without(sha, ReputationOutcome.OverQuota, "VirusTotal says the key's allowance is used up for now.", now),
                _ => ReputationReport.Without(sha, ReputationOutcome.Failed, string.Create(CultureInfo.InvariantCulture, $"VirusTotal answered {(int)response.StatusCode}."), now),
            };
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or IOException)
        {
            token.ThrowIfCancellationRequested();
            return ReputationReport.Without(sha, ReputationOutcome.Failed, "VirusTotal could not be reached, or answered more than an answer holds.", now);
        }
        catch (Exception ex) when (BlocklistFormats.IsMalformed(ex))
        {
            return ReputationReport.Without(sha, ReputationOutcome.Failed, "VirusTotal's answer could not be read.", now);
        }

        if (report.Outcome == ReputationOutcome.OverQuota)
        {
            _allowance.Refused();
        }
        else
        {
            _allowance.Accepted();
        }

        if (report.Outcome is ReputationOutcome.Known or ReputationOutcome.Unknown)
        {
            Remember(report);
        }

        return report;
    }

    private static bool IsFresh(ReputationReport report, DateTimeOffset now) =>
        now - report.CheckedAt < (report.Outcome == ReputationOutcome.Unknown ? KeepUnknown : KeepKnown)
        && now >= report.CheckedAt;

    private void Remember(ReputationReport report)
    {
        List<ReputationReport> keep;

        lock (_gate)
        {
            LoadCache();
            _cache[report.Sha256] = report;

            DateTimeOffset now = _clock.GetUtcNow();
            keep = [.. _cache.Values.Where(each => IsFresh(each, now))];
        }

        if (_cachePath is null)
        {
            return;
        }

        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_cachePath)!);
            File.WriteAllText(_cachePath + ".tmp", JsonSerializer.Serialize(keep, CacheFormat), new UTF8Encoding(false));
            File.Move(_cachePath + ".tmp", _cachePath, overwrite: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
    }

    /// <summary>Reads the kept answers once. Call with the gate held.</summary>
    private void LoadCache()
    {
        if (_loaded)
        {
            return;
        }

        _loaded = true;

        try
        {
            if (_cachePath is null || !File.Exists(_cachePath))
            {
                return;
            }

            List<ReputationReport>? kept = JsonSerializer.Deserialize<List<ReputationReport>>(File.ReadAllText(_cachePath, Encoding.UTF8), CacheFormat);
            DateTimeOffset now = _clock.GetUtcNow();

            foreach (ReputationReport? report in kept ?? [])
            {
                if (report is { Sha256.Length: 64 } && IsFresh(report, now))
                {
                    _cache[report.Sha256] = report;
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException || BlocklistFormats.IsMalformed(ex) || ex is NotSupportedException)
        {
        }
    }

    public void Dispose() => _http.Dispose();
}
