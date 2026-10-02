using System;

namespace ClassicMac.Files.Compression
{
    /// <summary>
    /// bzip2 decompression (UDIF runs of type $80000006), written from the format as Julian Seward's bzip2 1.0 writes it:
    /// a "BZh1"–"BZh9" stream of blocks, each Huffman-coded move-to-front indexes with zero runs (RUNA/RUNB) over a
    /// Burrows–Wheeler transform of run-length-coded bytes, with a CRC per block and one for the stream. Bits are read
    /// most significant first. Concatenated streams are read one after another.
    /// </summary>
    internal static class BZip2
    {
        /// <summary>How decompression ended.</summary>
        public enum Result
        {
            Done,
            BadData,
            BadCrc,
            OutputFull,
            Randomised,
        }

        private const ulong BlockMagic = 0x314159265359, EndMagic = 0x177245385090;
        private const int MaxGroups = 6, GroupSize = 50, MaxCodeLength = 20;

        private static readonly uint[] CrcTable = MakeCrcTable();

        /// <summary>Decompresses <paramref name="input"/> into <paramref name="output"/>.</summary>
        public static Result Decompress(ReadOnlySpan<byte> input, Span<byte> output, out int written)
        {
            written = 0;
            var bits = new BitReader(input);
            var any = false;
            while (bits.Remaining >= 32 && bits.Peek(24) == 0x425A68) // "BZh"
            {
                bits.Read(24);
                var level = (int)bits.Read(8) - '0';
                if (level is < 1 or > 9)
                {
                    return Result.BadData;
                }

                var tt = new uint[level * 100_000];
                uint combined = 0;
                while (true)
                {
                    if (bits.Remaining < 48)
                    {
                        return Result.BadData;
                    }

                    var magic = ((ulong)bits.Read(24) << 24) | bits.Read(24);
                    if (magic == EndMagic)
                    {
                        if (bits.Read(32) != combined)
                        {
                            return Result.BadCrc;
                        }

                        bits.AlignToByte();
                        break;
                    }
                    if (magic != BlockMagic)
                    {
                        return Result.BadData;
                    }

                    var result = Block(ref bits, tt, output, ref written, out var crc);
                    if (result != Result.Done)
                    {
                        return result;
                    }

                    combined = ((combined << 1) | (combined >> 31)) ^ crc;
                }
                any = true;
            }
            return any ? Result.Done : Result.BadData;
        }

        private static Result Block(ref BitReader bits, uint[] tt, Span<byte> output, ref int written, out uint crc)
        {
            crc = 0;
            var expectedCrc = bits.Read(32);
            if (bits.Read(1) != 0)
            {
                return Result.Randomised; // bzip2 0.9.0's randomised blocks, never written since
            }

            var origin = (int)bits.Read(24);

            // The bytes in use: a 16-bit map of 16-byte ranges, then a 16-bit map for each range present.
            Span<byte> symbols = stackalloc byte[256];
            var inUse = 0;
            var ranges = bits.Read(16);
            for (var i = 0; i < 16; i++)
            {
                if ((ranges & (0x8000u >> i)) == 0)
                {
                    continue;
                }

                var used = bits.Read(16);
                for (var j = 0; j < 16; j++)
                {
                    if ((used & (0x8000u >> j)) != 0)
                    {
                        symbols[inUse++] = (byte)(i * 16 + j);
                    }
                }
            }
            if (inUse == 0)
            {
                return Result.BadData;
            }

            var alphabet = inUse + 2; // RUNA, RUNB, indexes 1 … inUse − 1, end of block

            var groups = (int)bits.Read(3);
            var selectorCount = (int)bits.Read(15);
            if (groups is < 2 or > MaxGroups || selectorCount == 0)
            {
                return Result.BadData;
            }

            // Selectors, move-to-front coded in unary.
            Span<byte> order = stackalloc byte[MaxGroups];
            for (var i = 0; i < groups; i++)
            {
                order[i] = (byte)i;
            }

            var selectors = new byte[selectorCount];
            for (var i = 0; i < selectorCount; i++)
            {
                var j = 0;
                while (bits.Read(1) == 1)
                {
                    if (++j >= groups)
                    {
                        return Result.BadData;
                    }
                }
                var chosen = order[j];
                for (; j > 0; j--)
                {
                    order[j] = order[j - 1];
                }

                order[0] = chosen;
                selectors[i] = chosen;
            }

            // Code lengths, delta coded, then canonical Huffman tables.
            var tables = new Huffman[groups];
            Span<byte> lengths = stackalloc byte[258];
            for (var t = 0; t < groups; t++)
            {
                var length = (int)bits.Read(5);
                for (var s = 0; s < alphabet; s++)
                {
                    while (true)
                    {
                        if (length is < 1 or > MaxCodeLength)
                        {
                            return Result.BadData;
                        }

                        if (bits.Read(1) == 0)
                        {
                            break;
                        }

                        length += bits.Read(1) == 0 ? 1 : -1;
                    }
                    lengths[s] = (byte)length;
                }
                tables[t] = new Huffman(lengths[..alphabet]);
            }

            // The move-to-front indexes, with runs of index 0 in bijective base 2 (RUNA = 1, RUNB = 2 at each place).
            Span<byte> mtf = stackalloc byte[256];
            for (var i = 0; i < inUse; i++)
            {
                mtf[i] = (byte)i;
            }

            Span<int> counts = stackalloc int[256];
            counts.Clear();
            var count = 0;
            var selector = 0;
            var left = 0;
            Huffman? table = null;
            int run = 0, runWeight = 1;
            while (true)
            {
                if (left == 0)
                {
                    if (selector >= selectorCount)
                    {
                        return Result.BadData;
                    }

                    table = tables[selectors[selector++]];
                    left = GroupSize;
                }
                left--;
                var symbol = table!.Decode(ref bits);
                if (symbol < 0)
                {
                    return Result.BadData;
                }

                if (symbol <= 1)
                {
                    run += (symbol + 1) * runWeight;
                    runWeight <<= 1;
                    if (run > tt.Length)
                    {
                        return Result.BadData;
                    }

                    continue;
                }
                if (run > 0)
                {
                    var value = symbols[mtf[0]];
                    if (count + run > tt.Length)
                    {
                        return Result.BadData;
                    }

                    counts[value] += run;
                    for (; run > 0; run--)
                    {
                        tt[count++] = value;
                    }

                    runWeight = 1;
                }
                if (symbol == alphabet - 1)
                {
                    break;
                }

                var index = symbol - 1;
                var front = mtf[index];
                for (var i = index; i > 0; i--)
                {
                    mtf[i] = mtf[i - 1];
                }

                mtf[0] = front;
                var b = symbols[front];
                if (count >= tt.Length)
                {
                    return Result.BadData;
                }

                counts[b]++;
                tt[count++] = b;
            }
            if (origin >= count)
            {
                return Result.BadData;
            }

            // The inverse Burrows–Wheeler transform: link each position to the next in the original order.
            Span<int> starts = stackalloc int[256];
            for (int i = 0, sum = 0; i < 256; i++)
            {
                starts[i] = sum;
                sum += counts[i];
            }
            for (var i = 0; i < count; i++)
            {
                var b = (int)(tt[i] & 0xFF);
                tt[starts[b]++] |= (uint)i << 8;
            }

            // Walk it, undoing the first run-length stage: four equal bytes, then a count of more.
            var position = tt[origin] >> 8;
            crc = 0xFFFFFFFF;
            int last = -1, same = 0;
            for (var n = 0; n < count; n++)
            {
                var entry = tt[position];
                var b = (byte)entry;
                position = entry >> 8;
                if (same == 4)
                {
                    if (written + b > output.Length)
                    {
                        return Result.OutputFull;
                    }

                    output.Slice(written, b).Fill((byte)last);
                    for (var i = 0; i < b; i++)
                    {
                        crc = (crc << 8) ^ CrcTable[(crc >> 24) ^ (byte)last];
                    }

                    written += b;
                    same = 0;
                    last = -1;
                    continue;
                }
                if (written >= output.Length)
                {
                    return Result.OutputFull;
                }

                output[written++] = b;
                crc = (crc << 8) ^ CrcTable[(crc >> 24) ^ b];
                same = b == last ? same + 1 : 1;
                last = b;
            }
            crc = ~crc;
            return crc == expectedCrc ? Result.Done : Result.BadCrc;
        }

