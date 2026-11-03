using System.Globalization;
using System.Text;
using System.Xml;
using System.Xml.Linq;

namespace Windsock.Core.Wifi;

/// <summary>How a saved Wi-Fi network is secured, as far as a QR code can say.</summary>
public enum WifiShareSecurity
{
    /// <summary>No password.</summary>
    Open,

    /// <summary>WEP.</summary>
    Wep,

    /// <summary>WPA or WPA2 with a password.</summary>
    Wpa,

    /// <summary>WPA3 with a password only (SAE).</summary>
    Wpa3,

    /// <summary>Enterprise sign-in (802.1X), which a QR code cannot carry.</summary>
    Enterprise,

    /// <summary>Something else, such as Enhanced Open.</summary>
    Other,
}

/// <summary>What a saved profile holds, read from its XML. The key is never kept beyond the call that asked.</summary>
/// <param name="Ssid">The network name.</param>
/// <param name="Hidden">Whether the network does not announce its name.</param>
/// <param name="Security">How it is secured.</param>
/// <param name="KeyProtected">
/// Whether Windows withheld the key: it returns the encrypted form, marked
/// protected, to a caller it will not trust with the plain one.
/// </param>
/// <param name="Key">The password, when Windows gave it in plain text.</param>
/// <param name="KeyIsPassphrase">
/// Whether the key is a passphrase people type (keyType passPhrase) rather
/// than a raw key in hex (networkKey: a 64-digit WPA key, or a WEP key).
/// </param>
public sealed record WifiProfileSecret(string Ssid, bool Hidden, WifiShareSecurity Security, bool KeyProtected, string? Key, bool KeyIsPassphrase = true)
{
    /// <summary>Whether a QR code can be made: the key is in hand, or none is needed.</summary>
    public bool CanShare => Security == WifiShareSecurity.Open
        || (Security is WifiShareSecurity.Wpa or WifiShareSecurity.Wpa3 or WifiShareSecurity.Wep && !KeyProtected && !string.IsNullOrEmpty(Key));
}

/// <summary>Reads a WLAN profile's XML and writes the text a Wi-Fi QR code carries.</summary>
/// <remarks>
/// <para>
/// Profile elements are from Microsoft's WLAN_profile schema: WLANProfile/name,
/// SSIDConfig/SSID/name (or hex), SSIDConfig/nonBroadcast, and
/// MSM/security/authEncryption/authentication and sharedKey/{keyType,
/// protected, keyMaterial}. WlanGetProfile with the plain-text flag returns
/// <c>protected</c> false and the key when Windows trusts the caller, and the
/// encrypted blob marked true when it does not - it does not fail.
/// </para>
/// <para>
/// The QR text is ZXing's, which phone cameras read:
/// <c>WIFI:T:WPA;S:name;P:password;H:true;;</c>. Backslash, semicolon, comma,
/// double quote and colon are escaped with a backslash. WPA3-only networks are
/// written <c>T:WPA</c>, which more scanners read than the <c>SAE</c> Android
/// writes; a WPA3 phone negotiates WPA3 anyway.
/// </para>
/// <para>
/// A reader takes an unquoted field of an even number of hex digits for raw
/// bytes. So a name, or a passphrase, that only looks like hex is quoted to be
/// read as text, while a raw key - keyType networkKey: a 64-digit WPA key, or
/// a hex WEP key - is left bare to be read as the bytes it is. Quoting those
/// made the phone use the hex digits as a passphrase, and joining failed.
/// </para>
/// </remarks>
public static class WifiShare
{
    /// <summary>Reads a profile's XML. Nothing for XML that is not a WLAN profile.</summary>
    public static WifiProfileSecret? ReadProfile(string? xml)
    {
        if (string.IsNullOrWhiteSpace(xml))
        {
            return null;
        }

        XDocument document;

        try
        {
            document = XDocument.Parse(xml, LoadOptions.None);
        }
        catch (XmlException)
        {
            return null;
        }

        XElement? root = document.Root;

        if (root is null || root.Name.LocalName != "WLANProfile")
        {
            return null;
        }

        XElement? ssid = Find(root, "SSIDConfig", "SSID");
        string name = Find(ssid, "name")?.Value
            ?? FromHex(Find(ssid, "hex")?.Value)
            ?? Find(root, "name")?.Value
            ?? string.Empty;

        bool hidden = string.Equals(Find(root, "SSIDConfig", "nonBroadcast")?.Value, "true", StringComparison.OrdinalIgnoreCase);
        XElement? security = Find(root, "MSM", "security");
        string authentication = Find(security, "authEncryption", "authentication")?.Value ?? string.Empty;
        string encryption = Find(security, "authEncryption", "encryption")?.Value ?? string.Empty;
        XElement? shared = Find(security, "sharedKey");
        bool isProtected = !string.Equals(Find(shared, "protected")?.Value, "false", StringComparison.OrdinalIgnoreCase);
        string? key = shared is null ? null : Find(shared, "keyMaterial")?.Value;
        bool passphrase = !string.Equals(Find(shared, "keyType")?.Value, "networkKey", StringComparison.OrdinalIgnoreCase);

        WifiShareSecurity kind = authentication.ToUpperInvariant() switch
        {
            "OPEN" when encryption.Equals("WEP", StringComparison.OrdinalIgnoreCase) => WifiShareSecurity.Wep,
            "OPEN" or "" => WifiShareSecurity.Open,
            "SHARED" => WifiShareSecurity.Wep,
            "WPAPSK" or "WPA2PSK" => WifiShareSecurity.Wpa,
            "WPA3SAE" => WifiShareSecurity.Wpa3,
            "WPA" or "WPA2" or "WPA3" or "WPA3ENT" or "WPA3ENT192" => WifiShareSecurity.Enterprise,
            _ => WifiShareSecurity.Other,
        };

        return new WifiProfileSecret(
            name,
            hidden,
            kind,
            shared is not null && isProtected,
            shared is not null && !isProtected ? key : null,
            passphrase);
    }

