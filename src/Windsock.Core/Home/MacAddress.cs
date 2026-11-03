using System.Buffers.Binary;
using System.Globalization;

namespace Windsock.Core.Home;

/// <summary>
/// A 48-bit hardware address, as an Ethernet or Wi-Fi adapter carries it.
/// </summary>
/// <remarks>
/// Held as a number so it can key a table and sort, written the way Windows
/// writes it ("AA-BB-CC-DD-EE-FF"). The first octet's two low bits mean
/// something (IEEE 802-2014, 8.2.2): bit 0 set is a group (multicast)
/// address, bit 1 set is a locally administered one - the private, randomised
/// addresses phones and Windows use - which no maker registered, so no maker
/// can be named for it.
/// </remarks>
/// <param name="Value">The six octets, first octet in the high byte.</param>
public readonly record struct MacAddress(ulong Value) : IComparable<MacAddress>
{
    /// <summary>Six octets.</summary>
    public const int Length = 6;

    private const ulong Mask = 0xFFFF_FFFF_FFFF;

    /// <summary>The broadcast address, all ones.</summary>
    public static MacAddress Broadcast { get; } = new(Mask);

    /// <summary>The first octet.</summary>
    public byte First => (byte)(Value >> 40);

    /// <summary>Whether it names a group of adapters rather than one.</summary>
    public bool IsMulticast => (First & 0x01) != 0;

    /// <summary>Whether it was set locally rather than assigned by a maker: a private, randomised address.</summary>
    public bool IsLocallyAdministered => (First & 0x02) != 0;

    /// <summary>Whether it is all zeros, which no adapter has.</summary>
    public bool IsZero => (Value & Mask) == 0;

    /// <summary>Whether it is the broadcast address.</summary>
    public bool IsBroadcast => (Value & Mask) == Mask;

    /// <summary>Whether it can name one real adapter: not zero, broadcast or multicast.</summary>
    public bool IsUnicast => !IsZero && !IsBroadcast && !IsMulticast;

    /// <summary>Whether a maker can be looked up for it: one adapter, with an address a maker assigned.</summary>
    public bool HasMaker => IsUnicast && !IsLocallyAdministered;

    /// <summary>The first 24 bits, an MA-L (OUI) prefix.</summary>
    public uint Prefix24 => (uint)((Value & Mask) >> 24);

    /// <summary>The first 28 bits, an MA-M prefix.</summary>
    public uint Prefix28 => (uint)((Value & Mask) >> 20);

    /// <summary>The first 36 bits, an MA-S or IAB prefix.</summary>
    public ulong Prefix36 => (Value & Mask) >> 12;

    /// <summary>The address from six octets, or nothing when there are not six.</summary>
    public static MacAddress? From(ReadOnlySpan<byte> octets)
    {
        if (octets.Length != Length)
        {
            return null;
        }

        ulong value = 0;

        foreach (byte octet in octets)
        {
            value = (value << 8) | octet;
        }

        return new MacAddress(value);
    }

    /// <summary>
    /// Reads "AA-BB-CC-DD-EE-FF", "aa:bb:cc:dd:ee:ff", "aabb.ccdd.eeff" or twelve
    /// hex digits in a row.
    /// </summary>
    public static bool TryParse(string? text, out MacAddress address)
    {
        address = default;

        if (string.IsNullOrWhiteSpace(text))
        {
            return false;
        }

        Span<char> digits = stackalloc char[12];
        int count = 0;

        foreach (char each in text.AsSpan().Trim())
        {
            if (each is '-' or ':' or '.' or ' ')
            {
                continue;
            }

            if (!char.IsAsciiHexDigit(each) || count == digits.Length)
            {
                return false;
            }

            digits[count++] = each;
        }

        if (count != digits.Length)
        {
            return false;
        }

        address = new MacAddress(ulong.Parse(digits, NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture));
        return true;
    }

    /// <summary>The six octets, first first.</summary>
    public void WriteTo(Span<byte> destination)
    {
        if (destination.Length < Length)
        {
            throw new ArgumentException("Six bytes are needed.", nameof(destination));
        }

        Span<byte> all = stackalloc byte[8];
        BinaryPrimitives.WriteUInt64BigEndian(all, Value & Mask);
        all[2..].CopyTo(destination);
    }

    /// <summary>The six octets as an array.</summary>
    public byte[] ToBytes()
    {
        byte[] octets = new byte[Length];
        WriteTo(octets);
        return octets;
    }

    /// <inheritdoc />
    public int CompareTo(MacAddress other) => (Value & Mask).CompareTo(other.Value & Mask);

    /// <summary>The address as Windows writes it: "AA-BB-CC-DD-EE-FF".</summary>
    public override string ToString() =>
        string.Create(17, Value, static (text, value) =>
        {
            for (int index = 0; index < Length; index++)
            {
                byte octet = (byte)(value >> (40 - (8 * index)));
                text[index * 3] = HexDigit(octet >> 4);
                text[(index * 3) + 1] = HexDigit(octet & 0xF);

                if (index < Length - 1)
                {
                    text[(index * 3) + 2] = '-';
                }
            }
        });

    private static char HexDigit(int nibble) => (char)(nibble < 10 ? '0' + nibble : 'A' + nibble - 10);

    public static bool operator <(MacAddress left, MacAddress right) => left.CompareTo(right) < 0;

    public static bool operator <=(MacAddress left, MacAddress right) => left.CompareTo(right) <= 0;

    public static bool operator >(MacAddress left, MacAddress right) => left.CompareTo(right) > 0;

    public static bool operator >=(MacAddress left, MacAddress right) => left.CompareTo(right) >= 0;
}
