using System.Globalization;
using System.Text;
using Windsock.Core.Connectivity;
using Windsock.Core.Formatting;
using Windsock.Core.Lookups;
using Windsock.Core.Wifi;

namespace Windsock.Core.Adapters;

/// <summary>What goes into the text copied for a help request.</summary>
/// <param name="HardwareAddresses">Whether to include MAC addresses, which identify the hardware.</param>
/// <param name="NetworkNames">Whether to include network and Wi-Fi names.</param>
/// <param name="AllAdapters">Whether to include adapters that are down, too.</param>
public sealed record SupportTextOptions(bool HardwareAddresses = false, bool NetworkNames = true, bool AllAdapters = false);

/// <summary>Everything the text can describe; any part may be missing.</summary>
/// <param name="Adapters">The adapters and Windows' connectivity verdict.</param>
/// <param name="Connection">The latest connection check.</param>
/// <param name="Wifi">The latest Wi-Fi reading.</param>
/// <param name="PublicAddress">The last public address lookup, if the person made one.</param>
/// <param name="DnsTimings">DNS timings, if the person ran them.</param>
/// <param name="CarrierNat">The carrier-grade NAT verdict.</param>
public sealed record SupportFacts(
    AdapterReading Adapters,
    ConnectionSample? Connection = null,
    WifiReading? Wifi = null,
    PublicAddressResult? PublicAddress = null,
    IReadOnlyList<DnsTiming>? DnsTimings = null,
    CarrierNatVerdict? CarrierNat = null);

