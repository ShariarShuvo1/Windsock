using System.Text.Json.Serialization;

namespace Windsock.Core.Settings;

/// <summary>The local status feed: read-only JSON on this PC's loopback address.</summary>
/// <remarks>
/// Off until switched on. It listens only on 127.0.0.1 and ::1, answers only
/// requests that carry its token, and sends nothing anywhere: other programs on
/// this PC ask it. The token itself is not a setting - it lives beside the
/// settings in feed.json, so exporting settings never carries it to another PC.
/// </remarks>
public sealed class FeedSettings
{
    /// <summary>The port the feed listens on unless another is chosen.</summary>
    public const int DefaultPort = 40406;

    /// <summary>The lowest port that may be chosen: below it are the well-known services.</summary>
    public const int LowestPort = 1024;

    /// <summary>Whether the feed is on.</summary>
    public bool Enabled { get; set; }

    /// <summary>The port it listens on.</summary>
    public int Port { get; set; } = DefaultPort;

    /// <summary>
    /// Whether the token may also be given in the address (<c>?token=</c>), for
    /// clients that cannot send a header: a browser's EventSource, an OBS browser
    /// source. A token in an address can end up in a client's logs or history, so
    /// a client that can send the Authorization header should.
    /// </summary>
    public bool TokenInAddress { get; set; } = true;

    /// <summary>The port in use: <see cref="Port"/> when it is one that can be, otherwise the default.</summary>
    [JsonIgnore]
    public int PortInUse => Port is >= LowestPort and <= 65535 ? Port : DefaultPort;
}
