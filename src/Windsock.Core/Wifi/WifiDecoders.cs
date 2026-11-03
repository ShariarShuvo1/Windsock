using System.Buffers.Binary;
using System.Text;

namespace Windsock.Core.Wifi;

/// <summary>One radio link of a Wi-Fi connection. A Wi-Fi 7 multi-link connection has several.</summary>
/// <param name="LinkId">The link's id within the connection.</param>
/// <param name="FrequencyMhz">The channel's centre frequency.</param>
/// <param name="WidthMhz">The channel width, or 0 when Windows gave none that Wi-Fi uses.</param>
/// <param name="Rssi">Received signal strength in dBm.</param>
/// <param name="RatesMbps">The rates the link supports, in megabits a second.</param>
public sealed record WifiLink(int LinkId, double FrequencyMhz, int WidthMhz, int Rssi, IReadOnlyList<double> RatesMbps)
{
    /// <summary>The band the link is in.</summary>
    public WifiBand Band => WifiBands.BandOf(FrequencyMhz);

    /// <summary>The link's channel number.</summary>
    public int Channel => WifiBands.ChannelOf(FrequencyMhz);
}

/// <summary>
/// What Windows' realtime connection quality says about the Wi-Fi link: the
/// opcode Microsoft documents as needing no location permission.
/// </summary>
/// <param name="Phy">The physical layer, which gives the Wi-Fi generation.</param>
/// <param name="LinkQuality">Windows' quality figure, 0 to 100.</param>
/// <param name="ReceiveKbps">The receive rate in kilobits a second, or nothing when it is not a believable one.</param>
/// <param name="TransmitKbps">The transmit rate in kilobits a second, or nothing.</param>
/// <param name="IsMultiLink">Whether this is a Wi-Fi 7 multi-link connection.</param>
/// <param name="Links">The radio links.</param>
public sealed record WifiLinkQuality(
    WifiPhy Phy,
    uint LinkQuality,
    long? ReceiveKbps,
    long? TransmitKbps,
    bool IsMultiLink,
    IReadOnlyList<WifiLink> Links)
{
    /// <summary>The link with the strongest signal, which is the one figures are quoted for.</summary>
    public WifiLink? Best => Links.Count == 0 ? null : Links.MaxBy(each => each.Rssi);

    /// <summary>The signal in dBm: the best link's, or one worked out from the quality figure.</summary>
    public int Rssi => Best?.Rssi ?? WifiBands.RssiFromQuality(LinkQuality);
}

/// <summary>What the current-connection opcode says, which since Windows 11 24H2 needs location permission.</summary>
/// <param name="ProfileName">The saved profile in use.</param>
/// <param name="Ssid">The network's name as bytes, usually UTF-8 but not always.</param>
/// <param name="Bssid">The access point's address.</param>
/// <param name="Phy">The physical layer.</param>
/// <param name="SignalQuality">Windows' quality figure, 0 to 100.</param>
/// <param name="ReceiveKbps">The receive rate.</param>
/// <param name="TransmitKbps">The transmit rate.</param>
/// <param name="SecurityEnabled">Whether the network is secured.</param>
/// <param name="Authentication">DOT11_AUTH_ALGORITHM.</param>
/// <param name="Cipher">DOT11_CIPHER_ALGORITHM.</param>
public sealed record WifiAssociation(
    string ProfileName,
    byte[] Ssid,
    string Bssid,
    WifiPhy Phy,
    uint SignalQuality,
    long? ReceiveKbps,
    long? TransmitKbps,
    bool SecurityEnabled,
    uint Authentication,
    uint Cipher)
{
    /// <summary>The network's name as text.</summary>
    public string Name => WifiDecoders.SsidText(Ssid);

    /// <summary>The security as people name it: "WPA2-Personal", "WPA3-Personal", "Open".</summary>
    public string SecurityName => WifiDecoders.AuthenticationName(Authentication, SecurityEnabled);
}

/// <summary>An adapter WLAN AutoConfig knows about.</summary>
/// <param name="Id">The adapter's GUID, the same as its network adapter id.</param>
/// <param name="Description">The adapter's description.</param>
/// <param name="State">WLAN_INTERFACE_STATE.</param>
public sealed record WlanInterface(Guid Id, string Description, uint State)
{
    /// <summary>wlan_interface_state_connected.</summary>
    public const uint Connected = 1;

    /// <summary>wlan_interface_state_disconnected.</summary>
    public const uint Disconnected = 4;

