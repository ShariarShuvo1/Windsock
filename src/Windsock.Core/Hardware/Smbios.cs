using System.Globalization;
using System.Text;

namespace Windsock.Core.Hardware;

/// <summary>
/// What the firmware says is fitted to this machine.
/// </summary>
public sealed record SmbiosTables(
    string Board,
    string Bios,
    string Socket,
    int Cores,
    int Threads,
    int MaxMegahertz,
    IReadOnlyList<MemoryModule> Modules)
{
    /// <summary>What an unreadable or absent table yields.</summary>
    public static SmbiosTables Empty { get; } = new(string.Empty, string.Empty, string.Empty, 0, 0, 0, []);
}

/// <summary>
/// Reads the firmware's own description of the hardware.
/// </summary>
public static class Smbios
{
    private const byte BiosType = 0;
    private const byte BaseboardType = 2;
    private const byte ProcessorType = 4;
    private const byte MemoryDeviceType = 17;
    private const int RawHeader = 8;

    /// <summary>
    /// Reads the buffer as Windows hands it over, header and all.
    /// </summary>
    public static SmbiosTables Read(ReadOnlySpan<byte> raw)
    {
        if (raw.Length <= RawHeader)
        {
            return SmbiosTables.Empty;
        }
        int declared = BitConverter.ToInt32(raw[4..RawHeader]);
        int length = Math.Clamp(declared, 0, raw.Length - RawHeader);

        return Walk(raw.Slice(RawHeader, length));
    }

    private static SmbiosTables Walk(ReadOnlySpan<byte> table)
    {
        string board = string.Empty;
        string bios = string.Empty;
        string socket = string.Empty;
        int cores = 0;
        int threads = 0;
        int megahertz = 0;
        List<MemoryModule> modules = [];

        int at = 0;

        while (at + 4 <= table.Length)
        {
            byte type = table[at];
            byte formatted = table[at + 1];
            if (formatted < 4 || at + formatted > table.Length)
            {
                break;
            }

            ReadOnlySpan<byte> body = table.Slice(at, formatted);
            int stringsAt = at + formatted;
            int next = EndOfStrings(table, stringsAt);

            if (next <= at)
            {
                break;
            }

            ReadOnlySpan<byte> strings = table[stringsAt..next];

            switch (type)
            {
                case BiosType:
                    bios = Join(Text(body, strings, 0x04), Text(body, strings, 0x05));
                    break;

                case BaseboardType:
                    board = Join(Text(body, strings, 0x04), Text(body, strings, 0x05));
                    break;

                case ProcessorType:
                    socket = Text(body, strings, 0x04);
                    megahertz = Word(body, 0x14);
                    cores = Count(body, 0x23, 0x2A);
                    threads = Count(body, 0x25, 0x2E);
                    break;

                case MemoryDeviceType:
                    if (Module(body, strings) is { } module)
                    {
                        modules.Add(module);
                    }

                    break;

                default:
                    break;
            }

            at = next;
        }

        return new SmbiosTables(board, bios, socket, cores, threads, megahertz, modules);
    }

    private static int EndOfStrings(ReadOnlySpan<byte> table, int from)
    {
        int at = from;

        while (at < table.Length)
        {
            if (table[at] != 0)
            {
                at++;
                continue;
            }
            if (at + 1 < table.Length && table[at + 1] == 0)
            {
                return at + 2;
            }

            if (at + 1 >= table.Length)
            {
                return table.Length;
            }

            at++;
        }

        return table.Length;
    }

    private static MemoryModule? Module(ReadOnlySpan<byte> body, ReadOnlySpan<byte> strings)
    {
        long bytes = Capacity(body);
        if (bytes <= 0)
        {
            return null;
        }
        int speed = Word(body, 0x20);

        if (speed == 0)
        {
            speed = Word(body, 0x15);
        }

        return new MemoryModule(
            Text(body, strings, 0x10),
            bytes,
            speed,
            Kind(Byte(body, 0x12)),
            Text(body, strings, 0x17),
            Text(body, strings, 0x1A));
    }

    private static long Capacity(ReadOnlySpan<byte> body)
    {
        int size = Word(body, 0x0C);

        if (size == 0 || size == 0xFFFF)
        {
            return 0;
        }

        if (size == 0x7FFF)
        {
            // The extended field counts megabytes in its lower 31 bits.
            long extended = Dword(body, 0x1C) & 0x7FFFFFFF;
            return extended * 1024L * 1024L;
        }

        return (size & 0x8000) != 0
            ? (size & 0x7FFF) * 1024L
            : size * 1024L * 1024L;
    }

    private static int Count(ReadOnlySpan<byte> body, int narrow, int wide)
    {
        int value = Byte(body, narrow);

        // 0xFF is the firmware saying the real number did not fit.
        return value == 0xFF ? Word(body, wide) : value;
    }

    private static string Kind(int code) => code switch
    {
        0x12 => "DDR",
        0x13 => "DDR2",
        0x18 => "DDR3",
        0x1A => "DDR4",
        0x1B => "LPDDR",
        0x1C => "LPDDR2",
        0x1D => "LPDDR3",
        0x1E => "LPDDR4",
        0x20 => "HBM",
        0x21 => "HBM2",
        0x22 => "DDR5",
        0x23 => "LPDDR5",
        0x24 => "HBM3",
        _ => string.Empty,
    };

    private static string Text(ReadOnlySpan<byte> body, ReadOnlySpan<byte> strings, int at)
    {
        int index = Byte(body, at);

        if (index == 0)
        {
            return string.Empty;
        }

        int start = 0;

        for (int found = 1; start < strings.Length; found++)
        {
            int end = strings[start..].IndexOf((byte)0);

            if (end < 0)
            {
                end = strings.Length - start;
            }

            if (found == index)
            {
                string text = Encoding.ASCII.GetString(strings.Slice(start, end)).Trim();
                return Placeholder(text) ? string.Empty : text;
            }

            start += end + 1;
        }

        return string.Empty;
    }

    private static bool Placeholder(string text) =>
        text.Length == 0
        || text.Equals("To Be Filled By O.E.M.", StringComparison.OrdinalIgnoreCase)
        || text.Equals("To be filled by O.E.M.", StringComparison.OrdinalIgnoreCase)
        || text.Equals("Not Specified", StringComparison.OrdinalIgnoreCase)
        || text.Equals("None", StringComparison.OrdinalIgnoreCase)
        || text.Equals("Unknown", StringComparison.OrdinalIgnoreCase)
        || text.Equals("Default string", StringComparison.OrdinalIgnoreCase)
        || text.Equals("System manufacturer", StringComparison.OrdinalIgnoreCase);

    /// <summary>Two names as one line, dropping either if it is missing.</summary>
    public static string Join(string first, string second)
    {
        if (first.Length == 0)
        {
            return second;
        }

        if (second.Length == 0)
        {
            return first;
        }
        return second.StartsWith(first, StringComparison.OrdinalIgnoreCase)
            ? second
            : string.Create(CultureInfo.InvariantCulture, $"{first} {second}");
    }

    private static int Byte(ReadOnlySpan<byte> body, int at) =>
        at < body.Length ? body[at] : 0;

    private static int Word(ReadOnlySpan<byte> body, int at) =>
        at + 2 <= body.Length ? BitConverter.ToUInt16(body[at..]) : 0;

    private static long Dword(ReadOnlySpan<byte> body, int at) =>
        at + 4 <= body.Length ? BitConverter.ToUInt32(body[at..]) : 0;
}
