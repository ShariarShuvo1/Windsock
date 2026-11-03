using System.Text;

namespace Windsock.Core.Qr;

/// <summary>How much of a QR code can be damaged and still read.</summary>
public enum QrErrorCorrection
{
    /// <summary>About 7%.</summary>
    Low,

    /// <summary>About 15%.</summary>
    Medium,

    /// <summary>About 25%.</summary>
    Quartile,

    /// <summary>About 30%.</summary>
    High,
}

/// <summary>A QR code (ISO/IEC 18004), made from bytes in byte mode.</summary>
/// <remarks>
/// <para>
/// Windsock's own encoder, rather than a package, because a Wi-Fi code is the
/// only thing it draws and the format is fixed: byte mode, the smallest version
/// that fits, Reed-Solomon over GF(256) with the polynomial 0x11D, the eight
/// masks scored by the standard's four penalty rules. It follows the standard's
/// procedure step for step in the order Project Nayuki's well-known reference
/// implementation lays it out; the tables of error correction blocks are the
/// standard's own.
/// </para>
/// <para>
/// Checked module for module on 2026-10-05, for versions 1 to 40 at every
/// error correction level: against python-qrcode 8.2 with each mask forced
/// (328 symbols), and against Nayuki's qrcodegen 1.8.0 with the mask chosen
/// (320 symbols, every choice the same). QrCodeTests carries three of those
/// matrices. segno 1.6.6 pads its data differently, so its symbols differ
/// while decoding to the same bytes (zxing-cpp read both).
/// </para>
/// </remarks>
public sealed class QrCode
{
    private const int PenaltyRun = 3;
    private const int PenaltyBlock = 3;
    private const int PenaltyFinder = 40;
    private const int PenaltyBalance = 10;

    // Error correction codewords per block and number of blocks, by level and
    // version (ISO/IEC 18004 table 9); index 0 is unused.
    private static readonly int[][] CodewordsPerBlock =
    [
        [-1, 7, 10, 15, 20, 26, 18, 20, 24, 30, 18, 20, 24, 26, 30, 22, 24, 28, 30, 28, 28, 28, 28, 30, 30, 26, 28, 30, 30, 30, 30, 30, 30, 30, 30, 30, 30, 30, 30, 30, 30],
        [-1, 10, 16, 26, 18, 24, 16, 18, 22, 22, 26, 30, 22, 22, 24, 24, 28, 28, 26, 26, 26, 26, 28, 28, 28, 28, 28, 28, 28, 28, 28, 28, 28, 28, 28, 28, 28, 28, 28, 28, 28],
        [-1, 13, 22, 18, 26, 18, 24, 18, 22, 20, 24, 28, 26, 24, 20, 30, 24, 28, 28, 26, 30, 28, 30, 30, 30, 30, 28, 30, 30, 30, 30, 30, 30, 30, 30, 30, 30, 30, 30, 30, 30],
        [-1, 17, 28, 22, 16, 22, 28, 26, 26, 24, 28, 24, 28, 22, 24, 24, 30, 28, 28, 26, 28, 30, 24, 30, 30, 30, 30, 30, 30, 30, 30, 30, 30, 30, 30, 30, 30, 30, 30, 30, 30],
    ];

    private static readonly int[][] Blocks =
    [
        [-1, 1, 1, 1, 1, 1, 2, 2, 2, 2, 4, 4, 4, 4, 4, 6, 6, 6, 6, 7, 8, 8, 9, 9, 10, 12, 12, 12, 13, 14, 15, 16, 17, 18, 19, 19, 20, 21, 22, 24, 25],
        [-1, 1, 1, 1, 2, 2, 4, 4, 4, 5, 5, 5, 8, 9, 9, 10, 10, 11, 13, 14, 16, 17, 17, 18, 20, 21, 23, 25, 26, 28, 29, 31, 33, 35, 37, 38, 40, 43, 45, 47, 49],
        [-1, 1, 1, 2, 2, 4, 4, 6, 6, 8, 8, 8, 10, 12, 16, 12, 17, 16, 18, 21, 20, 23, 23, 25, 27, 29, 34, 34, 35, 38, 40, 43, 45, 48, 51, 53, 56, 59, 62, 65, 68],
        [-1, 1, 1, 2, 4, 4, 4, 5, 6, 8, 8, 11, 11, 16, 16, 18, 16, 19, 21, 25, 25, 25, 34, 30, 32, 35, 37, 40, 42, 45, 48, 51, 54, 57, 60, 63, 66, 70, 74, 77, 81],
    ];

