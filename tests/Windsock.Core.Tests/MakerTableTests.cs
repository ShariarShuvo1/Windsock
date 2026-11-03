using System.Text;
using Windsock.Core.Home;
using Xunit;

namespace Windsock.Core.Tests;

public sealed class MakerTableTests
{
    private static readonly DateOnly Day = new(2026, 10, 5);

    [Theory]
    [InlineData("50-C7-BF-01-02-03")]
    [InlineData("50:c7:bf:01:02:03")]
    [InlineData("50c7.bf01.0203")]
    [InlineData("50C7BF010203")]
    public void Addresses_ReadInEveryUsualSpelling(string text)
    {
        Assert.True(MacAddress.TryParse(text, out MacAddress address));
        Assert.Equal("50-C7-BF-01-02-03", address.ToString());
    }

    [Theory]
    [InlineData("")]
    [InlineData("50-C7-BF-01-02")]
    [InlineData("50-C7-BF-01-02-03-04")]
    [InlineData("50-C7-BF-01-02-0G")]
    public void NotAnAddress_IsRefused(string text) => Assert.False(MacAddress.TryParse(text, out _));

    /// <summary>IEEE 802-2014 8.2.2: bit 0 of the first octet is group, bit 1 is local.</summary>
    [Fact]
    public void TheFirstOctetsLowBits_SayWhatKindOfAddressItIs()
    {
        Assert.True(Parse("01-00-5E-00-00-FB").IsMulticast);
        Assert.True(Parse("DA-A1-19-00-00-01").IsLocallyAdministered);
        Assert.False(Parse("DA-A1-19-00-00-01").HasMaker);
        Assert.True(Parse("FF-FF-FF-FF-FF-FF").IsBroadcast);
        Assert.False(Parse("00-00-00-00-00-00").IsUnicast);
        Assert.True(Parse("50-C7-BF-01-02-03").HasMaker);
    }

    [Fact]
    public void Prefixes_AreTheLeadingBits()
    {
        MacAddress address = Parse("70-B3-D5-12-34-56");

        Assert.Equal(0x70B3D5u, address.Prefix24);
        Assert.Equal(0x70B3D51u, address.Prefix28);
        Assert.Equal(0x70B3D5123ul, address.Prefix36);
    }

    [Fact]
    public void Octets_RoundTrip()
    {
        byte[] octets = [0x50, 0xC7, 0xBF, 0x01, 0x02, 0x03];

        Assert.Equal(octets, MacAddress.From(octets)!.Value.ToBytes());
        Assert.Null(MacAddress.From(octets.AsSpan(0, 5)));
    }

    [Fact]
    public void Csv_ReadsQuotedNamesAndSkipsCompanyIds()
    {
        const string Csv = """
            Registry,Assignment,Organization Name,Organization Address
            MA-L,286FB9,"Nokia Shanghai Bell Co., Ltd.","No.388 Ning Qiao Road, Shanghai CN 201206 "
            MA-M,C85CE27,SYNERGY SYSTEMS AND SOLUTIONS,"A1526, Faridabad IN 121001 "
            MA-S,8C1F64AFA,"DATA ELECTRONIC DEVICES, INC",32 NORTHWESTERN DR SALEM NH US 03079
            IAB,0050C2F71,RF Code,Austin TX US 78758
            CID,0A1B2C,Some Company,Nowhere
            MA-L,ZZZZZZ,Broken,Nowhere
            """;

        OuiAssignment[] read = [.. OuiTableWriter.ReadCsv(new StringReader(Csv))];

        Assert.Equal(
            [
                new OuiAssignment(OuiBlock.Large, 0x286FB9, "Nokia Shanghai Bell Co., Ltd."),
                new OuiAssignment(OuiBlock.Medium, 0xC85CE27, "SYNERGY SYSTEMS AND SOLUTIONS"),
                new OuiAssignment(OuiBlock.Small, 0x8C1F64AFA, "DATA ELECTRONIC DEVICES, INC"),
                new OuiAssignment(OuiBlock.Small, 0x0050C2F71, "RF Code"),
            ],
            read);
    }

    /// <summary>The narrowest block wins; a block carved out of the registry's own MA-L names nobody.</summary>
    [Fact]
    public void Lookup_TakesTheLongestPrefix()
    {
        OuiTable table = OuiTable.Load(OuiTableWriter.Build(
            [
                new OuiAssignment(OuiBlock.Large, 0x70B3D5, "IEEE Registration Authority"),
                new OuiAssignment(OuiBlock.Medium, 0x70B3D51, "Medium Maker"),
                new OuiAssignment(OuiBlock.Small, 0x70B3D5123, "Small Maker"),
                new OuiAssignment(OuiBlock.Large, 0x50C7BF, "TP-LINK TECHNOLOGIES CO.,LTD."),
                new OuiAssignment(OuiBlock.Large, 0x001122, "  Padded    Name  "),
                new OuiAssignment(OuiBlock.Large, 0x003344, "Private"),
            ],
            Day));

        Assert.Equal("Small Maker", table.Find(Parse("70-B3-D5-12-34-56")));
        Assert.Equal("Medium Maker", table.Find(Parse("70-B3-D5-1F-00-00")));
        Assert.Null(table.Find(Parse("70-B3-D5-20-00-00")));
        Assert.Equal("TP-LINK TECHNOLOGIES CO.,LTD.", table.Find(Parse("50-C7-BF-AA-BB-CC")));
        Assert.Equal("Padded Name", table.Find(Parse("00-11-22-33-44-55")));
        Assert.Null(table.Find(Parse("00-33-44-00-00-01")));
        Assert.Null(table.Find(Parse("52-C7-BF-AA-BB-CC")));
        Assert.Equal(Day, table.Retrieved);
        Assert.Equal(4, table.Organisations);
    }

    [Fact]
    public void Header_IsTheDocumentedLayout()
    {
        byte[] file = OuiTableWriter.Build([new OuiAssignment(OuiBlock.Large, 0x50C7BF, "Maker")], Day);

        Assert.Equal("WSCKOUI1", Encoding.ASCII.GetString(file, 0, 8));
        Assert.Equal(20261005, BitConverter.ToInt32(file, 8));
        Assert.Equal(1, BitConverter.ToInt32(file, 12));
        Assert.Equal(1, BitConverter.ToInt32(file, 16));
        Assert.Equal(48, BitConverter.ToInt32(file, 28));

        int rows = BitConverter.ToInt32(file, 36);
        Assert.Equal([0xBF, 0xC7, 0x50, 0x00, 0x00], file[rows..(rows + 5)]);
    }

    [Fact]
    public void SomethingElse_IsNotATable()
    {
        Assert.Throws<InvalidDataException>(() => OuiTable.Load(Encoding.ASCII.GetBytes("WSCKGEO1 and then some more bytes that are not a table at all")));
    }

    /// <summary>The table that ships: present, current enough, and naming makers everyone knows.</summary>
    [Fact]
    public void ShippedTable_NamesWellKnownMakers()
    {
        OuiTable? table = OuiTable.Open();

        Assert.NotNull(table);
        Assert.True(table.Blocks > 50_000);
        Assert.StartsWith("Cisco", table.Find(Parse("00-00-0C-00-00-01")), StringComparison.OrdinalIgnoreCase);
        Assert.StartsWith("Apple", table.Find(Parse("00-03-93-00-00-01")), StringComparison.OrdinalIgnoreCase);
    }

    private static MacAddress Parse(string text)
    {
        Assert.True(MacAddress.TryParse(text, out MacAddress address));
        return address;
    }
}
