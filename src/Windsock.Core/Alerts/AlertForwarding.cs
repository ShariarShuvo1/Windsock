using System.Globalization;
using System.Net;
using System.Text.Json;
using System.Text.Json.Nodes;
using Windsock.Core.Adapters;
using Windsock.Core.Settings;

namespace Windsock.Core.Alerts;

/// <summary>
/// Checks where forwarded alerts are to go, and says so without giving away
/// any secret the address holds.
/// </summary>
/// <remarks>
/// A webhook address often carries its secret in its path - Discord's and
/// Slack's do - and an ntfy topic is itself the password to a public topic. So
/// nothing here, and nothing that uses it, writes more than the scheme and the
/// host anywhere: not the log, not the record, not an error.
/// </remarks>
public static class ForwardTarget
{
    /// <summary>The longest ntfy topic name ntfy accepts.</summary>
    public const int LongestTopic = 64;

    /// <summary>
    /// What is wrong with the destination, in a sentence, or nothing when alerts
    /// can be sent there.
    /// </summary>
    public static string? Problem(ForwardSettings forward)
    {
        ArgumentNullException.ThrowIfNull(forward);

        return forward.To switch
        {
            ForwardKind.Webhook => ProblemWith(forward.WebhookUrl, "the webhook"),
            _ => ProblemWith(forward.NtfyServer, "the ntfy server")
                ?? (IsTopic(forward.NtfyTopic)
                    ? null
                    : "An ntfy topic is 1 to 64 letters, digits, hyphens and underscores."),
        };
    }

    /// <summary>Where the alerts are posted, or nothing when the destination is not usable.</summary>
    public static Uri? Address(ForwardSettings forward)
    {
        ArgumentNullException.ThrowIfNull(forward);

        if (Problem(forward) is not null)
        {
            return null;
        }

        // ntfy takes a JSON message posted to its root, naming the topic inside.
        return forward.To == ForwardKind.Webhook
            ? new Uri(forward.WebhookUrl.Trim(), UriKind.Absolute)
            : new Uri(new Uri(forward.NtfyServer.Trim(), UriKind.Absolute), "/");
    }

    /// <summary>Whether text is an ntfy topic name.</summary>
    public static bool IsTopic(string? topic) =>
        topic is { Length: > 0 and <= LongestTopic }
        && topic.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_');

    /// <summary>
    /// Splits what somebody pasted for ntfy - a server, or a topic's whole
    /// address such as <c>https://ntfy.sh/my-alerts</c> - into the server and
    /// the topic. A bare topic name keeps the public server.
    /// </summary>
    public static (string Server, string Topic) SplitNtfy(string? pasted)
    {
        string text = pasted?.Trim() ?? string.Empty;

        if (IsTopic(text))
        {
            return (ForwardSettings.PublicNtfy, text);
        }

        if (!Uri.TryCreate(text, UriKind.Absolute, out Uri? address) || address.Scheme is not ("https" or "http"))
        {
            return (text, string.Empty);
        }

        string server = address.GetLeftPart(UriPartial.Authority);
        string topic = address.AbsolutePath.Trim('/');

        return (server, IsTopic(topic) ? topic : string.Empty);
    }

    /// <summary>
    /// The destination in words, with no secret in it: "ntfy.sh" or
    /// "hooks.example.com". Never the path, the query or the topic.
    /// </summary>
    public static string Describe(ForwardSettings forward)
    {
        ArgumentNullException.ThrowIfNull(forward);

        string text = forward.To == ForwardKind.Webhook ? forward.WebhookUrl : forward.NtfyServer;

        return Uri.TryCreate(text?.Trim(), UriKind.Absolute, out Uri? address) && address.Host.Length > 0
            ? address.IsDefaultPort ? address.Host : string.Concat(address.Host, ":", address.Port.ToString(CultureInfo.InvariantCulture))
            : string.Empty;
    }

