using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Windsock.Core.Adapters;
using Windsock.Core.Settings;

namespace Windsock.Core.Security;

/// <summary>Which reader a blocklist's files need.</summary>
public enum BlocklistFormat
{
    /// <summary>Spamhaus DROP's JSON lines.</summary>
    SpamhausJson,

    /// <summary>Feodo Tracker's JSON array.</summary>
    FeodoJson,
}

/// <summary>A blocklist Windsock can fetch, and whose terms it follows.</summary>
/// <param name="Id">A short key for files and settings.</param>
/// <param name="Name">What the person sees: "Spamhaus DROP".</param>
/// <param name="Publisher">Who publishes it, for the credit their terms ask for.</param>
/// <param name="Licence">The terms it is used under, in a few words.</param>
/// <param name="TermsUrl">Where the terms are.</param>
/// <param name="Urls">The files fetched, all of them every time.</param>
/// <param name="Format">How they are read.</param>
public sealed record BlocklistSource(
    string Id,
    string Name,
    string Publisher,
    string Licence,
    Uri TermsUrl,
    IReadOnlyList<Uri> Urls,
    BlocklistFormat Format);

/// <summary>The blocklists offered.</summary>
/// <remarks>
/// <para>
/// Neither is shipped with Windsock. Spamhaus's terms allow free use with
/// credit but do not address redistribution, so DROP is fetched from Spamhaus
/// by the person's own PC, and only once they switch it on. Feodo Tracker is
/// CC0 and could be shipped, but its list changes every few minutes, so a copy
/// in the installer would be stale before anyone ran it.
/// </para>
/// <para>
/// FireHOL's aggregate and DShield were considered and left out: DShield is
/// CC BY-NC-SA, which an MIT project cannot carry, and FireHOL inherits it.
/// </para>
/// </remarks>
public static class BlocklistSources
{
    /// <summary>
    /// Spamhaus "Don't Route Or Peer": netblocks run by or for criminals. EDROP
    /// was merged into it on 2024-04-10, so this is the whole of both.
    /// </summary>
    public static BlocklistSource SpamhausDrop { get; } = new(
        "spamhaus-drop",
        "Spamhaus DROP",
        "The Spamhaus Project",
        "Free to use with credit to The Spamhaus Project",
        new Uri("https://www.spamhaus.org/drop/terms/"),
        [new Uri("https://www.spamhaus.org/drop/drop_v4.json"), new Uri("https://www.spamhaus.org/drop/drop_v6.json")],
        BlocklistFormat.SpamhausJson);

    /// <summary>abuse.ch Feodo Tracker: botnet control servers seen online lately.</summary>
    public static BlocklistSource FeodoTracker { get; } = new(
        "feodo-tracker",
        "Feodo Tracker",
        "abuse.ch",
        "CC0 (no rights reserved)",
        new Uri("https://feodotracker.abuse.ch/blocklist/"),
        [new Uri("https://feodotracker.abuse.ch/downloads/ipblocklist_recommended.json")],
        BlocklistFormat.FeodoJson);

    /// <summary>Every list offered.</summary>
    public static IReadOnlyList<BlocklistSource> All { get; } = [SpamhausDrop, FeodoTracker];

    /// <summary>Whether the person has switched a list on.</summary>
    public static bool IsWanted(BlocklistSource source, InsightSettings settings)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(settings);

        return source.Id switch
        {
            "spamhaus-drop" => settings.SpamhausDrop,
            "feodo-tracker" => settings.FeodoTracker,
            _ => false,
        };
    }
}

/// <summary>An address found on a blocklist.</summary>
/// <param name="Source">The list.</param>
/// <param name="Entry">The network it falls in, and the list's note about it.</param>
/// <param name="ListedAsOf">When the list was fetched.</param>
/// <param name="Copyright">The publisher's copyright line, to show with the mark.</param>
public sealed record BlocklistHit(BlocklistSource Source, BlocklistEntry Entry, DateTimeOffset ListedAsOf, string? Copyright)
{
    /// <summary>
    /// The mark in words. "Listed in", never "malicious": a list is advice,
    /// and false positives happen.
    /// </summary>
    public string Text => string.Format(
        CultureInfo.CurrentCulture,
        "Listed in {0} ({1}), as of {2:d MMM yyyy}",
        Source.Name,
        Entry.Reference ?? Entry.Cidr,
        ListedAsOf.ToLocalTime());
}