    private readonly bool[,] _modules;
    private readonly bool[,] _function;

    private QrCode(int version, QrErrorCorrection level, byte[] codewords, int mask)
    {
        Version = version;
        ErrorCorrection = level;
        Size = (version * 4) + 17;
        _modules = new bool[Size, Size];
        _function = new bool[Size, Size];

        DrawFunctionPatterns();
        byte[] all = AddErrorCorrection(codewords);
        DrawCodewords(all);

        if (mask < 0)
        {
            int best = int.MaxValue;

            for (int each = 0; each < 8; each++)
            {
                ApplyMask(each);
                DrawFormatBits(each);
                int penalty = Penalty();

                if (penalty < best)
                {
                    mask = each;
                    best = penalty;
                }

                ApplyMask(each);
            }
        }

        Mask = mask;
        ApplyMask(mask);
        DrawFormatBits(mask);
    }

    /// <summary>The version, 1 to 40.</summary>
    public int Version { get; }

    /// <summary>Modules on a side.</summary>
    public int Size { get; }

    /// <summary>The error correction level.</summary>
    public QrErrorCorrection ErrorCorrection { get; }

    /// <summary>The mask used, 0 to 7.</summary>
    public int Mask { get; }

    /// <summary>Whether the module at a column and row is dark.</summary>
    public bool this[int x, int y] => x >= 0 && y >= 0 && x < Size && y < Size && _modules[y, x];

    /// <summary>Encodes text as UTF-8 bytes.</summary>
    public static QrCode EncodeText(string text, QrErrorCorrection level = QrErrorCorrection.Medium)
    {
        ArgumentNullException.ThrowIfNull(text);
        return EncodeBytes(Encoding.UTF8.GetBytes(text), level);
    }

    /// <summary>
    /// Encodes bytes in the smallest version that holds them.
    /// </summary>
    /// <param name="data">The bytes.</param>
    /// <param name="level">The error correction level.</param>
    /// <param name="mask">A mask to force, or -1 to choose the best.</param>
    /// <param name="version">A version to force, or 0 for the smallest that fits.</param>
    public static QrCode EncodeBytes(ReadOnlySpan<byte> data, QrErrorCorrection level, int mask = -1, int version = 0)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(mask, -1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(mask, 7);

        int chosen = 0;

        for (int each = version > 0 ? version : 1; each <= (version > 0 ? version : 40); each++)
        {
            int bits = 4 + CountBits(each) + (data.Length * 8);

            if (bits <= DataCodewords(each, level) * 8)
            {
                chosen = each;
                break;
            }
        }

        if (chosen == 0)
        {
            throw new ArgumentException("Too much to fit in a QR code.", nameof(data));
        }

        int capacity = DataCodewords(chosen, level) * 8;
        BitWriter writer = new();
        writer.Append(0b0100, 4);
        writer.Append(data.Length, CountBits(chosen));

        foreach (byte each in data)
        {
            writer.Append(each, 8);
        }

        writer.Append(0, Math.Min(4, capacity - writer.Length));
        writer.Append(0, (8 - (writer.Length % 8)) % 8);

        for (int pad = 0xEC; writer.Length < capacity; pad ^= 0xEC ^ 0x11)
        {
            writer.Append(pad, 8);
        }

        return new QrCode(chosen, level, writer.ToBytes(), mask);
    }

    /// <summary>How many data codewords a version and level hold.</summary>
    public static int DataCodewords(int version, QrErrorCorrection level) =>
        (RawDataModules(version) / 8) - (CodewordsPerBlock[(int)level][version] * Blocks[(int)level][version]);