    /// <summary>
    /// What is wrong with an address, or nothing. HTTPS anywhere; plain HTTP
    /// only to this PC or the local network, where a person may run their own
    /// ntfy without a certificate - never across the internet in the clear.
    /// </summary>
    private static string? ProblemWith(string? text, string what)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return string.Concat("Enter the address of ", what, ".");
        }

        if (!Uri.TryCreate(text.Trim(), UriKind.Absolute, out Uri? address) || address.Scheme is not ("https" or "http") || address.Host.Length == 0)
        {
            return string.Concat("The address of ", what, " must start with https://.");
        }

        if (address.Scheme == "http" && !IsLocal(address))
        {
            return string.Concat("The address of ", what, " must use https:// unless it is on this PC or your own network.");
        }

        return null;
    }

    /// <summary>
    /// Whether an address is this PC or on the local network: loopback, a
    /// private IPv4 range, link-local, or an IPv6 unique local address. Plain
    /// http is sent only to these, checked again on the address connected to.
    /// </summary>
    public static bool IsPrivate(IPAddress address)
    {
        ArgumentNullException.ThrowIfNull(address);

        return AddressScopes.Of(address) is AddressScope.Private or AddressScope.LinkLocal or AddressScope.UniqueLocal or AddressScope.Loopback;
    }

    /// <summary>
    /// Whether a host could be this PC or on the local network, as written: an
    /// address that is, or a name of the kinds used on one. A name is only a
    /// claim - a DNS suffix can send "nas" out to the internet - so the
    /// forwarder checks what it resolves to as it connects.
    /// </summary>
    internal static bool IsLocal(Uri address)
    {
        if (address.IsLoopback)
        {
            return true;
        }

        string host = address.IdnHost.Trim('[', ']');

        if (IPAddress.TryParse(host, out IPAddress? literal))
        {
            return IsPrivate(literal);
        }

        return !host.Contains('.', StringComparison.Ordinal)
            || host.EndsWith(".local", StringComparison.OrdinalIgnoreCase)
            || host.EndsWith(".lan", StringComparison.OrdinalIgnoreCase)
            || host.EndsWith(".home.arpa", StringComparison.OrdinalIgnoreCase);
    }
}

/// <summary>What a forwarded alert says, as the destination receives it.</summary>
/// <remarks>
/// Only the alert itself: its kind, how much it matters, its two lines and
/// when it was raised, plus the name the person gave this PC if they gave one.
/// PRIVACY.md lists exactly these fields; add one here and it must be added there.
/// </remarks>
public static class ForwardPayload
{
    private static readonly JsonSerializerOptions Writing = new() { WriteIndented = false };

    /// <summary>
    /// The JSON posted to a webhook. <c>text</c> and <c>content</c> repeat the
    /// alert in one line, the fields Slack's and Discord's incoming webhooks read.
    /// </summary>
    public static string Webhook(AlertRecord record, string? senderName)
    {
        ArgumentNullException.ThrowIfNull(record);

        string line = Line(record, senderName);

        JsonObject body = new()
        {
            ["source"] = "Windsock",
            ["kind"] = record.Topic,
            ["name"] = AlertCatalogue.NameOf(record.Topic),
            ["level"] = LevelWord(record.Level),
            ["title"] = record.Title,
            ["message"] = record.Message,
            ["at"] = record.At.ToString("O", CultureInfo.InvariantCulture),
            ["text"] = line,
            ["content"] = line,
        };

        if (!string.IsNullOrWhiteSpace(senderName))
        {
            body["sender"] = senderName.Trim();
        }

        return body.ToJsonString(Writing);
    }

    /// <summary>The JSON posted to an ntfy server's root, naming the topic.</summary>
    public static string Ntfy(AlertRecord record, string topic, string? senderName)
    {
        ArgumentNullException.ThrowIfNull(record);
        ArgumentException.ThrowIfNullOrWhiteSpace(topic);

        string title = string.IsNullOrWhiteSpace(senderName)
            ? record.Title
            : string.Concat(senderName.Trim(), ": ", record.Title);

        JsonObject body = new()
        {
            ["topic"] = topic.Trim(),
            ["title"] = title,
            ["message"] = string.IsNullOrWhiteSpace(record.Message) ? record.Title : record.Message,
            ["priority"] = record.Level switch
            {
                AlertLevel.Problem => 4,
                AlertLevel.Warning => 3,
                _ => 2,
            },
            ["tags"] = new JsonArray(record.Level switch
            {
                AlertLevel.Problem => "rotating_light",
                AlertLevel.Warning => "warning",
                _ => "information_source",
            }),
        };

        return body.ToJsonString(Writing);
    }

    /// <summary>The level as one lower-case word.</summary>
    public static string LevelWord(AlertLevel level) => level switch
    {
        AlertLevel.Problem => "problem",
        AlertLevel.Warning => "warning",
        _ => "info",
    };

    private static string Line(AlertRecord record, string? senderName)
    {
        string said = string.IsNullOrWhiteSpace(record.Message)
            ? record.Title
            : string.Concat(record.Title, ": ", record.Message.Replace('\n', ' '));

        return string.IsNullOrWhiteSpace(senderName) ? said : string.Concat("[", senderName.Trim(), "] ", said);
    }
}
