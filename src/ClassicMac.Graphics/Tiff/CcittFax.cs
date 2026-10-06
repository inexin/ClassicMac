using System;
using System.Collections.Generic;

namespace ClassicMac.Graphics;

/// <summary>
/// CCITT fax coding in TIFF (docs/formats/graphics/tiff.md §2.5): Modified Huffman rows (compression 2, and 32771 with
/// rows on 16-bit words), Group 3 (3: ITU-T T.4, one- and two-dimensional, with EOLs) and Group 4 (4: ITU-T T.6). Rows
/// come out packed most significant bit first, a black run as 1 bits, a white run as 0 bits.
/// </summary>
internal static class CcittFax
{
    // The run-length codes of T.4 Tables 2 and 3: (bits, code, run).
    private static readonly (string Bits, int Run)[] WhiteCodes =
    [
        ("00110101", 0), ("000111", 1), ("0111", 2), ("1000", 3), ("1011", 4), ("1100", 5), ("1110", 6), ("1111", 7),
        ("10011", 8), ("10100", 9), ("00111", 10), ("01000", 11), ("001000", 12), ("000011", 13), ("110100", 14),
        ("110101", 15), ("101010", 16), ("101011", 17), ("0100111", 18), ("0001100", 19), ("0001000", 20),
        ("0010111", 21), ("0000011", 22), ("0000100", 23), ("0101000", 24), ("0101011", 25), ("0010011", 26),
        ("0100100", 27), ("0011000", 28), ("00000010", 29), ("00000011", 30), ("00011010", 31), ("00011011", 32),
        ("00010010", 33), ("00010011", 34), ("00010100", 35), ("00010101", 36), ("00010110", 37), ("00010111", 38),
        ("00101000", 39), ("00101001", 40), ("00101010", 41), ("00101011", 42), ("00101100", 43), ("00101101", 44),
        ("00000100", 45), ("00000101", 46), ("00001010", 47), ("00001011", 48), ("01010010", 49), ("01010011", 50),
        ("01010100", 51), ("01010101", 52), ("00100100", 53), ("00100101", 54), ("01011000", 55), ("01011001", 56),
        ("01011010", 57), ("01011011", 58), ("01001010", 59), ("01001011", 60), ("00110010", 61), ("00110011", 62),
        ("00110100", 63),
        ("11011", 64), ("10010", 128), ("010111", 192), ("0110111", 256), ("00110110", 320), ("00110111", 384),
        ("01100100", 448), ("01100101", 512), ("01101000", 576), ("01100111", 640), ("011001100", 704),
        ("011001101", 768), ("011010010", 832), ("011010011", 896), ("011010100", 960), ("011010101", 1024),
        ("011010110", 1088), ("011010111", 1152), ("011011000", 1216), ("011011001", 1280), ("011011010", 1344),
        ("011011011", 1408), ("010011000", 1472), ("010011001", 1536), ("010011010", 1600), ("011000", 1664),
        ("010011011", 1728),
    ];

    private static readonly (string Bits, int Run)[] BlackCodes =
    [
        ("0000110111", 0), ("010", 1), ("11", 2), ("10", 3), ("011", 4), ("0011", 5), ("0010", 6), ("00011", 7),
        ("000101", 8), ("000100", 9), ("0000100", 10), ("0000101", 11), ("0000111", 12), ("00000100", 13),
        ("00000111", 14), ("000011000", 15), ("0000010111", 16), ("0000011000", 17), ("0000001000", 18),
        ("00001100111", 19), ("00001101000", 20), ("00001101100", 21), ("00000110111", 22), ("00000101000", 23),
        ("00000010111", 24), ("00000011000", 25), ("000011001010", 26), ("000011001011", 27), ("000011001100", 28),
        ("000011001101", 29), ("000001101000", 30), ("000001101001", 31), ("000001101010", 32), ("000001101011", 33),
        ("000011010010", 34), ("000011010011", 35), ("000011010100", 36), ("000011010101", 37), ("000011010110", 38),
        ("000011010111", 39), ("000001101100", 40), ("000001101101", 41), ("000011011010", 42), ("000011011011", 43),
        ("000001010100", 44), ("000001010101", 45), ("000001010110", 46), ("000001010111", 47), ("000001100100", 48),
        ("000001100101", 49), ("000001010010", 50), ("000001010011", 51), ("000000100100", 52), ("000000110111", 53),
        ("000000111000", 54), ("000000100111", 55), ("000000101000", 56), ("000001011000", 57), ("000001011001", 58),
        ("000000101011", 59), ("000000101100", 60), ("000001011010", 61), ("000001100110", 62), ("000001100111", 63),
        ("0000001111", 64), ("000011001000", 128), ("000011001001", 192), ("000001011011", 256), ("000000110011", 320),
        ("000000110100", 384), ("000000110101", 448), ("0000001101100", 512), ("0000001101101", 576),
        ("0000001001010", 640), ("0000001001011", 704), ("0000001001100", 768), ("0000001001101", 832),
        ("0000001110010", 896), ("0000001110011", 960), ("0000001110100", 1024), ("0000001110101", 1088),
        ("0000001110110", 1152), ("0000001110111", 1216), ("0000001010010", 1280), ("0000001010011", 1344),
        ("0000001010100", 1408), ("0000001010101", 1472), ("0000001011010", 1536), ("0000001011011", 1600),
        ("0000001100100", 1664), ("0000001100101", 1728),
    ];

