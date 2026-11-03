using System.Globalization;
using System.Text.Json;

namespace Windsock.Core.Feed;

/// <summary>
/// Where the local feed listens and the token it takes, kept in
/// <c>feed.json</c> in the data folder so the command line and the AI tools
/// can find it.
/// </summary>
/// <remarks>
/// The token lives here rather than in settings.json so that exporting
/// settings never carries it to another PC. The folder is the user's own local
/// app data, which other Windows accounts cannot read; anything already running
/// as the same user could read the history file itself, so the token adds no
/// secret such a program lacks, and keeps everyone else out.
/// </remarks>
/// <param name="Port">The port it listens on.</param>
/// <param name="Token">The token every request carries.</param>
/// <param name="Running">Whether the feed was running when this was written.</param>
/// <param name="ProcessId">The Windsock it runs in, while it runs.</param>
public sealed record FeedEndpoint(int Port, string Token, bool Running, int? ProcessId)
{
    /// <summary>The file's name in the data folder.</summary>
    public const string FileName = "feed.json";

    private const string Format = "windsock-feed";

    /// <summary>The usual file, in the data folder.</summary>
    public static string DefaultPath => Path.Combine(WindsockPaths.Root, FileName);

    /// <summary>The feed's address.</summary>
    public Uri Address => new(string.Create(CultureInfo.InvariantCulture, $"http://127.0.0.1:{Port}/"));

    /// <summary>The file as it stands, or nothing when there is none or it cannot be read. Never throws.</summary>
    public static FeedEndpoint? Read(string path)
    {
        try
        {
            if (!File.Exists(path))
            {
                return null;
            }

            using JsonDocument document = JsonDocument.Parse(File.ReadAllText(path));
            JsonElement root = document.RootElement;

            if (root.ValueKind != JsonValueKind.Object
                || !root.TryGetProperty("format", out JsonElement format)
                || format.GetString() != Format
                || !root.TryGetProperty("port", out JsonElement port)
                || !port.TryGetInt32(out int number)
                || !root.TryGetProperty("token", out JsonElement token)
                || token.GetString() is not { Length: >= 16 } text)
            {
                return null;
            }

            bool running = root.TryGetProperty("running", out JsonElement on) && on.ValueKind == JsonValueKind.True;
            int? process = root.TryGetProperty("processId", out JsonElement id) && id.ValueKind == JsonValueKind.Number && id.TryGetInt32(out int pid)
                ? pid
                : null;

            return new FeedEndpoint(number, text, running, process);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or InvalidOperationException)
        {
            return null;
        }
    }

    /// <summary>
    /// The endpoint for a port, keeping the token already in the file and making
    /// one when there is none.
    /// </summary>
    public static FeedEndpoint Prepare(string path, int port) =>
        new(port, Read(path)?.Token ?? FeedGuard.NewToken(), Running: false, ProcessId: null);

    /// <summary>Writes the file, all at once, so a reader never sees half of it.</summary>
    public void Write(string path)
    {
        string? directory = Path.GetDirectoryName(path);

        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        string temporary = path + ".tmp";

        using (FileStream stream = File.Create(temporary))
        using (Utf8JsonWriter json = new(stream, new JsonWriterOptions { Indented = true }))
        {
            json.WriteStartObject();
            json.WriteString("format", Format);
            json.WriteNumber("version", 1);
            json.WriteNumber("port", Port);
            json.WriteString("token", Token);
            json.WriteBoolean("running", Running);

            if (ProcessId is { } id)
            {
                json.WriteNumber("processId", id);
            }
            else
            {
                json.WriteNull("processId");
            }

            json.WriteEndObject();
        }

        File.Move(temporary, path, overwrite: true);
    }

    /// <summary>Makes a new token, so every client given the old one is shut out.</summary>
    public FeedEndpoint WithNewToken() => this with { Token = FeedGuard.NewToken() };

    /// <summary>The endpoint without its token, which has no business in a log.</summary>
    public override string ToString() =>
        string.Create(CultureInfo.InvariantCulture, $"FeedEndpoint {{ Port = {Port}, Running = {Running} }}");
}
