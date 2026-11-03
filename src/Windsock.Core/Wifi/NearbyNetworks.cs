using System.Buffers.Binary;

namespace Windsock.Core.Wifi;

/// <summary>One access point heard in a scan.</summary>
/// <param name="Ssid">Its network name as bytes; empty for a hidden network.</param>
/// <param name="Bssid">Its radio's address.</param>
/// <param name="Rssi">How strongly it was heard, in dBm.</param>
/// <param name="LinkQuality">Windows' quality figure for it, 0 to 100.</param>
/// <param name="FrequencyMhz">The centre of its primary channel.</param>
/// <param name="Phy">The physical layer Windows reports for it.</param>
/// <param name="Generation">The newest generation its beacon advertises, from its elements.</param>
/// <param name="WidthMhz">The channel width its beacon declares, 20 when it declares none.</param>
/// <param name="CentreChannel">The centre of its whole (possibly bonded) channel, or its primary channel.</param>
/// <param name="StationCount">How many devices it says are joined to it, when it says.</param>
/// <param name="Utilisation">How busy it says its channel is, 0 to 1, when it says.</param>
public sealed record NearbyNetwork(
    byte[] Ssid,
    string Bssid,
    int Rssi,
    uint LinkQuality,
    double FrequencyMhz,
    WifiPhy Phy,
    WifiPhy Generation,
    int WidthMhz,
    int CentreChannel,
    int? StationCount,
    double? Utilisation)
{
    /// <summary>The network's name as text, empty for a hidden network.</summary>
    public string Name => WifiDecoders.SsidText(Ssid);

    /// <summary>The band.</summary>
    public WifiBand Band => WifiBands.BandOf(FrequencyMhz);

    /// <summary>The primary channel.</summary>
    public int Channel => WifiBands.ChannelOf(FrequencyMhz);

    /// <summary>The lowest and highest frequencies it occupies, in MHz.</summary>
    public (double Low, double High) Span
    {
        get
        {
            double centre = CentreChannel > 0 ? WifiBands.FrequencyOf(Band, CentreChannel) : FrequencyMhz;

            if (centre <= 0)
            {
                centre = FrequencyMhz;
            }

            int width = WidthMhz > 0 ? WidthMhz : 20;

            // 2.4 GHz channels are 5 MHz apart but each transmission is about
            // 20 MHz wide, which is why only 1, 6 and 11 stay out of each other's way.
            return (centre - (width / 2d), centre + (width / 2d));
        }
    }
}

/// <summary>What a beacon's information elements say.</summary>
/// <param name="Generation">The newest generation advertised.</param>
/// <param name="WidthMhz">The operating width declared, 20 when none.</param>
/// <param name="CentreChannel">The centre channel of the operating width, or 0 when it is the primary.</param>
/// <param name="StationCount">From the BSS Load element.</param>
/// <param name="Utilisation">From the BSS Load element, 0 to 1.</param>
public readonly record struct BeaconFacts(WifiPhy Generation, int WidthMhz, int CentreChannel, int? StationCount, double? Utilisation);

/// <summary>Decodes a scan's WLAN_BSS_LIST and the elements of each beacon.</summary>
/// <remarks>
/// <para>
/// WLAN_BSS_LIST is a total size and a count, then WLAN_BSS_ENTRY records of
/// 360 bytes each; an entry's information elements sit at its own offset
/// <c>ulIeOffset</c> from the start of that entry, <c>ulIeSize</c> long (wlanapi.h).
/// Beacons come from radios nobody vouches for, so every length is checked
/// against the buffer.
/// </para>
/// <para>
/// Element layouts are the Linux kernel's (include/linux/ieee80211*.h, read
/// 2026-10-05): HT Operation (id 61) carries the secondary channel offset in
/// the low two bits of its second byte and "any width" in bit 2; VHT Operation
/// (id 192) starts with a width byte (0 use HT, 1 80/160/80+80, 2 160, 3 80+80)
/// and two centre segment indexes; HE Operation (extension 36) is four bytes of
/// parameters and two of MCS, then optional VHT information (3 bytes, bit 14),
/// a co-hosted BSS byte (bit 15) and the 6 GHz information (5 bytes, bit 17)
/// whose control byte's low two bits give 20/40/80/160; EHT Operation
/// (extension 106) is a parameters byte and four bytes of MCS, then, when bit 0
/// is set, a control byte whose low three bits give 20/40/80/160/320 and two
/// centre indexes. BSS Load (id 11) is a station count (2 bytes) and a channel
/// utilisation out of 255.
/// </para>
/// </remarks>
public static class NearbyNetworks
{
    /// <summary>Size of one WLAN_BSS_ENTRY.</summary>
    public const int EntrySize = 360;

