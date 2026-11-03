using Windsock.Core.Qr;
using Windsock.Core.Wifi;
using Xunit;

namespace Windsock.Core.Tests;

/// <summary>
/// The QR encoder against symbols made by Project Nayuki's qrcodegen 1.8.0 (an
/// independent encoder whose output decodes with zxing-cpp), and the Wi-Fi
/// text against ZXing's format.
/// </summary>
public sealed class QrCodeTests
{

    private static readonly int[] VersionTwo = [6, 18];
    private static readonly int[] VersionSeven = [6, 22, 38];
    private static readonly int[] VersionThirtyTwo = [6, 34, 60, 86, 112, 138];
    private static readonly int[] VersionForty = [6, 30, 58, 86, 114, 142, 170];
    [Fact]
    public void ShortWifiCode_MatchesTheReferenceModuleForModule()
    {
        QrCode code = QrCode.EncodeText(@"WIFI:T:WPA;S:Home\;Net;P:pa\:ss;H:true;;", QrErrorCorrection.Medium);

        Assert.Equal(3, code.Version);
        Assert.Equal(3, code.Mask);
        AssertModules(ShortWifi, code);
    }

    [Fact]
    public void LongWifiCodeWithUtf8_MatchesTheReference()
    {
        QrCode code = QrCode.EncodeText(
            "WIFI:T:WPA;S:Caf\u00e9 Wi-Fi 5G;P:correct horse battery staple, with a longer passphrase of 63 chars;;",
            QrErrorCorrection.Medium);

        Assert.Equal(6, code.Version);
        Assert.Equal(2, code.Mask);
        AssertModules(LongWifi, code);
    }

    [Fact]
    public void VersionEight_CarriesVersionInformation()
    {
        QrCode code = QrCode.EncodeBytes("WIFI:T:nopass;S:Guest;;"u8, QrErrorCorrection.Low, version: 8);

        Assert.Equal(8, code.Version);
        Assert.Equal(49, code.Size);
        Assert.Equal(2, code.Mask);
        AssertModules(OpenVersionEight, code);
    }

    [Theory]
    [InlineData(1, 208)]
    [InlineData(2, 359)]
    [InlineData(7, 1568)]
    [InlineData(40, 29648)]
    public void RawDataModules_MatchTheStandard(int version, int modules)
    {
        Assert.Equal(modules, QrCode.RawDataModules(version));
    }

    [Theory]
    [InlineData(1, QrErrorCorrection.Low, 19)]
    [InlineData(1, QrErrorCorrection.High, 9)]
    [InlineData(10, QrErrorCorrection.Medium, 216)]
    [InlineData(40, QrErrorCorrection.Low, 2956)]
    public void DataCodewords_MatchTheStandardsTable(int version, QrErrorCorrection level, int codewords)
    {
        Assert.Equal(codewords, QrCode.DataCodewords(version, level));
    }

    [Fact]
    public void AlignmentPositions_MatchTheStandard()
    {
        Assert.Empty(QrCode.AlignmentPositions(1));
        Assert.Equal(VersionTwo, QrCode.AlignmentPositions(2));
        Assert.Equal(VersionSeven, QrCode.AlignmentPositions(7));
        Assert.Equal(VersionThirtyTwo, QrCode.AlignmentPositions(32));
        Assert.Equal(VersionForty, QrCode.AlignmentPositions(40));
    }

    [Fact]
    public void TooMuch_IsRefused()
    {
        Assert.Throws<ArgumentException>(() => QrCode.EncodeBytes(new byte[3000], QrErrorCorrection.High));
    }

