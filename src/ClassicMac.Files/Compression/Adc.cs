using System;

namespace ClassicMac.Files.Compression
{
    /// <summary>
    /// Apple Data Compression (ADC), as Disk Copy 6.3.3 decodes NDIF chunks (disassembly of its <c>hdi</c> codec,
    /// confirmed on its own images). Each opcode byte is a literal run (high bit set: 1–128 bytes follow), a short match
    /// (<c>00LLLLDD DDDDDDDD</c>: 3–18 bytes from 1–1024 back) or a long match (<c>01LLLLLL</c> + 16-bit distance: 4–67
    /// bytes from 1–65536 back); matches copy byte by byte, so they may overlap what they write. Decoding stops when the
    /// output is full; a token that would pass its end is an error (checked before anything of it is written). No state
    /// carries from one chunk to the next. Disk Copy checks nothing else — a match before the start reads the memory
    /// before its buffer, and input is read past its stored length — so those are reported here instead.
    /// </summary>
    internal static class Adc
    {
        /// <summary>How a decompression ended.</summary>
        public enum Result
        {
            /// <summary>The output is full.</summary>
            Done,

            /// <summary>A token would pass the end of the output (Disk Copy: "damaged").</summary>
            Overrun,

            /// <summary>The input ran out before the output was full.</summary>
            Truncated,

            /// <summary>A match reached before the start of the output.</summary>
            BadDistance,
        }

        private const int MaxMatch = 67, MaxShortMatch = 18, MaxShortDistance = 1024, MaxLiteralRun = 128;

        /// <summary>Compresses <paramref name="input"/> as one chunk, as Disk Copy 6.3.3 does (adc.md §3).</summary>
        public static byte[] Compress(ReadOnlySpan<byte> input) => Compress(input, out _);

        /// <summary>
        /// Compresses <paramref name="input"/> as one chunk, as Disk Copy 6.3.3's encoder does (adc.md §3): every position
        /// goes into binary search trees of the earlier ones, by its first 2 bytes (1 byte under 4,000 bytes, none under
        /// 200), and the longest match met is taken greedily. <paramref name="margin"/> is how far decoding in place runs
        /// ahead of the input, the room NDIF's buffer size (<c>+$48</c>) keeps.
        /// </summary>
        public static byte[] Compress(ReadOnlySpan<byte> input, out int margin)
        {
            var encoder = new Encoder(input.Length);
            return encoder.Run(input, out margin);
        }

        // Disk Copy 6.3.3's ADC encoder (ADCCOMPRESSDATA) over its match finder [Code: Disk Copy 6.3.3; Verified: its
        // images' chunks re-encode exactly].
        private sealed class Encoder(int length)
        {
            private readonly TreeMatcher matcher = new(length, 65_536, MaxMatch);

            public byte[] Run(ReadOnlySpan<byte> input, out int margin)
            {
                var output = new System.IO.MemoryStream(input.Length / 2 + 16);
                int literalStart = 0, at = 0, mostAhead = 0;

                // After each flush: how far the input covered so far is ahead of the bytes written.
                void Note(int covered)
                {
                    mostAhead = Math.Max(mostAhead, covered - (int)output.Length);
                }

                void FlushLiterals(ReadOnlySpan<byte> bytes, int end)
                {
                    while (literalStart < end)
                    {
                        int run = Math.Min(MaxLiteralRun, end - literalStart);
                        output.WriteByte((byte)(0x80 | (run - 1)));
                        output.Write(bytes.Slice(literalStart, run));
                        literalStart += run;
                    }
                }

                while (at < input.Length)
                {
                    var (length, from) = matcher.Search(input, at);
                    int distance = at - from;
                    bool isShort = length >= 3 && length <= MaxShortMatch && distance <= MaxShortDistance;
                    if (!isShort && length < 4)
                    {
                        at++;
                        if (at - literalStart == MaxLiteralRun)
                        {
                            FlushLiterals(input, at);
                            Note(at);
                        }

                        continue;
                    }

                    FlushLiterals(input, at);
                    if (isShort)
                    {
                        output.WriteByte((byte)(((length - 3) << 2) | ((distance - 1) >> 8)));
                        output.WriteByte((byte)(distance - 1));
                    }
                    else
                    {
                        output.WriteByte((byte)(0x40 | (length - 4)));
                        output.WriteByte((byte)((distance - 1) >> 8));
                        output.WriteByte((byte)(distance - 1));
                    }

                    // The positions the match covers go into the trees too.
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
                margin = mostAhead - (input.Length - (int)output.Length) + 4;
                return output.ToArray();
            }
        }

        /// <summary>Decompresses into <paramref name="output"/> until it is full; returns how it ended.</summary>
        public static Result Decompress(ReadOnlySpan<byte> input, Span<byte> output, out int written)
        {
            written = 0;
            var i = 0;
            while (written < output.Length)
            {
                if (i >= input.Length)
                {
                    return Result.Truncated;
                }

                var op = input[i++];
                int length, distance;
                if ((op & 0x80) != 0)
                {
                    length = (op & 0x7F) + 1;
                    if (written + length > output.Length)
                    {
                        return Result.Overrun;
                    }

                    if (i + length > input.Length)
                    {
                        return Result.Truncated;
                    }

                    input.Slice(i, length).CopyTo(output[written..]);
                    written += length;
                    i += length;
                    continue;
                }
                if ((op & 0x40) != 0)
                {
                    length = (op & 0x3F) + 4;
                    if (i + 2 > input.Length)
                    {
                        return Result.Truncated;
                    }

                    distance = (input[i] << 8 | input[i + 1]) + 1;
                    i += 2;
                }
                else
                {
                    length = (op >> 2) + 3;
                    if (i + 1 > input.Length)
                    {
                        return Result.Truncated;
                    }

                    distance = ((op & 0x03) << 8 | input[i]) + 1;
                    i += 1;
                }
                if (written + length > output.Length)
                {
                    return Result.Overrun;
                }

                if (distance > written)
                {
                    return Result.BadDistance;
                }

                for (var n = 0; n < length; n++, written++)
                {
                    output[written] = output[written - distance];
                }
            }
            return Result.Done;
        }
    }
}
