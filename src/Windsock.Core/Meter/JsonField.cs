using System.Globalization;
using System.Net;
using System.Text.Json;

namespace Windsock.Core.Meter;

/// <summary>What reading one field of a JSON document found.</summary>
/// <param name="Value">The field's value as the meter writes it, or nothing.</param>
/// <param name="Number">The value as a number, when it is one, for colour levels.</param>
/// <param name="Problem">Why there is no value, in a sentence, when there is none.</param>
public readonly record struct FieldReading(string? Value, double? Number, string? Problem)
{
    /// <summary>A reading that found nothing, saying why.</summary>
    public static FieldReading Missing(string problem) => new(null, null, problem);
}

/// <summary>
/// Reads one value out of a JSON document by a path like
/// <c>data.items[0].price</c>.
/// </summary>
/// <remarks>
/// The path is property names joined by dots, with <c>[n]</c> for an item of
/// an array (from the end when negative) and <c>["name"]</c> for a name with a
/// dot in it; a leading <c>$</c> or <c>$.</c> is allowed and ignored. Only a
/// string, a number, true, false or null is a value - an object or an array is
/// not something a taskbar can show. A string longer than the meter has room
/// for is cut short.
/// </remarks>
public static class JsonField
{
    /// <summary>The longest value the meter writes.</summary>
    public const int Longest = 24;

    /// <summary>The deepest a document may nest.</summary>
    private const int Depth = 64;

    /// <summary>Reads the field at <paramref name="path"/> in <paramref name="json"/>.</summary>
    public static FieldReading Read(string? json, string? path)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return FieldReading.Missing("The answer was empty.");
        }

        if (Parse(path) is not { } steps)
        {
            return FieldReading.Missing("The field is not written the way a field is.");
        }

        try
        {
            using JsonDocument document = JsonDocument.Parse(json, new JsonDocumentOptions { MaxDepth = Depth });
            JsonElement here = document.RootElement;

            foreach (Step step in steps)
            {
                if (!Walk(ref here, step))
                {
                    return FieldReading.Missing(string.Concat("There is no ", step.Describe(), " there."));
                }
            }

            return Value(here);
        }
        catch (JsonException)
        {
            return FieldReading.Missing("The answer is not JSON.");
        }
        catch (Exception ex) when (ex is InvalidOperationException or ArgumentException or FormatException)
        {
            // Whatever a hostile document finds to throw - a string that is
            // not text, a value of a kind no reader expected - is an answer
            // that cannot be read, never a reason to stop.
            return FieldReading.Missing("The answer holds something that cannot be read as text.");
        }
    }

    /// <summary>Whether a path is written the way a path is.</summary>
    public static bool IsPath(string? path) => Parse(path) is not null;

    private static bool Walk(ref JsonElement here, Step step)
    {
        if (step.Name is { } name)
        {
            if (here.ValueKind != JsonValueKind.Object || !here.TryGetProperty(name, out JsonElement found))
            {
                return false;
            }

            here = found;
            return true;
        }

        if (here.ValueKind != JsonValueKind.Array)
        {
            return false;
        }

        int length = here.GetArrayLength();
        int index = step.Index < 0 ? length + step.Index : step.Index;

        if (index < 0 || index >= length)
        {
            return false;
        }

        here = here[index];
        return true;
    }

    private static FieldReading Value(JsonElement element) => element.ValueKind switch
    {
        JsonValueKind.String => TextOf(element),
        JsonValueKind.Number => Number(element),
        JsonValueKind.True => new FieldReading("true", 1, null),
        JsonValueKind.False => new FieldReading("false", 0, null),
        JsonValueKind.Null => new FieldReading("—", null, null),
        _ => FieldReading.Missing("That field holds a list or an object, not a value."),
    };

    /// <summary>
    /// A string value. A string that is not text - a lone half of a surrogate
    /// pair, escaped - makes the reader throw rather than answer, so it is
    /// asked for here and refused here.
    /// </summary>
    private static FieldReading TextOf(JsonElement element)
    {
        string? text;

        try
        {
            text = element.GetString();
        }
        catch (InvalidOperationException)
        {
            return FieldReading.Missing("That field holds text that is not text.");
        }

        return Text(text ?? string.Empty);
    }

    private static FieldReading Text(string text)
    {
        string shown = text.Length > Longest ? string.Concat(text.AsSpan(0, Longest - 1), "…") : text;
        double? number = double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out double parsed)
            && double.IsFinite(parsed)
                ? parsed
                : null;

        return new FieldReading(shown, number, null);
    }

    private static FieldReading Number(JsonElement element)
    {
        if (!element.TryGetDouble(out double value) || !double.IsFinite(value))
        {
            // Beyond what a double holds: shown as written, cut to the room
            // there is, and no number for the colour levels.
            string raw = element.GetRawText();

            return new FieldReading(raw.Length > Longest ? string.Concat(raw.AsSpan(0, Longest - 1), "…") : raw, null, null);
        }

        // Six significant figures: enough for a price or a reading, short
        // enough for a taskbar, and no floating-point tail.
        string shown = value.ToString("G6", CultureInfo.InvariantCulture);

        return new FieldReading(shown.Length > Longest ? shown[..Longest] : shown, value, null);
    }

    private static List<Step>? Parse(string? path)
    {
        if (path is null)
        {
            return null;
        }

        string text = path.Trim();

        if (text.StartsWith('$'))
        {
            text = text[1..];
        }

        List<Step> steps = [];
        int at = 0;

        while (at < text.Length)
        {
            char next = text[at];

            if (next == '.')
            {
                if (at == text.Length - 1 || text[at + 1] is '.' or '[')
                {
                    return null;
                }

                at++;
                continue;
            }

            if (next == '[')
            {
                int close = text.IndexOf(']', at);

                if (close < 0)
                {
                    return null;
                }

                string inside = text[(at + 1)..close].Trim();
                at = close + 1;

                if (inside.Length >= 2 && inside[0] is '"' or '\'' && inside[^1] == inside[0])
                {
                    steps.Add(new Step(inside[1..^1], 0));
                }
                else if (int.TryParse(inside, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out int index))
                {
                    steps.Add(new Step(null, index));
                }
                else
                {
                    return null;
                }

                continue;
            }

            int end = at;

            while (end < text.Length && text[end] is not ('.' or '['))
            {
                end++;
            }

            string name = text[at..end].Trim();

            if (name.Length == 0)
            {
                return null;
            }

            steps.Add(new Step(name, 0));
            at = end;
        }

        return steps.Count > 0 ? steps : null;
    }

    private readonly record struct Step(string? Name, int Index)
    {
        public string Describe() => Name is { } named
            ? string.Concat("field \"", named, "\"")
            : string.Create(CultureInfo.InvariantCulture, $"item {Index}");
    }
}