    [Fact]
    public void Payload_EscapesSpecialCharacters()
    {
        WifiProfileSecret profile = new(@"Home;Net\2", Hidden: true, WifiShareSecurity.Wpa, KeyProtected: false, Key: "pa:ss,\"x\"");

        Assert.Equal(@"WIFI:T:WPA;S:Home\;Net\\2;P:pa\:ss\,\""x\"";H:true;;", WifiShare.Payload(profile));
    }

    [Fact]
    public void Payload_QuotesTextThatWouldReadAsHex()
    {
        WifiProfileSecret profile = new("CAFE", Hidden: false, WifiShareSecurity.Wpa, KeyProtected: false, Key: "0123456789");

        Assert.Equal("WIFI:T:WPA;S:\"CAFE\";P:\"0123456789\";;", WifiShare.Payload(profile));
    }

    [Fact]
    public void Payload_LeavesARawHexKeyBare_SoItIsReadAsBytes()
    {
        const string Wpa = "5a9f0c3e8b7d2a1f6e4c0b9a8d7e6f5a4b3c2d1e0f9a8b7c6d5e4f3a2b1c0d9e";
        WifiProfileSecret raw = new("Home", false, WifiShareSecurity.Wpa, KeyProtected: false, Key: Wpa, KeyIsPassphrase: false);
        WifiProfileSecret wep = new("Old", false, WifiShareSecurity.Wep, KeyProtected: false, Key: "0123456789", KeyIsPassphrase: false);

        Assert.Equal(string.Concat("WIFI:T:WPA;S:Home;P:", Wpa, ";;"), WifiShare.Payload(raw));
        Assert.Equal("WIFI:T:WEP;S:Old;P:0123456789;;", WifiShare.Payload(wep));
    }

    [Fact]
    public void ReadProfile_TellsANetworkKeyFromAPassphrase()
    {
        const string Xml = """
            <WLANProfile xmlns="http://www.microsoft.com/networking/WLAN/profile/v1">
              <name>Old</name>
              <SSIDConfig><SSID><name>Old</name></SSID></SSIDConfig>
              <MSM><security>
                <authEncryption><authentication>open</authentication><encryption>WEP</encryption></authEncryption>
                <sharedKey><keyType>networkKey</keyType><protected>false</protected><keyMaterial>0123456789</keyMaterial></sharedKey>
              </security></MSM>
            </WLANProfile>
            """;

        WifiProfileSecret profile = WifiShare.ReadProfile(Xml)!;

        Assert.Equal(WifiShareSecurity.Wep, profile.Security);
        Assert.False(profile.KeyIsPassphrase);
        Assert.Equal("WIFI:T:WEP;S:Old;P:0123456789;;", WifiShare.Payload(profile));
    }

    [Fact]
    public void Payload_OpenNetwork_HasNoPassword()
    {
        Assert.Equal("WIFI:T:nopass;S:Guest;;", WifiShare.Payload(new WifiProfileSecret("Guest", false, WifiShareSecurity.Open, false, null)));
    }

    [Fact]
    public void Payload_ProtectedOrEnterprise_CannotBeShared()
    {
        Assert.Null(WifiShare.Payload(new WifiProfileSecret("Home", false, WifiShareSecurity.Wpa, KeyProtected: true, Key: null)));
        Assert.Null(WifiShare.Payload(new WifiProfileSecret("Office", false, WifiShareSecurity.Enterprise, false, null)));
    }

    [Fact]
    public void ReadProfile_PlainKey_IsRead()
    {
        const string Xml = """
            <?xml version="1.0"?>
            <WLANProfile xmlns="http://www.microsoft.com/networking/WLAN/profile/v1">
              <name>Home</name>
              <SSIDConfig><SSID><hex>486F6D65</hex><name>Home</name></SSID><nonBroadcast>true</nonBroadcast></SSIDConfig>
              <MSM><security>
                <authEncryption><authentication>WPA2PSK</authentication><encryption>AES</encryption><useOneX>false</useOneX></authEncryption>
                <sharedKey><keyType>passPhrase</keyType><protected>false</protected><keyMaterial>correct horse</keyMaterial></sharedKey>
              </security></MSM>
            </WLANProfile>
            """;

        WifiProfileSecret profile = WifiShare.ReadProfile(Xml)!;

        Assert.Equal("Home", profile.Ssid);
        Assert.True(profile.Hidden);
        Assert.Equal(WifiShareSecurity.Wpa, profile.Security);
        Assert.False(profile.KeyProtected);
        Assert.Equal("correct horse", profile.Key);
        Assert.True(profile.KeyIsPassphrase);
        Assert.True(profile.CanShare);
    }

    [Fact]
    public void ReadProfile_ProtectedKey_IsNotTakenForThePassword()
    {
        const string Xml = """
            <WLANProfile xmlns="http://www.microsoft.com/networking/WLAN/profile/v1">
              <name>Home</name>
              <SSIDConfig><SSID><hex>486F6D65</hex></SSID></SSIDConfig>
              <MSM><security>
                <authEncryption><authentication>WPA3SAE</authentication><encryption>AES</encryption></authEncryption>
                <sharedKey><keyType>passPhrase</keyType><protected>true</protected><keyMaterial>01000000D08C9DDF0115D1118C7A00C04FC297EB</keyMaterial></sharedKey>
              </security></MSM>
            </WLANProfile>
            """;

        WifiProfileSecret profile = WifiShare.ReadProfile(Xml)!;

        Assert.Equal("Home", profile.Ssid);
        Assert.Equal(WifiShareSecurity.Wpa3, profile.Security);
        Assert.True(profile.KeyProtected);
        Assert.Null(profile.Key);
        Assert.False(profile.CanShare);
    }

    [Fact]
    public void ReadProfile_NotAProfile_IsNothing()
    {
        Assert.Null(WifiShare.ReadProfile("<html/>"));
        Assert.Null(WifiShare.ReadProfile("not xml"));
    }

    private static void AssertModules(string[] expected, QrCode code)
    {
        Assert.Equal(expected.Length, code.Size);

        for (int y = 0; y < code.Size; y++)
        {
            char[] row = new char[code.Size];

            for (int x = 0; x < code.Size; x++)
            {
                row[x] = code[x, y] ? '1' : '0';
            }

            Assert.Equal(expected[y], new string(row));
        }
    }

    // 'WIFI:T:WPA;S:Home\\;Net;P:pa\\:ss;H:true;;' 3 3
    private static readonly string[] ShortWifi =
    [
        "11111110100010101101001111111",
        "10000010111100000000101000001",
        "10111010010011111011101011101",
        "10111010111111111000101011101",
        "10111010011010111111001011101",
        "10000010001001101001101000001",
        "11111110101010101010101111111",
        "00000000100110001100000000000",
        "10110111011010000101001001011",
        "00111101001001101101101010110",
        "11110010101101100000101011110",
        "00011101000101111000100001000",
        "10010110100111001101000111100",
        "10100100011001010110000000000",
        "00100010010010001001100111111",
        "10111100000110110011010001000",
        "11100011111100110011010011101",
        "01010101001010000000000000001",
        "10100111010001111010001110000",
        "00001100000011110110011100111",
        "01011010000001110010111110111",
        "00000000111000000101100010100",
        "11111110111100101110101010010",
        "10000010100011111000100010001",
        "10111010011110011110111111011",
        "10111010110101011011110101101",
        "10111010101010100100000100101",
        "10000010001110000010101011010",
        "11111110101110001011110101110",
    ];

    // 'WIFI:T:WPA;S:Café Wi-Fi 5G;P:correct horse battery staple, with a longer passphrase of 63 chars;;' 6 2
    private static readonly string[] LongWifi =
    [
        "11111110011110100001101100001001101111111",
        "10000010000110100010101000010100101000001",
        "10111010110000000001000000110010001011101",
        "10111010100100101010111011111111001011101",
        "10111010110001111101110100101001101011101",
        "10000010100100010010010011011010101000001",
        "11111110101010101010101010101010101111111",
        "00000000100011100001010110101010100000000",
        "10111110000111111110011011100000101111100",
        "00111100101000111111111110011010111011011",
        "11110110111011010010010001010110110111100",
        "00011001110110010001111010000001110111011",
        "10001111101111100110001111100100100000010",
        "01100100001010100010111011101001011110111",
        "11100010011100011111000000110000011101100",
        "01001101000000000001110110010010011011010",
        "01000011101111011101100111100100000101000",
        "11111001001010011101110100000011100010001",
        "10011011001110101100110001011110101111010",
        "01010001101011101110011100010001101001001",
        "11111110101001001000110011110110000001100",
        "00011101110111001011111100101011101010000",
        "01011010101111010010101010010010001111000",
        "11010000010001100010111010110001010111011",
        "01110111111000110110001011101110110001110",
        "00011101101111001000110110000000111110101",
        "00000011011011101010111101011000001011000",
        "00111100011101001010110110100011001101010",
        "10100110111011100101101001110111000000011",
        "10000101111111000000011111001111011110001",
        "10100111100000001010110001111010000000000",
        "10001101001011100010010110100011110000001",
        "10010110111010110100001111110110111111110",
        "00000000111111100011101100001011100010010",
        "11111110011110110010001011010011101010000",
        "10000010111100111000000100110011100011000",
        "10111010111010111010011111010100111110100",
        "10111010101110010001100110101110010010011",
        "10111010110000000100010001110110001111100",
        "10000010010001011100111001010001110001010",
        "11111110110011010010000010111001011110100",
    ];

    // 'WIFI:T:nopass;S:Guest;;' 8 2
    private static readonly string[] OpenVersionEight =
    [
        "1111111000010000101111011000001101011000101111111",
        "1000001010100001101010001101011000001011101000001",
        "1011101001100100111110111001001001001001101011101",
        "1011101010111001001001101100111110010001001011101",
        "1011101000001100111110111111101011000100001011101",
        "1000001011100001101010100010111110010110001000001",
        "1111111010101010101010101010101010101010101111111",
        "0000000000011100111111100011010000101110000000000",
        "1111101110110000001101111110100111110010010101010",
        "1011000011011101011000111001110010100111001101101",
        "0011101110000110010101101100101111010000011000010",
        "0010000110011011000000101110010100111011001111000",
        "1110011110100110010101010011000001101110110100110",
        "0001100011101001001001000110010100111000001111001",
        "0001011000110110010100010001001001001110110000011",
        "0011100100011011000001001110001101011001010001000",
        "0010011110111111010000010011011000001101000110011",
        "1111110101000000101111000110001101011000110011111",
        "1111001011010001101010010001011000001101101111101",
        "1011000011000100111111011001001001001010100110011",
        "0010011110111001001000001100111110010110110100001",
        "1000110101101100111110111001101011000000011100101",
        "0110111111010001101010111110111110010000111111111",
        "0111100010111100111111100011010000101111100010111",
        "1011101010101000001100101010100111110010101011000",
        "1101100010110101011000100011110010100111100010001",
        "0100111111011110010101111110101111010000111111010",
        "0101010110010011000000001100010100111111011001000",
        "0011101010001110010100111001000001101011101100110",
        "0001010101100001001000001100010100111111011001001",
        "1001111011010110010101111001001001001001001100011",
        "0000000000001011000001001100001101011000111001010",
        "1011011100011111010001001111011000001101101010011",
        "0001110101000000101111110010001101011001110101111",
        "1110101100110001101011000111011000001101001001101",
        "1110000010110100111111110001001001001001110000011",
        "0101111100101001001001001110111110010100010001101",
        "0001110111110100111111110011101011000000100110111",
        "0100011110100001101011000110111110010100110011111",
        "0111000100010100111110110001010000101110101110110",
        "1110001001111000001100111110100111110011111111011",
        "0000000010101101011000100011110010100110100010101",
        "1111111011011110010101101010101111010000101011110",
        "1000001001001011000000100010010100111110100010000",
        "1011101011111110010100111111000001101011111111110",
        "1011101011001001001001011000010100111110000111011",
        "1011101011111110010100101101001001001001000100001",
        "1000001010001011000001111000001101011000011111001",
        "1111111010101111010000101101011000001101001110011",
    ];
}
