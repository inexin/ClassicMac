using System;
using System.Buffers.Binary;

namespace ClassicMac.Files.Compression
{
    /// <summary>
    /// DART's "fast" run-length compression, NDIF chunk type $81 (Disk Copy 6.3.3 disassembly): big-endian 16-bit
    /// counts; a count n ≥ 0 is followed by n literal words, a count n &lt; 0 by one word repeated −n times.
    /// </summary>
    internal static class DartRle
    {
        /// <summary>Decompresses into <paramref name="output"/> until it is full; false if the input runs out first or a run passes the end.</summary>
        public static bool Decompress(ReadOnlySpan<byte> input, Span<byte> output, out int written)
        {
            written = 0;
            var i = 0;
            while (written < output.Length)
            {
                if (i + 2 > input.Length) return false;
                var count = BinaryPrimitives.ReadInt16BigEndian(input[i..]);
                i += 2;
                var words = Math.Abs((int)count);
                if (written + words * 2 > output.Length) return false;
                if (count >= 0)
                {
                    if (i + words * 2 > input.Length) return false;
                    input.Slice(i, words * 2).CopyTo(output[written..]);
                    i += words * 2;
                    written += words * 2;
                }
                else
                {
                    if (i + 2 > input.Length) return false;
                    for (var n = 0; n < words; n++, written += 2) input.Slice(i, 2).CopyTo(output[written..]);
                    i += 2;
                }
            }
            return true;
        }
    }
}