/// <summary>Where one list stands, for the settings page.</summary>
/// <param name="Source">The list.</param>
/// <param name="Wanted">Whether it is switched on.</param>
/// <param name="FetchedAt">When it was last fetched and found current, or null.</param>
/// <param name="Networks">How many networks it holds now.</param>
/// <param name="Problem">Why the last fetch failed, or null.</param>
public sealed record BlocklistStatus(BlocklistSource Source, bool Wanted, DateTimeOffset? FetchedAt, int Networks, string? Problem);

/// <summary>
/// Checks addresses against the blocklists the person switched on, on this
/// PC, and keeps those lists fresh from their publishers.
/// </summary>
/// <remarks>
/// <para>
/// Only public addresses are ever looked up. The lists carry ranges such as
/// carrier-grade NAT (100.64.0.0/10) and private networks in places, and a
/// naive check would mark the person's own router; an address that is not
/// public is never marked, whatever a list says.
/// </para>
/// <para>
/// A list is fetched only while it is switched on, never more than once an
/// hour (Spamhaus asks for no more; once a day is enough for DROP), and
/// otherwise twice a day. Each fetch is a plain HTTPS GET carrying Windsock's
/// name and address and the date of the copy already held, so an unchanged
/// list costs a "not modified". Nothing about the PC's connections is sent:
/// the addresses are checked here. Switching a list off deletes its copy.
/// </para>
/// </remarks>
public sealed partial class Blocklists : BackgroundService
{
    /// <summary>The shortest gap between two fetches of one list, whatever asks.</summary>
    public static readonly TimeSpan Floor = TimeSpan.FromHours(1);

    /// <summary>How old a list may get before it is fetched again.</summary>
    public static readonly TimeSpan Refresh = TimeSpan.FromHours(12);

    private const long MostBytes = 8L * 1024 * 1024;
    private static readonly TimeSpan Look = TimeSpan.FromMinutes(10);

    private static readonly JsonSerializerOptions MetaFormat = new() { WriteIndented = true };

    private readonly WindsockSettings _settings;
    private readonly string _folder;
    private readonly HttpClient _http;
    private readonly TimeProvider _clock;
    private readonly ILogger<Blocklists> _logger;
    private readonly SemaphoreSlim _wake = new(0, 1);
    private readonly Lock _gate = new();
    private readonly Dictionary<string, Loaded> _loaded = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string> _problems = new(StringComparer.Ordinal);

    public Blocklists(WindsockSettings settings, ILogger<Blocklists> logger)
        : this(settings, Insight.InsightPaths.Blocklists, new HttpClient(new SocketsHttpHandler
        {
            AutomaticDecompression = DecompressionMethods.All,
            PooledConnectionLifetime = TimeSpan.FromMinutes(5),
            MaxAutomaticRedirections = 3,
        }), TimeProvider.System, logger)
    {
    }

    internal Blocklists(WindsockSettings settings, string folder, HttpClient http, TimeProvider clock, ILogger<Blocklists> logger)
    {
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentException.ThrowIfNullOrWhiteSpace(folder);
        ArgumentNullException.ThrowIfNull(http);
        ArgumentNullException.ThrowIfNull(clock);
        ArgumentNullException.ThrowIfNull(logger);

        _settings = settings;
        _folder = folder;
        _http = http;
        _clock = clock;
        _logger = logger;

        _http.Timeout = TimeSpan.FromSeconds(30);
        _http.DefaultRequestHeaders.UserAgent.ParseAdd(UserAgent);

        // Every answer is buffered whole (SendAsync's default) and never past
        // this, counted after decompression, so a small gzip that unpacks to
        // gigabytes is refused as it arrives rather than after.
        _http.MaxResponseContentBufferSize = MostBytes;
    }

    /// <summary>Raised when a list was loaded, refreshed or removed, so marks can be drawn again.</summary>
    public event EventHandler? Changed;

