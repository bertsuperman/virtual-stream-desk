using System.Text;

namespace VirtualStreamDeck.Core.Remote;

/// <summary>
/// Minimal QR code encoder: byte mode, error correction level M, versions 1–10
/// (up to 213 bytes — plenty for a LAN URL with a token). No dependencies.
/// Follows ISO/IEC 18004; structure modelled on Project Nayuki's reference implementation.
/// </summary>
public sealed class QrCode
{
    // Error correction level M, per version (index = version):
    // EC codewords per block, and the block layout (count, data codewords) groups.
    private static readonly int[] EcPerBlock = { 0, 10, 16, 26, 18, 24, 16, 18, 22, 22, 26 };
    private static readonly (int Count, int Data)[][] Blocks =
    {
        Array.Empty<(int, int)>(),
        new[] { (1, 16) },
        new[] { (1, 28) },
        new[] { (1, 44) },
        new[] { (2, 32) },
        new[] { (2, 43) },
        new[] { (4, 27) },
        new[] { (4, 31) },
        new[] { (2, 38), (2, 39) },
        new[] { (3, 36), (2, 37) },
        new[] { (4, 43), (1, 44) },
    };
    private static readonly int[][] AlignmentPositions =
    {
        Array.Empty<int>(), Array.Empty<int>(),
        new[] { 6, 18 }, new[] { 6, 22 }, new[] { 6, 26 }, new[] { 6, 30 }, new[] { 6, 34 },
        new[] { 6, 22, 38 }, new[] { 6, 24, 42 }, new[] { 6, 26, 46 }, new[] { 6, 28, 50 },
    };
    private const int MaxVersion = 10;
    private const int FormatBitsM = 0; // L=1, M=0, Q=3, H=2

    private readonly bool[,] _modules;
    private readonly bool[,] _isFunction;

    public int Version { get; }
    public int Size { get; }
    public int Mask { get; private set; }

    /// <summary>True = dark module. [row, column]</summary>
    public bool this[int row, int col] => _modules[row, col];

    private QrCode(int version)
    {
        Version = version;
        Size = version * 4 + 17;
        _modules = new bool[Size, Size];
        _isFunction = new bool[Size, Size];
    }

    public static QrCode Encode(string text) => Encode(Encoding.UTF8.GetBytes(text));

    public static QrCode Encode(byte[] data)
    {
        int version = 1;
        while (version <= MaxVersion && data.Length > MaxPayloadBytes(version)) version++;
        if (version > MaxVersion) throw new ArgumentException(L.T($"Text je pre QR kód príliš dlhý ({data.Length} B).", $"The text is too long for a QR code ({data.Length} B)."));

        var qr = new QrCode(version);
        var codewords = qr.AddErrorCorrection(qr.EncodeData(data));
        qr.DrawFunctionPatterns();
        qr.DrawCodewords(codewords);
        qr.ApplyBestMask();
        return qr;
    }

    private static int DataCodewords(int version) => Blocks[version].Sum(b => b.Count * b.Data);

    private static int MaxPayloadBytes(int version)
    {
        int countBits = version < 10 ? 8 : 16;
        return (DataCodewords(version) * 8 - 4 - countBits) / 8;
    }

    // ───────────────────────── data ─────────────────────────

    private byte[] EncodeData(byte[] data)
    {
        var bits = new List<bool>();
        void Append(int value, int length)
        {
            for (int i = length - 1; i >= 0; i--) bits.Add(((value >> i) & 1) != 0);
        }

        Append(0b0100, 4); // byte mode
        Append(data.Length, Version < 10 ? 8 : 16);
        foreach (var b in data) Append(b, 8);

        int capacityBits = DataCodewords(Version) * 8;
        Append(0, Math.Min(4, capacityBits - bits.Count)); // terminator
        Append(0, (8 - bits.Count % 8) % 8);               // byte align
        for (int pad = 0xEC; bits.Count < capacityBits; pad ^= 0xEC ^ 0x11) Append(pad, 8);

        var result = new byte[bits.Count / 8];
        for (int i = 0; i < bits.Count; i++)
            if (bits[i]) result[i >> 3] |= (byte)(0x80 >> (i & 7));
        return result;
    }