    // The extended makeup codes of T.4 Table 3, shared by both colours.
    private static readonly (string Bits, int Run)[] ExtendedCodes =
    [
        ("00000001000", 1792), ("00000001100", 1856), ("00000001101", 1920), ("000000010010", 1984),
        ("000000010011", 2048), ("000000010100", 2112), ("000000010101", 2176), ("000000010110", 2240),
        ("000000010111", 2304), ("000000011100", 2368), ("000000011101", 2432), ("000000011110", 2496),
        ("000000011111", 2560),
    ];

    // Codes keyed by (length << 16 | value).
    private static readonly Dictionary<int, int> White = Table(WhiteCodes), Black = Table(BlackCodes);

    private static Dictionary<int, int> Table((string Bits, int Run)[] codes)
    {
        var table = new Dictionary<int, int>();
        foreach (var (bits, run) in codes)
        {
            table[bits.Length << 16 | Convert.ToInt32(bits, 2)] = run;
        }

        foreach (var (bits, run) in ExtendedCodes)
        {
            table[bits.Length << 16 | Convert.ToInt32(bits, 2)] = run;
        }

        return table;
    }

    private enum Mode { Pass, Horizontal, Vertical }

    /// <summary>
    /// Decodes <paramref name="rows"/> rows of <paramref name="width"/> pixels into <paramref name="output"/>; returns
    /// the bytes of the rows decoded whole (fewer when the data ends or is damaged).
    /// </summary>
    public static int Decode(ReadOnlySpan<byte> input, Span<byte> output, int width, int rows, uint compression, uint t4Options)
    {
        var bits = new BitReader(input);
        var rowBytes = (width + 7) / 8;
        var reference = new List<int> { width, width };
        var current = new List<int>();
        var done = 0;
        for (; done < rows; done++)
        {
            var twoDimensional = compression == 4;
            if (compression == 3)
            {
                // An EOL (eleven zeros and a one, after any fill), then with 2-D coding a bit: 1 one-dimensional, 0 two.
                while (bits.Peek(12) == 0 && bits.Left >= 12)
                {
                    bits.Skip(1);
                }

                if (bits.Peek(12) == 1)
                {
                    bits.Skip(12);
                }

                if ((t4Options & 1) != 0)
                {
                    twoDimensional = bits.Read(1) == 0;
                }
            }

            current.Clear();
            var ok = twoDimensional ? TwoDimensional(ref bits, width, reference, current) : OneDimensional(ref bits, width, current);
            if (!ok)
            {
                break;
            }

            Fill(output.Slice(done * rowBytes, rowBytes), current, width);
            (reference, current) = (current, reference);
            reference.Add(width);
            reference.Add(width);
            if (compression == 2)
            {
                bits.Align(8);
            }
            else if (compression == 32771)
            {
                bits.Align(16);
            }
        }

        return done * rowBytes;
    }

    // A row's changes as black runs: positions alternate white→black, black→white.
    private static void Fill(Span<byte> row, List<int> changes, int width)
    {
        row.Clear();
        for (var i = 0; i < changes.Count; i += 2)
        {
            var start = changes[i];
            var end = i + 1 < changes.Count ? changes[i + 1] : width;
            for (var x = start; x < Math.Min(end, width); x++)
            {
                row[x >> 3] |= (byte)(0x80 >> (x & 7));
            }
        }
    }

    // A Modified Huffman row: runs alternating white and black, from white, until the width is filled.
    private static bool OneDimensional(ref BitReader bits, int width, List<int> changes)
    {
        var at = 0;
        var white = true;
        while (at < width)
        {
            if (Run(ref bits, white) is not { } run)
            {
                return false;
            }

            at += run;
            if (at > width)
            {
                return false;
            }

            if (at < width)
            {
                changes.Add(at);
            }

            white = !white;
        }

        return true;
    }

