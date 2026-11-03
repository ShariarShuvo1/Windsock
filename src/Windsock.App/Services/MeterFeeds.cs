using System.IO;
using System.Net.Http;
using System.Text;
using System.Windows.Threading;
using Microsoft.Extensions.Logging;
using Windsock.Core.Formatting;
using Windsock.Core.Meter;
using Windsock.Core.Settings;

namespace Windsock.App.Services;

/// <summary>
/// Reads the JSON documents the meter's JSON places point at, only while the
/// meter's feeds are switched on and a place shows one.
/// </summary>
/// <remarks>
/// <para>
/// Each address is asked once however many places read it, at the shortest
/// interval any of them wants and never more often than every fifteen
/// seconds. A web address gets five seconds to answer, no redirects (a
/// redirect could lead from the address shown to one that is not), and 64 KB
/// at most; a file on this PC's own drives gets the same five seconds and the
/// same size, opened and read off the interface thread. An address on this PC
/// or the local network is asked directly, never through the system's proxy,
/// which has no business seeing them. A failure leaves a dash and a sentence
/// saying why, never an exception.
/// </para>
/// <para>
/// This is the one part of the meter that can reach beyond this PC, which is
/// why it has its own switch, off by default, and is listed on the privacy
/// page. A layout file can carry an address but never switches feeds on.
/// </para>
/// </remarks>
internal sealed partial class MeterFeeds : IDisposable
{
    private const int Largest = 64 * 1024;
    private static readonly TimeSpan Patience = TimeSpan.FromSeconds(5);

    private readonly Dispatcher _dispatcher;
    private readonly ILogger _logger;
    private readonly HttpClient _web;
    private readonly HttpClient _local;
    private readonly DispatcherTimer _clock;
    private readonly Dictionary<string, Source> _sources = new(StringComparer.Ordinal);
    private Dictionary<string, FieldReading> _readings = new(StringComparer.Ordinal);
    private List<MeterFeed> _feeds = [];
    private bool _disposed;

    public MeterFeeds(Dispatcher dispatcher, ILogger logger)
    {
        _dispatcher = dispatcher;
        _logger = logger;
        _web = Client(useProxy: true);
        _local = Client(useProxy: false);

        _clock = new DispatcherTimer(DispatcherPriority.Background, dispatcher) { Interval = TimeSpan.FromSeconds(5) };
        _clock.Tick += (_, _) => Due();
    }

    /// <summary>Raised on the UI thread when a value changed.</summary>
    public event EventHandler? Changed;

    /// <summary>Every value read, by <see cref="MeterReadout.FeedKey"/>.</summary>
    public IReadOnlyDictionary<string, FieldReading> Readings => _readings;

    /// <summary>
    /// Reads these feeds from now on, or none when <paramref name="allowed"/>
    /// is off.
    /// </summary>
    public void Follow(IReadOnlyList<MeterFeed> feeds, bool allowed)
    {
        ArgumentNullException.ThrowIfNull(feeds);

        if (_disposed)
        {
            return;
        }

        // The address as written, here on the interface thread; whether a
        // drive letter is a share is asked once, off it, as a file is read.
        _feeds = allowed ? [.. feeds.Where(feed => FeedAddress.Check(feed.Address, NoDriveYet).Problem is null)] : [];

        HashSet<string> wanted = new(_feeds.Select(feed => feed.Address.Trim()), StringComparer.Ordinal);

        foreach (string gone in _sources.Keys.Where(address => !wanted.Contains(address)).ToList())
        {
            _sources.Remove(gone);
        }

        Dictionary<string, TimeSpan> intervals = new(StringComparer.Ordinal);

        foreach (MeterFeed feed in _feeds)
        {
            string address = feed.Address.Trim();
            TimeSpan every = TimeSpan.FromSeconds(Math.Max(15, feed.Seconds));

            intervals[address] = intervals.TryGetValue(address, out TimeSpan known) && known < every ? known : every;
        }

        foreach ((string address, TimeSpan every) in intervals)
        {
            if (_sources.TryGetValue(address, out Source? known))
            {
                known.Every = every;
            }
            else
            {
                _sources[address] = new Source(address, every);
            }
        }

        Extract();

        if (_sources.Count == 0)
        {
            _clock.Stop();
            return;
        }

        _clock.Start();
        Due();
    }