/// <summary>Where a place's JSON value may come from.</summary>
public enum FeedKind
{
    /// <summary>An address on the internet, over HTTPS.</summary>
    Web,

    /// <summary>A server on this PC or the local network, over plain HTTP or HTTPS.</summary>
    Local,

    /// <summary>A file on this PC.</summary>
    File,
}

/// <summary>Checks an address a person typed for a JSON feed.</summary>
/// <remarks>
/// <para>
/// HTTPS may go anywhere: the person typed it, and the page shows it. Plain
/// HTTP is allowed only to this PC and the private networks a router or a
/// home server lives on, written as an address or as localhost - anything
/// beyond those would cross the internet in the clear.
/// </para>
/// <para>
/// A file must be a file on this PC's own drives, named by its whole path
/// from a drive letter (<c>C:\data\price.json</c>, or
/// <c>file:///C:/data/price.json</c>). Everything else that Windows would
/// also open as a file is refused, because each reaches somewhere else: a
/// share (<c>\\server\share</c>, <c>file://server/share</c>, a WebDAV
/// <c>\\host@SSL\DavWWWRoot</c>) makes Windows sign in to that host with the
/// person's own credentials, and WebDAV may fall back to plain http; a device
/// or pipe path (<c>\\.\</c>, <c>\\?\</c>) or a reserved name such as
/// <c>CON</c> or <c>COM1</c> is not a file at all; a mapped network drive is a
/// share under a letter.
/// </para>
/// </remarks>
public static class FeedAddress
{
    /// <summary>Names Windows keeps for devices, which open a device whatever folder they are written in.</summary>
    private static readonly HashSet<string> DeviceNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "CON", "PRN", "AUX", "NUL", "CONIN$", "CONOUT$", "CLOCK$",
        "COM0", "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9", "COM¹", "COM²", "COM³",
        "LPT0", "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9", "LPT¹", "LPT²", "LPT³",
    };

    /// <summary>What an address is, or why it cannot be used.</summary>
    public static (FeedKind? Kind, string? Problem) Check(string? address) => Check(address, IsNetworkDrive);

    /// <summary>
    /// What an address is, or why it cannot be used, asking
    /// <paramref name="isNetworkDrive"/> whether a drive letter is a share.
    /// </summary>
    public static (FeedKind? Kind, string? Problem) Check(string? address, Func<char, bool> isNetworkDrive)
    {
        ArgumentNullException.ThrowIfNull(isNetworkDrive);

        string text = address?.Trim() ?? string.Empty;

        if (text.Length == 0)
        {
            return (null, "No address yet.");
        }

        if (text.StartsWith(@"\\", StringComparison.Ordinal) || text.StartsWith("//", StringComparison.Ordinal)
            || text.StartsWith(@"\/", StringComparison.Ordinal) || text.StartsWith(@"/\", StringComparison.Ordinal))
        {
            return (null, NotThisPc);
        }

        if (!text.Contains("://", StringComparison.Ordinal))
        {
            return Path.IsPathFullyQualified(text) ? LocalFile(text, isNetworkDrive) : (null, "That is not an address.");
        }

        if (!Uri.TryCreate(text, UriKind.Absolute, out Uri? uri))
        {
            return (null, "That is not an address.");
        }

        if (uri.IsFile)
        {
            // A file address names a host for a share; this PC's own files
            // have none (file:///C:/...).
            return uri.IsUnc || uri.Host.Length > 0 ? (null, NotThisPc) : LocalFile(uri.LocalPath, isNetworkDrive);
        }

        bool local = IsLocal(uri.Host);

        return uri.Scheme switch
        {
            "https" => (local ? FeedKind.Local : FeedKind.Web, null),
            "http" when local => (FeedKind.Local, null),
            "http" => (null, "Plain http is allowed only to this PC or the local network; use https."),
            _ => (null, "Only https, http on this PC or the local network, and files can be read."),
        };
    }

    /// <summary>
    /// The file an address names, as a path on this PC, or nothing when it
    /// names no file this PC may read.
    /// </summary>
    public static string? FilePath(string? address) => FilePath(address, IsNetworkDrive);

    /// <summary>
    /// The file an address names, as a path on this PC, or nothing, asking
    /// <paramref name="isNetworkDrive"/> whether a drive letter is a share.
    /// </summary>
    public static string? FilePath(string? address, Func<char, bool> isNetworkDrive)
    {
        if (Check(address, isNetworkDrive) is not (FeedKind.File, null))
        {
            return null;
        }

        string text = address!.Trim();

        return text.Contains("://", StringComparison.Ordinal) && Uri.TryCreate(text, UriKind.Absolute, out Uri? uri)
            ? uri.LocalPath
            : text;
    }

    /// <summary>Whether a drive letter is a share mapped to a letter, by asking Windows what the drive is.</summary>
    public static bool IsNetworkDrive(char letter)
    {
        try
        {
            return new DriveInfo(letter.ToString()).DriveType == DriveType.Network;
        }
        catch (ArgumentException)
        {
            return false;
        }
    }

    private const string NotThisPc = "Only a file on this PC's own drives can be read: not a share, a device or another computer.";

    /// <summary>A path from a drive letter, every part of it an ordinary name, on a drive that is not a share.</summary>
    private static (FeedKind? Kind, string? Problem) LocalFile(string path, Func<char, bool> isNetworkDrive)
    {
        if (path.Length < 3 || !char.IsAsciiLetter(path[0]) || path[1] != ':' || path[2] is not ('\\' or '/'))
        {
            return (null, "Name a file by its whole path from its drive, such as C:\\data\\price.json.");
        }

        if (path.AsSpan(2).Contains(':'))
        {
            return (null, NotThisPc);
        }

        foreach (string part in path[3..].Split(['\\', '/'], StringSplitOptions.RemoveEmptyEntries))
        {
            string name = part.TrimEnd(' ', '.');
            int dot = name.IndexOf('.', StringComparison.Ordinal);
            string stem = (dot < 0 ? name : name[..dot]).TrimEnd(' ');

            if (DeviceNames.Contains(stem))
            {
                return (null, NotThisPc);
            }
        }

        return isNetworkDrive(char.ToUpperInvariant(path[0])) ? (null, NotThisPc) : (FeedKind.File, null);
    }

    /// <summary>Whether a host is this PC or a private network, by its name or address.</summary>
    public static bool IsLocal(string host)
    {
        if (string.Equals(host, "localhost", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        if (!IPAddress.TryParse(host.Trim('[', ']'), out IPAddress? address))
        {
            return false;
        }

        if (IPAddress.IsLoopback(address))
        {
            return true;
        }

        if (address.IsIPv4MappedToIPv6)
        {
            address = address.MapToIPv4();
        }

        if (address.AddressFamily == System.Net.Sockets.AddressFamily.InterNetworkV6)
        {
            return address.IsIPv6LinkLocal || address.IsIPv6UniqueLocal;
        }

        byte[] bytes = address.GetAddressBytes();

        return bytes[0] == 10
            || (bytes[0] == 172 && bytes[1] >= 16 && bytes[1] <= 31)
            || (bytes[0] == 192 && bytes[1] == 168)
            || (bytes[0] == 169 && bytes[1] == 254);
    }
}
