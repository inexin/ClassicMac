using System;

namespace ClassicMac.Files.Compression
{
    /// <summary>
    /// DART's "best" compression, NDIF chunk type $82: Okumura and Yoshizaki's LZHUF (adaptive Huffman coding over a
    /// 4096-byte window, matches of 3–60 bytes), as Disk Copy 6.3.3's codec runs it (disassembly; decodes DART 1.5.3's
    /// own files exactly). Differences from the published <c>lzhuf.c</c>: the window's first 4036 bytes are filled with
    /// zeros (not spaces) for every block, while its last 60 carry over from the previous block (zero at first); the
    /// Huffman tree restarts per block; past the end of the input a byte reads as $FF (a bit as 0), and decoding stops
    /// quietly once the input has been read past its end — DART's own files can end a block one byte short.
    /// </summary>
    internal sealed class DartLzh
    {
        private const int N = 4096, F = 60, Threshold = 2;
        private const int NChar = 256 - Threshold + F; // 314
        private const int T = NChar * 2 - 1; // 627
        private const int R = T - 1;
        private const int MaxFreq = 0x8000;

        private static readonly byte[] DCode = BuildDCode();
        private static readonly byte[] DLen = BuildDLen();

        // The window keeps its tail from block to block.
        private readonly byte[] text = new byte[N];

        /// <summary>Decodes one block into <paramref name="output"/>; returns how many bytes were written.</summary>
        public int Decode(ReadOnlySpan<byte> input, Span<byte> output)
        {
            var freq = new ushort[T + 1];
            var prnt = new int[T + NChar];
            var son = new int[T];
            for (var i = 0; i < NChar; i++)
            {
                freq[i] = 1;
                son[i] = i + T;
                prnt[i + T] = i;
            }
            for (int i = 0, j = NChar; j <= R; i += 2, j++)
            {
                freq[j] = (ushort)(freq[i] + freq[i + 1]);
                son[j] = i;
                prnt[i] = prnt[i + 1] = j;
            }
            freq[T] = 0xFFFF;
            prnt[R] = 0;

            var bits = new BitReader(input);
            Array.Clear(text, 0, N - F);
            var r = N - F;
            var written = 0;
            if (output.Length == 0) return 0;
            while (true)
            {
                var c = DecodeChar(ref bits, freq, prnt, son);
                if (c < 256)
                {
                    output[written++] = (byte)c;
                    text[r] = (byte)c;
                    r = (r + 1) & (N - 1);
                }
                else
                {
                    var i = (r - DecodePosition(ref bits) - 1) & (N - 1);
                    var length = c - 255 + Threshold;
                    for (var k = 0; k < length; k++)
                    {
                        if (written >= output.Length) return written;
                        var ch = text[(i + k) & (N - 1)];
                        output[written++] = ch;
                        text[r] = ch;
                        r = (r + 1) & (N - 1);
                    }
                }
                if (written >= output.Length || bits.PastEnd) return written;
            }
        }

        private static int DecodeChar(ref BitReader bits, ushort[] freq, int[] prnt, int[] son)
        {
            var c = son[R];
            while (c < T) c = son[c + bits.Bit()];
            c -= T;
            Update(c, freq, prnt, son);
            return c;
        }

        private static int DecodePosition(ref BitReader bits)
        {
            var i = bits.Byte();
            var c = DCode[i] << 6;
            var j = DLen[i] - 2;
            while (j-- > 0) i = ((i << 1) + bits.Bit()) & 0xFFFF;
            return c | (i & 0x3F);
        }

        private static void Update(int c, ushort[] freq, int[] prnt, int[] son)
        {
            if (freq[R] == MaxFreq) Reconstruct(freq, prnt, son);
            c = prnt[c + T];
            do
            {
                var k = ++freq[c];
                // The new count is compared as a signed 16-bit number with the (unsigned) others, as the Mac code does;
                // freq[T] ($FFFF) stays the largest.
                int signedK = (short)k;
                if (signedK > freq[c + 1])
                {
                    var l = c + 2;
                    while (signedK > freq[l]) l++;
                    l--;
                    freq[c] = freq[l];
                    freq[l] = k;
                    var i = son[c];
                    prnt[i] = l;
                    if (i < T) prnt[i + 1] = l;
                    var j = son[l];
                    son[l] = i;
                    prnt[j] = c;
                    if (j < T) prnt[j + 1] = c;
                    son[c] = j;
                    c = l;
                }
                c = prnt[c];
            }
            while (c != 0);
        }

        private static void Reconstruct(ushort[] freq, int[] prnt, int[] son)
        {
            var j = 0;
            for (var i = 0; i < T; i++)
            {
                if (son[i] >= T)
                {
                    freq[j] = (ushort)((freq[i] + 1) >> 1);
                    son[j] = son[i];
                    j++;
                }
            }
            for (int i = 0, n = NChar; n < T; i += 2, n++)
            {
                var f = freq[n] = (ushort)(freq[i] + freq[i + 1]);
                var k = n - 1;
                while (f < freq[k]) k--;
                k++;
                Array.Copy(freq, k, freq, k + 1, n - k);
                freq[k] = f;
                Array.Copy(son, k, son, k + 1, n - k);
                son[k] = i;
            }
            for (var i = 0; i < T; i++)
            {
                var k = son[i];
                prnt[k] = i;
                if (k < T) prnt[k + 1] = i;
            }
        }

        // Bits most significant first through a 16-bit buffer, refilled a byte at a time.
        private ref struct BitReader(ReadOnlySpan<byte> input)
        {
            private readonly ReadOnlySpan<byte> input = input;
            private int position;
            private int buffer;
            private int length;

            public readonly bool PastEnd => position > input.Length;

            private void Fill(int past)
            {
                while (length <= 8)
                {
                    var c = position < input.Length ? input[position] : past;
                    position++;
                    buffer = (buffer | ((c << (8 - length)) & 0xFFFF)) & 0xFFFF;
                    length += 8;
                }
            }

            public int Bit()
            {
                Fill(0);
                var v = buffer;
                buffer = (buffer << 1) & 0xFFFF;
                length--;
                return v >> 15;
            }

            public int Byte()
            {
                Fill(0xFFFF);
                var v = buffer;
                buffer = (buffer << 8) & 0xFFFF;
                length -= 8;
                return v >> 8;
            }
        }

        // lzhuf.c's decoding tables for the upper 6 bits of a position.
        private static byte[] BuildDCode()
        {
            var table = new byte[256];
            var at = 0;
            void Run(int value, int count)
            {
                for (var i = 0; i < count; i++) table[at++] = (byte)value;
            }
            Run(0, 32);
            Run(1, 16);
            Run(2, 16);
            Run(3, 16);
            for (var v = 4; v < 12; v++) Run(v, 8);
            for (var v = 12; v < 24; v++) Run(v, 4);
            for (var v = 24; v < 48; v++) Run(v, 2);
            for (var v = 48; v < 64; v++) Run(v, 1);
            return table;
        }

        private static byte[] BuildDLen()
        {
            var table = new byte[256];
            int[] counts = [32, 48, 64, 48, 48, 16];
            var at = 0;
            for (var l = 0; l < counts.Length; l++)
            {
                for (var i = 0; i < counts[l]; i++) table[at++] = (byte)(l + 3);
            }
            return table;
        }
    }
}
