using System.Buffers.Binary;
using System.Text;

namespace Windsock.Core.Power;

/// <summary>
/// The battery class driver's structures and control codes, as
/// <c>poclass.h</c> in the Windows SDK declares them, and the
/// <c>SYSTEM_POWER_STATUS</c> that <c>GetSystemPowerStatus</c> fills.
/// </summary>
/// <remarks>
/// Every structure here is a run of 32-bit fields with no pointer in it, so
/// its layout is the same in a 32- and a 64-bit process. They are read and
/// written as bytes rather than marshalled, so the tests can hold each one to
/// the header field by field: a field read from the wrong offset does not
/// throw, it returns a plausible number.
/// </remarks>
public static class BatteryLayout
{
    /// <summary><c>FILE_DEVICE_BATTERY</c>.</summary>
    public const uint DeviceType = 0x29;

    /// <summary><c>IOCTL_BATTERY_QUERY_TAG</c>: function 0x10, buffered, read access.</summary>
    public static uint QueryTag { get; } = ControlCode(DeviceType, 0x10);

    /// <summary><c>IOCTL_BATTERY_QUERY_INFORMATION</c>: function 0x11.</summary>
    public static uint QueryInformation { get; } = ControlCode(DeviceType, 0x11);

    /// <summary><c>IOCTL_BATTERY_QUERY_STATUS</c>: function 0x13.</summary>
    public static uint QueryStatus { get; } = ControlCode(DeviceType, 0x13);

    /// <summary><c>BATTERY_TAG_INVALID</c>: no battery in the slot.</summary>
    public const uint NoTag = 0;

    /// <summary><c>BATTERY_UNKNOWN_CAPACITY</c>, also the unknown voltage and time.</summary>
    public const uint Unknown = 0xFFFFFFFF;

    /// <summary><c>BATTERY_UNKNOWN_RATE</c>: the rate's sign bit alone.</summary>
    public const uint UnknownRate = 0x80000000;

    /// <summary>The size of <c>BATTERY_QUERY_INFORMATION</c>.</summary>
    public const int QuerySize = 12;

    /// <summary>The size of <c>BATTERY_INFORMATION</c>.</summary>
    public const int InformationSize = 36;

    /// <summary>The size of <c>BATTERY_WAIT_STATUS</c>.</summary>
    public const int WaitSize = 20;

    /// <summary>The size of <c>BATTERY_STATUS</c>.</summary>
    public const int StatusSize = 16;

    /// <summary>The size of <c>SYSTEM_POWER_STATUS</c>.</summary>
    public const int SystemStatusSize = 12;

    /// <summary>
    /// <c>MAX_BATTERY_STRING_SIZE</c>: the most a driver's string answer is
    /// meant to take, in bytes. Asked for with room to spare.
    /// </summary>
    public const int StringSize = 128;

    /// <summary>
    /// <c>CTL_CODE(type, function, METHOD_BUFFERED, FILE_READ_ACCESS)</c>.
    /// </summary>
    public static uint ControlCode(uint deviceType, uint function) =>
        (deviceType << 16) | (ReadAccess << 14) | (function << 2) | Buffered;

    private const uint Buffered = 0;
    private const uint ReadAccess = 1;

    /// <summary>Writes a <c>BATTERY_QUERY_INFORMATION</c>.</summary>
    public static byte[] Query(uint tag, BatteryInformationLevel level, uint atRate = 0)
    {
        byte[] query = new byte[QuerySize];
        BinaryPrimitives.WriteUInt32LittleEndian(query.AsSpan(0), tag);
        BinaryPrimitives.WriteUInt32LittleEndian(query.AsSpan(4), (uint)level);
        BinaryPrimitives.WriteUInt32LittleEndian(query.AsSpan(8), atRate);
        return query;
    }

