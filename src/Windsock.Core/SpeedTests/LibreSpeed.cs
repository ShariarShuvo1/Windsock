using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;

namespace Windsock.Core.SpeedTests;

/// <summary>Runs tests against a LibreSpeed server the person runs.</summary>
/// <remarks>
/// <para>
/// LibreSpeed's backend (LGPL-3.0; nothing of it is copied here, only its HTTP
/// endpoints are called) serves <c>garbage.php?ckSize=N</c>, N megabytes of
/// random data, and <c>empty.php</c>, an empty answer used to time requests and
/// to take uploads. Its own client downloads on six streams and uploads on
/// three for up to fifteen seconds; this one does six and three for ten,
/// matching the NDT7 test's length. Speeds are what arrived, without the 6%
/// LibreSpeed's page adds to guess at wire overhead, so they compare with NDT7.
/// </para>
/// <para>
/// Telemetry and the ISP lookup LibreSpeed offers (telemetry.php,
/// getIP.php?isp=true) are never called. The server is found by trying the
/// address given, then its <c>backend/</c> folder, which is where the stock
/// install keeps the files.
/// </para>
/// </remarks>
public sealed class LibreSpeedClient(HttpClient http, Uri root)
{
    private const int DownloadStreams = 6;
    private const int UploadStreams = 3;
    private static readonly TimeSpan Planned = TimeSpan.FromSeconds(10);

    private Uri? _folder;

    /// <summary>The server's address, as the person typed it.</summary>
    public Uri Root => root;

    /// <summary>Reads a typed address: HTTP or HTTPS, made to end in a slash.</summary>
    public static Uri? ParseRoot(string? text)
    {
        if (string.IsNullOrWhiteSpace(text) || !Uri.TryCreate(text.Trim(), UriKind.Absolute, out Uri? address)
            || address.Scheme is not ("http" or "https"))
        {
            return null;
        }

        string path = address.AbsolutePath;

        if (path.EndsWith(".php", StringComparison.OrdinalIgnoreCase) || path.EndsWith(".html", StringComparison.OrdinalIgnoreCase))
        {
            path = path[..(path.LastIndexOf('/') + 1)];
        }

        UriBuilder built = new(address) { Path = path.EndsWith('/') ? path : path + "/", Query = string.Empty, Fragment = string.Empty };
        return built.Uri;
    }

    /// <summary>Finds the folder holding empty.php, and times one request. Nothing when neither place answers.</summary>
    public async Task<Uri?> FindAsync(CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(http);

        foreach (Uri folder in new[] { root, new Uri(root, "backend/") })
        {
            if (await TimeAsync(folder, cancellationToken).ConfigureAwait(false) is not null)
            {
                _folder = folder;
                return folder;
            }
        }

        return null;
    }

    /// <summary>Times one empty request, in milliseconds, or nothing when it failed.</summary>
    public async Task<double?> PingAsync(CancellationToken cancellationToken) =>
        _folder is { } folder ? await TimeAsync(folder, cancellationToken).ConfigureAwait(false) : null;