/// <summary>
/// The network's details as plain text, for pasting into a help request: what
/// a support line asks people to read out of ipconfig, and a little more.
/// </summary>
public static class NetworkSupportText
{
    /// <summary>Writes the text.</summary>
    public static string Write(SupportFacts facts, SupportTextOptions? options = null, DateTimeOffset? at = null)
    {
        ArgumentNullException.ThrowIfNull(facts);

        SupportTextOptions choose = options ?? new SupportTextOptions();
        StringBuilder text = new();
        CultureInfo invariant = CultureInfo.InvariantCulture;

        text.Append("Network details from Windsock, ")
            .AppendLine((at ?? facts.Adapters.Snapshot.At).ToString(string.Concat("yyyy-MM-dd ", ClockFormat.Current.ShortTime, " zzz"), invariant));
        text.AppendLine();

        if (facts.Connection is { } check)
        {
            text.Append("Windows says: ").AppendLine(Reach(check.Facts.Reach));
            Line(text, "Router responds", check.RouterChecked ? Ms(check.Router) : "no router");

            if (check.InternetChecked)
            {
                Line(text, "Internet responds", Ms(check.Internet));
            }

            if (choose.NetworkNames && check.Facts.Network is { } network)
            {
                Line(text, "Network", network.Describe());
            }

            if (check.Facts.VpnActive)
            {
                Line(text, "VPN", "on");
            }
        }

        Line(text, "IPv6", Ipv6Judge.Describe(facts.Adapters.Ipv6));

        if (facts.CarrierNat is { } nat)
        {
            Line(text, "Carrier-grade NAT", string.Concat(nat.State.ToString(), " - ", nat.Reason));
        }

        if (facts.PublicAddress is { Outcome: LookupOutcome.Answered } found)
        {
            Line(text, "Public IPv4", found.Four?.ToString() ?? "—");
            Line(text, "Public IPv6", found.Six?.ToString() ?? "—");

            if (found.Network is { } network)
            {
                Line(text, "Provider", string.Concat(network.Organisation, " (", network.Label, ")"));
            }
        }

        AdapterDetails? carrier = facts.Adapters.Snapshot.Carrier;
        IEnumerable<AdapterDetails> adapters = choose.AllAdapters
            ? facts.Adapters.Snapshot.Adapters.Where(each => each.Kind != AdapterKind.Loopback)
            : facts.Adapters.Snapshot.Shown.Where(each => each.IsUp);

        foreach (AdapterDetails adapter in adapters)
        {
            text.AppendLine();
            text.Append(adapter.Name);

            if (ReferenceEquals(adapter, carrier))
            {
                text.Append(" (carries internet traffic)");
            }

            text.AppendLine();
            Line(text, "Description", adapter.Description);
            Line(text, "Kind", adapter.Kind.ToString());
            Line(text, "State", adapter.State.ToString());

            if (choose.NetworkNames && adapter.NetworkName is { Length: > 0 } networkName)
            {
                Line(text, "Windows network", networkName);
            }

            if (choose.HardwareAddresses && adapter.HardwareAddress is { } mac)
            {
                Line(text, "MAC", mac);
            }

            if (adapter.ReceiveBitsPerSecond is { } receive)
            {
                Line(text, "Link speed", adapter.TransmitBitsPerSecond is { } send && send != receive
                    ? string.Concat(Speed(receive), " in, ", Speed(send), " out")
                    : Speed(receive));
            }

            if (adapter.Mtu is { } mtu)
            {
                Line(text, "MTU", mtu.ToString(invariant));
            }

            foreach (AdapterAddress address in adapter.Addresses)
            {
                Line(text, address.Address.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork ? "IPv4" : "IPv6", string.Concat(
                    address.ToString(),
                    address.FromDhcp ? " (DHCP)" : string.Empty,
                    address.Temporary ? " (temporary)" : string.Empty,
                    address.Preferred ? string.Empty : " (not ready)"));
            }

            foreach (System.Net.IPAddress gateway in adapter.Gateways)
            {
                Line(text, "Gateway", gateway.ToString());
            }

            if (adapter.DhcpServer is { } dhcp)
            {
                Line(text, "DHCP server", dhcp.ToString());
            }

            foreach (DnsServer server in adapter.DnsServers)
            {
                Line(text, "DNS", string.Concat(server.Address.ToString(), " (", Origin(server.Origin), ", ", Encryption(server.Encryption), ")"));
            }

            if (adapter.DnsSuffix.Length > 0)
            {
                Line(text, "DNS suffix", adapter.DnsSuffix);
            }

            if (adapter.Cost is { } cost)
            {
                Line(text, "Metered", cost.IsMetered ? string.Concat("yes (", cost.Kind.ToString(), ")") : "no");
            }

            if (adapter.Kind == AdapterKind.WiFi && facts.Wifi is { IsConnected: true } wifi && wifi.Adapter?.Id.ToString("B").Equals(adapter.Id, StringComparison.OrdinalIgnoreCase) != false)
            {
                Line(text, "Wi-Fi", string.Concat(wifi.Generation, ", ", WifiBands.DescribeChannel(wifi.Channel, wifi.Band, wifi.WidthMhz)));
                Line(text, "Signal", wifi.Rssi is { } rssi ? string.Create(invariant, $"{rssi} dBm ({wifi.SignalPercent}%)") : "—");
                Line(text, "Link rate", wifi.ReceiveMbps is { } down ? string.Create(invariant, $"{down:0} Mbit/s in, {wifi.TransmitMbps:0} Mbit/s out") : "—");

                if (choose.NetworkNames && wifi.NetworkName is { } ssid)
                {
                    Line(text, "Wi-Fi network", ssid);
                }
            }
        }

        if (facts.DnsTimings is { Count: > 0 } timings)
        {
            text.AppendLine();
            text.AppendLine("DNS response times (root query, past Windows' cache)");

            foreach (DnsTiming timing in timings)
            {
                Line(text, timing.Server.ToString(), timing.MedianMs is { } median
                    ? string.Create(invariant, $"{median:0.0} ms median, {timing.Answered} of {timing.Sent} answered")
                    : timing.Problem ?? "no answer");
            }
        }

        return text.ToString();
    }

    private static void Line(StringBuilder text, string label, string value) =>
        text.Append("  ").Append(label).Append(": ").AppendLine(value);

    private static string Ms(TimeSpan? took) => took is { } value
        ? string.Create(CultureInfo.InvariantCulture, $"{value.TotalMilliseconds:0} ms")
        : "no answer";

    private static string Speed(ulong bitsPerSecond) => bitsPerSecond >= 1_000_000_000
        ? string.Create(CultureInfo.InvariantCulture, $"{bitsPerSecond / 1e9:0.##} Gbit/s")
        : string.Create(CultureInfo.InvariantCulture, $"{bitsPerSecond / 1e6:0.#} Mbit/s");

    private static string Reach(WindowsReach reach) => reach switch
    {
        WindowsReach.Internet => "internet",
        WindowsReach.LocalOnly => "local network only",
        WindowsReach.SignInRequired => "sign-in needed",
        WindowsReach.None => "no network",
        _ => "not known",
    };

    private static string Origin(DnsServerOrigin origin) => origin switch
    {
        DnsServerOrigin.Manual => "set by hand",
        DnsServerOrigin.Automatic => "automatic",
        _ => "origin not known",
    };

    private static string Encryption(DnsEncryption encryption) => encryption switch
    {
        DnsEncryption.Configured => "encryption set in Windows",
        DnsEncryption.Available => "could be encrypted, not set",
        DnsEncryption.NotAvailable => "plain",
        _ => "encryption not known",
    };
}
