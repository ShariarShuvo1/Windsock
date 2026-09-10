using System.Globalization;

namespace Windsock.Core.Processes;

/// <summary>
/// Names the services behind well-known port numbers.
/// </summary>
public static class PortNames
{
    private static readonly Dictionary<int, string> Known = new()
    {
        [20] = "ftp-data",
        [21] = "ftp",
        [22] = "ssh",
        [23] = "telnet",
        [25] = "smtp",
        [53] = "dns",
        [67] = "dhcp",
        [68] = "dhcp",
        [80] = "http",
        [110] = "pop3",
        [123] = "ntp",
        [137] = "netbios",
        [138] = "netbios",
        [139] = "netbios",
        [143] = "imap",
        [161] = "snmp",
        [389] = "ldap",
        [443] = "https",
        [445] = "smb",
        [465] = "smtps",
        [587] = "smtp",
        [636] = "ldaps",
        [853] = "dns-tls",
        [993] = "imaps",
        [995] = "pop3s",
        [1194] = "openvpn",
        [1433] = "mssql",
        [1723] = "pptp",
        [3306] = "mysql",
        [3389] = "rdp",
        [5060] = "sip",
        [5222] = "xmpp",
        [5353] = "mdns",
        [5432] = "postgres",
        [5938] = "teamviewer",
        [6379] = "redis",
        [8080] = "http-alt",
        [8443] = "https-alt",
        [27017] = "mongodb",
        [51820] = "wireguard",
    };

    /// <summary>
    /// The service a port is normally used for, or the number on its own.
    /// </summary>
    public static string Describe(int port) =>
        Known.TryGetValue(port, out string? name)
            ? string.Create(CultureInfo.InvariantCulture, $"{port} ({name})")
            : port.ToString(CultureInfo.InvariantCulture);

    /// <summary>Whether a port has a well-known service behind it.</summary>
    public static bool IsKnown(int port) => Known.ContainsKey(port);
}
