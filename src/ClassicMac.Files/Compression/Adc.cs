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
