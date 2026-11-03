using System.Net;
using Microsoft.Win32;

namespace Windsock.Core.Adapters;

/// <summary>
/// What Windows' settings say about one adapter's DNS servers: which were typed
/// in, which servers Windows knows how to encrypt to, and which the adapter is
/// told to encrypt to.
/// </summary>
/// <param name="Manual">The servers typed into the adapter's settings.</param>
/// <param name="Encryptable">Servers Windows can reach over HTTPS, each with its template.</param>
/// <param name="Encrypted">Servers this adapter's settings ask Windows to encrypt lookups to.</param>
public sealed record DnsSettings(
    IReadOnlySet<IPAddress> Manual,
    IReadOnlyDictionary<IPAddress, string> Encryptable,
    IReadOnlySet<IPAddress> Encrypted)
{
    /// <summary>Nothing known.</summary>
    public static DnsSettings None { get; } = new(new HashSet<IPAddress>(), new Dictionary<IPAddress, string>(), new HashSet<IPAddress>());

    /// <summary>Describes one server the adapter uses.</summary>
    public DnsServer Describe(IPAddress server, bool readSettings)
    {
        ArgumentNullException.ThrowIfNull(server);

        if (!readSettings)
        {
            return new DnsServer(server, DnsServerOrigin.Unknown, DnsEncryption.Unknown, null);
        }

        DnsServerOrigin origin = Manual.Contains(server) ? DnsServerOrigin.Manual : DnsServerOrigin.Automatic;
        string? template = Encryptable.TryGetValue(server, out string? known) ? known : null;

        DnsEncryption encryption = Encrypted.Contains(server)
            ? DnsEncryption.Configured
            : template is not null ? DnsEncryption.Available : DnsEncryption.NotAvailable;

        return new DnsServer(server, origin, encryption, template);
    }

    /// <summary>
    /// Reads a list of servers as the registry keeps them: separated by commas
    /// or spaces, as NameServer and DhcpNameServer variously are.
    /// </summary>
    public static IReadOnlyList<IPAddress> ParseList(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return [];
        }

        List<IPAddress> found = [];

        foreach (string piece in text.Split([',', ' ', ';'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (IPAddress.TryParse(piece, out IPAddress? address))
            {
                found.Add(address);
            }
        }

        return found;
    }

    /// <summary>
    /// Whether an adapter's DNS list entry is real: Windows fills an adapter
    /// with no IPv6 servers with the long-retired fec0:0:0:ffff::1, ::2 and ::3.
    /// </summary>
    public static bool IsReal(IPAddress server) =>
        server is not null && AddressScopes.Of(server) is not (AddressScope.SiteLocal or AddressScope.Unspecified);
}

/// <summary>Reads <see cref="DnsSettings"/> from the registry, without writing anything.</summary>
/// <remarks>
/// All read unelevated on 2026-10-05: the per-adapter NameServer values under
/// Tcpip and Tcpip6, the DoH server table under Dnscache, and the per-adapter
/// DoH keys, which were present but empty on the development machine. The value
/// that says what an adapter's encryption choice is was not seen filled in, so
/// "configured" means only that Windows keeps an encryption entry for that
/// server on that adapter.
/// </remarks>
public static class DnsSettingsReader
{
    private const string Interfaces4 = @"SYSTEM\CurrentControlSet\Services\Tcpip\Parameters\Interfaces\";
    private const string Interfaces6 = @"SYSTEM\CurrentControlSet\Services\Tcpip6\Parameters\Interfaces\";
    private const string WellKnown = @"SYSTEM\CurrentControlSet\Services\Dnscache\Parameters\DohWellKnownServers";
    private const string PerInterface = @"SYSTEM\CurrentControlSet\Services\Dnscache\InterfaceSpecificParameters\";

    /// <summary>The servers Windows knows how to reach over HTTPS, and their templates.</summary>
    public static IReadOnlyDictionary<IPAddress, string> ReadEncryptable()
    {
        Dictionary<IPAddress, string> table = [];

        try
        {
            using RegistryKey? root = Registry.LocalMachine.OpenSubKey(WellKnown);

            if (root is null)
            {
                return table;
            }

            foreach (string name in root.GetSubKeyNames())
            {
                if (!IPAddress.TryParse(name, out IPAddress? server))
                {
                    continue;
                }

                using RegistryKey? entry = root.OpenSubKey(name);

                if (entry?.GetValue("Template") is string template && template.Length > 0)
                {
                    table[server] = template;
                }
            }
        }
        catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException or IOException)
        {
        }

        return table;
    }

    /// <summary>One adapter's settings, given the shared table read once.</summary>
    /// <param name="adapterId">The adapter's GUID in braces.</param>
    /// <param name="encryptable">From <see cref="ReadEncryptable"/>.</param>
    public static DnsSettings Read(string adapterId, IReadOnlyDictionary<IPAddress, string> encryptable)
    {
        ArgumentNullException.ThrowIfNull(encryptable);

        if (string.IsNullOrWhiteSpace(adapterId))
        {
            return DnsSettings.None;
        }

        HashSet<IPAddress> manual = [];
        HashSet<IPAddress> encrypted = [];

        try
        {
            foreach (string root in new[] { Interfaces4, Interfaces6 })
            {
                using RegistryKey? key = Registry.LocalMachine.OpenSubKey(root + adapterId);

                if (key?.GetValue("NameServer") is string typed)
                {
                    manual.UnionWith(DnsSettings.ParseList(typed));
                }
            }

            foreach (string family in new[] { "Doh", "Doh6" })
            {
                using RegistryKey? key = Registry.LocalMachine.OpenSubKey($@"{PerInterface}{adapterId}\DohInterfaceSettings\{family}");

                if (key is null)
                {
                    continue;
                }

                foreach (string name in key.GetSubKeyNames())
                {
                    using RegistryKey? entry = key.OpenSubKey(name);

                    if (IPAddress.TryParse(name, out IPAddress? server) && entry?.GetValue("DohFlags") is long or int)
                    {
                        long flags = Convert.ToInt64(entry.GetValue("DohFlags"), System.Globalization.CultureInfo.InvariantCulture);

                        if (flags != 0)
                        {
                            encrypted.Add(server);
                        }
                    }
                }
            }
        }
        catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException or IOException)
        {
        }

        return new DnsSettings(manual, encryptable, encrypted);
    }
}