    /// <summary>Measures download.</summary>
    public async Task<TransferResult> DownloadAsync(Action<double>? progress, CancellationToken cancellationToken)
    {
        if (_folder is not { } folder)
        {
            return new TransferResult(null, [], null, "The server was not found.");
        }

        long bytes = 0;
        long started = Stopwatch.GetTimestamp();
        using CancellationTokenSource stop = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        stop.CancelAfter(Planned);

        async Task Stream()
        {
            byte[] buffer = new byte[1 << 16];

            while (!stop.IsCancellationRequested)
            {
                Uri address = new(folder, string.Create(CultureInfo.InvariantCulture, $"garbage.php?ckSize=100&r={RandomNumberGenerator.GetInt32(int.MaxValue)}"));
                using HttpResponseMessage response = await http.GetAsync(address, HttpCompletionOption.ResponseHeadersRead, stop.Token).ConfigureAwait(false);
                response.EnsureSuccessStatusCode();
                using Stream body = await response.Content.ReadAsStreamAsync(stop.Token).ConfigureAwait(false);

                int read;

                while ((read = await body.ReadAsync(buffer, stop.Token).ConfigureAwait(false)) > 0)
                {
                    Interlocked.Add(ref bytes, read);
                }
            }
        }

        return await RunAsync(Stream, DownloadStreams, () => Interlocked.Read(ref bytes), started, progress, stop, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Measures upload.</summary>
    public async Task<TransferResult> UploadAsync(Action<double>? progress, CancellationToken cancellationToken)
    {
        if (_folder is not { } folder)
        {
            return new TransferResult(null, [], null, "The server was not found.");
        }

        long bytes = 0;
        long started = Stopwatch.GetTimestamp();
        byte[] blob = RandomNumberGenerator.GetBytes(4 << 20);
        using CancellationTokenSource stop = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        stop.CancelAfter(Planned);

        async Task Stream()
        {
            while (!stop.IsCancellationRequested)
            {
                using CountingContent content = new(blob, read => Interlocked.Add(ref bytes, read));
                Uri address = new(folder, string.Create(CultureInfo.InvariantCulture, $"empty.php?r={RandomNumberGenerator.GetInt32(int.MaxValue)}"));
                using HttpResponseMessage response = await http.PostAsync(address, content, stop.Token).ConfigureAwait(false);
            }
        }

        return await RunAsync(Stream, UploadStreams, () => Interlocked.Read(ref bytes), started, progress, stop, cancellationToken).ConfigureAwait(false);
    }

    private static async Task<TransferResult> RunAsync(
        Func<Task> stream,
        int streams,
        Func<long> counted,
        long started,
        Action<double>? progress,
        CancellationTokenSource stop,
        CancellationToken cancellationToken)
    {
        Task[] running = [.. Enumerable.Range(0, streams).Select(_ => Task.Run(stream, CancellationToken.None))];
        Task all = Task.WhenAll(running);

        while (!all.IsCompleted)
        {
            await Task.WhenAny(all, Task.Delay(250, CancellationToken.None)).ConfigureAwait(false);
            progress?.Invoke(counted() * 8d / Math.Max(0.001, Stopwatch.GetElapsedTime(started).TotalSeconds));
        }

        cancellationToken.ThrowIfCancellationRequested();

        double seconds = Stopwatch.GetElapsedTime(started).TotalSeconds;
        long bytes = counted();
        bool failed = running.All(each => each.IsFaulted) || (bytes == 0 && !stop.IsCancellationRequested);

        foreach (Task each in running)
        {
            // Observed so an unobserved failure cannot surface later.
            _ = each.Exception;
        }

        return bytes > 0 && !failed
            ? new TransferResult(bytes * 8d / seconds, [], null, null)
            : new TransferResult(null, [], null, "The server did not take part.");
    }

    private async Task<double?> TimeAsync(Uri folder, CancellationToken cancellationToken)
    {
        Uri address = new(folder, string.Create(CultureInfo.InvariantCulture, $"empty.php?r={RandomNumberGenerator.GetInt32(int.MaxValue)}"));
        long started = Stopwatch.GetTimestamp();

        try
        {
            using HttpResponseMessage response = await http.GetAsync(address, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
            return response.StatusCode == HttpStatusCode.OK ? Stopwatch.GetElapsedTime(started).TotalMilliseconds : null;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException && !cancellationToken.IsCancellationRequested)
        {
            return null;
        }
    }

    /// <summary>Request content that reports how much of it has been written to the connection.</summary>
    private sealed class CountingContent(byte[] blob, Action<int> wrote) : HttpContent
    {
        protected override async Task SerializeToStreamAsync(Stream stream, TransportContext? context) =>
            await SerializeToStreamAsync(stream, context, CancellationToken.None).ConfigureAwait(false);

        protected override async Task SerializeToStreamAsync(Stream stream, TransportContext? context, CancellationToken cancellationToken)
        {
            const int Piece = 64 * 1024;

            for (int at = 0; at < blob.Length; at += Piece)
            {
                int length = Math.Min(Piece, blob.Length - at);
                await stream.WriteAsync(blob.AsMemory(at, length), cancellationToken).ConfigureAwait(false);
                wrote(length);
            }
        }

        protected override bool TryComputeLength(out long length)
        {
            length = blob.Length;
            Headers.ContentType ??= new MediaTypeHeaderValue("application/octet-stream");
            return true;
        }
    }
}