    /// <inheritdoc />
    public void Dispose()
    {
        _disposed = true;
        _clock.Stop();
        _sources.Clear();
        _web.Dispose();
        _local.Dispose();
    }

    private static HttpClient Client(bool useProxy)
    {
        HttpClient client = new(new SocketsHttpHandler
        {
            AllowAutoRedirect = false,
            AutomaticDecompression = System.Net.DecompressionMethods.All,
            ConnectTimeout = Patience,
            UseProxy = useProxy,
        })
        {
            Timeout = Patience,
        };

        client.DefaultRequestHeaders.UserAgent.ParseAdd("Windsock");
        client.DefaultRequestHeaders.Accept.ParseAdd("application/json");
        return client;
    }

    private void Due()
    {
        DateTimeOffset now = DateTimeOffset.UtcNow;

        foreach (Source source in _sources.Values)
        {
            if (!source.Busy && now - source.AskedAt >= source.Every)
            {
                source.Busy = true;
                source.AskedAt = now;
                _ = AskAsync(source);
            }
        }
    }

    private async Task AskAsync(Source source)
    {
        string? text = null;
        string? problem = null;

        try
        {
            (FeedKind? kind, string? refused) = FeedAddress.Check(source.Address, NoDriveYet);

            text = kind switch
            {
                FeedKind.File => await ReadFileAsync(source.Address).ConfigureAwait(false),
                FeedKind.Local => await ReadWebAsync(_local, source.Address).ConfigureAwait(false),
                FeedKind.Web => await ReadWebAsync(_web, source.Address).ConfigureAwait(false),
                _ => throw new FeedRefusedException(refused ?? "That address cannot be read."),
            };
        }
        catch (FeedRefusedException ex)
        {
            problem = ex.Message;
        }
        catch (HttpRequestException ex)
        {
            problem = ex.StatusCode is { } status
                ? string.Concat("The address answered ", ((int)status).ToString(System.Globalization.CultureInfo.InvariantCulture), ".")
                : "The address did not answer.";
        }
        catch (OperationCanceledException)
        {
            problem = "It took longer than five seconds to read.";
        }
        catch (InvalidDataException)
        {
            // A body that says it is compressed and is not: not an IOException.
            problem = "The answer said it was compressed, and could not be unpacked.";
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException or NotSupportedException or UriFormatException or ArgumentException)
        {
            problem = string.Concat("It could not be read: ", ex.Message);
        }
        finally
        {
            // Whatever happened, the address is free to be asked again; a
            // failure that skipped this would leave it never asked again.
            Finish(source, text, problem);
        }
    }

    private void Finish(Source source, string? text, string? problem) =>
        _ = _dispatcher.BeginInvoke(DispatcherPriority.Background, new Action(() =>
        {
            source.Busy = false;

            if (_disposed || !_sources.ContainsKey(source.Address))
            {
                return;
            }

            source.Text = text;
            source.Problem = text is null ? problem ?? "Nothing came back." : null;

            if (source.Problem is { } said && said != source.Told)
            {
                source.Told = said;
                string where = Where(source.Address);
                LogFeedFailed(where, said);
            }

            Extract();
        }));

    /// <summary>
    /// What a log may say about an address: its scheme and host, or that it is
    /// a file. Never the rest - a path or a query may hold a key.
    /// </summary>
    private static string Where(string address) =>
        Uri.TryCreate(address, UriKind.Absolute, out Uri? uri) && !uri.IsFile
            ? string.Concat(uri.Scheme, "://", uri.Host)
            : "a file on this PC";

    /// <summary>For a check on the interface thread: no drive is asked about there.</summary>
    private static bool NoDriveYet(char letter) => false;

