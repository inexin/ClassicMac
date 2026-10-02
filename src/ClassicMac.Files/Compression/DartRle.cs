using System;
using ClassicMac.Core;

namespace ClassicMac.Files.Compression
{
    /// <summary>
    /// DART's "fast" run-length compression, NDIF chunk type $81 (Disk Copy 6.3.3 disassembly): big-endian 16-bit
    /// counts; a count n ≥ 0 is followed by n literal words, a count n &lt; 0 by one word repeated −n times.
    /// </summary>
    internal static class DartRle
    {
        /// <summary>Decompresses into <paramref name="output"/> until it is full; false if the input runs out first or a run passes the end.</summary>
        public static bool Decompress(ReadOnlyMemory<byte> input, Span<byte> output, out int written)
        {
            written = 0;
            var reader = new BigEndianReader(input);
            while (written < output.Length)
            {
                if (!reader.TryReadInt16(out var count))
                {
                    return false;
                }

                var words = Math.Abs((int)count);
                if (written + words * 2 > output.Length)
                {
                    return false;
                }

                if (count >= 0)
                {
                    if (!reader.TryReadBytes(words * 2, out var literal))
                    {
                        return false;
                    }

                    literal.CopyTo(output[written..]);
                    written += words * 2;
                }
                else
                {
                    if (!reader.TryReadBytes(2, out var word))
                    {
                        return false;
                    }

                    for (var n = 0; n < words; n++, written += 2)
                    {
                        word.CopyTo(output[written..]);
                    }
                }
            }
            return true;
        }
    }
}
