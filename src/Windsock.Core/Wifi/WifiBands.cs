using System.Globalization;

namespace Windsock.Core.Wifi;

/// <summary>The radio band a Wi-Fi channel is in.</summary>
public enum WifiBand
{
    Unknown,

    /// <summary>2.4 GHz: long reach, few channels, crowded.</summary>
    TwoPointFour,

    /// <summary>5 GHz.</summary>
    Five,

    /// <summary>6 GHz, Wi-Fi 6E and 7 only.</summary>
    Six,
}

/// <summary>
/// The physical layer Windows reports for a link or a network: DOT11_PHY_TYPE
/// in windot11.h.
/// </summary>
public enum WifiPhy
{
    Unknown = 0,
    Fhss = 1,
    Dsss = 2,
    Infrared = 3,

    /// <summary>802.11a.</summary>
    Ofdm = 4,

    /// <summary>802.11b.</summary>
    HrDsss = 5,

    /// <summary>802.11g.</summary>
    Erp = 6,

    /// <summary>802.11n, Wi-Fi 4.</summary>
    Ht = 7,

    /// <summary>802.11ac, Wi-Fi 5.</summary>
    Vht = 8,

    /// <summary>802.11ad, WiGig.</summary>
    Dmg = 9,

    /// <summary>802.11ax, Wi-Fi 6 (6E on a 6 GHz channel).</summary>
    He = 10,

    /// <summary>802.11be, Wi-Fi 7.</summary>
    Eht = 11,
}

/// <summary>Channel and band arithmetic, and the words for them.</summary>
/// <remarks>
/// Channel numbers from centre frequencies follow the Linux kernel's
/// <c>ieee80211_freq_khz_to_channel</c> (net/wireless/util.c): 2.4 GHz is
/// (f - 2407) / 5 with channel 14 at 2484 MHz; 5 GHz is (f - 5000) / 5; 6 GHz
/// is (f - 5950) / 5, with the odd channel 2 at 5935 MHz.
/// </remarks>
public static class WifiBands
{
    /// <summary>The band of a centre frequency in MHz.</summary>
    public static WifiBand BandOf(double megahertz) => megahertz switch
    {
        >= 2400 and <= 2500 => WifiBand.TwoPointFour,
        >= 5150 and < 5925 => WifiBand.Five,
        >= 5925 and <= 7125 => WifiBand.Six,
        _ => WifiBand.Unknown,
    };

    /// <summary>The channel of a centre frequency in MHz, or 0 when it is on no channel.</summary>
    public static int ChannelOf(double megahertz)
    {
        int f = (int)Math.Round(megahertz);

        return BandOf(f) switch
        {
            WifiBand.TwoPointFour when f == 2484 => 14,
            WifiBand.TwoPointFour when f is >= 2412 and <= 2472 => (f - 2407) / 5,
            WifiBand.Five => (f - 5000) / 5,
            WifiBand.Six when f == 5935 => 2,
            WifiBand.Six => (f - 5950) / 5,
            _ => 0,
        };
    }

    /// <summary>The centre frequency of a channel in a band, in MHz, or 0.</summary>
    public static int FrequencyOf(WifiBand band, int channel) => band switch
    {
        WifiBand.TwoPointFour when channel == 14 => 2484,
        WifiBand.TwoPointFour when channel is >= 1 and <= 13 => 2407 + (5 * channel),
        WifiBand.Five when channel is >= 32 and <= 177 => 5000 + (5 * channel),
        WifiBand.Six when channel == 2 => 5935,
        WifiBand.Six when channel is >= 1 and <= 233 => 5950 + (5 * channel),
        _ => 0,
    };

    /// <summary>
    /// The centre channel of the bonded block a channel belongs to at a width:
    /// channel 36 at 80 MHz is the block 36 to 48, centred on 42. 2.4 GHz and
    /// 20 MHz return the channel itself.
    /// </summary>
    /// <remarks>
    /// 5 GHz blocks follow the fixed 802.11ac/ax channelisation (40 MHz centres
    /// 38 ... 159, 80 MHz 42, 58, 106, 122, 138, 155, 160 MHz 50, 114, 163);
    /// 6 GHz blocks run from channel 1 in steps of the width.
    /// </remarks>
    public static int BlockCentre(WifiBand band, int channel, int widthMhz)
    {
        if (band == WifiBand.TwoPointFour || widthMhz <= 20 || channel <= 0)
        {
            return channel;
        }

        if (band == WifiBand.Six)
        {
            int span = widthMhz / 5;
            int start = 1 + ((channel - 1) / span * span);
            return start + ((span - 4) / 2);
        }

        int[] centres = widthMhz switch
        {
            40 => [38, 46, 54, 62, 102, 110, 118, 126, 134, 142, 151, 159],
            80 => [42, 58, 106, 122, 138, 155],
            _ => [50, 114, 163],
        };

        int half = widthMhz / 10;

        foreach (int centre in centres)
        {
            if (Math.Abs(channel - centre) < half)
            {
                return centre;
            }
        }

        return channel;
    }

