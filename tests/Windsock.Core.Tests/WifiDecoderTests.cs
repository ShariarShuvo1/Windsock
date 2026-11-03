using System.Buffers.Binary;
using System.Runtime.CompilerServices;
using System.Text;
using Windows.Win32.NetworkManagement.WiFi;
using Windsock.Core.Wifi;
using Xunit;

namespace Windsock.Core.Tests;

/// <summary>
/// The Native Wi-Fi decoders, against buffers laid out byte by byte from
/// wlanapi.h, and those layouts against the declarations CsWin32 compiles
/// from Microsoft's metadata. Nothing here needs a Wi-Fi adapter: the
/// development machine's was disconnected, so these are the only proof the
/// decoding is right until someone runs it on a connected one.
/// </summary>
public sealed class WifiDecoderTests
{
    private static readonly double[] BasicRates = [6, 9, 12];

    [Fact]
    public void RealtimeQuality_Layout_MatchesTheHeader()
    {
        WLAN_REALTIME_CONNECTION_QUALITY quality = default;
        ref byte start = ref Unsafe.As<WLAN_REALTIME_CONNECTION_QUALITY, byte>(ref quality);

        Assert.Equal(4, (int)Unsafe.ByteOffset(ref start, ref Unsafe.As<uint, byte>(ref quality.ulLinkQuality)));
        Assert.Equal(8, (int)Unsafe.ByteOffset(ref start, ref Unsafe.As<uint, byte>(ref quality.ulRxRate)));
        Assert.Equal(12, (int)Unsafe.ByteOffset(ref start, ref Unsafe.As<uint, byte>(ref quality.ulTxRate)));
        Assert.Equal(20, (int)Unsafe.ByteOffset(ref start, ref Unsafe.As<uint, byte>(ref quality.ulNumLinks)));
        Assert.Equal(WifiDecoders.RealtimeHeader, (int)Unsafe.ByteOffset(ref start, ref Unsafe.As<Windows.Win32.VariableLengthInlineArray<WLAN_REALTIME_CONNECTION_QUALITY_LINK_INFO>, byte>(ref quality.linksInfo)));
    }

    [Fact]
    public void RealtimeLink_Layout_MatchesTheHeader()
    {
        WLAN_REALTIME_CONNECTION_QUALITY_LINK_INFO link = default;
        ref byte start = ref Unsafe.As<WLAN_REALTIME_CONNECTION_QUALITY_LINK_INFO, byte>(ref link);

        Assert.Equal(WifiDecoders.RealtimeLink, Unsafe.SizeOf<WLAN_REALTIME_CONNECTION_QUALITY_LINK_INFO>());
        Assert.Equal(WifiDecoders.RateSet, Unsafe.SizeOf<WLAN_RATE_SET>());
        Assert.Equal(4, (int)Unsafe.ByteOffset(ref start, ref Unsafe.As<uint, byte>(ref link.ulChannelCenterFrequencyMhz)));
        Assert.Equal(8, (int)Unsafe.ByteOffset(ref start, ref Unsafe.As<uint, byte>(ref link.ulBandwidth)));
        Assert.Equal(12, (int)Unsafe.ByteOffset(ref start, ref Unsafe.As<int, byte>(ref link.lRssi)));
        Assert.Equal(16, (int)Unsafe.ByteOffset(ref start, ref Unsafe.As<WLAN_RATE_SET, byte>(ref link.wlanRateSet)));
    }

