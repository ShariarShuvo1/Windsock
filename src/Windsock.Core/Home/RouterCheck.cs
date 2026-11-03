using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Windsock.Core.Home;

/// <summary>How a router check went.</summary>
public enum RouterAnswer
{
    /// <summary>The router answered UPnP and was read.</summary>
    Answered,

    /// <summary>The router did not answer a UPnP search: UPnP is off, or it never had it.</summary>
    NoUpnp,

    /// <summary>The router answered, but not as an internet gateway, or not readably.</summary>
    NotAGateway,

    /// <summary>Not on a home network with a router, so nothing was asked.</summary>
    NotHome,
}

/// <summary>What the router's own internet address says about the connection beyond it.</summary>
public enum ExternalAddressKind
{
    /// <summary>The router did not say.</summary>
    Unknown,

    /// <summary>A public address: the router is on the internet itself.</summary>
    Public,

    /// <summary>A private address (RFC 1918): there is another router between this one and the internet.</summary>
    Private,

    /// <summary>A carrier-grade NAT address (100.64.0.0/10): the provider shares one public address among customers.</summary>
    Shared,
}

/// <summary>A port the router forwards from the internet to a device, as UPnP set it up.</summary>
/// <param name="Protocol">TCP or UDP.</param>
/// <param name="ExternalPort">The port open on the internet side.</param>
/// <param name="RemoteHost">The only internet address allowed in, or empty for anyone.</param>
/// <param name="InternalClient">The device it forwards to, as the router wrote it.</param>
/// <param name="InternalPort">The port on that device.</param>
/// <param name="Enabled">Whether the mapping is switched on.</param>
/// <param name="Description">What the program that asked for it called it.</param>
/// <param name="LeaseSeconds">How long it lasts, or 0 for until removed.</param>
public sealed record PortMapping(
    string Protocol,
    int ExternalPort,
    string RemoteHost,
    string InternalClient,
    int InternalPort,
    bool Enabled,
    string Description,
    int LeaseSeconds);