    /// <summary>
    /// Writes a <c>BATTERY_WAIT_STATUS</c> that does not wait: a timeout of
    /// zero answers at once with the battery as it is.
    /// </summary>
    public static byte[] Wait(uint tag)
    {
        byte[] wait = new byte[WaitSize];
        BinaryPrimitives.WriteUInt32LittleEndian(wait.AsSpan(0), tag);
        return wait;
    }

    /// <summary>Reads a <c>BATTERY_INFORMATION</c>.</summary>
    public static BatteryInformation? ReadInformation(ReadOnlySpan<byte> data)
    {
        if (data.Length < InformationSize)
        {
            return null;
        }

        return new BatteryInformation(
            (BatteryCapabilities)BinaryPrimitives.ReadUInt32LittleEndian(data),
            data[4] != 0,
            Chemistry(data.Slice(8, 4)),
            BinaryPrimitives.ReadUInt32LittleEndian(data[12..]),
            BinaryPrimitives.ReadUInt32LittleEndian(data[16..]),
            BinaryPrimitives.ReadUInt32LittleEndian(data[20..]),
            BinaryPrimitives.ReadUInt32LittleEndian(data[24..]),
            BinaryPrimitives.ReadUInt32LittleEndian(data[32..]));
    }

    /// <summary>Reads a <c>BATTERY_STATUS</c>.</summary>
    public static BatteryStatus? ReadStatus(ReadOnlySpan<byte> data)
    {
        if (data.Length < StatusSize)
        {
            return null;
        }

        return new BatteryStatus(
            (BatteryPowerState)BinaryPrimitives.ReadUInt32LittleEndian(data),
            BinaryPrimitives.ReadUInt32LittleEndian(data[4..]),
            BinaryPrimitives.ReadUInt32LittleEndian(data[8..]),
            BinaryPrimitives.ReadInt32LittleEndian(data[12..]));
    }

    /// <summary>Reads a <c>SYSTEM_POWER_STATUS</c>.</summary>
    public static SystemPowerStatus? ReadSystemStatus(ReadOnlySpan<byte> data)
    {
        if (data.Length < SystemStatusSize)
        {
            return null;
        }

        return new SystemPowerStatus(
            data[0],
            data[1],
            data[2],
            data[3],
            BinaryPrimitives.ReadUInt32LittleEndian(data[4..]),
            BinaryPrimitives.ReadUInt32LittleEndian(data[8..]));
    }

    /// <summary>
    /// Reads a driver's string answer: UTF-16, ended by a null or by the
    /// bytes returned, whichever comes first.
    /// </summary>
    public static string? ReadText(ReadOnlySpan<byte> data)
    {
        int length = data.Length & ~1;
        string text = Encoding.Unicode.GetString(data[..length]);
        int end = text.IndexOf('\0', StringComparison.Ordinal);

        if (end >= 0)
        {
            text = text[..end];
        }

        text = text.Trim();
        return text.Length > 0 ? text : null;
    }

    /// <summary>
    /// Reads a <c>BATTERY_MANUFACTURE_DATE</c>: day, month, then a 16-bit year.
    /// </summary>
    public static DateOnly? ReadDate(ReadOnlySpan<byte> data)
    {
        if (data.Length < 4)
        {
            return null;
        }

        int day = data[0];
        int month = data[1];
        int year = BinaryPrimitives.ReadUInt16LittleEndian(data[2..]);

        if (year is < 1990 or > 2100 || month is < 1 or > 12 || day < 1 || day > DateTime.DaysInMonth(year, month))
        {
            return null;
        }

        return new DateOnly(year, month, day);
    }

    /// <summary>
    /// Reads a <c>BatteryTemperature</c> answer: tenths of a kelvin. Nothing
    /// outside what a battery could survive is believed.
    /// </summary>
    public static double? ReadCelsius(ReadOnlySpan<byte> data)
    {
        if (data.Length < 4)
        {
            return null;
        }

        uint tenths = BinaryPrimitives.ReadUInt32LittleEndian(data);

        if (tenths is 0 or Unknown)
        {
            return null;
        }

        double celsius = (tenths / 10.0) - 273.15;
        return celsius is >= -30 and <= 90 ? celsius : null;
    }

