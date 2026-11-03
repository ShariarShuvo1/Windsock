using System.Globalization;
using System.Text.Json;

namespace Windsock.Core.Security;

/// <summary>What one fetched blocklist file held.</summary>
/// <param name="Entries">The networks listed.</param>
/// <param name="Copyright">The publisher's copyright line, kept to show beside the list as their terms ask.</param>
/// <param name="PublishedAt">When the publisher built the file, when it says.</param>
public sealed record BlocklistFile(IReadOnlyList<BlocklistEntry> Entries, string? Copyright, DateTimeOffset? PublishedAt);

/// <summary>Reads the blocklist formats Windsock can fetch.</summary>
/// <remarks>Formats checked against the publishers' live files on 2026-10-05.</remarks>
public static class BlocklistFormats
{
    /// <summary>
    /// Spamhaus DROP's JSON: one object a line, <c>{"cidr":"1.10.16.0/20","sblid":"SBL256894","rir":"apnic"}</c>,
    /// ending in a metadata line with the build time and the copyright.
    /// </summary>
    public static BlocklistFile ReadSpamhaus(string text)
    {
        ArgumentNullException.ThrowIfNull(text);

        List<BlocklistEntry> entries = [];
        string? copyright = null;
        DateTimeOffset? published = null;

        foreach (string raw in text.Split('\n'))
        {
            string line = raw.Trim();

            if (line.Length == 0 || line[0] != '{')
            {
                continue;
            }

            try
            {
                using JsonDocument document = JsonDocument.Parse(line);
                JsonElement root = document.RootElement;

                if (root.TryGetProperty("cidr", out JsonElement cidr) && cidr.ValueKind == JsonValueKind.String)
                {
                    string? sbl = root.TryGetProperty("sblid", out JsonElement id) && id.ValueKind == JsonValueKind.String
                        ? id.GetString()
                        : null;

                    entries.Add(new BlocklistEntry(cidr.GetString()!, sbl));
                }
                else if (root.TryGetProperty("type", out JsonElement type)
                    && type.ValueKind == JsonValueKind.String
                    && string.Equals(type.GetString(), "metadata", StringComparison.Ordinal))
                {
                    if (root.TryGetProperty("copyright", out JsonElement notice) && notice.ValueKind == JsonValueKind.String)
                    {
                        copyright = notice.GetString();
                    }

                    if (root.TryGetProperty("timestamp", out JsonElement stamp)
                        && stamp.ValueKind == JsonValueKind.Number
                        && stamp.TryGetInt64(out long seconds)
                        && seconds is > 0 and < MaxUnixSeconds)
                    {
                        published = DateTimeOffset.FromUnixTimeSeconds(seconds);
                    }
                }
            }
            catch (Exception ex) when (IsMalformed(ex))
            {
                // One broken line costs that line, not the list.
            }
        }

        return new BlocklistFile(entries, copyright, published);
    }

    /// <summary>
    /// Feodo Tracker's JSON: an array of
    /// <c>{"ip_address":"50.16.16.211","port":443,"status":"online","malware":"QakBot",...}</c>.
    /// </summary>
    public static BlocklistFile ReadFeodo(string text)
    {
        ArgumentNullException.ThrowIfNull(text);

        List<BlocklistEntry> entries = [];

        try
        {
            using JsonDocument document = JsonDocument.Parse(text);

            if (document.RootElement.ValueKind != JsonValueKind.Array)
            {
                return new BlocklistFile(entries, null, null);
            }

            foreach (JsonElement item in document.RootElement.EnumerateArray())
            {
                if (item.ValueKind != JsonValueKind.Object
                    || !item.TryGetProperty("ip_address", out JsonElement address)
                    || address.ValueKind != JsonValueKind.String)
                {
                    continue;
                }

                string? malware = item.TryGetProperty("malware", out JsonElement kind) && kind.ValueKind == JsonValueKind.String
                    ? kind.GetString()
                    : null;

                string? port = item.TryGetProperty("port", out JsonElement number)
                    && number.ValueKind == JsonValueKind.Number
                    && number.TryGetInt32(out int value)
                    ? value.ToString(CultureInfo.InvariantCulture)
                    : null;

                string? reference = (malware, port) switch
                {
                    ({ } m, { } p) => string.Concat(m, " botnet control server, port ", p),
                    ({ } m, null) => string.Concat(m, " botnet control server"),
                    _ => "Botnet control server",
                };

                entries.Add(new BlocklistEntry(address.GetString()!, reference));
            }
        }
        catch (Exception ex) when (IsMalformed(ex))
        {
        }

        return new BlocklistFile(entries, null, null);
    }

    /// <summary>Unix seconds for the year 9999, the last a date can hold.</summary>
    private const long MaxUnixSeconds = 253_402_300_799;

    /// <summary>
    /// Whether an exception means the file was not shaped as expected. A
    /// value of the wrong kind throws <see cref="InvalidOperationException"/>,
    /// not <see cref="JsonException"/>; either costs the line or the file,
    /// never the app.
    /// </summary>
    internal static bool IsMalformed(Exception ex) =>
        ex is JsonException or InvalidOperationException or FormatException or ArgumentException;
}