    // One run: makeup codes, then a terminating code (below 64).
    private static int? Run(ref BitReader bits, bool white)
    {
        var table = white ? White : Black;
        var total = 0;
        while (true)
        {
            var found = -1;
            var value = 0;
            for (var length = 1; length <= 13; length++)
            {
                if (bits.Left < 1)
                {
                    return null;
                }

                value = value << 1 | bits.Read(1);
                if (table.TryGetValue(length << 16 | value, out var run))
                {
                    found = run;
                    break;
                }
            }

            if (found < 0)
            {
                return null;
            }

            total += found;
            if (found < 64)
            {
                return total;
            }
        }
    }

    // A two-dimensional row (T.4 §4.2, T.6): each change coded against the row above (the reference).
    private static bool TwoDimensional(ref BitReader bits, int width, List<int> reference, List<int> changes)
    {
        var a0 = -1;
        var white = true;
        while (a0 < width)
        {
            // b1: the first change on the reference row to the right of a0 that is to the other colour; b2 the next.
            var index = FirstB1(reference, a0, white);
            var b1 = reference[Math.Min(index, reference.Count - 1)];
            var b2 = reference[Math.Min(index + 1, reference.Count - 1)];
            if (ReadMode(ref bits, out var delta) is not { } mode)
            {
                return false;
            }

            switch (mode)
            {
                case Mode.Pass:
                    a0 = b2;
                    break;
                case Mode.Horizontal:
                    var start = Math.Max(a0, 0);
                    if (Run(ref bits, white) is not { } first || Run(ref bits, !white) is not { } second)
                    {
                        return false;
                    }

                    var a1 = start + first;
                    var a2 = a1 + second;
                    if (a1 > width || a2 > width)
                    {
                        return false;
                    }

                    if (a1 < width)
                    {
                        changes.Add(a1);
                    }

                    if (a2 < width)
                    {
                        changes.Add(a2);
                    }

                    a0 = a2;
                    break;
                default:
                    var to = b1 + delta;
                    if (to < Math.Max(a0, 0) || to > width)
                    {
                        return false;
                    }

                    if (to < width)
                    {
                        changes.Add(to);
                    }

                    a0 = to;
                    white = !white;
                    break;
            }

            if (a0 >= width)
            {
                break;
            }
        }

        return true;
    }

    // The index of b1 on the reference row: the first change past a0 (from the row's start when a0 is −1) that turns
    // to the colour opposite the current one; changes at even indexes turn to black.
    private static int FirstB1(List<int> reference, int a0, bool white)
    {
        for (var i = 0; i < reference.Count; i++)
        {
            var past = a0 < 0 ? reference[i] >= 0 : reference[i] > a0;
            var turnsToBlack = i % 2 == 0;
            if (past && turnsToBlack == white)
            {
                return i;
            }

            if (reference[i] >= reference[^1])
            {
                return i;
            }
        }

        return reference.Count - 1;
    }

    // The 2-D mode codes (T.4 Table 4): pass 0001, horizontal 001, vertical 1, 011, 000011, 0000011 (right) and 010,
    // 000010, 0000010 (left).
    private static Mode? ReadMode(ref BitReader bits, out int delta)
    {
        delta = 0;
        if (bits.Left < 1)
        {
            return null;
        }

        if (bits.Read(1) == 1)
        {
            return Mode.Vertical;
        }

        var two = bits.Read(2);
        switch (two)
        {
            case 0b11:
                delta = 1;
                return Mode.Vertical;
            case 0b10:
                delta = -1;
                return Mode.Vertical;
            case 0b01:
                return Mode.Horizontal;
        }

        // 000…
        if (bits.Read(1) == 1)
        {
            return Mode.Pass;
        }

        // 0000…
        var next = bits.Read(2);
        if (next == 0b11)
        {
            delta = 2;
            return Mode.Vertical;
        }

        if (next == 0b10)
        {
            delta = -2;
            return Mode.Vertical;
        }

        if (next == 0b01)
        {
            var last = bits.Read(1);
            delta = last == 1 ? 3 : -3;
            return Mode.Vertical;
        }

        // Extensions (uncompressed mode) and EOLs inside a row are not read.
        return null;
    }

    // Bits most significant first.
    private ref struct BitReader(ReadOnlySpan<byte> data)
    {
        private readonly ReadOnlySpan<byte> data = data;
        private long position;

        public readonly long Left => data.Length * 8L - position;

        public int Read(int count)
        {
            var value = Peek(count);
            position = Math.Min(position + count, data.Length * 8L);
            return value;
        }

        public readonly int Peek(int count)
        {
            var value = 0;
            for (var k = 0; k < count; k++)
            {
                var at = position + k;
                var bit = at < data.Length * 8L ? (data[(int)(at >> 3)] >> (7 - (int)(at & 7))) & 1 : 0;
                value = value << 1 | bit;
            }

            return value;
        }

        public void Skip(int count) => position = Math.Min(position + count, data.Length * 8L);

        public void Align(int bits) => position = Math.Min((position + bits - 1) / bits * bits, data.Length * 8L);
    }
}