    private byte[] AddErrorCorrection(byte[] data)
    {
        int ecLen = EcPerBlock[Version];
        var divisor = ReedSolomonDivisor(ecLen);

        var dataBlocks = new List<byte[]>();
        var ecBlocks = new List<byte[]>();
        int offset = 0;
        foreach (var (count, len) in Blocks[Version])
        {
            for (int i = 0; i < count; i++)
            {
                var block = data[offset..(offset + len)];
                offset += len;
                dataBlocks.Add(block);
                ecBlocks.Add(ReedSolomonRemainder(block, divisor));
            }
        }

        // Interleave: data codewords column by column (shorter blocks just run out), then EC codewords.
        var result = new List<byte>();
        int maxData = dataBlocks.Max(b => b.Length);
        for (int i = 0; i < maxData; i++)
            foreach (var b in dataBlocks)
                if (i < b.Length) result.Add(b[i]);
        for (int i = 0; i < ecLen; i++)
            foreach (var b in ecBlocks)
                result.Add(b[i]);
        return result.ToArray();
    }

    private static byte[] ReedSolomonDivisor(int degree)
    {
        var result = new byte[degree];
        result[degree - 1] = 1;
        int root = 1;
        for (int i = 0; i < degree; i++)
        {
            for (int j = 0; j < degree; j++)
            {
                result[j] = GfMultiply(result[j], root);
                if (j + 1 < degree) result[j] ^= result[j + 1];
            }
            root = GfMultiply(root, 0x02);
        }
        return result;
    }

    private static byte[] ReedSolomonRemainder(byte[] data, byte[] divisor)
    {
        var result = new byte[divisor.Length];
        foreach (var b in data)
        {
            int factor = b ^ result[0];
            Array.Copy(result, 1, result, 0, result.Length - 1);
            result[^1] = 0;
            for (int i = 0; i < result.Length; i++) result[i] ^= GfMultiply(divisor[i], factor);
        }
        return result;
    }

    private static byte GfMultiply(int x, int y)
    {
        int z = 0;
        for (int i = 7; i >= 0; i--)
        {
            z = (z << 1) ^ ((z >> 7) * 0x11D);
            z ^= ((y >> i) & 1) * x;
        }
        return (byte)z;
    }

    // ───────────────────────── layout ─────────────────────────

    private void SetFunction(int col, int row, bool dark)
    {
        _modules[row, col] = dark;
        _isFunction[row, col] = true;
    }

    private void DrawFunctionPatterns()
    {
        for (int i = 0; i < Size; i++)
        {
            SetFunction(6, i, i % 2 == 0);
            SetFunction(i, 6, i % 2 == 0);
        }

        DrawFinder(3, 3);
        DrawFinder(Size - 4, 3);
        DrawFinder(3, Size - 4);

        var pos = AlignmentPositions[Version];
        int last = pos.Length - 1;
        for (int i = 0; i < pos.Length; i++)
        {
            for (int j = 0; j < pos.Length; j++)
            {
                bool overlapsFinder = (i == 0 && j == 0) || (i == 0 && j == last) || (i == last && j == 0);
                if (!overlapsFinder) DrawAlignment(pos[i], pos[j]);
            }
        }

        DrawFormatBits(0); // reserve; redrawn with the real mask later
        DrawVersion();
    }

    private void DrawFinder(int x, int y)
    {
        for (int dy = -4; dy <= 4; dy++)
        {
            for (int dx = -4; dx <= 4; dx++)
            {
                int dist = Math.Max(Math.Abs(dx), Math.Abs(dy));
                int xx = x + dx, yy = y + dy;
                if (xx >= 0 && xx < Size && yy >= 0 && yy < Size) SetFunction(xx, yy, dist != 2 && dist != 4);
            }
        }
    }

    private void DrawAlignment(int x, int y)
    {
        for (int dy = -2; dy <= 2; dy++)
            for (int dx = -2; dx <= 2; dx++)
                SetFunction(x + dx, y + dy, Math.Max(Math.Abs(dx), Math.Abs(dy)) != 1);
    }

    private void DrawFormatBits(int mask)
    {
        int data = FormatBitsM << 3 | mask;
        int rem = data;
        for (int i = 0; i < 10; i++) rem = (rem << 1) ^ ((rem >> 9) * 0x537);
        int bits = (data << 10 | rem) ^ 0x5412;
        bool Bit(int i) => ((bits >> i) & 1) != 0;

        for (int i = 0; i <= 5; i++) SetFunction(8, i, Bit(i));
        SetFunction(8, 7, Bit(6));
        SetFunction(8, 8, Bit(7));
        SetFunction(7, 8, Bit(8));
        for (int i = 9; i < 15; i++) SetFunction(14 - i, 8, Bit(i));

        for (int i = 0; i < 8; i++) SetFunction(Size - 1 - i, 8, Bit(i));
        for (int i = 8; i < 15; i++) SetFunction(8, Size - 15 + i, Bit(i));
        SetFunction(8, Size - 8, true); // dark module
    }