    /// <summary>Whether it is joined to a network.</summary>
    public bool IsConnected => State == Connected;

    /// <summary>Whether it is on its way to joining one: associating, discovering or authenticating.</summary>
    public bool IsConnecting => State is 5 or 6 or 7;
}

/// <summary>
/// Decodes the structures Native Wi-Fi hands back, from their bytes.
/// </summary>
/// <remarks>
/// Offsets are from wlanapi.h, windot11.h and l2cmn.h in SDK 10.0.26100.0, and
/// are checked against the same declarations as compiled by the metadata
/// CsWin32 reads (WifiLayoutTests). Decoding bytes rather than marshalling
/// structs means a layout mistake fails a test rather than reading a plausible
/// number from the wrong offset; every count is clamped to what the buffer
/// actually holds.
/// </remarks>
public static class WifiDecoders
{
    /// <summary>Size of WLAN_REALTIME_CONNECTION_QUALITY before its links.</summary>
    public const int RealtimeHeader = 24;

    /// <summary>Size of one WLAN_REALTIME_CONNECTION_QUALITY_LINK_INFO.</summary>
    public const int RealtimeLink = 272;

    /// <summary>Size of WLAN_RATE_SET: a length and 126 rates.</summary>
    public const int RateSet = 256;

    /// <summary>Size of WLAN_CONNECTION_ATTRIBUTES.</summary>
    public const int ConnectionAttributes = 604;

    /// <summary>Size of one WLAN_INTERFACE_INFO.</summary>
    public const int InterfaceInfo = 532;

    /// <summary>Size of DOT11_SSID: a length and 32 bytes.</summary>
    public const int Ssid = 36;

    private const int MostLinks = 16;

    /// <summary>Decodes opcode 19's WLAN_REALTIME_CONNECTION_QUALITY, or nothing for a buffer too short.</summary>
    public static WifiLinkQuality? RealtimeQuality(ReadOnlySpan<byte> bytes)
    {
        if (bytes.Length < RealtimeHeader)
        {
            return null;
        }

        uint phy = U32(bytes, 0);
        uint quality = U32(bytes, 4);
        uint receive = U32(bytes, 8);
        uint transmit = U32(bytes, 12);
        bool multiLink = U32(bytes, 16) != 0;
        uint stated = U32(bytes, 20);

        int fits = (bytes.Length - RealtimeHeader) / RealtimeLink;
        int count = (int)Math.Min(Math.Min(stated, (uint)fits), MostLinks);

        List<WifiLink> links = new(count);

        for (int each = 0; each < count; each++)
        {
            ReadOnlySpan<byte> link = bytes.Slice(RealtimeHeader + (each * RealtimeLink), RealtimeLink);

            links.Add(new WifiLink(
                link[0],
                WifiBands.Megahertz(U32(link, 4)),
                WifiBands.Width(U32(link, 8)),
                BinaryPrimitives.ReadInt32LittleEndian(link[12..]),
                Rates(link.Slice(16, RateSet))));
        }

        return new WifiLinkQuality(WifiBands.Phy(phy), Math.Min(quality, 100u), Rate(receive), Rate(transmit), multiLink, links);
    }

    /// <summary>Decodes opcode 7's WLAN_CONNECTION_ATTRIBUTES, or nothing for a buffer too short.</summary>
    public static WifiAssociation? ConnectionAttributesOf(ReadOnlySpan<byte> bytes)
    {
        if (bytes.Length < ConnectionAttributes)
        {
            return null;
        }

        string profile = WideText(bytes.Slice(8, 512));
        ReadOnlySpan<byte> association = bytes.Slice(520, 68);
        ReadOnlySpan<byte> security = bytes.Slice(588, 16);

        return new WifiAssociation(
            profile,
            SsidBytes(association[..Ssid]),
            Mac(association.Slice(40, 6)),
            WifiBands.Phy(U32(association, 48)),
            Math.Min(U32(association, 56), 100u),
            Rate(U32(association, 60)),
            Rate(U32(association, 64)),
            U32(security, 0) != 0,
            U32(security, 8),
            U32(security, 12));
    }

    /// <summary>Decodes a WLAN_INTERFACE_INFO_LIST.</summary>
    public static IReadOnlyList<WlanInterface> Interfaces(ReadOnlySpan<byte> bytes)
    {
        if (bytes.Length < 8)
        {
            return [];
        }

        uint stated = U32(bytes, 0);
        int fits = (bytes.Length - 8) / InterfaceInfo;
        int count = (int)Math.Min(stated, (uint)fits);
        List<WlanInterface> found = new(count);

        for (int each = 0; each < count; each++)
        {
            ReadOnlySpan<byte> info = bytes.Slice(8 + (each * InterfaceInfo), InterfaceInfo);
            found.Add(new WlanInterface(new Guid(info[..16]), WideText(info.Slice(16, 512)), U32(info, 528)));
        }

        return found;
    }