    /// <summary>
    /// The text for a QR code that joins the network, or nothing when it
    /// cannot be shared that way.
    /// </summary>
    public static string? Payload(WifiProfileSecret profile)
    {
        ArgumentNullException.ThrowIfNull(profile);

        if (!profile.CanShare || profile.Ssid.Length == 0)
        {
            return null;
        }

        StringBuilder text = new("WIFI:");

        string type = profile.Security switch
        {
            WifiShareSecurity.Open => "nopass",
            WifiShareSecurity.Wep => "WEP",
            _ => "WPA",
        };

        text.Append("T:").Append(type).Append(';');
        text.Append("S:").Append(Field(profile.Ssid)).Append(';');

        if (profile.Security != WifiShareSecurity.Open && profile.Key is { } key)
        {
            text.Append("P:").Append(Field(key, quoteHex: profile.KeyIsPassphrase)).Append(';');
        }

        if (profile.Hidden)
        {
            text.Append("H:true;");
        }

        return text.Append(';').ToString();
    }

    /// <summary>
    /// One field's value: escaped, and - when <paramref name="quoteHex"/> - quoted
    /// when it would otherwise read as hex digits, which ZXing's reader takes for raw bytes.
    /// </summary>
    /// <param name="value">The text.</param>
    /// <param name="quoteHex">False for a raw key in hex, which is meant to be read as bytes.</param>
    public static string Field(string value, bool quoteHex = true)
    {
        ArgumentNullException.ThrowIfNull(value);

        StringBuilder escaped = new(value.Length + 8);

        foreach (char each in value)
        {
            if (each is '\\' or ';' or ',' or '"' or ':')
            {
                escaped.Append('\\');
            }

            escaped.Append(each);
        }

        string written = escaped.ToString();
        bool looksHex = quoteHex && value.Length > 0 && value.Length % 2 == 0 && value.All(char.IsAsciiHexDigit);

        return looksHex ? string.Concat("\"", written, "\"") : written;
    }

    private static XElement? Find(XElement? from, params string[] path)
    {
        XElement? at = from;

        foreach (string step in path)
        {
            at = at?.Elements().FirstOrDefault(each => each.Name.LocalName == step);
        }

        return at;
    }

    private static string? FromHex(string? hex)
    {
        if (string.IsNullOrEmpty(hex) || hex.Length % 2 != 0)
        {
            return null;
        }

        try
        {
            return WifiDecoders.SsidText(Convert.FromHexString(hex));
        }
        catch (FormatException)
        {
            return null;
        }
    }

    /// <summary>The share kind in words: "WPA2/WPA3 password", "No password".</summary>
    public static string Describe(WifiShareSecurity security) => security switch
    {
        WifiShareSecurity.Open => "No password",
        WifiShareSecurity.Wep => "WEP password",
        WifiShareSecurity.Wpa => "WPA/WPA2 password",
        WifiShareSecurity.Wpa3 => "WPA3 password",
        WifiShareSecurity.Enterprise => "Enterprise sign-in",
        _ => string.Create(CultureInfo.InvariantCulture, $"{security}"),
    };
}