    /// <summary>Modules left for data and error correction once the function patterns are drawn.</summary>
    public static int RawDataModules(int version)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(version, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(version, 40);

        int result = (((16 * version) + 128) * version) + 64;

        if (version >= 2)
        {
            int aligns = (version / 7) + 2;
            result -= (((25 * aligns) - 10) * aligns) - 55;

            if (version >= 7)
            {
                result -= 36;
            }
        }

        return result;
    }

    /// <summary>The rows and columns alignment patterns are centred on.</summary>
    public static int[] AlignmentPositions(int version)
    {
        if (version == 1)
        {
            return [];
        }

        int count = (version / 7) + 2;
        int step = version == 32 ? 26 : ((version * 4) + (count * 2) + 1) / ((count * 2) - 2) * 2;
        int[] positions = new int[count];
        positions[0] = 6;

        for (int each = count - 1, at = (version * 4) + 17 - 7; each >= 1; each--, at -= step)
        {
            positions[each] = at;
        }

        return positions;
    }

    private static int CountBits(int version) => version <= 9 ? 8 : 16;

    private void DrawFunctionPatterns()
    {
        for (int each = 0; each < Size; each++)
        {
            SetFunction(6, each, each % 2 == 0);
            SetFunction(each, 6, each % 2 == 0);
        }

        DrawFinder(3, 3);
        DrawFinder(Size - 4, 3);
        DrawFinder(3, Size - 4);

        int[] aligns = AlignmentPositions(Version);

        for (int i = 0; i < aligns.Length; i++)
        {
            for (int j = 0; j < aligns.Length; j++)
            {
                bool corner = (i == 0 && j == 0) || (i == 0 && j == aligns.Length - 1) || (i == aligns.Length - 1 && j == 0);

                if (!corner)
                {
                    DrawAlignment(aligns[i], aligns[j]);
                }
            }
        }

        // Reserve the format areas now; the real bits go in once the mask is known.
        DrawFormatBits(0);
        DrawVersion();
    }

    private void DrawFinder(int x, int y)
    {
        for (int dy = -4; dy <= 4; dy++)
        {
            for (int dx = -4; dx <= 4; dx++)
            {
                int distance = Math.Max(Math.Abs(dx), Math.Abs(dy));
                int column = x + dx;
                int row = y + dy;

                if (column >= 0 && column < Size && row >= 0 && row < Size)
                {
                    SetFunction(column, row, distance != 2 && distance != 4);
                }
            }
        }
    }

    private void DrawAlignment(int x, int y)
    {
        for (int dy = -2; dy <= 2; dy++)
        {
            for (int dx = -2; dx <= 2; dx++)
            {
                SetFunction(x + dx, y + dy, Math.Max(Math.Abs(dx), Math.Abs(dy)) != 1);
            }
        }
    }

    private void DrawFormatBits(int mask)
    {
        int levelBits = ErrorCorrection switch
        {
            QrErrorCorrection.Low => 1,
            QrErrorCorrection.Medium => 0,
            QrErrorCorrection.Quartile => 3,
            _ => 2,
        };

        int data = (levelBits << 3) | mask;
        int remainder = data;

        for (int each = 0; each < 10; each++)
        {
            remainder = (remainder << 1) ^ ((remainder >> 9) * 0x537);
        }

        int bits = ((data << 10) | remainder) ^ 0x5412;

        for (int i = 0; i <= 5; i++)
        {
            SetFunction(8, i, Bit(bits, i));
        }

        SetFunction(8, 7, Bit(bits, 6));
        SetFunction(8, 8, Bit(bits, 7));
        SetFunction(7, 8, Bit(bits, 8));

        for (int i = 9; i < 15; i++)
        {
            SetFunction(14 - i, 8, Bit(bits, i));
        }

        for (int i = 0; i < 8; i++)
        {
            SetFunction(Size - 1 - i, 8, Bit(bits, i));
        }

        for (int i = 8; i < 15; i++)
        {
            SetFunction(8, Size - 15 + i, Bit(bits, i));
        }

        SetFunction(8, Size - 8, true);
    }