    /// <summary>The bytes of a DOT11_SSID, clamped to 32.</summary>
    public static byte[] SsidBytes(ReadOnlySpan<byte> dot11Ssid)
    {
        if (dot11Ssid.Length < 4)
        {
            return [];
        }

        int length = (int)Math.Min(U32(dot11Ssid, 0), 32u);
        return dot11Ssid.Slice(4, Math.Min(length, dot11Ssid.Length - 4)).ToArray();
    }

    /// <summary>
    /// A network name as text: UTF-8 when the bytes are valid UTF-8, which nearly
    /// all are; otherwise Latin-1, so every byte still shows as something.
    /// </summary>
    public static string SsidText(ReadOnlySpan<byte> ssid)
    {
        if (ssid.IsEmpty)
        {
            return string.Empty;
        }

        try
        {
            return new UTF8Encoding(false, throwOnInvalidBytes: true).GetString(ssid);
        }
        catch (DecoderFallbackException)
        {
            return Encoding.Latin1.GetString(ssid);
        }
    }

    /// <summary>A hardware address as "aa:bb:cc:dd:ee:ff".</summary>
    public static string Mac(ReadOnlySpan<byte> bytes) =>
        string.Join(':', bytes.ToArray().Select(each => each.ToString("x2", System.Globalization.CultureInfo.InvariantCulture)));

    /// <summary>
    /// The rates in a WLAN_RATE_SET, in Mbit/s: each is in half-megabit units,
    /// with the top bit marking a basic rate.
    /// </summary>
    public static IReadOnlyList<double> Rates(ReadOnlySpan<byte> rateSet)
    {
        if (rateSet.Length < 4)
        {
            return [];
        }

        // The length is in bytes; each rate is a USHORT.
        int count = (int)Math.Min(U32(rateSet, 0) / 2, Math.Min(126u, (uint)(rateSet.Length - 4) / 2));
        double[] rates = new double[count];

        for (int each = 0; each < count; each++)
        {
            rates[each] = (BinaryPrimitives.ReadUInt16LittleEndian(rateSet[(4 + (each * 2))..]) & 0x7FFF) * 0.5;
        }

        return rates;
    }

    /// <summary>
    /// A link rate in Kbit/s, as WLAN_ASSOCIATION_ATTRIBUTES documents it and as
    /// the realtime struct is assumed to use too, or nothing when it cannot be a
    /// real one: under 1,000 (a link under a megabit, which no Wi-Fi standard
    /// runs at) or over 100 Gbit/s.
    /// </summary>
    /// <remarks>
    /// One unit for every value. Reading a value under 1,000 as Mbit/s instead
    /// would have turned a misread 900 into 900 Mbit/s; a dash says less, and
    /// nothing false. The realtime struct's unit is not verified against a
    /// live Wi-Fi link: the first connected run should compare it with
    /// Windows' own link speed.
    /// </remarks>
    public static long? Rate(uint value) => value is >= 1000 and <= 100_000_000 ? value : null;

    /// <summary>The security of a connection as people name it.</summary>
    /// <remarks>DOT11_AUTH_ALGORITHM values from windot11.h.</remarks>
    public static string AuthenticationName(uint algorithm, bool securityEnabled) => algorithm switch
    {
        1 when !securityEnabled => "Open",
        1 => "WEP",
        2 => "WEP (shared key)",
        3 => "WPA-Enterprise",
        4 => "WPA-Personal",
        6 => "WPA2-Enterprise",
        7 => "WPA2-Personal",
        8 => "WPA3-Enterprise 192-bit",
        9 => "WPA3-Personal",
        10 => "Enhanced Open",
        11 => "WPA3-Enterprise",
        _ => "—",
    };

    private static uint U32(ReadOnlySpan<byte> bytes, int at) => BinaryPrimitives.ReadUInt32LittleEndian(bytes[at..]);

    private static string WideText(ReadOnlySpan<byte> bytes)
    {
        string text = Encoding.Unicode.GetString(bytes);
        int end = text.IndexOf('\0', StringComparison.Ordinal);
        return end < 0 ? text : text[..end];
    }
}