        // The CRC-32 bzip2 uses: polynomial $04C11DB7, most significant bit first.
        private static uint[] MakeCrcTable()
        {
            var table = new uint[256];
            for (uint i = 0; i < 256; i++)
            {
                var c = i << 24;
                for (var k = 0; k < 8; k++)
                {
                    c = (c & 0x80000000) != 0 ? (c << 1) ^ 0x04C11DB7 : c << 1;
                }

                table[i] = c;
            }
            return table;
        }

        // A canonical Huffman code: codes assigned in order of length, then symbol.
        private sealed class Huffman
        {
            private readonly int[] limit = new int[MaxCodeLength + 2];
            private readonly int[] first = new int[MaxCodeLength + 2];
            private readonly int[] offset = new int[MaxCodeLength + 2];
            private readonly int[] sorted;

            public Huffman(ReadOnlySpan<byte> lengths)
            {
                sorted = new int[lengths.Length];
                var n = 0;
                var code = 0;
                for (var length = 1; length <= MaxCodeLength; length++)
                {
                    first[length] = code;
                    offset[length] = n;
                    for (var s = 0; s < lengths.Length; s++)
                    {
                        if (lengths[s] == length)
                        {
                            sorted[n++] = s;
                        }
                    }
                    code += n - offset[length];
                    limit[length] = code; // codes of this length are first … limit − 1
                    code <<= 1;
                }
            }

            public int Decode(ref BitReader bits)
            {
                var code = 0;
                for (var length = 1; length <= MaxCodeLength; length++)
                {
                    if (bits.Remaining < 1)
                    {
                        return -1;
                    }

                    code = (code << 1) | (int)bits.Read(1);
                    if (code < limit[length])
                    {
                        return sorted[offset[length] + code - first[length]];
                    }
                }
                return -1;
            }
        }

        private ref struct BitReader(ReadOnlySpan<byte> data)
        {
            private readonly ReadOnlySpan<byte> data = data;
            private long position;

            public readonly long Remaining => data.Length * 8L - position;

            public uint Read(int count)
            {
                uint value = 0;
                for (var i = 0; i < count; i++)
                {
                    if (position >= data.Length * 8L)
                    {
                        return value << (count - i); // past the end: zeros
                    }

                    value = (value << 1) | (uint)((data[(int)(position >> 3)] >> (7 - (int)(position & 7))) & 1);
                    position++;
                }
                return value;
            }

            public readonly uint Peek(int count)
            {
                var copy = this;
                return copy.Read(count);
            }

            public void AlignToByte() => position = (position + 7) & ~7L;
        }
    }
}
