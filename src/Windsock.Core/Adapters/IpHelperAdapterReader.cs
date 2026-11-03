using System.Net;
using System.Runtime.InteropServices;
using Windows.Win32;
using Windows.Win32.NetworkManagement.IpHelper;
using Windows.Win32.Networking.WinSock;

namespace Windsock.Core.Adapters;

/// <summary>Reads every adapter's details at one moment.</summary>
public interface IAdapterReader
{
    /// <summary>Every adapter as it stands. Never throws; an empty list is a reading.</summary>
    IReadOnlyList<AdapterDetails> Read();
}

/// <summary>Reads adapters through IP Helper's <c>GetAdaptersAddresses</c>.</summary>
/// <remarks>
/// One call gives everything the Adapters page shows - addresses, routers,
/// DNS servers, link speeds, MTU, tunnel type - which .NET's NetworkInterface
/// spreads over several calls and partly hides (the tunnel type, the send
/// speed, the DHCP server and the metrics are not exposed at all). Microsoft
/// warns the call is not cheap, so it is made when something changes, never
/// on a timer of a second.
/// </remarks>
public sealed class IpHelperAdapterReader : IAdapterReader
{
    private const uint NoError = 0;
    private const uint BufferOverflow = 111;
    private const uint Unspecified = 0;

    /// <summary>Whether to look up each adapter's DNS settings in the registry.</summary>
    public bool ReadDnsSettings { get; init; } = true;

    /// <inheritdoc />
    public unsafe IReadOnlyList<AdapterDetails> Read()
    {
        GET_ADAPTERS_ADDRESSES_FLAGS flags =
            GET_ADAPTERS_ADDRESSES_FLAGS.GAA_FLAG_INCLUDE_GATEWAYS
            | GET_ADAPTERS_ADDRESSES_FLAGS.GAA_FLAG_SKIP_ANYCAST
            | GET_ADAPTERS_ADDRESSES_FLAGS.GAA_FLAG_SKIP_MULTICAST;

        uint size = 16 * 1024;

        for (int attempt = 0; attempt < 4; attempt++)
        {
            IntPtr buffer = Marshal.AllocHGlobal((int)size);

            try
            {
                uint result = PInvoke.GetAdaptersAddresses(Unspecified, flags, null, (IP_ADAPTER_ADDRESSES_LH*)buffer, &size);

                if (result == BufferOverflow)
                {
                    continue;
                }

                return result == NoError ? Walk((IP_ADAPTER_ADDRESSES_LH*)buffer) : [];
            }
            finally
            {
                Marshal.FreeHGlobal(buffer);
            }
        }

        return [];
    }

    private unsafe List<AdapterDetails> Walk(IP_ADAPTER_ADDRESSES_LH* first)
    {
        List<AdapterDetails> adapters = [];
        IReadOnlyDictionary<IPAddress, string> encryptable = ReadDnsSettings
            ? DnsSettingsReader.ReadEncryptable()
            : new Dictionary<IPAddress, string>();
        int order = 0;

        for (IP_ADAPTER_ADDRESSES_LH* each = first; each is not null; each = each->Next)
        {
            string id = each->AdapterName.ToString() ?? string.Empty;
            string name = each->FriendlyName.ToString() ?? string.Empty;
            string description = each->Description.ToString() ?? string.Empty;
            uint tunnel = (uint)each->TunnelType;

            ReadOnlySpan<byte> mac = each->PhysicalAddress.AsReadOnlySpan()[..(int)Math.Min(each->PhysicalAddressLength, 8u)];
            DnsSettings dns = ReadDnsSettings ? DnsSettingsReader.Read(id, encryptable) : DnsSettings.None;

            List<AdapterAddress> addresses = [];

            for (IP_ADAPTER_UNICAST_ADDRESS_LH* unicast = each->FirstUnicastAddress; unicast is not null; unicast = unicast->Next)
            {
                if (Address(unicast->Address) is not { } address)
                {
                    continue;
                }

                addresses.Add(new AdapterAddress(
                    address,
                    unicast->OnLinkPrefixLength,
                    AddressScopes.Of(address),
                    Preferred: unicast->DadState == NL_DAD_STATE.IpDadStatePreferred,
                    // IPv4 link-local addresses are random too, but only IPv6
                    // has the rotating privacy addresses this flag means.
                    Temporary: address.AddressFamily == System.Net.Sockets.AddressFamily.InterNetworkV6 && unicast->SuffixOrigin == NL_SUFFIX_ORIGIN.IpSuffixOriginRandom,
                    FromDhcp: unicast->PrefixOrigin == NL_PREFIX_ORIGIN.IpPrefixOriginDhcp));
            }

            List<IPAddress> gateways = [];

            for (IP_ADAPTER_GATEWAY_ADDRESS_LH* gateway = each->FirstGatewayAddress; gateway is not null; gateway = gateway->Next)
            {
                if (Address(gateway->Address) is { } address && AddressScopes.Of(address) != AddressScope.Unspecified)
                {
                    gateways.Add(address);
                }
            }

            List<DnsServer> servers = [];

            for (IP_ADAPTER_DNS_SERVER_ADDRESS_XP* server = each->FirstDnsServerAddress; server is not null; server = server->Next)
            {
                if (Address(server->Address) is { } address && DnsSettings.IsReal(address))
                {
                    servers.Add(dns.Describe(address, ReadDnsSettings));
                }
            }

            IPAddress? dhcpServer = each->Dhcpv4Enabled ? Address(each->Dhcpv4Server) : null;
            AdapterState state = AdapterClassifier.State((uint)each->OperStatus);

            // An adapter that is down keeps reporting its last router, DNS
            // servers and link speed (the disconnected Wi-Fi adapter did, on
            // 2026-10-05); none of it is true of a link that is not there.
            bool live = state == AdapterState.Up;

            adapters.Add(new AdapterDetails(
                id,
                each->Luid.Value,
                each->IfIndex,
                name,
                description,
                AdapterClassifier.Classify(each->IfType, tunnel, description, name),
                each->IfType,
                tunnel,
                state,
                AdapterClassifier.HardwareAddress(mac),
                AdapterClassifier.Mtu(each->Mtu),
                live ? AdapterClassifier.Speed(each->TransmitLinkSpeed) : null,
                live ? AdapterClassifier.Speed(each->ReceiveLinkSpeed) : null,
                addresses,
                live ? gateways : [],
                live ? servers : [],
                each->DnsSuffix.ToString() ?? string.Empty,
                each->Dhcpv4Enabled,
                live && dhcpServer is not null && AddressScopes.Of(dhcpServer) != AddressScope.Unspecified ? dhcpServer : null,
                each->Ipv4Metric,
                each->Ipv6Metric,
                order++));
        }

        return adapters;
    }

    private static unsafe IPAddress? Address(SOCKET_ADDRESS socket)
    {
        if (socket.lpSockaddr is null || socket.iSockaddrLength <= 0)
        {
            return null;
        }

        return SocketAddressReader.Read(new ReadOnlySpan<byte>(socket.lpSockaddr, Math.Min(socket.iSockaddrLength, 128)));
    }
}
