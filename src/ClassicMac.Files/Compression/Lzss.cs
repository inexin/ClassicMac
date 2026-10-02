using System;
using System.IO;

namespace ClassicMac.Files.Compression
{
    /// <summary>
    /// Okumura's LZSS (lzss.c, 1989), as the NewWorld "Mac OS ROM" file stores the ROM image: a 4096-byte window filled
    /// with spaces, writing from 4078 (4096 − 18); a flag byte read least significant bit first, 1 for a literal byte and
    /// 0 for a two-byte match <c>pppppppp PPPPLLLL</c> — window position <c>P:p</c> (12 bits), length <c>L</c> + 3
    /// (3–18). The stream has no length; it ends with the input, and a match cut short by the end is dropped.
    /// </summary>
    internal static class Lzss
    {
        private const int WindowSize = 4096, MaxMatch = 18, Threshold = 2;

        /// <summary>Decompresses <paramref name="input"/>; throws when the output would pass <paramref name="maxLength"/>.</summary>
        public static byte[] Decompress(ReadOnlySpan<byte> input, long maxLength)
        {
            var window = new byte[WindowSize];
            window.AsSpan().Fill((byte)' ');
            var r = WindowSize - MaxMatch;
            using var output = new MemoryStream();
            var i = 0;
            uint flags = 0;
            while (true)
            {
                flags >>= 1;
                if ((flags & 0x100) == 0)
                {
                    if (i >= input.Length) break;
                    flags = input[i++] | 0xFF00u;
                }
                if ((flags & 1) != 0)
                {
                    if (i >= input.Length) break;
                    var c = input[i++];
                    Put(c);
                }
                else
                {
                    if (i > input.Length - 2) break;
                    var position = input[i] | ((input[i + 1] & 0xF0) << 4);
                    var count = (input[i + 1] & 0x0F) + Threshold + 1;
                    i += 2;
                    for (var k = 0; k < count; k++) Put(window[(position + k) & (WindowSize - 1)]);
                }
            }
            return output.ToArray();

            void Put(byte c)
            {
                if (output.Length >= maxLength)
                    throw new InvalidDataException("LZSS expansion exceeds the configured expanded-size limit.");
                output.WriteByte(c);
                window[r] = c;
                r = (r + 1) & (WindowSize - 1);
            }
        }
    }
}