    /// <summary>Reads a <c>BatteryEstimatedTime</c> answer: seconds, or unknown.</summary>
    public static TimeSpan? ReadSeconds(ReadOnlySpan<byte> data)
    {
        if (data.Length < 4)
        {
            return null;
        }

        uint seconds = BinaryPrimitives.ReadUInt32LittleEndian(data);
        return seconds == Unknown ? null : TimeSpan.FromSeconds(seconds);
    }

    /// <summary>Reads the DWORD a tag query answers with.</summary>
    public static uint ReadTag(ReadOnlySpan<byte> data) =>
        data.Length < 4 ? NoTag : BinaryPrimitives.ReadUInt32LittleEndian(data);

    /// <summary>
    /// Splits a <c>REG_MULTI_SZ</c>-style list, as
    /// <c>CM_Get_Device_Interface_List</c> returns the battery paths.
    /// </summary>
    public static IReadOnlyList<string> SplitList(ReadOnlySpan<char> list)
    {
        List<string> items = [];
        int start = 0;

        for (int at = 0; at < list.Length; at++)
        {
            if (list[at] != '\0')
            {
                continue;
            }

            if (at == start)
            {
                break;
            }

            items.Add(list[start..at].ToString());
            start = at + 1;
        }

        return items;
    }

    private static string Chemistry(ReadOnlySpan<byte> code)
    {
        Span<char> letters = stackalloc char[4];
        int count = 0;

        foreach (byte value in code)
        {
            if (value is 0)
            {
                break;
            }

            letters[count++] = value is >= 0x20 and < 0x7F ? (char)value : '?';
        }

        return new string(letters[..count]).Trim();
    }
}

/// <summary>What a <c>BATTERY_QUERY_INFORMATION</c> asks for.</summary>
public enum BatteryInformationLevel : uint
{
    Information = 0,

    Granularity = 1,

    Temperature = 2,

    EstimatedTime = 3,

    DeviceName = 4,

    ManufactureDate = 5,

    ManufactureName = 6,

    UniqueId = 7,

    SerialNumber = 8,
}

/// <summary>The <c>BATTERY_INFORMATION.Capabilities</c> flags.</summary>
[Flags]
public enum BatteryCapabilities : uint
{
    None = 0,

    /// <summary><c>BATTERY_SEALED</c>.</summary>
    Sealed = 0x10000000,

    /// <summary><c>BATTERY_IS_SHORT_TERM</c>: a UPS, not a laptop's cell.</summary>
    ShortTerm = 0x20000000,

    /// <summary>
    /// <c>BATTERY_CAPACITY_RELATIVE</c>: the capacities are in units of the
    /// driver's own, not milliwatt-hours, so neither they nor the rate are
    /// energy.
    /// </summary>
    Relative = 0x40000000,

    /// <summary><c>BATTERY_SYSTEM_BATTERY</c>: it powers the machine.</summary>
    System = 0x80000000,
}

/// <summary>The <c>BATTERY_STATUS.PowerState</c> flags.</summary>
[Flags]
public enum BatteryPowerState : uint
{
    None = 0,

    /// <summary><c>BATTERY_POWER_ON_LINE</c>: the machine is on mains power.</summary>
    OnLine = 1,

    /// <summary><c>BATTERY_DISCHARGING</c>.</summary>
    Discharging = 2,

    /// <summary><c>BATTERY_CHARGING</c>.</summary>
    Charging = 4,

    /// <summary><c>BATTERY_CRITICAL</c>.</summary>
    Critical = 8,
}

