using System.Net;
using System.Text.Json.Serialization;

namespace Windsock.Core.Settings;

/// <summary>How Windsock decides whether the internet is reachable.</summary>
public enum InternetCheck
{
    /// <summary>
    /// Ask Windows, and ping the router. Nothing leaves the local network.
    /// </summary>
    WindowsOnly,

    /// <summary>Also ping Cloudflare's resolver at 1.1.1.1.</summary>
    Cloudflare,

    /// <summary>Also ping Google's resolver at 8.8.8.8.</summary>
    Google,

    /// <summary>Also ping Quad9's resolver at 9.9.9.9.</summary>
    Quad9,

    /// <summary>Also ping an address the person chose.</summary>
    Custom,
}

/// <summary>
/// How Windsock watches the connection, and what it says about it.
/// </summary>
public sealed class ConnectionSettings
{
    /// <summary>Shortest gap between checks, in seconds.</summary>
    public const int FastestCheck = 2;

    /// <summary>Longest gap between checks, in seconds.</summary>
    public const int SlowestCheck = 60;

    /// <summary>Shortest silence that counts as an outage, in seconds.</summary>
    public const int ShortestOutage = 5;

    /// <summary>Longest silence before an outage is called, in seconds.</summary>
    public const int LongestOutage = 300;

    /// <summary>
    /// Whether the connection is watched while the window is closed, which is
    /// what alerts and the outage log need. Off, it is watched only while the
    /// Connection section is on screen.
    /// </summary>
    public bool WatchInBackground { get; set; } = true;

    /// <summary>Whether to notify when the internet drops and when it comes back.</summary>
    public bool NotifyLostAndRestored { get; set; } = true;

    /// <summary>Whether to notify when this PC moves to another network, or a VPN goes on or off.</summary>
    public bool NotifyNetworkChanges { get; set; }

    /// <summary>Whether anything outside the local network is pinged, and what.</summary>
    public InternetCheck InternetCheck { get; set; } = InternetCheck.WindowsOnly;

    /// <summary>The address pinged when <see cref="InternetCheck"/> is <see cref="InternetCheck.Custom"/>.</summary>
    public string CustomTarget { get; set; } = string.Empty;

    /// <summary>Seconds between checks.</summary>
    public int CheckEverySeconds { get; set; } = 5;

    /// <summary>Seconds without the internet before an outage is called.</summary>
    public int OutageAfterSeconds { get; set; } = 15;

    /// <summary>The gap between checks, held to what is sensible.</summary>
    [JsonIgnore]
    public TimeSpan CheckEvery => TimeSpan.FromSeconds(Math.Clamp(CheckEverySeconds, FastestCheck, SlowestCheck));

    /// <summary>How long the internet must be gone before it counts, held to what is sensible.</summary>
    [JsonIgnore]
    public TimeSpan OutageAfter => TimeSpan.FromSeconds(Math.Clamp(OutageAfterSeconds, ShortestOutage, LongestOutage));

    /// <summary>
    /// The address to ping beyond the router, or nothing when only Windows is
    /// asked - including when the chosen address is not one.
    /// </summary>
    [JsonIgnore]
    public string? InternetTarget => InternetCheck switch
    {
        InternetCheck.Cloudflare => "1.1.1.1",
        InternetCheck.Google => "8.8.8.8",
        InternetCheck.Quad9 => "9.9.9.9",
        InternetCheck.Custom => IsTarget(CustomTarget) ? CustomTarget.Trim() : null,
        _ => null,
    };

    /// <summary>
    /// Whether text could be an address to ping: an IP address, or a host name of
    /// letters, digits, dots and hyphens. Nothing else reaches the ping.
    /// </summary>
    public static bool IsTarget(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return false;
        }

        string trimmed = text.Trim();

        if (IPAddress.TryParse(trimmed, out _))
        {
            return true;
        }

        if (trimmed.Length > 253 || !trimmed.Any(char.IsAsciiLetter))
        {
            return false;
        }

        // Each label: 1 to 63 letters, digits and hyphens, not starting or ending with a hyphen.
        return trimmed.TrimEnd('.').Split('.').All(label =>
            label.Length is > 0 and <= 63
            && label[0] != '-'
            && label[^1] != '-'
            && label.All(c => char.IsAsciiLetterOrDigit(c) || c == '-'));
    }
}
