using System.Text;
using Windsock.Core.Hardware;
using Xunit;

namespace Windsock.Core.Tests;

public sealed class SmbiosTests
{
    [Fact]
    public void AFirmwareTable_YieldsTheBoardAndTheBios()
    {
        byte[] table = Table(
            Structure(0, Named(0x18), "American Megatrends Inc.", "2602"),
            Structure(2, Named(0x0F), "ASUSTeK COMPUTER INC.", "TUF GAMING Z690-PLUS D4"));

        SmbiosTables read = Smbios.Read(table);

        Assert.Equal("American Megatrends Inc. 2602", read.Bios);
        Assert.Equal("ASUSTeK COMPUTER INC. TUF GAMING Z690-PLUS D4", read.Board);
    }

    [Fact]
    public void AProcessorStructure_YieldsTheSocketAndTheCounts()
    {
        byte[] body = new byte[0x2C];
        body[0x04] = 1;
        Write(body, 0x14, (ushort)3700);
        body[0x23] = 10;
        body[0x25] = 16;

        SmbiosTables read = Smbios.Read(Table(Structure(4, body, "LGA1700")));

        Assert.Equal("LGA1700", read.Socket);
        Assert.Equal(10, read.Cores);
        Assert.Equal(16, read.Threads);
        Assert.Equal(3700, read.MaxMegahertz);
    }

    [Fact]
    public void ACountThatOutgrewItsByte_ComesFromTheWiderField()
    {
        byte[] body = new byte[0x30];
        body[0x23] = 0xFF;
        body[0x25] = 0xFF;
        Write(body, 0x2A, (ushort)288);
        Write(body, 0x2E, (ushort)576);

        SmbiosTables read = Smbios.Read(Table(Structure(4, body)));

        Assert.Equal(288, read.Cores);
        Assert.Equal(576, read.Threads);
    }

    [Fact]
    public void AMemoryDevice_YieldsWhatIsInTheSlot()
    {
        SmbiosTables read = Smbios.Read(Table(Module(
            megabytes: 16384,
            configured: 3200,
            rated: 3600,
            kind: 0x1A,
            slot: "DIMM_A1",
            maker: "G Skill Intl",
            part: "F4-3200C16-16GTZ")));

        MemoryModule module = Assert.Single(read.Modules);

        Assert.Equal("DIMM_A1", module.Slot);
        Assert.Equal(16L * 1024 * 1024 * 1024, module.Bytes);
        Assert.Equal("DDR4", module.Kind);
        Assert.Equal("G Skill Intl", module.Maker);
        Assert.Equal("F4-3200C16-16GTZ", module.Part);
    }

    [Fact]
    public void AModuleRunningBelowItsRating_ReportsTheSpeedItIsRunningAt()
    {
        SmbiosTables read = Smbios.Read(Table(Module(
            megabytes: 8192,
            configured: 3200,
            rated: 3600)));

        Assert.Equal(3200, Assert.Single(read.Modules).Megatransfers);
    }

    [Fact]
    public void AModuleThatDoesNotSayWhatItIsConfiguredAt_FallsBackToItsRating()
    {
        SmbiosTables read = Smbios.Read(Table(Module(
            megabytes: 8192,
            configured: 0,
            rated: 2666)));

        Assert.Equal(2666, Assert.Single(read.Modules).Megatransfers);
    }

    [Fact]
    public void AnEmptySlot_IsNotAModule()
    {
        SmbiosTables read = Smbios.Read(Table(Module(megabytes: 0)));

        Assert.Empty(read.Modules);
    }

    [Fact]
    public void ASlotMeasuredInKilobytes_IsReadAsKilobytes()
    {
        byte[] body = Body(0x20);
        Write(body, 0x0C, (ushort)(0x8000 | 512));

        SmbiosTables read = Smbios.Read(Table(Structure(17, body)));

        Assert.Equal(512L * 1024, Assert.Single(read.Modules).Bytes);
    }

    [Fact]
    public void AModuleLargerThanTheFieldHolds_ComesFromTheExtendedField()
    {
        byte[] body = Body(0x22);
        Write(body, 0x0C, (ushort)0x7FFF);
        Write(body, 0x1C, 64u * 1024);

        SmbiosTables read = Smbios.Read(Table(Structure(17, body)));

        Assert.Equal(64L * 1024 * 1024 * 1024, Assert.Single(read.Modules).Bytes);
    }

