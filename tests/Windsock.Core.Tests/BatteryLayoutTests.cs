using System.Buffers.Binary;
using System.Text;
using Windsock.Core.Power;
using Xunit;

namespace Windsock.Core.Tests;

/// <summary>
/// The battery driver's structures, held to <c>poclass.h</c> field by field.
/// No battery has been read on the development machine, a desktop; these are
/// what stands between a misread offset and a plausible wrong figure.
/// </summary>
public sealed class BatteryLayoutTests
{
    /// <summary>The control codes poclass.h builds with CTL_CODE.</summary>
    [Fact]
    public void ControlCodes_MatchTheHeader()
    {
        Assert.Equal(0x294040u, BatteryLayout.QueryTag);
        Assert.Equal(0x294044u, BatteryLayout.QueryInformation);
        Assert.Equal(0x29404Cu, BatteryLayout.QueryStatus);
    }

    /// <summary>BATTERY_QUERY_INFORMATION: tag, level, rate, each a ULONG.</summary>
    [Fact]
    public void Query_IsTagThenLevelThenRate()
    {
        byte[] query = BatteryLayout.Query(0x01020304, BatteryInformationLevel.DeviceName, 7);

        Assert.Equal(12, query.Length);
        Assert.Equal(0x01020304u, BinaryPrimitives.ReadUInt32LittleEndian(query));
        Assert.Equal(4u, BinaryPrimitives.ReadUInt32LittleEndian(query.AsSpan(4)));
        Assert.Equal(7u, BinaryPrimitives.ReadUInt32LittleEndian(query.AsSpan(8)));
    }

    /// <summary>The information levels are the header's enum, in its order.</summary>
    [Fact]
    public void InformationLevels_AreTheHeadersOrder()
    {
        Assert.Equal(0u, (uint)BatteryInformationLevel.Information);
        Assert.Equal(2u, (uint)BatteryInformationLevel.Temperature);
        Assert.Equal(3u, (uint)BatteryInformationLevel.EstimatedTime);
        Assert.Equal(4u, (uint)BatteryInformationLevel.DeviceName);
        Assert.Equal(5u, (uint)BatteryInformationLevel.ManufactureDate);
        Assert.Equal(6u, (uint)BatteryInformationLevel.ManufactureName);
        Assert.Equal(8u, (uint)BatteryInformationLevel.SerialNumber);
    }

    /// <summary>BATTERY_WAIT_STATUS is 20 bytes with the tag first and a zero timeout.</summary>
    [Fact]
    public void Wait_CarriesTheTagAndDoesNotWait()
    {
        byte[] wait = BatteryLayout.Wait(42);

        Assert.Equal(20, wait.Length);
        Assert.Equal(42u, BinaryPrimitives.ReadUInt32LittleEndian(wait));
        Assert.All(wait.AsSpan(4).ToArray(), value => Assert.Equal(0, value));
    }