    private void DrawVersion()
    {
        if (Version < 7)
        {
            return;
        }

        int remainder = Version;

        for (int each = 0; each < 12; each++)
        {
            remainder = (remainder << 1) ^ ((remainder >> 11) * 0x1F25);
        }

        int bits = (Version << 12) | remainder;

        for (int i = 0; i < 18; i++)
        {
            bool dark = Bit(bits, i);
            int a = Size - 11 + (i % 3);
            int b = i / 3;
            SetFunction(a, b, dark);
            SetFunction(b, a, dark);
        }
    }

    private void SetFunction(int x, int y, bool dark)
    {
        _modules[y, x] = dark;
        _function[y, x] = true;
    }

    private byte[] AddErrorCorrection(byte[] data)
    {
        int level = (int)ErrorCorrection;
        int blocks = Blocks[level][Version];
        int eccLength = CodewordsPerBlock[level][Version];
        int raw = RawDataModules(Version) / 8;
        int shortBlocks = blocks - (raw % blocks);
        int shortLength = raw / blocks;

        byte[] divisor = Divisor(eccLength);
        List<byte[]> made = new(blocks);

        for (int each = 0, at = 0; each < blocks; each++)
        {
            int dataLength = shortLength - eccLength + (each < shortBlocks ? 0 : 1);
            byte[] block = new byte[shortLength + 1];
            Array.Copy(data, at, block, 0, dataLength);
            at += dataLength;

            byte[] ecc = Remainder(block.AsSpan(0, dataLength), divisor);

            // Short blocks leave a hole before their correction bytes, so every
            // block's correction bytes line up when interleaved.
            ecc.CopyTo(block, shortLength + 1 - eccLength);
            made.Add(block);
        }

        byte[] result = new byte[raw];

        for (int column = 0, written = 0; column < shortLength + 1; column++)
        {
            for (int each = 0; each < made.Count; each++)
            {
                if (column != shortLength - eccLength || each >= shortBlocks)
                {
                    result[written++] = made[each][column];
                }
            }
        }

        return result;
    }

    private void DrawCodewords(byte[] data)
    {
        int bit = 0;

        for (int right = Size - 1; right >= 1; right -= 2)
        {
            if (right == 6)
            {
                right = 5;
            }

            for (int vertical = 0; vertical < Size; vertical++)
            {
                for (int j = 0; j < 2; j++)
                {
                    int x = right - j;
                    bool upward = ((right + 1) & 2) == 0;
                    int y = upward ? Size - 1 - vertical : vertical;

                    if (!_function[y, x] && bit < data.Length * 8)
                    {
                        _modules[y, x] = Bit(data[bit >> 3], 7 - (bit & 7));
                        bit++;
                    }
                }
            }
        }
    }

    private void ApplyMask(int mask)
    {
        for (int y = 0; y < Size; y++)
        {
            for (int x = 0; x < Size; x++)
            {
                bool invert = mask switch
                {
                    0 => (x + y) % 2 == 0,
                    1 => y % 2 == 0,
                    2 => x % 3 == 0,
                    3 => (x + y) % 3 == 0,
                    4 => ((x / 3) + (y / 2)) % 2 == 0,
                    5 => ((x * y) % 2) + ((x * y) % 3) == 0,
                    6 => (((x * y) % 2) + ((x * y) % 3)) % 2 == 0,
                    _ => (((x + y) % 2) + ((x * y) % 3)) % 2 == 0,
                };

                if (invert && !_function[y, x])
                {
                    _modules[y, x] = !_modules[y, x];
                }
            }
        }
    }

