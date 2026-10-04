using System.Globalization;
using System.Management;
using System.Net;
using System.Runtime.InteropServices;

namespace Windsock.Core.Processes;

/// <summary>One record from the DNS client's cache.</summary>
/// <param name="Entry">The name that was looked up.</param>
/// <param name="Type">The record type: 1 for IPv4, 28 for IPv6, 12 for a reverse lookup.</param>
/// <param name="Data">What the record holds: an address, or for a reverse lookup, a name.</param>
public readonly record struct DnsCacheEntry(string Entry, int Type, string? Data);

/// <summary>Reads the DNS client's cache.</summary>
public interface IDnsCacheSource
{
    /// <summary>Every record in the cache, or nothing when it cannot be read.</summary>
    IReadOnlyList<DnsCacheEntry> Read();
}

/// <summary>
/// Reads the cache the way <c>Get-DnsClientCache</c> does, through the
/// documented <c>MSFT_DNSClientCache</c> class. Nothing is looked up and nothing
/// leaves the machine: these are answers Windows already received for programs
/// that asked.
/// </summary>
public sealed class WmiDnsCacheSource : IDnsCacheSource
{
    private const string Query = "SELECT Entry, Type, Data FROM MSFT_DNSClientCache";

    // A reading normally takes a fraction of a second. WMI that has stopped
    // answering must not hold a thread, and every later reading, for good.
    private static readonly TimeSpan Patience = TimeSpan.FromSeconds(5);

    /// <inheritdoc />
    public IReadOnlyList<DnsCacheEntry> Read()
    {
        List<DnsCacheEntry> entries = [];

        try
        {
            ManagementScope scope = new(@"\\.\root\StandardCimv2");
            System.Management.EnumerationOptions options = new() { Timeout = Patience, ReturnImmediately = true, Rewindable = false };
            using ManagementObjectSearcher searcher = new(scope, new ObjectQuery(Query), options);
            using ManagementObjectCollection results = searcher.Get();

            foreach (ManagementBaseObject result in results)
            {
                using (result)
                {
                    if (result["Entry"] is not string entry || string.IsNullOrWhiteSpace(entry))
                    {
                        continue;
                    }

                    int type = result["Type"] is { } value ? Convert.ToInt32(value, CultureInfo.InvariantCulture) : 0;
                    entries.Add(new DnsCacheEntry(entry, type, result["Data"] as string));
                }
            }
        }
        catch (Exception ex) when (ex is ManagementException or COMException or UnauthorizedAccessException or InvalidCastException or FormatException or OverflowException)
        {
            // A cache that cannot be read names nothing; the addresses still show.
        }

        return entries;
    }
}

/// <summary>
/// Works out, from the DNS cache, which name each address was reached by.
/// </summary>
/// <remarks>
/// A forward answer is better than a reverse lookup: it is the name the program
/// actually asked for - <c>www.example.com</c> - where reverse DNS names whatever
/// the address's owner chose, usually a hosting provider's machine. Reverse
/// answers already in the cache are used only where no forward one exists.
/// </remarks>
public static class DnsCacheNames
{
    private const int IPv4 = 1;
    private const int Pointer = 12;
    private const int IPv6 = 28;

    /// <summary>A name for each address the cache can speak for.</summary>
    public static IReadOnlyDictionary<string, string> Index(IEnumerable<DnsCacheEntry> entries)
    {
        ArgumentNullException.ThrowIfNull(entries);

        Dictionary<string, string> forward = new(StringComparer.OrdinalIgnoreCase);
        Dictionary<string, string> reverse = new(StringComparer.OrdinalIgnoreCase);

        foreach (DnsCacheEntry entry in entries)
        {
            string name = Tidy(entry.Entry);

            if (entry.Type is IPv4 or IPv6)
            {
                if (Address(entry.Data) is { } address && Usable(name))
                {
                    forward.TryAdd(address, name);
                }
            }
            else if (entry.Type == Pointer && FromReverse(name) is { } address && Usable(Tidy(entry.Data)))
            {
                reverse.TryAdd(address, Tidy(entry.Data));
            }
        }

        foreach ((string address, string name) in reverse)
        {
            forward.TryAdd(address, name);
        }

        return forward;
    }

    /// <summary>
    /// The address a reverse-lookup name stands for:
    /// <c>4.3.2.1.in-addr.arpa</c> is 1.2.3.4.
    /// </summary>
    public static string? FromReverse(string name)
    {
        const string Four = ".in-addr.arpa";
        const string Six = ".ip6.arpa";

        if (name.EndsWith(Four, StringComparison.OrdinalIgnoreCase))
        {
            string[] parts = name[..^Four.Length].Split('.');

            if (parts.Length != 4)
            {
                return null;
            }

            Array.Reverse(parts);
            return Address(string.Join('.', parts));
        }

        if (name.EndsWith(Six, StringComparison.OrdinalIgnoreCase))
        {
            string[] nibbles = name[..^Six.Length].Split('.');

            if (nibbles.Length != 32 || nibbles.Any(nibble => nibble.Length != 1 || !Uri.IsHexDigit(nibble[0])))
            {
                return null;
            }

            Array.Reverse(nibbles);
            string hex = string.Concat(nibbles);
            string grouped = string.Join(':', Enumerable.Range(0, 8).Select(group => hex.Substring(group * 4, 4)));
            return Address(grouped);
        }

        return null;
    }

    private static string? Address(string? text)
    {
        if (string.IsNullOrWhiteSpace(text) || !IPAddress.TryParse(text.Trim(), out IPAddress? parsed))
        {
            return null;
        }

        if (parsed.IsIPv4MappedToIPv6)
        {
            parsed = parsed.MapToIPv4();
        }

        return LoopbackEnds.IsLoopback(parsed) || parsed.Equals(IPAddress.Any) || parsed.Equals(IPAddress.IPv6Any)
            ? null
            : parsed.ToString();
    }

    private static string Tidy(string? name) => (name ?? string.Empty).Trim().TrimEnd('.');

    private static bool Usable(string name) =>
        name.Length is > 0 and <= 253
        && !string.Equals(name, "localhost", StringComparison.OrdinalIgnoreCase)
        && !name.EndsWith(".arpa", StringComparison.OrdinalIgnoreCase)
        && !IPAddress.TryParse(name, out _);
}