    /// <summary>
    /// BATTERY_INFORMATION: Capabilities @0, Technology @4, Reserved[3],
    /// Chemistry[4] @8, DesignedCapacity @12, FullChargedCapacity @16,
    /// DefaultAlert1 @20, DefaultAlert2 @24, CriticalBias @28, CycleCount @32.
    /// </summary>
    [Fact]
    public void Information_ReadsEveryFieldFromItsOffset()
    {
        byte[] data = new byte[36];
        BinaryPrimitives.WriteUInt32LittleEndian(data, 0x90000000);
        data[4] = 1;
        data[5] = 0xEE;
        data[6] = 0xEE;
        data[7] = 0xEE;
        Encoding.ASCII.GetBytes("LION").CopyTo(data, 8);
        BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(12), 57_000);
        BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(16), 51_300);
        BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(20), 2_850);
        BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(24), 1_140);
        BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(28), 0xDEAD);
        BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(32), 312);

        BatteryInformation information = Assert.NotNull(BatteryLayout.ReadInformation(data));

        Assert.Equal(BatteryCapabilities.System | BatteryCapabilities.Sealed, information.Capabilities);
        Assert.True(information.Rechargeable);
        Assert.Equal("LION", information.Chemistry);
        Assert.Equal(57_000u, information.DesignedCapacity);
        Assert.Equal(51_300u, information.FullChargedCapacity);
        Assert.Equal(2_850u, information.DefaultAlert1);
        Assert.Equal(1_140u, information.DefaultAlert2);
        Assert.Equal(312u, information.CycleCount);
        Assert.True(information.IsEnergy);
    }

    /// <summary>Capabilities flags are the header's values.</summary>
    [Fact]
    public void Capabilities_AreTheHeadersValues()
    {
        Assert.Equal(0x80000000u, (uint)BatteryCapabilities.System);
        Assert.Equal(0x40000000u, (uint)BatteryCapabilities.Relative);
        Assert.Equal(0x20000000u, (uint)BatteryCapabilities.ShortTerm);
        Assert.Equal(0x10000000u, (uint)BatteryCapabilities.Sealed);
    }

    /// <summary>A battery counting in its own units is not energy.</summary>
    [Fact]
    public void Information_RelativeCapacityIsNotEnergy()
    {
        byte[] data = new byte[36];
        BinaryPrimitives.WriteUInt32LittleEndian(data, 0x40000000);

        Assert.False(Assert.NotNull(BatteryLayout.ReadInformation(data)).IsEnergy);
    }

    /// <summary>A chemistry shorter than four letters is padded with nulls.</summary>
    [Fact]
    public void Information_ChemistryStopsAtANull()
    {
        byte[] data = new byte[36];
        Encoding.ASCII.GetBytes("RAM").CopyTo(data, 8);

        Assert.Equal("RAM", Assert.NotNull(BatteryLayout.ReadInformation(data)).Chemistry);
    }

    /// <summary>A short answer is not read past its end.</summary>
    [Fact]
    public void Information_TooShortIsNothing()
    {
        Assert.Null(BatteryLayout.ReadInformation(new byte[35]));
    }

    /// <summary>
    /// BATTERY_STATUS: PowerState @0, Capacity @4, Voltage @8, Rate @12 as a
    /// signed LONG.
    /// </summary>
    [Fact]
    public void Status_ReadsASignedRate()
    {
        byte[] data = new byte[16];
        BinaryPrimitives.WriteUInt32LittleEndian(data, 2);
        BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(4), 40_000);
        BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(8), 11_820);
        BinaryPrimitives.WriteInt32LittleEndian(data.AsSpan(12), -9_750);

        BatteryStatus status = Assert.NotNull(BatteryLayout.ReadStatus(data));

        Assert.Equal(BatteryPowerState.Discharging, status.PowerState);
        Assert.Equal(40_000u, status.Capacity);
        Assert.Equal(11_820u, status.Voltage);
        Assert.Equal(-9_750, status.Rate);
        Assert.Equal(-9_750, status.KnownRate);
    }

    /// <summary>BATTERY_UNKNOWN_RATE is 0x80000000, which as a LONG is int.MinValue.</summary>
    [Fact]
    public void Status_UnknownRateIsNothing()
    {
        byte[] data = new byte[16];
        BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(12), 0x80000000);

        Assert.Null(Assert.NotNull(BatteryLayout.ReadStatus(data)).KnownRate);
    }

    /// <summary>The power-state flags are the header's.</summary>
    [Fact]
    public void PowerState_FlagsAreTheHeadersValues()
    {
        Assert.Equal(1u, (uint)BatteryPowerState.OnLine);
        Assert.Equal(2u, (uint)BatteryPowerState.Discharging);
        Assert.Equal(4u, (uint)BatteryPowerState.Charging);
        Assert.Equal(8u, (uint)BatteryPowerState.Critical);
    }

    /// <summary>
    /// SYSTEM_POWER_STATUS: four bytes (AC, flag, percent, saver) then two
    /// DWORDs. These are the values measured on this desktop.
    /// </summary>
    [Fact]
    public void SystemStatus_ReadsTheDesktopAsMeasured()
    {
        byte[] data = [1, 128, 255, 0, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF];

        SystemPowerStatus status = Assert.NotNull(BatteryLayout.ReadSystemStatus(data));

        Assert.False(status.HasBattery);
        Assert.True(status.OnMains);
        Assert.Null(status.Percent);
        Assert.False(status.SaverOn);
        Assert.Null(status.TimeLeft);
    }

    /// <summary>A laptop on battery with saver on and Windows' estimate.</summary>
    [Fact]
    public void SystemStatus_ReadsALaptopOnBattery()
    {
        byte[] data = new byte[12];
        data[0] = 0;
        data[1] = 2;
        data[2] = 27;
        data[3] = 1;
        BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(4), 5_400);
        BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(8), 20_000);

        SystemPowerStatus status = Assert.NotNull(BatteryLayout.ReadSystemStatus(data));

        Assert.True(status.HasBattery);
        Assert.False(status.OnMains);
        Assert.Equal(27, status.Percent);
        Assert.True(status.SaverOn);
        Assert.Equal(TimeSpan.FromMinutes(90), status.TimeLeft);
        Assert.Equal(20_000u, status.BatteryFullLifeTime);
    }

    /// <summary>A flag of 255 is "unknown", which is not a battery.</summary>
    [Fact]
    public void SystemStatus_UnknownFlagIsNoBattery()
    {
        byte[] data = [255, 255, 255, 0, 0, 0, 0, 0, 0, 0, 0, 0];

        SystemPowerStatus status = Assert.NotNull(BatteryLayout.ReadSystemStatus(data));

        Assert.False(status.HasBattery);
        Assert.Null(status.OnMains);
    }

    /// <summary>Driver strings are UTF-16 and end at a null or at what was returned.</summary>
    [Fact]
    public void Text_StopsAtTheNull()
    {
        byte[] data = new byte[64];
        Encoding.Unicode.GetBytes("DELL 7FHHV09\0garbage").CopyTo(data, 0);

        Assert.Equal("DELL 7FHHV09", BatteryLayout.ReadText(data));
        Assert.Null(BatteryLayout.ReadText(new byte[16]));
    }

    /// <summary>BATTERY_MANUFACTURE_DATE: day, month, then a 16-bit year.</summary>
    [Fact]
    public void Date_IsDayMonthYear()
    {
        byte[] data = [17, 3, 0, 0];
        BinaryPrimitives.WriteUInt16LittleEndian(data.AsSpan(2), 2023);

        Assert.Equal(new DateOnly(2023, 3, 17), BatteryLayout.ReadDate(data));
    }

    /// <summary>A date that cannot be one is nothing.</summary>
    [Fact]
    public void Date_RefusesTheImpossible()
    {
        byte[] data = [31, 2, 0, 0];
        BinaryPrimitives.WriteUInt16LittleEndian(data.AsSpan(2), 2023);

        Assert.Null(BatteryLayout.ReadDate(data));
        Assert.Null(BatteryLayout.ReadDate([0, 0, 0, 0]));
    }

    /// <summary>The temperature is tenths of a kelvin.</summary>
    [Fact]
    public void Temperature_IsTenthsOfAKelvin()
    {
        byte[] data = new byte[4];
        BinaryPrimitives.WriteUInt32LittleEndian(data, 3_082);

        Assert.Equal(35.05, Assert.NotNull(BatteryLayout.ReadCelsius(data)), 6);
    }

    /// <summary>A temperature no battery survives is a misread, not a fire.</summary>
    [Fact]
    public void Temperature_RefusesTheImplausible()
    {
        byte[] hot = new byte[4];
        BinaryPrimitives.WriteUInt32LittleEndian(hot, 4_000);

        Assert.Null(BatteryLayout.ReadCelsius(hot));
        Assert.Null(BatteryLayout.ReadCelsius(new byte[4]));
        Assert.Null(BatteryLayout.ReadCelsius([0xFF, 0xFF, 0xFF, 0xFF]));
    }

    /// <summary>BATTERY_UNKNOWN_TIME is nothing; anything else is seconds.</summary>
    [Fact]
    public void Seconds_UnknownIsNothing()
    {
        byte[] data = new byte[4];
        BinaryPrimitives.WriteUInt32LittleEndian(data, 3_600);

        Assert.Equal(TimeSpan.FromHours(1), BatteryLayout.ReadSeconds(data));
        Assert.Null(BatteryLayout.ReadSeconds([0xFF, 0xFF, 0xFF, 0xFF]));
    }

    /// <summary>CM_Get_Device_Interface_List returns paths, each ended by a null, then one more.</summary>
    [Fact]
    public void List_SplitsAtNullsAndStopsAtTheEmptyOne()
    {
        string list = "\\\\?\\ACPI#PNP0C0A#1#{72631e54}\0\\\\?\\ACPI#PNP0C0A#2#{72631e54}\0\0";

        Assert.Equal(
            ["\\\\?\\ACPI#PNP0C0A#1#{72631e54}", "\\\\?\\ACPI#PNP0C0A#2#{72631e54}"],
            BatteryLayout.SplitList(list));

        Assert.Empty(BatteryLayout.SplitList("\0"));
    }
}