    private int Penalty()
    {
        int result = 0;

        for (int pass = 0; pass < 2; pass++)
        {
            bool rows = pass == 0;

            for (int line = 0; line < Size; line++)
            {
                bool colour = false;
                int run = 0;
                int[] history = new int[7];

                for (int along = 0; along < Size; along++)
                {
                    bool here = rows ? _modules[line, along] : _modules[along, line];

                    if (here == colour)
                    {
                        run++;

                        if (run == 5)
                        {
                            result += PenaltyRun;
                        }
                        else if (run > 5)
                        {
                            result++;
                        }
                    }
                    else
                    {
                        AddHistory(run, history);

                        if (!colour)
                        {
                            result += FinderLike(history) * PenaltyFinder;
                        }

                        colour = here;
                        run = 1;
                    }
                }

                result += FinderTerminated(colour, run, history) * PenaltyFinder;
            }
        }

        for (int y = 0; y < Size - 1; y++)
        {
            for (int x = 0; x < Size - 1; x++)
            {
                bool colour = _modules[y, x];

                if (colour == _modules[y, x + 1] && colour == _modules[y + 1, x] && colour == _modules[y + 1, x + 1])
                {
                    result += PenaltyBlock;
                }
            }
        }

        int dark = 0;

        foreach (bool module in _modules)
        {
            if (module)
            {
                dark++;
            }
        }

        int total = Size * Size;
        int k = ((Math.Abs((dark * 20) - (total * 10)) + total - 1) / total) - 1;
        result += k * PenaltyBalance;
        return result;
    }

    // The finder-like rule (1:1:3:1:1 with four light modules on a side),
    // counted over a history of run lengths where the area beyond the symbol
    // counts as light.
    private static int FinderLike(int[] history)
    {
        int n = history[1];
        bool core = n > 0 && history[2] == n && history[3] == n * 3 && history[4] == n && history[5] == n;

        return (core && history[0] >= n * 4 && history[6] >= n ? 1 : 0)
            + (core && history[6] >= n * 4 && history[0] >= n ? 1 : 0);
    }

    private int FinderTerminated(bool colour, int run, int[] history)
    {
        if (colour)
        {
            AddHistory(run, history);
            run = 0;
        }

        run += Size;
        AddHistory(run, history);
        return FinderLike(history);
    }

    private void AddHistory(int run, int[] history)
    {
        if (history[0] == 0)
        {
            // The light border before the first run.
            run += Size;
        }

        Array.Copy(history, 0, history, 1, history.Length - 1);
        history[0] = run;
    }

    private static byte[] Divisor(int degree)
    {
        byte[] result = new byte[degree];
        result[degree - 1] = 1;
        int root = 1;

        for (int i = 0; i < degree; i++)
        {
            for (int j = 0; j < result.Length; j++)
            {
                result[j] = Multiply(result[j], root);

                if (j + 1 < result.Length)
                {
                    result[j] ^= result[j + 1];
                }
            }

            root = Multiply(root, 0x02);
        }

        return result;
    }

    private static byte[] Remainder(ReadOnlySpan<byte> data, byte[] divisor)
    {
        byte[] result = new byte[divisor.Length];

        foreach (byte each in data)
        {
            int factor = each ^ result[0];
            Array.Copy(result, 1, result, 0, result.Length - 1);
            result[^1] = 0;

            for (int i = 0; i < result.Length; i++)
            {
                result[i] ^= Multiply(divisor[i], factor);
            }
        }

        return result;
    }

    private static byte Multiply(int x, int y)
    {
        int z = 0;

        for (int i = 7; i >= 0; i--)
        {
            z = (z << 1) ^ ((z >> 7) * 0x11D);
            z ^= ((y >> i) & 1) * x;
        }

        return (byte)z;
    }

    private static bool Bit(int value, int index) => ((value >> index) & 1) != 0;

    private sealed class BitWriter
    {
        private readonly List<bool> _bits = [];

        public int Length => _bits.Count;

        public void Append(int value, int count)
        {
            for (int i = count - 1; i >= 0; i--)
            {
                _bits.Add(((value >> i) & 1) != 0);
            }
        }

        public byte[] ToBytes()
        {
            byte[] bytes = new byte[_bits.Count / 8];

            for (int i = 0; i < _bits.Count; i++)
            {
                if (_bits[i])
                {
                    bytes[i >> 3] |= (byte)(0x80 >> (i & 7));
                }
            }

            return bytes;
        }
    }
}