    [Fact]
    public void ConnectionAttributes_Layout_MatchesTheHeader()
    {
        WLAN_CONNECTION_ATTRIBUTES attributes = default;
        ref byte start = ref Unsafe.As<WLAN_CONNECTION_ATTRIBUTES, byte>(ref attributes);

        Assert.Equal(WifiDecoders.ConnectionAttributes, Unsafe.SizeOf<WLAN_CONNECTION_ATTRIBUTES>());
        Assert.Equal(520, (int)Unsafe.ByteOffset(ref start, ref Unsafe.As<WLAN_ASSOCIATION_ATTRIBUTES, byte>(ref attributes.wlanAssociationAttributes)));
        Assert.Equal(588, (int)Unsafe.ByteOffset(ref start, ref Unsafe.As<WLAN_SECURITY_ATTRIBUTES, byte>(ref attributes.wlanSecurityAttributes)));

        WLAN_ASSOCIATION_ATTRIBUTES association = default;
        ref byte inner = ref Unsafe.As<WLAN_ASSOCIATION_ATTRIBUTES, byte>(ref association);

        Assert.Equal(40, (int)Unsafe.ByteOffset(ref inner, ref Unsafe.As<Windows.Win32.__byte_6, byte>(ref association.dot11Bssid)));
        Assert.Equal(56, (int)Unsafe.ByteOffset(ref inner, ref Unsafe.As<uint, byte>(ref association.wlanSignalQuality)));
        Assert.Equal(60, (int)Unsafe.ByteOffset(ref inner, ref Unsafe.As<uint, byte>(ref association.ulRxRate)));
    }

    [Fact]
    public void BssEntry_Layout_MatchesTheHeader()
    {
        WLAN_BSS_ENTRY entry = default;
        ref byte start = ref Unsafe.As<WLAN_BSS_ENTRY, byte>(ref entry);

        Assert.Equal(NearbyNetworks.EntrySize, Unsafe.SizeOf<WLAN_BSS_ENTRY>());
        Assert.Equal(52, (int)Unsafe.ByteOffset(ref start, ref Unsafe.As<DOT11_PHY_TYPE, byte>(ref entry.dot11BssPhyType)));
        Assert.Equal(56, (int)Unsafe.ByteOffset(ref start, ref Unsafe.As<int, byte>(ref entry.lRssi)));
        Assert.Equal(92, (int)Unsafe.ByteOffset(ref start, ref Unsafe.As<uint, byte>(ref entry.ulChCenterFrequency)));
        Assert.Equal(352, (int)Unsafe.ByteOffset(ref start, ref Unsafe.As<uint, byte>(ref entry.ulIeOffset)));
        Assert.Equal(356, (int)Unsafe.ByteOffset(ref start, ref Unsafe.As<uint, byte>(ref entry.ulIeSize)));
    }

    [Fact]
    public void InterfaceInfo_IsGuidNameAndState()
    {
        Assert.Equal(WifiDecoders.InterfaceInfo, Unsafe.SizeOf<WLAN_INTERFACE_INFO>());
    }

    [Fact]
    public void RealtimeQuality_OneLink_ReadsEveryField()
    {
        byte[] buffer = new byte[WifiDecoders.RealtimeHeader + WifiDecoders.RealtimeLink];
        U32(buffer, 0, 10);            // HE, Wi-Fi 6
        U32(buffer, 4, 84);            // quality
        U32(buffer, 8, 1_200_950);     // Kbit/s
        U32(buffer, 12, 864_800);
        U32(buffer, 16, 0);            // not MLO
        U32(buffer, 20, 1);
        Link(buffer, 0, id: 0, frequency: 5180, width: 80, rssi: -54, 12, 18, 24);

        WifiLinkQuality quality = WifiDecoders.RealtimeQuality(buffer)!;

        Assert.Equal(WifiPhy.He, quality.Phy);
        Assert.Equal(84u, quality.LinkQuality);
        Assert.Equal(1_200_950, quality.ReceiveKbps);
        Assert.Equal(864_800, quality.TransmitKbps);
        Assert.False(quality.IsMultiLink);

        WifiLink link = Assert.Single(quality.Links);
        Assert.Equal(5180, link.FrequencyMhz);
        Assert.Equal(WifiBand.Five, link.Band);
        Assert.Equal(36, link.Channel);
        Assert.Equal(80, link.WidthMhz);
        Assert.Equal(-54, link.Rssi);
        Assert.Equal(BasicRates, link.RatesMbps);
    }

