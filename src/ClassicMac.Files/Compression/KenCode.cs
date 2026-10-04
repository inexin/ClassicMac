using System;
using System.Collections.Generic;

namespace ClassicMac.Files.Compression;

/// <summary>
/// KenCode, NDIF chunk type $80: Disk Copy 6's "Smaller (KC)" compression (the codec's disassembly, confirmed on an
/// image Disk Copy 6.3.3 made — it decodes to the stored CRC). An LZ77 stream of fixed prefix codes read most
/// significant bit first, with no stored tables: a copy-length code (0–2042), and when it is 0 after a match, a
/// literal run (1–63 bytes, which must be followed by a match unless it has 63); distances use a code whose width
/// grows with the number of bytes already written. As in Disk Copy, the bits read may not pass eight times the output
/// size, and a token may run past the output's end (its extra bytes are dropped). A match reaching before the output
/// start — memory Disk Copy would read — is reported instead.
/// </summary>
internal static class KenCode
{
    // The decoder's window limit (its workspace is set up with $2800), which caps the distance classes.
    private const int WindowLimit = 0x2800;

    /// <summary>How a decompression ended.</summary>
    public enum Result
    {
        /// <summary>The output is full.</summary>
        Done,

        /// <summary>The stream read more bits than eight times the output size (Disk Copy: "damaged").</summary>
        Overread,

        /// <summary>A match reached before the start of the output.</summary>
        BadDistance,
    }

    private const int MaxMatch = 0x40, MaxLiteralRun = 63;

    /// <summary>Compresses <paramref name="input"/> as one chunk, as Disk Copy 6.3.3 does (kencode.md §3).</summary>
    public static byte[] Compress(ReadOnlySpan<byte> input) => Compress(input, out _);

    /// <summary>
    /// Compresses <paramref name="input"/> as one chunk, as Disk Copy 6.3.3's encoder (KCCOMPRESSDATA) does
    /// (kencode.md §3) [Code: Disk Copy 6.3.3; Verified: its images' chunks re-encode exactly]: ADC's match finder with
    /// a <c>$2800</c>-byte window and matches of 3–64 bytes, taken greedily, and literal runs of up to 63.
    /// <paramref name="margin"/> is how far decoding in place runs ahead of the input, the room NDIF's buffer size
    /// (<c>+$48</c>) keeps.
    /// </summary>
    public static byte[] Compress(ReadOnlySpan<byte> input, out int margin)
    {
        var matcher = new TreeMatcher(input.Length, WindowLimit, MaxMatch);
        var bits = new BitWriter();
        int literalStart = 0, at = 0, mostAhead = 0;

        // After each flush: how far the input covered so far is ahead of the whole bytes written.
        void Note(int covered)
        {
            mostAhead = Math.Max(mostAhead, covered - (int)(bits.Count / 8));
        }

        // A literal run: length code 0, its count, its bytes.
        void FlushLiterals(ReadOnlySpan<byte> bytes, int end)
        {
            if (literalStart < end)
            {
                WriteLength(bits, 0);
                WriteCount(bits, end - literalStart);
                foreach (var b in bytes[literalStart..end])
                {
                    bits.Put(b, 8);
                }

                literalStart = end;
            }
        }

        while (at < input.Length)
        {
            var (length, from) = matcher.Search(input, at);
            if (length < 3)
            {
                at++;
                if (at - literalStart == MaxLiteralRun)
                {
                    FlushLiterals(input, at);
                    Note(at);
                }

                continue;
            }

            // After a run of fewer than 63 literals a match must follow, so its length code starts at 3.
            int pending = at - literalStart;
            FlushLiterals(input, at);
            WriteLength(bits, pending > 0 ? length - 3 : length - 2);
            WriteDistance(bits, at - from, at);
            for (int covered = at + 1; covered < at + length; covered++)
            {
                matcher.Search(input, covered);
            }

            at += length;
            literalStart = at;
            Note(at);
        }

        FlushLiterals(input, input.Length);
        Note(input.Length);
        margin = mostAhead - (input.Length - (int)(bits.Count / 8)) + 4;
        return bits.ToArray();
    }

    // The length code of §1.2 for v (0–2042).
    private static void WriteLength(BitWriter bits, int v)
    {
        switch (v)
        {
            case <= 1:
                bits.Put(0, 1);
                bits.Put(v, 1);
                return;
            case 2:
                bits.Put(0b100, 3);
                return;
            case <= 4:
                bits.Put(0b101, 3);
                bits.Put(v - 3, 1);
                return;
            case <= 6:
                bits.Put(0b1100, 4);
                bits.Put(v - 5, 1);
                return;
            case <= 10:
                bits.Put(0b1101, 4);
                bits.Put(v - 7, 2);
                return;
        }

        var (ones, @base, width) = v switch
        {
            <= 18 => (3, 11, 3),
            <= 26 => (4, 19, 3),
            <= 58 => (5, 27, 5),
            <= 122 => (6, 59, 6),
            <= 250 => (7, 123, 7),
            <= 506 => (8, 251, 8),
            <= 1018 => (9, 507, 9),
            _ => (10, 1019, 10),
        };
        bits.Put((1 << ones) - 1, ones);
        if (ones < 10)
        {
            bits.Put(0, 1);
        }

        bits.Put(v - @base, width);
    }

