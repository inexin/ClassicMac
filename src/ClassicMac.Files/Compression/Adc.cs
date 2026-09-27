using System;

namespace ClassicMac.Files.Compression
{
    /// <summary>
    /// Apple Data Compression (ADC), as Disk Copy 6 stores compressed NDIF chunks. Each opcode byte is a literal run
    /// (high bit set: 1–128 bytes follow), a short match (<c>00LLLLDD DDDDDDDD</c>: 3–18 bytes from 1–1024 back), or a
    /// long match (<c>01LLLLLL</c> + 16-bit distance: 4–67 bytes from 1–65536 back); matches copy byte by byte, so they
    /// may overlap what they write. Fitted to Disk Copy 6.3.3's images (every harness sample decodes to its source
    /// sectors); the handling of damaged input is ours until the disassembly settles it.
    /// </summary>
    internal static class Adc
    {
        /// <summary>How a decompression ended.</summary>
        public enum Result
        {
            /// <summary>The output is full or the input ran out between opcodes.</summary>
            Done,

            /// <summary>The input ended inside an opcode.</summary>
            Truncated,

            /// <summary>A match reached before the start of the output.</summary>
            BadDistance,
        }

        /// <summary>Decompresses into <paramref name="output"/>, stopping when it is full; returns how the input ended.</summary>
        public static Result Decompress(ReadOnlySpan<byte> input, Span<byte> output, out int written)
        {
            written = 0;
            var i = 0;
            while (i < input.Length && written < output.Length)
            {
                var op = input[i];
                int length, distance;
                if ((op & 0x80) != 0)
                {
                    length = (op & 0x7F) + 1;
                    if (i + 1 + length > input.Length) return Truncated(input[(i + 1)..], output, ref written);
                    var take = Math.Min(length, output.Length - written);
                    input.Slice(i + 1, take).CopyTo(output[written..]);
                    written += take;
                    i += 1 + length;
                    continue;
                }
                if ((op & 0x40) != 0)
                {
                    if (i + 3 > input.Length) return Result.Truncated;
                    length = (op & 0x3F) + 4;
                    distance = (input[i + 1] << 8 | input[i + 2]) + 1;
                    i += 3;
                }
                else
                {
                    if (i + 2 > input.Length) return Result.Truncated;
                    length = ((op >> 2) & 0x0F) + 3;
                    distance = ((op & 0x03) << 8 | input[i + 1]) + 1;
                    i += 2;
                }
                if (distance > written) return Result.BadDistance;
                for (var n = 0; n < length && written < output.Length; n++, written++) output[written] = output[written - distance];
            }
            return Result.Done;
        }

        // A literal run cut short: keep what is there.
        private static Result Truncated(ReadOnlySpan<byte> rest, Span<byte> output, ref int written)
        {
            var take = Math.Min(rest.Length, output.Length - written);
            rest[..take].CopyTo(output[written..]);
            written += take;
            return Result.Truncated;
        }
    }
}