/// <summary>Everything one router check found. Every field is the router's own word, and advisory.</summary>
/// <param name="CheckedAt">When it ran.</param>
/// <param name="Answer">How it went.</param>
/// <param name="Gateway">The router's address on the home network.</param>
/// <param name="Manufacturer">The router's maker.</param>
/// <param name="Model">Its model name.</param>
/// <param name="ModelNumber">Its model number.</param>
/// <param name="FriendlyName">The name it gives itself.</param>
/// <param name="ExternalAddress">Its own internet-side address, as it reports it.</param>
/// <param name="ExternalKind">What that address says about the connection beyond.</param>
/// <param name="ConnectionStatus">Its internet connection's state, such as "Connected".</param>
/// <param name="CountersOffered">Whether it reports its traffic counters.</param>
/// <param name="PortMappings">The ports it has opened to the internet through UPnP.</param>
/// <param name="MappingsComplete">
/// Whether the whole list was read. False when the router stopped answering part
/// way, or refused to list them, so more may be open than are shown.
/// </param>
public sealed record RouterReport(
    DateTimeOffset CheckedAt,
    RouterAnswer Answer,
    string? Gateway,
    string? Manufacturer,
    string? Model,
    string? ModelNumber,
    string? FriendlyName,
    string? ExternalAddress,
    ExternalAddressKind ExternalKind,
    string? ConnectionStatus,
    bool CountersOffered,
    IReadOnlyList<PortMapping> PortMappings,
    bool MappingsComplete)
{
    private static readonly JsonSerializerOptions Format = new()
    {
        Converters = { new JsonStringEnumConverter() },
    };

    /// <summary>The maker and model together, as "TP-Link TL-WR840N", or nothing.</summary>
    public string? MakeAndModel => Describe(Manufacturer, Model, FriendlyName);

    /// <summary>A router's maker and model together, or its own name when it gives neither.</summary>
    public static string? Describe(string? manufacturer, string? model, string? friendlyName) => (manufacturer, model) switch
    {
        ({ Length: > 0 } maker, { Length: > 0 } named) when named.StartsWith(maker, StringComparison.OrdinalIgnoreCase) => named,
        ({ Length: > 0 } maker, { Length: > 0 } named) => string.Concat(maker, " ", named),
        ({ Length: > 0 } maker, _) => maker,
        (_, { Length: > 0 } named) => named,
        _ => friendlyName,
    };

    /// <summary>The report as JSON, for keeping.</summary>
    public string ToJson() => JsonSerializer.Serialize(this, Format);

    /// <summary>A kept report, or nothing for text that is not one.</summary>
    public static RouterReport? FromJson(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return null;
        }

        try
        {
            return JsonSerializer.Deserialize<RouterReport>(json, Format);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>Classes an address the router gave for its internet side.</summary>
    public static ExternalAddressKind Classify(string? address)
    {
        if (!IPAddress.TryParse(address, out IPAddress? parsed) || parsed.AddressFamily != AddressFamily.InterNetwork)
        {
            return ExternalAddressKind.Unknown;
        }

        byte[] octets = parsed.GetAddressBytes();

        if (octets[0] == 0)
        {
            return ExternalAddressKind.Unknown;
        }

        if (octets[0] == 10
            || (octets[0] == 172 && octets[1] is >= 16 and <= 31)
            || (octets[0] == 192 && octets[1] == 168))
        {
            return ExternalAddressKind.Private;
        }

        return octets[0] == 100 && octets[1] is >= 64 and <= 127 ? ExternalAddressKind.Shared : ExternalAddressKind.Public;
    }
}

/// <summary>Reads the router's maker, model and opened ports over UPnP, asking nothing that changes it.</summary>
public static class RouterCheck
{
    /// <summary>The most port mappings read; a router listing more is cut short.</summary>
    public const int MostMappings = 256;

    private const int IndexOutOfRange = 713;
    private const int NoSuchEntry = 714;

    /// <summary>Runs a check against a home link's router.</summary>
    public static async Task<RouterReport> RunAsync(IRouterClient client, HomeLink? link, DateTimeOffset now, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(client);

        if (link?.Gateway is not { } gateway)
        {
            return Empty(now, RouterAnswer.NotHome, null);
        }

        GatewayDescription? description = await client.DescribeAsync(link, cancellationToken).ConfigureAwait(false);

        if (description is null)
        {
            return Empty(now, RouterAnswer.NoUpnp, gateway.ToString());
        }

        GatewayService? connection = description.Find("WANIPConnection") ?? description.Find("WANPPPConnection");
        GatewayService? common = description.Find("WANCommonInterfaceConfig");

        if (connection is null && common is null)
        {
            return Empty(now, RouterAnswer.NotAGateway, gateway.ToString()) with
            {
                Manufacturer = description.Manufacturer,
                Model = description.ModelName,
                ModelNumber = description.ModelNumber,
                FriendlyName = description.FriendlyName,
            };
        }

        string? external = null;
        string? status = null;
        List<PortMapping> mappings = [];
        bool complete = connection is not null;

        if (connection is not null)
        {
            SoapAnswer address = await client.CallAsync(connection, "GetExternalIPAddress", null, cancellationToken).ConfigureAwait(false);
            external = DeviceNames.Clean(address["NewExternalIPAddress"]);

            SoapAnswer state = await client.CallAsync(connection, "GetStatusInfo", null, cancellationToken).ConfigureAwait(false);
            status = DeviceNames.Clean(state["NewConnectionStatus"]);

            for (int index = 0; index < MostMappings; index++)
            {
                SoapAnswer entry = await client.CallAsync(
                    connection,
                    "GetGenericPortMappingEntry",
                    [("NewPortMappingIndex", index.ToString(CultureInfo.InvariantCulture))],
                    cancellationToken).ConfigureAwait(false);

                if (!entry.Succeeded)
                {
                    complete = entry.ErrorCode is IndexOutOfRange or NoSuchEntry;
                    break;
                }

                if (Mapping(entry) is { } mapping)
                {
                    mappings.Add(mapping);
                }

                if (index == MostMappings - 1)
                {
                    complete = false;
                }
            }
        }

        bool counters = false;

        if (common is not null)
        {
            SoapAnswer received = await client.CallAsync(common, "GetTotalBytesReceived", null, cancellationToken).ConfigureAwait(false);
            counters = ulong.TryParse(received["NewTotalBytesReceived"], NumberStyles.None, CultureInfo.InvariantCulture, out _);
        }

        return new RouterReport(
            now,
            RouterAnswer.Answered,
            gateway.ToString(),
            description.Manufacturer,
            description.ModelName,
            description.ModelNumber,
            description.FriendlyName,
            external,
            RouterReport.Classify(external),
            status,
            counters,
            mappings,
            complete);
    }

    /// <summary>One port mapping from a GetGenericPortMappingEntry answer, or nothing for one that does not read.</summary>
    internal static PortMapping? Mapping(SoapAnswer entry)
    {
        if (!int.TryParse(entry["NewExternalPort"], NumberStyles.None, CultureInfo.InvariantCulture, out int external)
            || !int.TryParse(entry["NewInternalPort"], NumberStyles.None, CultureInfo.InvariantCulture, out int internalPort))
        {
            return null;
        }

        _ = int.TryParse(entry["NewLeaseDuration"], NumberStyles.None, CultureInfo.InvariantCulture, out int lease);

        return new PortMapping(
            (DeviceNames.Clean(entry["NewProtocol"]) ?? string.Empty).ToUpperInvariant(),
            external,
            DeviceNames.Clean(entry["NewRemoteHost"]) ?? string.Empty,
            DeviceNames.Clean(entry["NewInternalClient"]) ?? string.Empty,
            internalPort,
            entry["NewEnabled"] is "1" or "true" or "yes",
            DeviceNames.Clean(entry["NewPortMappingDescription"]) ?? string.Empty,
            lease);
    }

    private static RouterReport Empty(DateTimeOffset now, RouterAnswer answer, string? gateway) =>
        new(now, answer, gateway, null, null, null, null, null, ExternalAddressKind.Unknown, null, false, [], false);
}