    /// <summary>The longest element blob a beacon may carry.</summary>
    public const int LongestElements = 2324;

    private const int HtCapabilities = 45;
    private const int HtOperation = 61;
    private const int VhtCapabilities = 191;
    private const int VhtOperation = 192;
    private const int BssLoad = 11;
    private const int Extension = 255;
    private const int HeCapabilities = 35;
    private const int HeOperation = 36;
    private const int EhtOperation = 106;
    private const int EhtCapabilities = 108;

    /// <summary>Decodes a WLAN_BSS_LIST.</summary>
    public static IReadOnlyList<NearbyNetwork> Decode(ReadOnlySpan<byte> bytes)
    {
        if (bytes.Length < 8)
        {
            return [];
        }

        uint total = Math.Min(BinaryPrimitives.ReadUInt32LittleEndian(bytes), (uint)bytes.Length);
        uint stated = BinaryPrimitives.ReadUInt32LittleEndian(bytes[4..]);
        int fits = (int)((total - Math.Min(total, 8u)) / EntrySize);
        int count = (int)Math.Min(stated, (uint)fits);

        ReadOnlySpan<byte> all = bytes[..(int)total];
        List<NearbyNetwork> found = new(count);

        for (int each = 0; each < count; each++)
        {
            int at = 8 + (each * EntrySize);
            ReadOnlySpan<byte> entry = all.Slice(at, EntrySize);

            uint offset = BinaryPrimitives.ReadUInt32LittleEndian(entry[352..]);
            uint size = BinaryPrimitives.ReadUInt32LittleEndian(entry[356..]);
            ReadOnlySpan<byte> elements = [];

            if (size > 0 && size <= LongestElements && offset >= EntrySize && at + (long)offset + size <= all.Length)
            {
                elements = all.Slice(at + (int)offset, (int)size);
            }

            double frequency = WifiBands.Megahertz(BinaryPrimitives.ReadUInt32LittleEndian(entry[92..]));
            WifiPhy phy = WifiBands.Phy(BinaryPrimitives.ReadUInt32LittleEndian(entry[52..]));
            BeaconFacts facts = Read(elements, WifiBands.BandOf(frequency), WifiBands.ChannelOf(frequency));

            found.Add(new NearbyNetwork(
                WifiDecoders.SsidBytes(entry[..WifiDecoders.Ssid]),
                WifiDecoders.Mac(entry.Slice(40, 6)),
                BinaryPrimitives.ReadInt32LittleEndian(entry[56..]),
                Math.Min(BinaryPrimitives.ReadUInt32LittleEndian(entry[60..]), 100u),
                frequency,
                phy,
                facts.Generation > phy ? facts.Generation : phy,
                facts.WidthMhz,
                facts.CentreChannel,
                facts.StationCount,
                facts.Utilisation));
        }

        return found;
    }