/// <summary>A <c>BATTERY_INFORMATION</c>, read.</summary>
/// <param name="Capabilities">What the battery and its figures are.</param>
/// <param name="Rechargeable">The <c>Technology</c> byte: 1 rechargeable, 0 not.</param>
/// <param name="Chemistry">The four-letter code, such as <c>LION</c>.</param>
/// <param name="DesignedCapacity">What it held new, mWh (or relative units).</param>
/// <param name="FullChargedCapacity">What it holds full today.</param>
/// <param name="DefaultAlert1">The manufacturer's low warning.</param>
/// <param name="DefaultAlert2">The manufacturer's critical warning.</param>
/// <param name="CycleCount">Charge cycles; zero where the battery does not count them.</param>
public readonly record struct BatteryInformation(
    BatteryCapabilities Capabilities,
    bool Rechargeable,
    string Chemistry,
    uint DesignedCapacity,
    uint FullChargedCapacity,
    uint DefaultAlert1,
    uint DefaultAlert2,
    uint CycleCount)
{
    /// <summary>Whether the capacities are energy rather than the driver's own units.</summary>
    public bool IsEnergy => (Capabilities & BatteryCapabilities.Relative) == 0;
}

/// <summary>A <c>BATTERY_STATUS</c>, read.</summary>
/// <param name="PowerState">Mains, charging, discharging, critical.</param>
/// <param name="Capacity">What it holds now, mWh (or relative units); <see cref="BatteryLayout.Unknown"/> if not known.</param>
/// <param name="Voltage">Millivolts, or <see cref="BatteryLayout.Unknown"/>.</param>
/// <param name="Rate">
/// Milliwatts, signed: positive charging, negative discharging, or
/// <see cref="BatteryLayout.UnknownRate"/> read as an <see cref="int"/>.
/// </param>
public readonly record struct BatteryStatus(
    BatteryPowerState PowerState,
    uint Capacity,
    uint Voltage,
    int Rate)
{
    /// <summary>The rate, or nothing where the driver does not know it.</summary>
    public int? KnownRate => Rate == unchecked((int)BatteryLayout.UnknownRate) ? null : Rate;
}

/// <summary>A <c>SYSTEM_POWER_STATUS</c>, read.</summary>
/// <param name="AcLineStatus">0 offline, 1 online, 255 unknown.</param>
/// <param name="BatteryFlag">1 high, 2 low, 4 critical, 8 charging, 128 no battery, 255 unknown.</param>
/// <param name="BatteryLifePercent">0 to 100, or 255 unknown.</param>
/// <param name="SystemStatusFlag">1 while battery (energy) saver is on.</param>
/// <param name="BatteryLifeTime">Seconds left, or <see cref="BatteryLayout.Unknown"/> (always on mains).</param>
/// <param name="BatteryFullLifeTime">Seconds a full charge lasts, or unknown.</param>
public readonly record struct SystemPowerStatus(
    byte AcLineStatus,
    byte BatteryFlag,
    byte BatteryLifePercent,
    byte SystemStatusFlag,
    uint BatteryLifeTime,
    uint BatteryFullLifeTime)
{
    private const byte NoBatteryFlag = 128;
    private const byte UnknownByte = 255;

    /// <summary>Nothing read.</summary>
    public static SystemPowerStatus Unread { get; } = new(UnknownByte, UnknownByte, UnknownByte, 0, BatteryLayout.Unknown, BatteryLayout.Unknown);

    /// <summary>Whether Windows says there is a system battery. Unknown counts as no.</summary>
    public bool HasBattery => BatteryFlag != UnknownByte && (BatteryFlag & NoBatteryFlag) == 0;

    /// <summary>Whether the machine is on mains power, or nothing where Windows does not know.</summary>
    public bool? OnMains => AcLineStatus switch
    {
        0 => false,
        1 => true,
        _ => null,
    };

    /// <summary>Windows' own percentage, or nothing.</summary>
    public int? Percent => BatteryLifePercent <= 100 ? BatteryLifePercent : null;

    /// <summary>Whether battery (energy) saver is on.</summary>
    public bool SaverOn => (SystemStatusFlag & 1) != 0;

    /// <summary>Windows' estimate of the time left, or nothing.</summary>
    public TimeSpan? TimeLeft => BatteryLifeTime == BatteryLayout.Unknown ? null : TimeSpan.FromSeconds(BatteryLifeTime);
}
