using System.Security.Cryptography;
using System.Text;

namespace Windsock.Core.Feed;

/// <summary>
/// The transparent stream overlay page the feed serves at <c>/overlay</c>, for
/// an OBS browser source: the rates, ping and today's use, live over the
/// feed's own Server-Sent Events.
/// </summary>
/// <remarks>
/// <para>
/// Served from the feed itself, so the page and the events it reads are the
/// same origin and need no CORS. The page paints no background, so the source
/// shows only the figures; OBS's own default CSS for a browser source already
/// makes the body transparent too.
/// </para>
/// <para>
/// Its script and style are inline, and the Content-Security-Policy names them
/// by their SHA-256 hashes, worked out here from the very text served: nothing
/// else can run in the page, and it may connect only to the feed. Query
/// options: <c>show</c> (any of down, up, ping, today, cpu; default
/// down,up,ping), <c>units</c> (bits or bytes), <c>size</c> (font size in
/// pixels), <c>layout=column</c> and <c>align=right</c>. Written for Chromium
/// 127, the engine OBS 31 and 32 ship.
/// </para>
/// </remarks>
public static class FeedOverlay
{
    private static readonly Lazy<(byte[] Page, string Policy)> Built = new(Build);

    /// <summary>The page, as UTF-8.</summary>
    public static byte[] Page => Built.Value.Page;

    /// <summary>The Content-Security-Policy that goes with it.</summary>
    public static string Policy => Built.Value.Policy;

    private static (byte[] Page, string Policy) Build()
    {
        using Stream? stream = typeof(FeedOverlay).Assembly.GetManifestResourceStream("Windsock.Core.Feed.Overlay.html");

        if (stream is null)
        {
            byte[] missing = Encoding.UTF8.GetBytes("<!doctype html><title>Windsock overlay</title>");
            return (missing, "default-src 'none'");
        }

        using StreamReader reader = new(stream, Encoding.UTF8);
        string page = reader.ReadToEnd();

        string policy = string.Concat(
            "default-src 'none'; ",
            "script-src ", Hash(Inner(page, "script")), "; ",
            "style-src ", Hash(Inner(page, "style")), "; ",
            "connect-src 'self'; base-uri 'none'; form-action 'none'; frame-ancestors 'none'");

        return (Encoding.UTF8.GetBytes(page), policy);
    }

    /// <summary>The text between an element's opening and closing tags, exactly as served.</summary>
    internal static string Inner(string page, string element)
    {
        string open = "<" + element + ">";
        string close = "</" + element + ">";
        int start = page.IndexOf(open, StringComparison.Ordinal);
        int end = start < 0 ? -1 : page.IndexOf(close, start, StringComparison.Ordinal);

        return start < 0 || end < 0 ? string.Empty : page[(start + open.Length)..end];
    }

    private static string Hash(string text) =>
        string.Concat("'sha256-", Convert.ToBase64String(SHA256.HashData(Encoding.UTF8.GetBytes(text))), "'");
}