    /// <summary>Windsock's name, version and project page, as the publishers ask callers to say.</summary>
    internal static string UserAgent { get; } = string.Create(
        CultureInfo.InvariantCulture,
        $"Windsock/{typeof(Blocklists).Assembly.GetName().Version?.ToString(3) ?? "0"} (+https://github.com/ShariarShuvo1/Windsock)");

    /// <summary>
    /// The list an address is on, or null. Instant: the lists are held in
    /// memory. Never marks an address that is not public.
    /// </summary>
    public BlocklistHit? Check(IPAddress? address)
    {
        if (address is null || AddressScopes.Of(address) != AddressScope.Public)
        {
            return null;
        }

        lock (_gate)
        {
            foreach (Loaded loaded in _loaded.Values)
            {
                if (loaded.Set.Find(address) is { } entry)
                {
                    return new BlocklistHit(loaded.Source, entry, loaded.FetchedAt, loaded.Copyright);
                }
            }
        }

        return null;
    }

    /// <summary>The list an address written as text is on, or null.</summary>
    public BlocklistHit? Check(string? address) =>
        IPAddress.TryParse(address, out IPAddress? parsed) ? Check(parsed) : null;

    /// <summary>Where every list stands.</summary>
    public IReadOnlyList<BlocklistStatus> Status()
    {
        List<BlocklistStatus> statuses = [];

        lock (_gate)
        {
            foreach (BlocklistSource source in BlocklistSources.All)
            {
                bool wanted = BlocklistSources.IsWanted(source, _settings.Insight);
                _loaded.TryGetValue(source.Id, out Loaded? loaded);
                statuses.Add(new BlocklistStatus(source, wanted, loaded?.FetchedAt, loaded?.Set.Count ?? 0, _problems.GetValueOrDefault(source.Id)));
            }
        }

        return statuses;
    }

    /// <summary>
    /// Acts on the switches now rather than at the next look: fetches a list
    /// just switched on, deletes one switched off. The hourly floor still holds.
    /// </summary>
    public void Apply()
    {
        _wake.Signal();
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            while (!stoppingToken.IsCancellationRequested)
            {
                await SettleAsync(stoppingToken).ConfigureAwait(false);
                _ = await _wake.WaitAsync(Look, stoppingToken).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
        }
    }

    /// <summary>
    /// One look: drops lists switched off, loads ones on disk, fetches ones due.
    /// Internal so tests can drive it.
    /// </summary>
    internal async Task SettleAsync(CancellationToken token)
    {
        DateTimeOffset now = _clock.GetUtcNow();

        foreach (BlocklistSource source in BlocklistSources.All)
        {
            if (!BlocklistSources.IsWanted(source, _settings.Insight))
            {
                Drop(source);
                continue;
            }

            Meta? meta = ReadMeta(source);

            if (!IsLoaded(source) && meta is { FetchedAt: not null })
            {
                Load(source, meta);
            }

            if (IsDue(now, meta?.FetchedAt, meta?.AttemptedAt))
            {
                await FetchAsync(source, meta, now, token).ConfigureAwait(false);
            }
            else if (!IsLoaded(source))
            {
                lock (_gate)
                {
                    _problems.TryAdd(source.Id, "Asked less than an hour ago. Windsock asks each publisher at most once an hour, so it will be fetched shortly.");
                }
            }
        }
    }

    /// <summary>Whether a list should be fetched now: never inside the hourly floor, then once it is stale.</summary>
    internal static bool IsDue(DateTimeOffset now, DateTimeOffset? fetchedAt, DateTimeOffset? attemptedAt)
    {
        if (attemptedAt is { } tried && now - tried < Floor && now >= tried)
        {
            return false;
        }

        return fetchedAt is not { } fetched || now - fetched >= Refresh || now < fetched;
    }

