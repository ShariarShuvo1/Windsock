using System.Buffers;
using System.Diagnostics;
using System.Net;
using System.Net.WebSockets;
using System.Security.Cryptography;
using System.Text.Json;

namespace Windsock.Core.SpeedTests;

/// <summary>One NDT7 server Locate offered.</summary>
/// <param name="Machine">The server's name.</param>
/// <param name="City">Its city, when given.</param>
/// <param name="Country">Its country, when given.</param>
/// <param name="Download">The wss download address, token included. Never logged.</param>
/// <param name="Upload">The wss upload address, token included. Never logged.</param>
public sealed record Ndt7Server(string Machine, string? City, string? Country, Uri Download, Uri Upload)
{
    /// <summary>"mlab1-dac01 (Dhaka, BD)".</summary>
    public string Describe() => City is { Length: > 0 }
        ? string.Concat(Machine, " (", City, Country is { Length: > 0 } ? string.Concat(", ", Country) : string.Empty, ")")
        : Machine;
}

/// <summary>One measurement message from either end of an NDT7 test.</summary>
/// <param name="ElapsedMicroseconds">AppInfo.ElapsedTime: microseconds since the test started.</param>
/// <param name="Bytes">AppInfo.NumBytes: application bytes so far.</param>
/// <param name="RttMicroseconds">TCPInfo.RTT: the server kernel's smoothed round trip.</param>
/// <param name="MinRttMicroseconds">TCPInfo.MinRTT: the least round trip it saw.</param>
/// <param name="Origin">"server" or "client".</param>
/// <param name="Test">"download" or "upload".</param>
public readonly record struct Ndt7Measurement(
    long? ElapsedMicroseconds,
    long? Bytes,
    long? RttMicroseconds,
    long? MinRttMicroseconds,
    string? Origin,
    string? Test)
{
    /// <summary>The speed the AppInfo gives, in bits a second.</summary>
    public double? BitsPerSecond => ElapsedMicroseconds is > 0 && Bytes is { } bytes
        ? bytes * 8d / (ElapsedMicroseconds.Value / 1e6)
        : null;
}

/// <summary>Reading the NDT7 protocol's JSON (m-lab/ndt-server, spec/ndt7-protocol.md).</summary>
public static class Ndt7Protocol
{
    /// <summary>The WebSocket subprotocol.</summary>
    public const string Subprotocol = "net.measurementlab.ndt.v7";

    /// <summary>Locate v2's address for the nearest NDT7 servers.</summary>
    public static readonly Uri Locate = new("https://locate.measurementlab.net/v2/nearest/ndt/ndt7");

    /// <summary>Reads Locate's answer: up to four servers, nearest first.</summary>
    public static IReadOnlyList<Ndt7Server> ReadLocate(string json)
    {
        List<Ndt7Server> servers = [];

        try
        {
            using JsonDocument document = JsonDocument.Parse(json);

            if (!document.RootElement.TryGetProperty("results", out JsonElement results) || results.ValueKind != JsonValueKind.Array)
            {
                return servers;
            }

            foreach (JsonElement result in results.EnumerateArray())
            {
                if (!result.TryGetProperty("urls", out JsonElement urls)
                    || !Address(urls, "wss:///ndt/v7/download", out Uri? download)
                    || !Address(urls, "wss:///ndt/v7/upload", out Uri? upload))
                {
                    continue;
                }

                string machine = result.TryGetProperty("machine", out JsonElement name) ? name.GetString() ?? "M-Lab" : "M-Lab";
                string? city = null;
                string? country = null;

                if (result.TryGetProperty("location", out JsonElement location) && location.ValueKind == JsonValueKind.Object)
                {
                    city = location.TryGetProperty("city", out JsonElement c) ? c.GetString() : null;
                    country = location.TryGetProperty("country", out JsonElement k) ? k.GetString() : null;
                }

                servers.Add(new Ndt7Server(machine, city, country, download!, upload!));
            }
        }
        catch (JsonException)
        {
        }

        return servers;
    }