    [Fact]
    public void RealtimeQuality_MultiLink_ReadsEachLinkAndPicksTheStrongest()
    {
        byte[] buffer = new byte[WifiDecoders.RealtimeHeader + (2 * WifiDecoders.RealtimeLink)];
        U32(buffer, 0, 11);            // EHT, Wi-Fi 7
        U32(buffer, 4, 70);
        U32(buffer, 8, 2_882_000);
        U32(buffer, 12, 2_402_000);
        U32(buffer, 16, 1);
        U32(buffer, 20, 2);
        Link(buffer, 0, id: 1, frequency: 5_955_000, width: 320, rssi: -61);   // in kHz, as the docs say
        Link(buffer, 1, id: 2, frequency: 5745, width: 160, rssi: -58);

        WifiLinkQuality quality = WifiDecoders.RealtimeQuality(buffer)!;

        Assert.True(quality.IsMultiLink);
        Assert.Equal(2, quality.Links.Count);
        Assert.Equal(WifiBand.Six, quality.Links[0].Band);
        Assert.Equal(1, quality.Links[0].Channel);
        Assert.Equal(320, quality.Links[0].WidthMhz);
        Assert.Equal(149, quality.Links[1].Channel);
        Assert.Equal(2, quality.Best!.LinkId);
        Assert.Equal(-58, quality.Rssi);
        Assert.Equal("Wi-Fi 7", WifiBands.Generation(quality.Phy, quality.Best.Band));
    }

    [Fact]
    public void RealtimeQuality_ClaimingMoreLinksThanFit_ReadsOnlyWhatIsThere()
    {
        byte[] buffer = new byte[WifiDecoders.RealtimeHeader + WifiDecoders.RealtimeLink];
        U32(buffer, 0, 8);
        U32(buffer, 20, 9);
        Link(buffer, 0, 0, 2437, 20, -70);

        Assert.Single(WifiDecoders.RealtimeQuality(buffer)!.Links);
        Assert.Null(WifiDecoders.RealtimeQuality(new byte[10]));
    }

    [Fact]
    public void RealtimeQuality_NoLinks_WorksOutTheSignalFromTheQuality()
    {
        byte[] buffer = new byte[WifiDecoders.RealtimeHeader];
        U32(buffer, 4, 60);

        Assert.Equal(-70, WifiDecoders.RealtimeQuality(buffer)!.Rssi);
    }

    [Fact]
    public void ConnectionAttributes_ReadsProfileSsidBssidAndSecurity()
    {
        byte[] buffer = new byte[WifiDecoders.ConnectionAttributes];
        U32(buffer, 0, 1);
        Encoding.Unicode.GetBytes("Home profile").CopyTo(buffer, 8);
        byte[] ssid = Encoding.UTF8.GetBytes("Café-5G");
        U32(buffer, 520, (uint)ssid.Length);
        ssid.CopyTo(buffer, 524);
        new byte[] { 0x02, 0x1a, 0x2b, 0x3c, 0x4d, 0x5e }.CopyTo(buffer, 520 + 40);
        U32(buffer, 520 + 48, 10);
        U32(buffer, 520 + 56, 92);
        U32(buffer, 520 + 60, 573_500);
        U32(buffer, 520 + 64, 286_700);
        U32(buffer, 588, 1);
        U32(buffer, 596, 9);           // WPA3-SAE
        U32(buffer, 600, 4);

        WifiAssociation association = WifiDecoders.ConnectionAttributesOf(buffer)!;

        Assert.Equal("Home profile", association.ProfileName);
        Assert.Equal("Café-5G", association.Name);
        Assert.Equal("02:1a:2b:3c:4d:5e", association.Bssid);
        Assert.Equal(WifiPhy.He, association.Phy);
        Assert.Equal(92u, association.SignalQuality);
        Assert.Equal(573_500, association.ReceiveKbps);
        Assert.Equal("WPA3-Personal", association.SecurityName);
    }