    /// <summary>
    /// Reads a frequency Windows gave in MHz or in kHz. The realtime quality
    /// struct names its field "MHz" while its documentation says kHz, and the
    /// BSS list's field is documented kHz; no Wi-Fi channel is above 7.2 GHz,
    /// so anything over a million is taken as kHz.
    /// </summary>
    public static double Megahertz(uint value) => value >= 1_000_000 ? value / 1000d : value;

    /// <summary>
    /// A channel width in MHz from what Windows gave, or 0 when it is not one
    /// of the widths Wi-Fi uses. The realtime quality struct does not state its
    /// unit; MHz is assumed, with kHz recognised by its size.
    /// </summary>
    public static int Width(uint value)
    {
        uint megahertz = value >= 1000 ? value / 1000 : value;
        return megahertz is 20 or 40 or 80 or 160 or 320 ? (int)megahertz : 0;
    }

    /// <summary>The band as people say it: "2.4 GHz", "5 GHz", "6 GHz".</summary>
    public static string Describe(WifiBand band) => band switch
    {
        WifiBand.TwoPointFour => "2.4 GHz",
        WifiBand.Five => "5 GHz",
        WifiBand.Six => "6 GHz",
        _ => "—",
    };

    /// <summary>The band in two or three characters, for a taskbar slot: "2.4G", "5G", "6G".</summary>
    public static string Abbreviated(WifiBand band) => band switch
    {
        WifiBand.TwoPointFour => "2.4G",
        WifiBand.Five => "5G",
        WifiBand.Six => "6G",
        _ => string.Empty,
    };

    /// <summary>
    /// The Wi-Fi generation as people know it: "Wi-Fi 7", "Wi-Fi 6E", "Wi-Fi 6",
    /// "Wi-Fi 5", "Wi-Fi 4", or the 802.11 letter for older ones.
    /// </summary>
    public static string Generation(WifiPhy phy, WifiBand band) => phy switch
    {
        WifiPhy.Eht => "Wi-Fi 7",
        WifiPhy.He when band == WifiBand.Six => "Wi-Fi 6E",
        WifiPhy.He => "Wi-Fi 6",
        WifiPhy.Vht => "Wi-Fi 5",
        WifiPhy.Ht => "Wi-Fi 4",
        WifiPhy.Erp => "802.11g",
        WifiPhy.HrDsss => "802.11b",
        WifiPhy.Ofdm => "802.11a",
        WifiPhy.Dmg => "802.11ad",
        WifiPhy.Dsss => "802.11",
        _ => "—",
    };

    /// <summary>The 802.11 amendment name: "802.11be", "802.11ax" and so on.</summary>
    public static string Standard(WifiPhy phy) => phy switch
    {
        WifiPhy.Eht => "802.11be",
        WifiPhy.He => "802.11ax",
        WifiPhy.Vht => "802.11ac",
        WifiPhy.Ht => "802.11n",
        WifiPhy.Erp => "802.11g",
        WifiPhy.HrDsss => "802.11b",
        WifiPhy.Ofdm => "802.11a",
        WifiPhy.Dmg => "802.11ad",
        _ => "—",
    };

    /// <summary>Reads a DOT11_PHY_TYPE value; vendors' own values are unknown.</summary>
    public static WifiPhy Phy(uint value) => value <= (uint)WifiPhy.Eht ? (WifiPhy)value : WifiPhy.Unknown;

    /// <summary>
    /// Windows' signal quality, 0 to 100, as dBm: the documentation sets 0 at
    /// -100 dBm and 100 at -50 dBm, linear between.
    /// </summary>
    public static int RssiFromQuality(uint quality) => -100 + (int)Math.Round(Math.Min(quality, 100u) / 2d);

    /// <summary>
    /// Bars of signal, 0 to 4, from a received signal strength in dBm.
    /// </summary>
    /// <remarks>
    /// A design choice, not a Windows rule: -60 dBm and better is four bars,
    /// -67 three (the level wireless design guides commonly ask for calls and
    /// video), -75 two, -82 one, and anything weaker none.
    /// </remarks>
    public static int Bars(int rssi) => rssi switch
    {
        >= -60 => 4,
        >= -67 => 3,
        >= -75 => 2,
        >= -82 => 1,
        _ => 0,
    };

    /// <summary>"Channel 36 (5 GHz, 80 MHz)".</summary>
    public static string DescribeChannel(int channel, WifiBand band, int widthMhz)
    {
        if (channel <= 0)
        {
            return "—";
        }

        string width = widthMhz > 0 ? string.Create(CultureInfo.InvariantCulture, $", {widthMhz} MHz") : string.Empty;
        return string.Create(CultureInfo.InvariantCulture, $"Channel {channel} ({Describe(band)}{width})");
    }
}