    /// <summary>Reads a measurement message, or nothing for one that is not JSON.</summary>
    public static Ndt7Measurement? ReadMeasurement(string json)
    {
        try
        {
            using JsonDocument document = JsonDocument.Parse(json);
            JsonElement root = document.RootElement;

            if (root.ValueKind != JsonValueKind.Object)
            {
                return null;
            }

            long? elapsed = null;
            long? bytes = null;
            long? rtt = null;
            long? minRtt = null;

            if (root.TryGetProperty("AppInfo", out JsonElement app) && app.ValueKind == JsonValueKind.Object)
            {
                elapsed = Long(app, "ElapsedTime");
                bytes = Long(app, "NumBytes");
            }

            if (root.TryGetProperty("TCPInfo", out JsonElement tcp) && tcp.ValueKind == JsonValueKind.Object)
            {
                rtt = Long(tcp, "RTT");
                minRtt = Long(tcp, "MinRTT");
            }

            return new Ndt7Measurement(
                elapsed,
                bytes,
                rtt,
                minRtt,
                root.TryGetProperty("Origin", out JsonElement origin) ? origin.GetString() : null,
                root.TryGetProperty("Test", out JsonElement test) ? test.GetString() : null);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static bool Address(JsonElement urls, string key, out Uri? address)
    {
        address = null;

        return urls.ValueKind == JsonValueKind.Object
            && urls.TryGetProperty(key, out JsonElement value)
            && Uri.TryCreate(value.GetString(), UriKind.Absolute, out address)
            && address.Scheme == "wss";
    }

    private static long? Long(JsonElement parent, string name) =>
        parent.TryGetProperty(name, out JsonElement value) && value.ValueKind == JsonValueKind.Number && value.TryGetInt64(out long number)
            ? number
            : null;
}

/// <summary>
/// Gathers a WebSocket's text messages a frame at a time, dropping any message
/// longer than <see cref="Largest"/>: a server's measurement is a few hundred
/// bytes, and one that never ends must not grow without bound.
/// </summary>
internal sealed class TextFrames
{
    /// <summary>The longest message kept.</summary>
    public const int Largest = 64 * 1024;

    private readonly byte[] _text = new byte[Largest];
    private int _length;
    private bool _tooLong;

    /// <summary>Takes one frame; returns the message when the frame ended one that fitted, otherwise nothing.</summary>
    public string? Add(ReadOnlySpan<byte> frame, bool endOfMessage)
    {
        if (!_tooLong)
        {
            if (_length + frame.Length > Largest)
            {
                _tooLong = true;
                _length = 0;
            }
            else
            {
                frame.CopyTo(_text.AsSpan(_length));
                _length += frame.Length;
            }
        }

        if (!endOfMessage)
        {
            return null;
        }

        string? message = _tooLong ? null : System.Text.Encoding.UTF8.GetString(_text, 0, _length);
        _length = 0;
        _tooLong = false;
        return message;
    }
}

/// <summary>What one direction of a transfer measured.</summary>
/// <param name="BitsPerSecond">The speed, or nothing when the transfer failed.</param>
/// <param name="ServerRttMs">The server's round-trip readings during the transfer, in milliseconds.</param>
/// <param name="ServerMinRttMs">The least round trip the server saw.</param>
/// <param name="Problem">What went wrong.</param>
public sealed record TransferResult(double? BitsPerSecond, IReadOnlyList<double> ServerRttMs, double? ServerMinRttMs, string? Problem);

/// <summary>Runs NDT7 tests against Measurement Lab.</summary>
/// <remarks>
/// Each direction is one WebSocket of at most ten seconds (thirteen at the
/// outside), as the spec sets. Download counts what arrives; upload sends
/// random data in messages that start at 8 KiB and double while smaller than a
/// sixteenth of what has been sent, and takes the server's own byte count as
/// the result, since the client's includes what still sits in its buffers.
/// The token-bearing addresses Locate returns live sixty seconds and are never
/// written to the log.
/// </remarks>
public sealed class Ndt7Client(HttpClient http)
{
    private static readonly TimeSpan Handshake = TimeSpan.FromSeconds(8);
    private static readonly TimeSpan Longest = TimeSpan.FromSeconds(13);
    private static readonly TimeSpan Planned = TimeSpan.FromSeconds(10);
    private const int LargestMessage = 1 << 20;

    /// <summary>Asks Locate for the nearest servers. Throws <see cref="SpeedTestRefusedException"/> when M-Lab has no room or this address has used its share.</summary>
    public async Task<IReadOnlyList<Ndt7Server>> LocateAsync(CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(http);

        using HttpResponseMessage response = await http.GetAsync(Ndt7Protocol.Locate, cancellationToken).ConfigureAwait(false);

        if (response.StatusCode is HttpStatusCode.NoContent or HttpStatusCode.TooManyRequests)
        {
            throw new SpeedTestRefusedException("M-Lab is busy or this address has run its share of tests for now. Try again later.");
        }

        if (!response.IsSuccessStatusCode)
        {
            throw new SpeedTestRefusedException("M-Lab could not find a server for this connection.");
        }

        string json = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        return Ndt7Protocol.ReadLocate(json);
    }

    /// <summary>Measures download from a server.</summary>
    /// <param name="server">The server.</param>
    /// <param name="progress">Told the speed so far, in bits a second.</param>
    /// <param name="cancellationToken">Stops the test.</param>
    public static async Task<TransferResult> DownloadAsync(Ndt7Server server, Action<double>? progress, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(server);

        List<double> rtts = [];
        double? minRtt = null;

        try
        {
            using ClientWebSocket socket = await ConnectAsync(server.Download, cancellationToken).ConfigureAwait(false);
            using CancellationTokenSource cap = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            cap.CancelAfter(Longest);

            byte[] buffer = ArrayPool<byte>.Shared.Rent(1 << 16);
            TextFrames text = new();
            long bytes = 0;
            long started = Stopwatch.GetTimestamp();
            long told = started;

            try
            {
                while (socket.State == WebSocketState.Open)
                {
                    WebSocketReceiveResult received;

                    try
                    {
                        received = await socket.ReceiveAsync(buffer, cap.Token).ConfigureAwait(false);
                    }
                    catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
                    {
                        break;
                    }
                    catch (WebSocketException)
                    {
                        // An abrupt close is how many tests end; it is not a failure.
                        break;
                    }

                    if (received.MessageType == WebSocketMessageType.Close)
                    {
                        break;
                    }

                    if (received.MessageType == WebSocketMessageType.Binary)
                    {
                        bytes += received.Count;
                    }
                    else if (text.Add(buffer.AsSpan(0, received.Count), received.EndOfMessage) is { } json)
                    {
                        Note(json, rtts, ref minRtt);
                    }

                    if (Stopwatch.GetElapsedTime(told) >= TimeSpan.FromMilliseconds(250))
                    {
                        told = Stopwatch.GetTimestamp();
                        progress?.Invoke(bytes * 8d / Math.Max(0.001, Stopwatch.GetElapsedTime(started).TotalSeconds));
                    }
                }
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(buffer);
            }

            double seconds = Stopwatch.GetElapsedTime(started).TotalSeconds;
            await CloseQuietlyAsync(socket).ConfigureAwait(false);

            return bytes > 0 && seconds > 0
                ? new TransferResult(bytes * 8d / seconds, rtts, minRtt, null)
                : new TransferResult(null, rtts, minRtt, "The server sent nothing.");
        }
        catch (Exception ex) when (ex is WebSocketException or HttpRequestException or IOException
            || (ex is OperationCanceledException && !cancellationToken.IsCancellationRequested))
        {
            // A handshake that timed out lands here too: a failed test, not a cancelled one.
            return new TransferResult(null, rtts, minRtt, "The download could not start.");
        }
    }

    /// <summary>Measures upload to a server.</summary>
    public static async Task<TransferResult> UploadAsync(Ndt7Server server, Action<double>? progress, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(server);

        List<double> rtts = [];
        double? minRtt = null;

        try
        {
            using ClientWebSocket socket = await ConnectAsync(server.Upload, cancellationToken).ConfigureAwait(false);
            using CancellationTokenSource cap = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            cap.CancelAfter(Longest);

            Ndt7Measurement? last = null;
            Lock gate = new();

            Task listening = Task.Run(
                async () =>
                {
                    byte[] buffer = new byte[1 << 14];
                    TextFrames text = new();

                    try
                    {
                        while (socket.State is WebSocketState.Open or WebSocketState.CloseSent)
                        {
                            WebSocketReceiveResult received = await socket.ReceiveAsync(buffer, cap.Token).ConfigureAwait(false);

                            if (received.MessageType == WebSocketMessageType.Close)
                            {
                                return;
                            }

                            if (received.MessageType != WebSocketMessageType.Text)
                            {
                                continue;
                            }

                            if (text.Add(buffer.AsSpan(0, received.Count), received.EndOfMessage) is not { } json)
                            {
                                continue;
                            }

                            if (Ndt7Protocol.ReadMeasurement(json) is { } measured)
                            {
                                lock (gate)
                                {
                                    last = measured;
                                    Note(measured, rtts, ref minRtt);
                                }
                            }
                        }
                    }
                    catch (Exception ex) when (ex is OperationCanceledException or WebSocketException or ObjectDisposedException)
                    {
                        // Closed, cancelled, or the socket let go of after the result was taken.
                    }
                },
                CancellationToken.None);

            byte[] message = RandomNumberGenerator.GetBytes(1 << 13);
            long sent = 0;
            long started = Stopwatch.GetTimestamp();
            long told = started;

            try
            {
                while (socket.State == WebSocketState.Open && Stopwatch.GetElapsedTime(started) < Planned)
                {
                    await socket.SendAsync(message, WebSocketMessageType.Binary, true, cap.Token).ConfigureAwait(false);
                    sent += message.Length;

                    if (message.Length < LargestMessage && message.Length < sent / 16)
                    {
                        message = RandomNumberGenerator.GetBytes(message.Length * 2);
                    }

                    if (Stopwatch.GetElapsedTime(told) >= TimeSpan.FromMilliseconds(250))
                    {
                        told = Stopwatch.GetTimestamp();
                        progress?.Invoke(sent * 8d / Math.Max(0.001, Stopwatch.GetElapsedTime(started).TotalSeconds));
                    }
                }
            }
            catch (Exception ex) when (ex is OperationCanceledException or WebSocketException && !cancellationToken.IsCancellationRequested)
            {
            }

            double seconds = Stopwatch.GetElapsedTime(started).TotalSeconds;
            await CloseQuietlyAsync(socket).ConfigureAwait(false);
            await Task.WhenAny(listening, Task.Delay(TimeSpan.FromSeconds(2), CancellationToken.None)).ConfigureAwait(false);

            double? speed;
            double[] readings;
            double? least;

            // The listener may still be adding when the wait gives up, so the
            // result takes a copy under its lock and the listener is stopped.
            lock (gate)
            {
                speed = last?.BitsPerSecond ?? (sent > 0 && seconds > 0 ? sent * 8d / seconds : null);
                readings = [.. rtts];
                least = minRtt;
            }

            await cap.CancelAsync().ConfigureAwait(false);

            return speed is null
                ? new TransferResult(null, readings, least, "Nothing could be sent.")
                : new TransferResult(speed, readings, least, null);
        }
        catch (Exception ex) when (ex is WebSocketException or HttpRequestException or IOException
            || (ex is OperationCanceledException && !cancellationToken.IsCancellationRequested))
        {
            return new TransferResult(null, [], null, "The upload could not start.");
        }
    }

    private static void Note(string json, List<double> rtts, ref double? minRtt)
    {
        if (Ndt7Protocol.ReadMeasurement(json) is { } measured)
        {
            Note(measured, rtts, ref minRtt);
        }
    }

    private static void Note(Ndt7Measurement measured, List<double> rtts, ref double? minRtt)
    {
        if (measured.RttMicroseconds is > 0 and { } rtt)
        {
            rtts.Add(rtt / 1000d);
        }

        if (measured.MinRttMicroseconds is > 0 and { } least)
        {
            minRtt = minRtt is { } had ? Math.Min(had, least / 1000d) : least / 1000d;
        }
    }

    private static async Task<ClientWebSocket> ConnectAsync(Uri address, CancellationToken cancellationToken)
    {
        ClientWebSocket socket = new();

        try
        {
            socket.Options.AddSubProtocol(Ndt7Protocol.Subprotocol);
            socket.Options.SetRequestHeader("User-Agent", Lookups.WindsockAgent.UserAgent);
            socket.Options.DangerousDeflateOptions = null;

            using CancellationTokenSource handshake = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            handshake.CancelAfter(Handshake);
            await socket.ConnectAsync(address, handshake.Token).ConfigureAwait(false);
            return socket;
        }
        catch
        {
            socket.Dispose();
            throw;
        }
    }

    private static async Task CloseQuietlyAsync(ClientWebSocket socket)
    {
        try
        {
            if (socket.State == WebSocketState.Open)
            {
                using CancellationTokenSource patience = new(TimeSpan.FromSeconds(2));
                await socket.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, "done", patience.Token).ConfigureAwait(false);
            }
        }
        catch (Exception ex) when (ex is WebSocketException or OperationCanceledException or IOException)
        {
        }
    }
}

/// <summary>A test server would not run a test: no capacity, or this address's share is used up.</summary>
public sealed class SpeedTestRefusedException : Exception
{
    public SpeedTestRefusedException()
    {
    }

    public SpeedTestRefusedException(string message)
        : base(message)
    {
    }

    public SpeedTestRefusedException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