    private async Task FetchAsync(BlocklistSource source, Meta? meta, DateTimeOffset now, CancellationToken token)
    {
        Meta next = new()
        {
            FetchedAt = meta?.FetchedAt,
            AttemptedAt = now,
            Copyright = meta?.Copyright,
            Files = [],
        };

        // Recorded before asking, so a fetch that hangs or crashes still
        // counts against the hourly floor.
        WriteMeta(source, next);

        List<string> bodies = [];
        bool changed = false;

        try
        {
            for (int index = 0; index < source.Urls.Count; index++)
            {
                Uri url = source.Urls[index];
                FileMeta? before = meta?.Files?.FirstOrDefault(each => each.Url == url.ToString());
                string path = BodyPath(source, index);

                using HttpRequestMessage request = new(HttpMethod.Get, url);

                if (before is not null && File.Exists(path))
                {
                    if (before.ETag is { Length: > 0 } etag && EntityTagHeaderValue.TryParse(etag, out EntityTagHeaderValue? tag))
                    {
                        request.Headers.IfNoneMatch.Add(tag);
                    }
                    else if (before.LastModified is { } modified)
                    {
                        request.Headers.IfModifiedSince = modified;
                    }
                }

                using HttpResponseMessage response = await _http.SendAsync(request, HttpCompletionOption.ResponseContentRead, token).ConfigureAwait(false);

                if (response.StatusCode == HttpStatusCode.NotModified && File.Exists(path))
                {
                    bodies.Add(await File.ReadAllTextAsync(path, token).ConfigureAwait(false));
                    next.Files.Add(before!);
                    continue;
                }

                if (!response.IsSuccessStatusCode)
                {
                    throw new HttpRequestException(string.Create(CultureInfo.InvariantCulture, $"{source.Publisher} answered {(int)response.StatusCode} for {url.AbsolutePath}."));
                }

                if (response.Content.Headers.ContentLength > MostBytes)
                {
                    throw new HttpRequestException("The list was larger than expected and was not kept.");
                }

                string body = await response.Content.ReadAsStringAsync(token).ConfigureAwait(false);

                if (body.Length > MostBytes)
                {
                    throw new HttpRequestException("The list was larger than expected and was not kept.");
                }

                bodies.Add(body);
                changed = true;
                next.Files.Add(new FileMeta
                {
                    Url = url.ToString(),
                    ETag = response.Headers.ETag?.ToString(),
                    LastModified = response.Content.Headers.LastModified,
                });
            }

            BlocklistFile parsed = Read(source, bodies);

            if (parsed.Entries.Count == 0)
            {
                throw new InvalidDataException("The list came back empty, so the copy already held was kept.");
            }

            if (changed)
            {
                for (int index = 0; index < bodies.Count; index++)
                {
                    await WriteAtomicallyAsync(BodyPath(source, index), bodies[index], token).ConfigureAwait(false);
                }
            }

            next.FetchedAt = now;
            next.Copyright = parsed.Copyright ?? next.Copyright;
            WriteMeta(source, next);
            Use(source, parsed, now);

            lock (_gate)
            {
                _problems.Remove(source.Id);
            }

            LogFetched(source.Name, parsed.Entries.Count, changed);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or IOException or UnauthorizedAccessException or InvalidDataException
            || BlocklistFormats.IsMalformed(ex))
        {
            token.ThrowIfCancellationRequested();

            lock (_gate)
            {
                _problems[source.Id] = ex is TaskCanceledException ? "The publisher did not answer in time." : ex.Message;
            }

            LogFetchFailed(source.Name, ex);
        }

        Changed?.Invoke(this, EventArgs.Empty);
    }

    private static BlocklistFile Read(BlocklistSource source, IReadOnlyList<string> bodies)
    {
        List<BlocklistEntry> entries = [];
        string? copyright = null;
        DateTimeOffset? published = null;

        foreach (string body in bodies)
        {
            BlocklistFile file = source.Format switch
            {
                BlocklistFormat.SpamhausJson => BlocklistFormats.ReadSpamhaus(body),
                _ => BlocklistFormats.ReadFeodo(body),
            };

            entries.AddRange(file.Entries);
            copyright ??= file.Copyright;
            published ??= file.PublishedAt;
        }

        return new BlocklistFile(entries, copyright, published);
    }