    private void DrawVersion()
    {
        if (Version < 7) return;
        int rem = Version;
        for (int i = 0; i < 12; i++) rem = (rem << 1) ^ ((rem >> 11) * 0x1F25);
        long bits = (long)Version << 12 | (long)rem;
        for (int i = 0; i < 18; i++)
        {
            bool bit = ((bits >> i) & 1) != 0;
            int a = Size - 11 + i % 3, b = i / 3;
            SetFunction(a, b, bit);
            SetFunction(b, a, bit);
        }
    }

    private void DrawCodewords(byte[] data)
    {
        int i = 0;
        for (int right = Size - 1; right >= 1; right -= 2)
        {
            if (right == 6) right = 5; // skip the vertical timing pattern
            for (int vert = 0; vert < Size; vert++)
            {
                for (int j = 0; j < 2; j++)
                {
                    int x = right - j;
                    bool upward = ((right + 1) & 2) == 0;
                    int y = upward ? Size - 1 - vert : vert;
                    if (!_isFunction[y, x] && i < data.Length * 8)
                    {
                        _modules[y, x] = ((data[i >> 3] >> (7 - (i & 7))) & 1) != 0;
                        i++;
                    }
                    // Remainder bits stay light (false), as the spec requires.
                }
            }
        }
    }

    // ───────────────────────── masking ─────────────────────────

    private static bool MaskBit(int mask, int x, int y) => mask switch
    {
        0 => (x + y) % 2 == 0,
        1 => y % 2 == 0,
        2 => x % 3 == 0,
        3 => (x + y) % 3 == 0,
        4 => (x / 3 + y / 2) % 2 == 0,
        5 => x * y % 2 + x * y % 3 == 0,
        6 => (x * y % 2 + x * y % 3) % 2 == 0,
        7 => ((x + y) % 2 + x * y % 3) % 2 == 0,
        _ => throw new ArgumentOutOfRangeException(nameof(mask)),
    };

    private void ApplyMask(int mask)
    {
        for (int y = 0; y < Size; y++)
            for (int x = 0; x < Size; x++)
                if (!_isFunction[y, x] && MaskBit(mask, x, y)) _modules[y, x] = !_modules[y, x];
    }

    private void ApplyBestMask()
    {
        int best = 0, bestPenalty = int.MaxValue;
        for (int m = 0; m < 8; m++)
        {
            ApplyMask(m);
            DrawFormatBits(m);
            int p = Penalty();
            if (p < bestPenalty) { bestPenalty = p; best = m; }
            ApplyMask(m); // XOR again = undo
        }
        Mask = best;
        ApplyMask(best);
        DrawFormatBits(best);
    }

    private int Penalty()
    {
        int penalty = 0;

        // Rule 1: runs of 5+ same-colored modules in a row / column.
        for (int pass = 0; pass < 2; pass++)
        {
            for (int a = 0; a < Size; a++)
            {
                int run = 1;
                for (int b = 1; b < Size; b++)
                {
                    bool cur = pass == 0 ? _modules[a, b] : _modules[b, a];
                    bool prev = pass == 0 ? _modules[a, b - 1] : _modules[b - 1, a];
                    if (cur == prev)
                    {
                        run++;
                        if (run == 5) penalty += 3;
                        else if (run > 5) penalty++;
                    }
                    else run = 1;
                }
            }
        }

        // Rule 2: 2×2 blocks of one color.
        for (int y = 0; y < Size - 1; y++)
            for (int x = 0; x < Size - 1; x++)
            {
                bool c = _modules[y, x];
                if (c == _modules[y, x + 1] && c == _modules[y + 1, x] && c == _modules[y + 1, x + 1]) penalty += 3;
            }

        // Rule 3: finder-like 1:1:3:1:1 patterns with 4 light modules on one side.
        bool[] p1 = { true, false, true, true, true, false, true, false, false, false, false };
        bool[] p2 = { false, false, false, false, true, false, true, true, true, false, true };
        for (int a = 0; a < Size; a++)
            for (int b = 0; b + 11 <= Size; b++)
            {
                bool row1 = true, row2 = true, col1 = true, col2 = true;
                for (int k = 0; k < 11; k++)
                {
                    bool r = _modules[a, b + k], c = _modules[b + k, a];
                    row1 &= r == p1[k]; row2 &= r == p2[k];
                    col1 &= c == p1[k]; col2 &= c == p2[k];
                }
                penalty += 40 * ((row1 ? 1 : 0) + (row2 ? 1 : 0) + (col1 ? 1 : 0) + (col2 ? 1 : 0));
            }

        // Rule 4: balance of dark and light.
        int dark = 0;
        foreach (var m in _modules) if (m) dark++;
        int total = Size * Size;
        int k5 = Math.Abs(dark * 20 - total * 10) / total; // deviation from 50 % in steps of 5 %
        penalty += k5 * 10;

        return penalty;
    }
}