    // The literal count of §1.5 (1–63).
    private static void WriteCount(BitWriter bits, int count)
    {
        switch (count)
        {
            case 1:
                bits.Put(0, 1);
                return;
            case 2:
                bits.Put(0b100, 3);
                return;
            case 3:
                bits.Put(0b101, 3);
                return;
            case <= 7:
                bits.Put(0b110, 3);
                bits.Put(count - 4, 2);
                return;
        }

        bits.Put(0b111, 3);
        if (count <= 15)
        {
            bits.Put(count - 8, 4);
        }
        else if (count <= 31)
        {
            bits.Put((count + 16) >> 2, 4);
            bits.Put((count + 16) & 3, 2);
        }
        else
        {
            bits.Put((count + 64) >> 3, 4);
            bits.Put((count + 64) & 7, 3);
        }
    }

    // The distance of §1.4, in the class of the output position where the match starts.
    private static void WriteDistance(BitWriter bits, int distance, int position)
    {
        int k = DistanceClass(position);
        if (distance <= 1 << k)
        {
            bits.Put(0, 1);
            bits.Put(distance - 1, k);
        }
        else if (distance <= 5 << k)
        {
            bits.Put(0b10, 2);
            bits.Put(distance - (1 << k) - 1, k + 2);
        }
        else
        {
            int width = FarWidth(position, k), value = distance - (5 << k) - 1;
            if (value >= 1 << width)
            {
                throw new InvalidOperationException($"A distance of {distance} at {position} does not fit its {width}-bit code.");
            }

            bits.Put(0b11, 2);
            bits.Put(value, width);
        }
    }

    // Bits most significant first, the last byte padded with zeros.
    private sealed class BitWriter
    {
        private readonly List<byte> bytes = [];
        private int pending, pendingBits;

        public long Count { get; private set; }

        public void Put(int value, int width)
        {
            for (int bit = width - 1; bit >= 0; bit--)
            {
                pending = (pending << 1) | ((value >> bit) & 1);
                if (++pendingBits == 8)
                {
                    bytes.Add((byte)pending);
                    pending = pendingBits = 0;
                }
            }

            Count += width;
        }

        public byte[] ToArray()
        {
            if (pendingBits > 0)
            {
                bytes.Add((byte)(pending << (8 - pendingBits)));
                pending = pendingBits = 0;
            }

            return bytes.ToArray();
        }
    }

    /// <summary>Decompresses into <paramref name="output"/> until it is full; returns how it ended.</summary>
    public static Result Decompress(ReadOnlySpan<byte> input, Span<byte> output, out int written)
    {
        written = 0;
        var bits = new BitReader(input, (long)output.Length * 8);
        var total = 0;
        var afterRun = false; // a literal run shorter than 63 must be followed by a match
        while (total < output.Length)
        {
            if (!LengthCode(ref bits, out var value))
            {
                return Result.Overread;
            }

            if (value > 0 || afterRun)
            {
                var length = value + 2 + (afterRun ? 1 : 0);
                afterRun = false;
                if (!Distance(ref bits, total, DistanceClass(total), out var distance))
                {
                    return Result.Overread;
                }

                var from = total - distance;
                if (from < 0)
                {
                    return Result.BadDistance;
                }

                for (var i = 0; i < length; i++, total++)
                {
                    if (total < output.Length)
                    {
                        output[total] = output[from + i];
                    }
                }
            }
            else
            {
                if (!LiteralCount(ref bits, out var count))
                {
                    return Result.Overread;
                }

                afterRun = count < 63;
                for (var i = 0; i < count; i++, total++)
                {
                    if (!bits.Get(8, out var b))
                    {
                        return Result.Overread;
                    }

                    if (total < output.Length)
                    {
                        output[total] = (byte)b;
                    }
                }
            }
            written = Math.Min(total, output.Length);
        }
        written = output.Length;
        return Result.Done;
    }

    // The copy-length code: a unary prefix of up to ten 1s, then a class-sized field.
    private static bool LengthCode(ref BitReader bits, out int value)
    {
        value = 0;
        var ones = bits.Ones(10);
        int a, b;
        switch (ones)
        {
            case 0:
                return bits.Get(1, out value);
            case 1:
                if (!bits.Get(1, out a))
                {
                    return false;
                }

                if (a == 0)
                {
                    value = 2;
                    return true;
                }
                if (!bits.Get(1, out b))
                {
                    return false;
                }

                value = b + 3;
                return true;
            case 2:
                if (!bits.Get(1, out a))
                {
                    return false;
                }

                if (a != 0)
                {
                    if (!bits.Get(2, out b))
                    {
                        return false;
                    }

                    value = b + 7;
                    return true;
                }
                if (!bits.Get(1, out b))
                {
                    return false;
                }

                value = b + 5;
                return true;
        }
        var (@base, width) = ones switch
        {
            3 => (11, 3),
            4 => (19, 3),
            5 => (27, 5),
            6 => (59, 6),
            7 => (123, 7),
            8 => (251, 8),
            9 => (507, 9),
            _ => (1019, 10),
        };
        if (!bits.Get(width, out b))
        {
            return false;
        }

        value = b + @base;
        return true;
    }

