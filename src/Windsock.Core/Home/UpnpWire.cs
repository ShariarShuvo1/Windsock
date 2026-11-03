using System.Globalization;
using System.Net;
using System.Security;
using System.Text;
using System.Xml;
using System.Xml.Linq;

namespace Windsock.Core.Home;

/// <summary>A UPnP service the router offers, and where to send it actions.</summary>
/// <param name="ServiceType">Its type, such as "urn:schemas-upnp-org:service:WANIPConnection:1".</param>
/// <param name="ControlUrl">Where its actions are posted.</param>
public sealed record GatewayService(string ServiceType, Uri ControlUrl);

/// <summary>What the router's UPnP description says about it.</summary>
/// <param name="FriendlyName">The name it gives itself.</param>
/// <param name="Manufacturer">Its maker, such as "TP-Link".</param>
/// <param name="ModelName">Its model, such as "TL-WR840N".</param>
/// <param name="ModelNumber">Its model number, when given.</param>
/// <param name="Services">The services it offers, each pointing at the router itself.</param>
public sealed record GatewayDescription(
    string? FriendlyName,
    string? Manufacturer,
    string? ModelName,
    string? ModelNumber,
    IReadOnlyList<GatewayService> Services)
{
    /// <summary>The first service of a kind ("WANIPConnection", "WANCommonInterfaceConfig"), any version.</summary>
    public GatewayService? Find(string kind) =>
        Services.FirstOrDefault(each => each.ServiceType.Contains(":service:" + kind + ":", StringComparison.OrdinalIgnoreCase));
}

/// <summary>What a SOAP action came back with.</summary>
/// <param name="Values">The response's output arguments by name, when it succeeded.</param>
/// <param name="ErrorCode">The UPnP error code of a fault, such as 713, when it failed.</param>
/// <param name="ErrorDescription">The fault's own words.</param>
public sealed record SoapAnswer(IReadOnlyDictionary<string, string>? Values, int? ErrorCode, string? ErrorDescription)
{
    /// <summary>Whether the action succeeded.</summary>
    public bool Succeeded => Values is not null;

    /// <summary>An output argument, or nothing.</summary>
    public string? this[string name] => Values?.GetValueOrDefault(name);

    /// <summary>Neither a response nor a fault: something that is not a SOAP answer.</summary>
    public static SoapAnswer Garbled { get; } = new(null, null, null);
}

/// <summary>
/// The wire formats of UPnP's Internet Gateway Device: the SSDP search, the
/// description document, and SOAP actions.
/// </summary>
/// <remarks>
/// <para>
/// Everything a device on the network says is untrusted. SSDP is unauthenticated,
/// so any device could answer a search with a description address pointing
/// anywhere - at another machine, at this PC's own loopback - and turn a router
/// check into a request Windsock never meant to make. So an answer counts only
/// when it comes from the router's own address and names the router's own
/// address, written as an address, over plain HTTP; and every service address
/// the description gives is held to the same rule. XML is read with DTDs
/// prohibited, no resolver and a size cap.
/// </para>
/// <para>
/// Read-only by design: the only actions ever built are the Get ones below.
/// Windsock never adds or removes a port mapping, and never subscribes to events.
/// </para>
/// </remarks>
public static class UpnpWire
{
    /// <summary>The SSDP port.</summary>
    public const int SsdpPort = 1900;

    /// <summary>The device type searched for.</summary>
    public const string GatewayType = "urn:schemas-upnp-org:device:InternetGatewayDevice:1";

    private const int LargestDocument = 512 * 1024;

    /// <summary>
    /// The M-SEARCH, in miniupnpc's field order. Sent to the router's own
    /// address rather than the multicast group, so nothing else on the network
    /// hears it; measured: the development machine's router answers it so.
    /// </summary>
    public static byte[] Search(string searchType = GatewayType) => Encoding.ASCII.GetBytes(
        "M-SEARCH * HTTP/1.1\r\n"
        + "HOST: 239.255.255.250:1900\r\n"
        + "ST: " + searchType + "\r\n"
        + "MAN: \"ssdp:discover\"\r\n"
        + "MX: 2\r\n"
        + "\r\n");

    /// <summary>
    /// The description address from a search answer, when the answer is a
    /// success and the address is plain HTTP to the router itself, written as
    /// its address. Nothing otherwise.
    /// </summary>
    public static Uri? Location(string answer, IPAddress gateway)
    {
        ArgumentNullException.ThrowIfNull(answer);
        ArgumentNullException.ThrowIfNull(gateway);

        string[] lines = answer.Split(["\r\n", "\n"], StringSplitOptions.None);

        if (lines.Length == 0 || !lines[0].StartsWith("HTTP/1.", StringComparison.OrdinalIgnoreCase) || !lines[0].Contains(" 200", StringComparison.Ordinal))
        {
            return null;
        }

        foreach (string line in lines.Skip(1))
        {
            int colon = line.IndexOf(':', StringComparison.Ordinal);

            if (colon > 0 && line[..colon].Trim().Equals("LOCATION", StringComparison.OrdinalIgnoreCase))
            {
                return Uri.TryCreate(line[(colon + 1)..].Trim(), UriKind.Absolute, out Uri? location) && IsGateway(location, gateway)
                    ? location
                    : null;
            }
        }

        return null;
    }