    private void Load(BlocklistSource source, Meta meta)
    {
        try
        {
            List<string> bodies = [];

            for (int index = 0; index < source.Urls.Count; index++)
            {
                string path = BodyPath(source, index);

                if (!File.Exists(path))
                {
                    return;
                }

                bodies.Add(File.ReadAllText(path, Encoding.UTF8));
            }

            BlocklistFile parsed = Read(source, bodies);

            if (parsed.Entries.Count > 0)
            {
                Use(source, parsed with { Copyright = parsed.Copyright ?? meta.Copyright }, meta.FetchedAt!.Value);
                Changed?.Invoke(this, EventArgs.Empty);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            LogFetchFailed(source.Name, ex);
        }
    }

    private void Use(BlocklistSource source, BlocklistFile file, DateTimeOffset fetchedAt)
    {
        Loaded loaded = new(source, CidrSet.From(file.Entries), fetchedAt, file.Copyright);

        lock (_gate)
        {
            _loaded[source.Id] = loaded;
        }
    }

    private bool IsLoaded(BlocklistSource source)
    {
        lock (_gate)
        {
            return _loaded.ContainsKey(source.Id);
        }
    }

    /// <summary>
    /// Forgets a list switched off, and deletes the copy: it is the publisher's
    /// data, not ours. Only when it was last asked for is kept, and only for
    /// the hour, so switching a list off and on again cannot ask the publisher
    /// more than once an hour.
    /// </summary>
    private void Drop(BlocklistSource source)
    {
        bool had;

        lock (_gate)
        {
            had = _loaded.Remove(source.Id);
            _problems.Remove(source.Id);
        }

        Meta? meta = ReadMeta(source);
        DateTimeOffset now = _clock.GetUtcNow();

        try
        {
            if (Directory.Exists(_folder))
            {
                foreach (string file in Directory.EnumerateFiles(_folder, source.Id + ".*"))
                {
                    File.Delete(file);
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }

        if (meta?.AttemptedAt is { } asked && now >= asked && now - asked < Floor)
        {
            WriteMeta(source, new Meta { AttemptedAt = asked });
        }

        if (had)
        {
            Changed?.Invoke(this, EventArgs.Empty);
        }
    }

    private string BodyPath(BlocklistSource source, int index) =>
        Path.Combine(_folder, string.Create(CultureInfo.InvariantCulture, $"{source.Id}.{index}.txt"));

    private string MetaPath(BlocklistSource source) => Path.Combine(_folder, source.Id + ".meta.json");

    private Meta? ReadMeta(BlocklistSource source)
    {
        try
        {
            string path = MetaPath(source);
            return File.Exists(path) ? JsonSerializer.Deserialize<Meta>(File.ReadAllText(path, Encoding.UTF8), MetaFormat) : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            return null;
        }
    }

    private void WriteMeta(BlocklistSource source, Meta meta)
    {
        try
        {
            Directory.CreateDirectory(_folder);
            string path = MetaPath(source);
            File.WriteAllText(path + ".tmp", JsonSerializer.Serialize(meta, MetaFormat), new UTF8Encoding(false));
            File.Move(path + ".tmp", path, overwrite: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            LogFetchFailed(source.Name, ex);
        }
    }

    private static async Task WriteAtomicallyAsync(string path, string text, CancellationToken token)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        await File.WriteAllTextAsync(path + ".tmp", text, new UTF8Encoding(false), token).ConfigureAwait(false);
        File.Move(path + ".tmp", path, overwrite: true);
    }

    public override void Dispose()
    {
        _http.Dispose();
        _wake.Dispose();
        base.Dispose();
    }

    private sealed record Loaded(BlocklistSource Source, CidrSet Set, DateTimeOffset FetchedAt, string? Copyright);

    /// <summary>What is kept beside a list: when it was fetched and asked for, and how to ask "has it changed".</summary>
    internal sealed class Meta
    {
        public DateTimeOffset? FetchedAt { get; set; }

        public DateTimeOffset? AttemptedAt { get; set; }

        public string? Copyright { get; set; }

        public List<FileMeta> Files { get; set; } = [];
    }

    internal sealed class FileMeta
    {
        public string Url { get; set; } = string.Empty;

        public string? ETag { get; set; }

        public DateTimeOffset? LastModified { get; set; }
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Blocklist {Name} holds {Count} networks (fetched anew: {Changed})")]
    private partial void LogFetched(string name, int count, bool changed);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Could not refresh blocklist {Name}")]
    private partial void LogFetchFailed(string name, Exception exception);
}