    // The literal-run length, 1–63.
    private static bool LiteralCount(ref BitReader bits, out int count)
    {
        count = 0;
        if (!bits.Get(1, out var first))
        {
            return false;
        }

        if (first == 0)
        {
            count = 1;
            return true;
        }
        if (!bits.Get(2, out var x))
        {
            return false;
        }

        switch (x)
        {
            case 0:
                count = 2;
                return true;
            case 1:
                count = 3;
                return true;
            case 2:
                if (!bits.Get(2, out var z))
                {
                    return false;
                }

                count = z + 4;
                return true;
        }
        if (!bits.Get(4, out var y))
        {
            return false;
        }

        if (y <= 7)
        {
            count = y + 8;
            return true;
        }
        if (y <= 11)
        {
            if (!bits.Get(2, out var low))
            {
                return false;
            }

            count = 4 * y + low - 16;
            return true;
        }
        if (!bits.Get(3, out var low3))
        {
            return false;
        }

        count = 8 * y + low3 - 64;
        return true;
    }

    // The distance code's width class for the bytes written so far.
    private static int DistanceClass(int position)
    {
        ReadOnlySpan<(int Limit, int Class)> small = [(0xB, 0), (0x15, 1), (0x29, 2), (0x51, 3), (0xA1, 4), (0x2A1, 5), (0x3E9, 6)];
        foreach (var (limit, k) in small)
        {
            if (position < limit)
            {
                return k;
            }
        }
        ReadOnlySpan<(int Limit, int Window, int Class)> large =
            [(0xA81, 0x801, 7), (0x1501, 0x1001, 8), (0x2A01, 0x2001, 9), (0x5401, 0x4001, 10), (0xA801, 0x8001, 11), (0x11171, 0x10001, 12), (0x2A001, 0x20001, 13)];
        foreach (var (limit, window, k) in large)
        {
            if (position < limit || WindowLimit < window)
            {
                return k;
            }
        }
        return 14;
    }

    private static bool Distance(ref BitReader bits, int position, int k, out int distance)
    {
        distance = 0;
        if (!bits.Get(1, out var first))
        {
            return false;
        }

        int value;
        if (first == 0)
        {
            if (!bits.Get(k, out value))
            {
                return false;
            }

            distance = value + 1;
            return true;
        }
        var @base = 1 << k;
        if (!bits.Get(1, out var second))
        {
            return false;
        }

        if (second == 0)
        {
            if (!bits.Get(k + 2, out value))
            {
                return false;
            }

            distance = @base + value + 1;
            return true;
        }
        @base *= 5;
        if (!bits.Get(FarWidth(position, k), out value))
        {
            return false;
        }

        distance = @base + value + 1;
        return true;
    }

    // The width of a distance after the 11 prefix, for the bytes written so far and the class.
    private static int FarWidth(int position, int k)
    {
        int @base = 5 << k;
        if (position <= @base + 2)
        {
            return 1;
        }

        if (position <= @base + 4)
        {
            return 2;
        }

        var threshold = @base + 4;
        var step = 4;
        var width = 3;
        while (true)
        {
            threshold += step;
            // $680 is compared as $66C: a quirk of Disk Copy's code, kept.
            var compare = threshold == 0x680 ? 0x66C : threshold;
            if (position <= compare || width == k + 4)
            {
                return width;
            }

            step <<= 1;
            width++;
        }
    }

    // Bits most significant first; past the input they read as zeros; Get fails once more than limit bits are read.
    private ref struct BitReader(ReadOnlySpan<byte> input, long limit)
    {
        private readonly ReadOnlySpan<byte> input = input;
        private long position;

        private readonly int BitAt(long p)
        {
            var index = p >> 3;
            return index < input.Length ? (input[(int)index] >> (7 - (int)(p & 7))) & 1 : 0;
        }

        public bool Get(int count, out int value)
        {
            value = 0;
            if (position + count > limit)
            {
                return false;
            }

            for (var i = 0; i < count; i++)
            {
                value = (value << 1) | BitAt(position + i);
            }

            position += count;
            return true;
        }

        // Counts 1 bits up to max, eating the 0 that ends them (not checked against the limit, as in Disk Copy).
        public int Ones(int max)
        {
            var count = 0;
            while (count < max)
            {
                var bit = BitAt(position++);
                if (bit == 0)
                {
                    return count;
                }

                count++;
            }
            return count;
        }
    }
}