    [Fact]
    public void Interfaces_ReadsGuidDescriptionAndState()
    {
        Guid id = Guid.Parse("4b0a0962-1dc4-4864-8a1c-21cd6bcd1853");
        byte[] buffer = new byte[8 + WifiDecoders.InterfaceInfo];
        U32(buffer, 0, 1);
        id.TryWriteBytes(buffer.AsSpan(8));
        Encoding.Unicode.GetBytes("Realtek 8811CU Wireless LAN 802.11ac USB NIC").CopyTo(buffer, 8 + 16);
        U32(buffer, 8 + 528, 4);

        WlanInterface adapter = Assert.Single(WifiDecoders.Interfaces(buffer));

        Assert.Equal(id, adapter.Id);
        Assert.StartsWith("Realtek", adapter.Description, StringComparison.Ordinal);
        Assert.False(adapter.IsConnected);
    }

    [Theory]
    [InlineData(0u, null)]
    [InlineData(866u, null)]
    [InlineData(1_000u, 1_000L)]
    [InlineData(866_700u, 866_700L)]
    [InlineData(200_000_000u, null)]
    public void Rate_IsKilobitsAlways_AndImpossibleRatesAreNothing(uint value, long? expected)
    {
        Assert.Equal(expected, WifiDecoders.Rate(value));
    }

    [Fact]
    public void SsidText_KeepsBytesThatAreNotUtf8()
    {
        Assert.Equal("Café", WifiDecoders.SsidText([0x43, 0x61, 0x66, 0xE9]));
        Assert.Equal("Café", WifiDecoders.SsidText(Encoding.UTF8.GetBytes("Café")));
    }

    [Theory]
    [InlineData(2412, WifiBand.TwoPointFour, 1)]
    [InlineData(2484, WifiBand.TwoPointFour, 14)]
    [InlineData(5180, WifiBand.Five, 36)]
    [InlineData(5825, WifiBand.Five, 165)]
    [InlineData(5935, WifiBand.Six, 2)]
    [InlineData(5955, WifiBand.Six, 1)]
    [InlineData(7115, WifiBand.Six, 233)]
    public void Channels_FollowTheKernelsArithmetic(int megahertz, WifiBand band, int channel)
    {
        Assert.Equal(band, WifiBands.BandOf(megahertz));
        Assert.Equal(channel, WifiBands.ChannelOf(megahertz));
        Assert.Equal(megahertz, WifiBands.FrequencyOf(band, channel));
    }

    [Theory]
    [InlineData(WifiBand.Five, 36, 80, 42)]
    [InlineData(WifiBand.Five, 48, 80, 42)]
    [InlineData(WifiBand.Five, 149, 80, 155)]
    [InlineData(WifiBand.Five, 100, 160, 114)]
    [InlineData(WifiBand.Five, 44, 40, 46)]
    [InlineData(WifiBand.Six, 37, 80, 39)]
    [InlineData(WifiBand.Six, 1, 320, 31)]
    [InlineData(WifiBand.Six, 5, 40, 3)]
    [InlineData(WifiBand.TwoPointFour, 6, 40, 6)]
    public void BlockCentre_FindsTheBondedBlock(WifiBand band, int channel, int width, int centre)
    {
        Assert.Equal(centre, WifiBands.BlockCentre(band, channel, width));
    }

    [Theory]
    [InlineData(-45, 4)]
    [InlineData(-60, 4)]
    [InlineData(-65, 3)]
    [InlineData(-72, 2)]
    [InlineData(-80, 1)]
    [InlineData(-90, 0)]
    public void Bars_FollowTheSignal(int rssi, int bars)
    {
        Assert.Equal(bars, WifiBands.Bars(rssi));
    }