    [Theory]
    [InlineData("To Be Filled By O.E.M.")]
    [InlineData("Not Specified")]
    [InlineData("Default string")]
    [InlineData("Unknown")]
    public void FirmwareThatFilledInAPlaceholder_SaysNothingInstead(string placeholder)
    {
        SmbiosTables read = Smbios.Read(Table(Structure(2, Named(0x0F), placeholder, placeholder)));

        Assert.Equal(string.Empty, read.Board);
    }

    [Fact]
    public void AStructureWithNoStrings_DoesNotStopTheWalk()
    {
        byte[] table = Table(
            Structure(4, Body(0x2C)),
            Structure(2, Named(0x0F), "ASRock", "B550M"));

        Assert.Equal("ASRock B550M", Smbios.Read(table).Board);
    }

    [Fact]
    public void AStructureRunningPastTheEnd_StopsTheWalkRatherThanReadingOn()
    {
        byte[] table = Table(Structure(2, Named(0x0F), "ASRock", "B550M"));
        SmbiosTables read = Smbios.Read(table.AsSpan(0, 12));

        Assert.Equal(string.Empty, read.Board);
    }

    [Fact]
    public void ATableThatIsNotThere_ReadsAsNothing()
    {
        Assert.Equal(SmbiosTables.Empty, Smbios.Read([]));
        Assert.Equal(SmbiosTables.Empty, Smbios.Read([0, 3, 4, 0]));
    }

    [Fact]
    public void ALengthLongerThanTheBuffer_IsHeldToWhatArrived()
    {
        byte[] table = Table(Structure(2, Named(0x0F), "ASRock", "B550M"));
        BitConverter.GetBytes(table.Length * 4).CopyTo(table, 4);

        Assert.Equal("ASRock B550M", Smbios.Read(table).Board);
    }

    [Theory]
    [InlineData("ASUSTeK", "TUF GAMING", "ASUSTeK TUF GAMING")]
    [InlineData("", "TUF GAMING", "TUF GAMING")]
    [InlineData("ASUSTeK", "", "ASUSTeK")]
    [InlineData("Dell Inc.", "Dell Inc. XPS 15", "Dell Inc. XPS 15")]
    public void AMakerAndAModel_ReadAsOneNameWithoutRepeatingThemselves(
        string maker,
        string model,
        string expected) =>
        Assert.Equal(expected, Smbios.Join(maker, model));

    private static byte[] Module(
        int megabytes,
        int configured = 3200,
        int rated = 3200,
        byte kind = 0x1A,
        string slot = "DIMM0",
        string maker = "Maker",
        string part = "Part")
    {
        byte[] body = Body(0x22);

        Write(body, 0x0C, (ushort)megabytes);
        body[0x10] = 1;
        body[0x12] = kind;
        Write(body, 0x15, (ushort)rated);
        body[0x17] = 2;
        body[0x1A] = 3;
        Write(body, 0x20, (ushort)configured);

        return Structure(17, body, slot, maker, part);
    }

    private static byte[] Body(int length) => new byte[length];

    private static byte[] Named(int length)
    {
        byte[] body = new byte[length];
        body[0x04] = 1;
        body[0x05] = 2;

        return body;
    }

    private static void Write(byte[] body, int at, ushort value) =>
        BitConverter.GetBytes(value).CopyTo(body, at);

    private static void Write(byte[] body, int at, uint value) =>
        BitConverter.GetBytes(value).CopyTo(body, at);

    private static byte[] Structure(byte type, byte[] body, params string[] strings)
    {
        body[0] = type;
        body[1] = (byte)body.Length;
        body[2] = 0;
        body[3] = 0;

        List<byte> bytes = [.. body];

        foreach (string text in strings)
        {
            bytes.AddRange(Encoding.ASCII.GetBytes(text));
            bytes.Add(0);
        }
        bytes.Add(0);

        if (strings.Length == 0)
        {
            bytes.Add(0);
        }

        return [.. bytes];
    }

    private static byte[] Table(params byte[][] structures)
    {
        List<byte> body = [];

        foreach (byte[] structure in structures)
        {
            body.AddRange(structure);
        }

        List<byte> table = [0, 3, 4, 0];
        table.AddRange(BitConverter.GetBytes(body.Count));
        table.AddRange(body);

        return [.. table];
    }
}