    /// <summary>Reads what a beacon's elements declare.</summary>
    /// <param name="elements">The element blob.</param>
    /// <param name="band">The band it was heard on, which decides how widths are read.</param>
    /// <param name="primary">Its primary channel.</param>
    public static BeaconFacts Read(ReadOnlySpan<byte> elements, WifiBand band, int primary)
    {
        WifiPhy generation = WifiPhy.Unknown;
        int htWidth = 20;
        int htCentre = 0;
        int vhtWidth = 0;
        int vhtCentre = 0;
        int heWidth = 0;
        int heCentre = 0;
        int ehtWidth = 0;
        int ehtCentre = 0;
        int? stations = null;
        double? utilisation = null;

        int at = 0;

        while (at + 2 <= elements.Length)
        {
            int id = elements[at];
            int length = elements[at + 1];

            if (at + 2 + length > elements.Length)
            {
                break;
            }

            ReadOnlySpan<byte> body = elements.Slice(at + 2, length);
            at += 2 + length;

            switch (id)
            {
                case HtCapabilities:
                    generation = Newer(generation, WifiPhy.Ht);
                    break;

                case HtOperation when body.Length >= 2:
                    generation = Newer(generation, WifiPhy.Ht);
                    int offset = body[1] & 0x03;

                    if ((body[1] & 0x04) != 0 && offset is 1 or 3)
                    {
                        htWidth = 40;
                        htCentre = offset == 1 ? primary + 2 : primary - 2;
                    }

                    break;

                case VhtCapabilities:
                    generation = Newer(generation, WifiPhy.Vht);
                    break;

                case VhtOperation when body.Length >= 3:
                    generation = Newer(generation, WifiPhy.Vht);
                    (vhtWidth, vhtCentre) = VhtWidth(body[0], body[1], body[2]);
                    break;

                case BssLoad when body.Length >= 3:
                    stations = BinaryPrimitives.ReadUInt16LittleEndian(body);
                    utilisation = body[2] / 255d;
                    break;

                case Extension when body.Length >= 1:
                    ReadOnlySpan<byte> extension = body[1..];

                    switch (body[0])
                    {
                        case HeCapabilities:
                            generation = Newer(generation, WifiPhy.He);
                            break;

                        case HeOperation:
                            generation = Newer(generation, WifiPhy.He);
                            (heWidth, heCentre) = HeWidth(extension);
                            break;

                        case EhtCapabilities:
                            generation = Newer(generation, WifiPhy.Eht);
                            break;

                        case EhtOperation:
                            generation = Newer(generation, WifiPhy.Eht);
                            (ehtWidth, ehtCentre) = EhtWidth(extension);
                            break;
                    }

                    break;
            }
        }

        // The newest declaration wins: EHT can widen to 320, HE carries 6 GHz's
        // widths, VHT the 5 GHz ones, and HT only ever says 20 or 40.
        (int width, int centre) = ehtWidth > 0 ? (ehtWidth, ehtCentre)
            : heWidth > 0 ? (heWidth, heCentre)
            : vhtWidth > 0 && band != WifiBand.TwoPointFour ? (vhtWidth, vhtCentre)
            : (htWidth, htCentre);

        return new BeaconFacts(generation, width, centre, stations, utilisation);
    }

    private static WifiPhy Newer(WifiPhy had, WifiPhy seen) => seen > had ? seen : had;

    /// <summary>Width and centre from a VHT Operation element's first three bytes.</summary>
    public static (int Width, int Centre) VhtWidth(byte width, byte segment0, byte segment1)
    {
        switch (width)
        {
            case 1 when segment1 != 0 && Math.Abs(segment1 - segment0) == 8:
                // 160 MHz written the 802.11ac-2016 way: segment 1 is the centre.
                return (160, segment1);

            case 1 when segment1 != 0:
                // 80+80: two separate 80s, counted here as the primary 80.
                return (80, segment0);

            case 1:
                return (80, segment0);

            case 2:
                return (160, segment0);

            case 3:
                return (80, segment0);

            default:
                return (0, 0);
        }
    }

    /// <summary>Width and centre from an HE Operation element's body after the extension id.</summary>
    public static (int Width, int Centre) HeWidth(ReadOnlySpan<byte> body)
    {
        if (body.Length < 6)
        {
            return (0, 0);
        }

        uint parameters = BinaryPrimitives.ReadUInt32LittleEndian(body);
        int at = 6;

        if ((parameters & 0x00004000) != 0)
        {
            at += 3;
        }

        if ((parameters & 0x00008000) != 0)
        {
            at += 1;
        }

        if ((parameters & 0x00020000) == 0 || at + 5 > body.Length)
        {
            return (0, 0);
        }

        // 6 GHz Operation Information: primary, control, CCFS0, CCFS1, minimum rate.
        byte control = body[at + 1];
        byte segment0 = body[at + 2];
        byte segment1 = body[at + 3];

        return (control & 0x03) switch
        {
            0 => (20, segment0),
            1 => (40, segment0),
            2 => (80, segment0),
            _ => (160, segment1 != 0 ? segment1 : segment0),
        };
    }

    /// <summary>Width and centre from an EHT Operation element's body after the extension id.</summary>
    public static (int Width, int Centre) EhtWidth(ReadOnlySpan<byte> body)
    {
        // Parameters (1) and the basic EHT-MCS and NSS set (4), then the
        // operation information when parameters bit 0 is set.
        if (body.Length < 8 || (body[0] & 0x01) == 0)
        {
            return (0, 0);
        }

        byte control = body[5];
        byte segment0 = body[6];
        byte segment1 = body[7];

        return (control & 0x07) switch
        {
            0 => (20, segment0),
            1 => (40, segment0),
            2 => (80, segment0),
            3 => (160, segment1 != 0 ? segment1 : segment0),
            4 => (320, segment1 != 0 ? segment1 : segment0),
            _ => (0, 0),
        };
    }
}