    [Theory]
    [InlineData(WifiPhy.He, WifiBand.Six, "Wi-Fi 6E")]
    [InlineData(WifiPhy.He, WifiBand.Five, "Wi-Fi 6")]
    [InlineData(WifiPhy.Vht, WifiBand.Five, "Wi-Fi 5")]
    [InlineData(WifiPhy.Ht, WifiBand.TwoPointFour, "Wi-Fi 4")]
    public void Generation_NamesItAsPeopleDo(WifiPhy phy, WifiBand band, string name)
    {
        Assert.Equal(name, WifiBands.Generation(phy, band));
    }

    [Fact]
    public void BssList_ReadsEntriesAndTheirElements()
    {
        // Two entries: a Wi-Fi 6 network on 5 GHz channel 36 at 80 MHz with a
        // BSS Load element, and a hidden Wi-Fi 4 network on 2.4 GHz channel 6
        // at 40 MHz (secondary above).
        byte[] heElements =
        [
            0, 4, (byte)'H', (byte)'o', (byte)'m', (byte)'e',
            11, 5, 0x0C, 0x00, 0x80, 0x00, 0x00,            // 12 stations, 128/255 busy
            61, 22, 36, 0x05, .. new byte[20],             // HT operation, primary 36, secondary above, any width
            192, 5, 1, 42, 0, 0, 0,                         // VHT operation: 80 MHz centred on 42
            255, 7, 36, 0, 0, 0, 0, 0, 0,                   // HE operation without 6 GHz info
        ];

        byte[] htElements =
        [
            0, 0,
            45, 26, .. new byte[26],                        // HT capabilities
            61, 22, 6, 0x05, .. new byte[20],              // HT operation: primary 6, secondary above
        ];

        byte[] buffer = BssList(
            (Mac: [1, 2, 3, 4, 5, 6], Rssi: -48, FrequencyKhz: 5_180_000u, Phy: 10u, Elements: heElements),
            (Mac: [6, 5, 4, 3, 2, 1], Rssi: -77, FrequencyKhz: 2_437_000u, Phy: 7u, Elements: htElements));

        IReadOnlyList<NearbyNetwork> networks = NearbyNetworks.Decode(buffer);

        Assert.Equal(2, networks.Count);

        NearbyNetwork first = networks[0];
        Assert.Equal("Home", first.Name);
        Assert.Equal("01:02:03:04:05:06", first.Bssid);
        Assert.Equal(-48, first.Rssi);
        Assert.Equal(36, first.Channel);
        Assert.Equal(WifiPhy.He, first.Generation);
        Assert.Equal(80, first.WidthMhz);
        Assert.Equal(42, first.CentreChannel);
        Assert.Equal(12, first.StationCount);
        Assert.Equal(128 / 255d, first.Utilisation!.Value, 6);
        Assert.Equal((5170d, 5250d), first.Span);

        NearbyNetwork second = networks[1];
        Assert.Equal(string.Empty, second.Name);
        Assert.Equal(6, second.Channel);
        Assert.Equal(WifiPhy.Ht, second.Generation);
        Assert.Equal(40, second.WidthMhz);
        Assert.Equal(8, second.CentreChannel);
    }

    [Fact]
    public void BssList_ElementsRunningPastTheBuffer_AreIgnored()
    {
        byte[] buffer = BssList((Mac: [1, 1, 1, 1, 1, 1], Rssi: -60, FrequencyKhz: 2_412_000u, Phy: 6u, Elements: [0, 2, 65, 66]));

        // Claim a far larger element blob than the list holds.
        BinaryPrimitives.WriteUInt32LittleEndian(buffer.AsSpan(8 + 356), 5000);

        NearbyNetwork network = Assert.Single(NearbyNetworks.Decode(buffer));
        Assert.Equal(20, network.WidthMhz);
    }