    /// <summary>Whether an address is plain HTTP to the router, written as its IP address.</summary>
    public static bool IsGateway(Uri address, IPAddress gateway)
    {
        ArgumentNullException.ThrowIfNull(address);
        ArgumentNullException.ThrowIfNull(gateway);

        return address.IsAbsoluteUri
            && address.Scheme == Uri.UriSchemeHttp
            && address.HostNameType is UriHostNameType.IPv4 or UriHostNameType.IPv6
            && IPAddress.TryParse(address.DnsSafeHost, out IPAddress? host)
            && host.Equals(gateway)
            && string.IsNullOrEmpty(address.UserInfo);
    }

    /// <summary>
    /// The router's description, or nothing for a document that is not one.
    /// Services whose control address is not the router itself are dropped.
    /// </summary>
    public static GatewayDescription? Describe(string xml, Uri location, IPAddress gateway)
    {
        ArgumentNullException.ThrowIfNull(xml);
        ArgumentNullException.ThrowIfNull(location);

        if (Load(xml) is not { Root: { } root })
        {
            return null;
        }

        XElement? device = root.Elements().FirstOrDefault(each => each.Name.LocalName == "device");

        if (device is null)
        {
            return null;
        }

        Uri baseAddress = location;

        if (Text(root, "URLBase") is { Length: > 0 } urlBase
            && Uri.TryCreate(urlBase, UriKind.Absolute, out Uri? declared)
            && IsGateway(declared, gateway))
        {
            baseAddress = declared;
        }

        List<GatewayService> services = [];

        foreach (XElement service in device.Descendants().Where(each => each.Name.LocalName == "service"))
        {
            if (Text(service, "serviceType") is not { Length: > 0 } type
                || Text(service, "controlURL") is not { Length: > 0 } control
                || !Uri.TryCreate(baseAddress, control, out Uri? controlUrl)
                || !IsGateway(controlUrl, gateway))
            {
                continue;
            }

            services.Add(new GatewayService(type, controlUrl));
        }

        return new GatewayDescription(
            Clean(Text(device, "friendlyName")),
            Clean(Text(device, "manufacturer")),
            Clean(Text(device, "modelName")),
            Clean(Text(device, "modelNumber")),
            services);
    }

    /// <summary>A SOAP envelope for an action with its input arguments.</summary>
    public static string Envelope(string serviceType, string action, IReadOnlyList<(string Name, string Value)>? arguments = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(serviceType);
        ArgumentException.ThrowIfNullOrWhiteSpace(action);

        StringBuilder body = new();
        body.Append("<?xml version=\"1.0\"?>")
            .Append("<s:Envelope xmlns:s=\"http://schemas.xmlsoap.org/soap/envelope/\" s:encodingStyle=\"http://schemas.xmlsoap.org/soap/encoding/\">")
            .Append("<s:Body><u:").Append(action).Append(" xmlns:u=\"").Append(SecurityElement.Escape(serviceType)).Append("\">");

        foreach ((string name, string value) in arguments ?? [])
        {
            body.Append('<').Append(name).Append('>').Append(SecurityElement.Escape(value)).Append("</").Append(name).Append('>');
        }

        body.Append("</u:").Append(action).Append("></s:Body></s:Envelope>");
        return body.ToString();
    }

    /// <summary>The SOAPAction header's value, quotes included as the spec has them.</summary>
    public static string ActionHeader(string serviceType, string action) => string.Concat("\"", serviceType, "#", action, "\"");

    /// <summary>Reads a SOAP answer: the response's arguments, or the fault's UPnP error.</summary>
    public static SoapAnswer Answer(string xml, string action)
    {
        ArgumentNullException.ThrowIfNull(xml);
        ArgumentNullException.ThrowIfNull(action);

        if (Load(xml) is not { Root: { } root })
        {
            return SoapAnswer.Garbled;
        }

        XElement? response = root.Descendants().FirstOrDefault(each => each.Name.LocalName == action + "Response");

        if (response is not null)
        {
            Dictionary<string, string> values = new(StringComparer.Ordinal);

            foreach (XElement each in response.Elements())
            {
                values[each.Name.LocalName] = each.Value.Trim();
            }

            return new SoapAnswer(values, null, null);
        }

        XElement? error = root.Descendants().FirstOrDefault(each => each.Name.LocalName == "UPnPError");

        if (error is not null)
        {
            int? code = int.TryParse(Text(error, "errorCode"), NumberStyles.Integer, CultureInfo.InvariantCulture, out int number) ? number : null;
            return new SoapAnswer(null, code, Clean(Text(error, "errorDescription")));
        }

        return root.Descendants().Any(each => each.Name.LocalName == "Fault")
            ? new SoapAnswer(null, null, null)
            : SoapAnswer.Garbled;
    }

    private static XDocument? Load(string xml)
    {
        if (xml.Length > LargestDocument)
        {
            return null;
        }

        XmlReaderSettings settings = new()
        {
            DtdProcessing = DtdProcessing.Prohibit,
            XmlResolver = null,
            MaxCharactersInDocument = LargestDocument,
            IgnoreComments = true,
            IgnoreProcessingInstructions = true,
        };

        try
        {
            using StringReader text = new(xml);
            using XmlReader reader = XmlReader.Create(text, settings);
            return XDocument.Load(reader);
        }
        catch (XmlException)
        {
            return null;
        }
    }

    private static string? Text(XElement parent, string name) =>
        parent.Elements().FirstOrDefault(each => each.Name.LocalName == name)?.Value.Trim();

    private static string? Clean(string? text) => DeviceNames.Clean(text);
}