    /// <summary>
    /// Asks a web address, reading at most 64 KB of the answer: the size is
    /// counted as it arrives, not trusted from what the server says.
    /// </summary>
    private static async Task<string> ReadWebAsync(HttpClient client, string address)
    {
        using CancellationTokenSource patience = new(Patience);
        using HttpResponseMessage response = await client
            .GetAsync(new Uri(address), HttpCompletionOption.ResponseHeadersRead, patience.Token)
            .ConfigureAwait(false);

        response.EnsureSuccessStatusCode();

        if (response.Content.Headers.ContentLength > Largest)
        {
            throw new FeedRefusedException(TooLarge("answer"));
        }

        Stream stream = await response.Content.ReadAsStreamAsync(patience.Token).ConfigureAwait(false);

        await using (stream.ConfigureAwait(false))
        {
            return await ReadCappedAsync(stream, "answer", patience.Token).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Reads a file on this PC's own drives, off the interface thread and in
    /// five seconds at most: opening a file can wait on a slow or sleeping
    /// drive, and the interface must not wait with it.
    /// </summary>
    private static async Task<string> ReadFileAsync(string address)
    {
        string path = FeedAddress.FilePath(address, NoDriveYet)
            ?? throw new FeedRefusedException(FeedAddress.Check(address, NoDriveYet).Problem ?? "That file cannot be read.");

        using CancellationTokenSource patience = new(Patience);

        return await Task.Run(() => ReadLocalFileAsync(path, patience.Token), patience.Token)
            .WaitAsync(patience.Token)
            .ConfigureAwait(false);
    }

    private static async Task<string> ReadLocalFileAsync(string path, CancellationToken cancellationToken)
    {
        // Asked here, once, off the interface thread: a mapped network drive
        // is a share under a letter, and is not read.
        if (FeedAddress.IsNetworkDrive(path[0]))
        {
            throw new FeedRefusedException("Only a file on this PC's own drives can be read: that drive is a network share.");
        }

        FileStream stream = new(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 4096, useAsync: true);

        await using (stream.ConfigureAwait(false))
        {
            if (stream.Length > Largest)
            {
                throw new FeedRefusedException(TooLarge("file"));
            }

            return await ReadCappedAsync(stream, "file", cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>Reads text up to the size limit, refusing the whole of anything longer.</summary>
    private static async Task<string> ReadCappedAsync(Stream stream, string what, CancellationToken cancellationToken)
    {
        byte[] buffer = new byte[Largest + 1];
        int filled = 0;

        while (filled < buffer.Length)
        {
            int read = await stream.ReadAsync(buffer.AsMemory(filled), cancellationToken).ConfigureAwait(false);

            if (read == 0)
            {
                break;
            }

            filled += read;
        }

        if (filled > Largest)
        {
            throw new FeedRefusedException(TooLarge(what));
        }

        using MemoryStream bytes = new(buffer, 0, filled, writable: false);
        using StreamReader reader = new(bytes, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
        return await reader.ReadToEndAsync(cancellationToken).ConfigureAwait(false);
    }

    private static string TooLarge(string what) =>
        string.Concat("The ", what, " is larger than 64 KB, far more than one value needs, so it was not read.");

    private void Extract()
    {
        Dictionary<string, FieldReading> readings = new(StringComparer.Ordinal);

        foreach (MeterFeed feed in _feeds)
        {
            string key = MeterReadout.FeedKey(feed);

            if (_sources.TryGetValue(feed.Address.Trim(), out Source? source))
            {
                readings[key] = source.Text is { } text
                    ? Read(text, feed.Path)
                    : FieldReading.Missing(source.Problem ?? "Not read yet.");
            }
        }

        bool same = readings.Count == _readings.Count
            && readings.All(pair => _readings.TryGetValue(pair.Key, out FieldReading known) && known == pair.Value);

        _readings = readings;

        if (!same)
        {
            Changed?.Invoke(this, EventArgs.Empty);
        }
    }

    /// <summary>
    /// One field of one answer. JsonField.Read already turns whatever a
    /// hostile document throws into a sentence; this is the last line, because
    /// it runs on the interface thread, where anything that escaped would stop
    /// the app at every start while the feed is set.
    /// </summary>
    private static FieldReading Read(string text, string path)
    {
        try
        {
            return JsonField.Read(text, path);
        }
        catch (Exception ex) when (ex is InvalidOperationException or ArgumentException or FormatException or System.Text.Json.JsonException)
        {
            return FieldReading.Missing("The answer holds something that cannot be read.");
        }
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "A meter feed could not be read from {Where}: {Problem}")]
    private partial void LogFeedFailed(string where, string problem);

    /// <summary>A refusal of ours, whose words are the reason as the page shows it.</summary>
    private sealed class FeedRefusedException(string message) : Exception(message);

    private sealed class Source(string address, TimeSpan every)
    {
        public string Address { get; } = address;

        public TimeSpan Every { get; set; } = every;

        public DateTimeOffset AskedAt { get; set; } = DateTimeOffset.MinValue;

        public bool Busy { get; set; }

        public string? Text { get; set; }

        public string? Problem { get; set; }

        public string? Told { get; set; }
    }
}