    [Fact]
    public void HeOperation_ReadsSixGigahertzWidth()
    {
        // Parameters with the 6 GHz information bit (0x00020000), MCS, then
        // primary 37, control 2 (80 MHz), CCFS0 39, CCFS1 0, minimum rate.
        byte[] body = [0x00, 0x00, 0x02, 0x00, 0x00, 0x00, 37, 2, 39, 0, 0];

        Assert.Equal((80, 39), NearbyNetworks.HeWidth(body));

        // With VHT information (bit 14) and a co-hosted byte (bit 15) before it.
        byte[] longer = [0x00, 0xC0, 0x02, 0x00, 0x00, 0x00, 0, 0, 0, 0, 37, 3, 39, 47, 0];
        Assert.Equal((160, 47), NearbyNetworks.HeWidth(longer));
    }

    [Fact]
    public void EhtOperation_ReadsThreeHundredAndTwenty()
    {
        byte[] body = [0x01, 0, 0, 0, 0, 0x04, 15, 31];

        Assert.Equal((320, 31), NearbyNetworks.EhtWidth(body));
        Assert.Equal((0, 0), NearbyNetworks.EhtWidth([0x00, 0, 0, 0, 0, 0x04, 15, 31]));
    }

    [Theory]
    [InlineData(1, 42, 50, 160, 50)]
    [InlineData(1, 42, 0, 80, 42)]
    [InlineData(1, 42, 155, 80, 42)]
    [InlineData(0, 0, 0, 0, 0)]
    public void VhtOperation_ReadsWidths(byte width, byte segment0, byte segment1, int expectedWidth, int expectedCentre)
    {
        Assert.Equal((expectedWidth, expectedCentre), NearbyNetworks.VhtWidth(width, segment0, segment1));
    }

    private static void U32(byte[] buffer, int at, uint value) => BinaryPrimitives.WriteUInt32LittleEndian(buffer.AsSpan(at), value);

    private static void Link(byte[] buffer, int index, byte id, uint frequency, uint width, int rssi, params ushort[] halfMegabitRates)
    {
        int at = WifiDecoders.RealtimeHeader + (index * WifiDecoders.RealtimeLink);
        buffer[at] = id;
        U32(buffer, at + 4, frequency);
        U32(buffer, at + 8, width);
        BinaryPrimitives.WriteInt32LittleEndian(buffer.AsSpan(at + 12), rssi);
        U32(buffer, at + 16, (uint)(halfMegabitRates.Length * 2));

        for (int each = 0; each < halfMegabitRates.Length; each++)
        {
            BinaryPrimitives.WriteUInt16LittleEndian(buffer.AsSpan(at + 20 + (each * 2)), halfMegabitRates[each]);
        }
    }

    private static byte[] BssList(params (byte[] Mac, int Rssi, uint FrequencyKhz, uint Phy, byte[] Elements)[] entries)
    {
        int size = 8 + (entries.Length * NearbyNetworks.EntrySize) + entries.Sum(each => each.Elements.Length);
        byte[] buffer = new byte[size];
        U32(buffer, 0, (uint)size);
        U32(buffer, 4, (uint)entries.Length);

        int elementsAt = 8 + (entries.Length * NearbyNetworks.EntrySize);

        for (int each = 0; each < entries.Length; each++)
        {
            int at = 8 + (each * NearbyNetworks.EntrySize);
            (byte[] mac, int rssi, uint frequency, uint phy, byte[] elements) = entries[each];

            // The SSID element, when the elements start with one, also fills DOT11_SSID.
            if (elements.Length >= 2 && elements[0] == 0)
            {
                U32(buffer, at, elements[1]);
                elements.AsSpan(2, elements[1]).CopyTo(buffer.AsSpan(at + 4));
            }

            mac.CopyTo(buffer, at + 40);
            U32(buffer, at + 48, 1);
            U32(buffer, at + 52, phy);
            BinaryPrimitives.WriteInt32LittleEndian(buffer.AsSpan(at + 56), rssi);
            U32(buffer, at + 60, 80);
            U32(buffer, at + 92, frequency);
            U32(buffer, at + 352, (uint)(elementsAt - at));
            U32(buffer, at + 356, (uint)elements.Length);
            elements.CopyTo(buffer, elementsAt);
            elementsAt += elements.Length;
        }

        return buffer;
    }
}
