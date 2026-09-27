namespace ClassicMac.Resources.Compression
{
    // System 'dcmp' 3 (version-9 form): a bit-stream LZ77. Every compressed resource in the Mac OS 9.0 System uses it.
    // First ported from resource_dasm's System3.cc (MIT, Martin Michelsen), then checked against the disassembly of the
    // Mac OS 9.0 System's dcmp 3 (68k; Decompress 0x20 → worker 0x50, command 0x142, length codes 0x294 and 0x1B8,
    // offset decoders 0x55C–0x1A3E, bit readers 0x1D42/0x1CD6), whose emulation matches this on all 34 System resources:
    // - Bits are read most significant first; literal bytes are the next 8 bits, not byte-aligned. The Mac's reader for
    //   9+ bits prefetches up to three bytes; reading a byte at a time gives the same values.
    // - No end code: it loops while written < size (only the header's size is read), so the last command may overshoot
    //   by up to 2044 bytes, into the expansion bytes or the memory after the block.
    // - Nothing is bounds-checked on the Mac: past the input it reads whatever follows, and an offset beyond the output
    //   reads memory before it. We model the first (zeros) and stop on the second.
    internal sealed class Dcmp3 : IResourceDecompressor
    {
        public short Id => 3;

        public int Decompress(DecompressionContext context)
        {
            var header = context.Header;
            if (header.IsVersion8)
                throw new DecompressionFormException("'dcmp' 3 has the version-9 entry points but the resource uses a version-8 header.");

            var cursor = new BlockCursor(context);
            var bits = new BitReader(cursor);
            var literalAllowed = true;
            while (cursor.Written < header.DecompressedSize)
            {
                var before = cursor.Written;
                var copyLength = ReadLength0To2042(bits);
                if (copyLength == 0 && literalAllowed)
                {
                    // A literal run; only a full 63-byte run may be followed by another.
                    var literal = ReadLength1To63(bits);
                    literalAllowed = literal >= 63;
                    for (var i = 0; i < literal; i++) cursor.WriteByte((byte)bits.Read(8));
                }
                else
                {
                    // A back-reference (at least 2 bytes, so every command advances); one straight after a short
                    // literal run is a byte longer.
                    copyLength += literalAllowed ? 2 : 3;
                    literalAllowed = true;
                    var offset = ReadOffset(before, bits);
                    cursor.CopyBack(offset, copyLength);
                }
            }
            return cursor.Written;
        }

        // 0 → 1; 100 → 2; 101 → 3; 110xx → 4+x; then 1110 + 4 bits: < 8 → 8+x; < 12 → 16 + 4(x−8) + 2 bits;
        // else 32 + 8(x−12) + 3 bits.
        private static int ReadLength1To63(BitReader bits)
        {
            if (bits.Read(1) == 0) return 1;
            switch (bits.Read(2))
            {
                case 0: return 2;
                case 1: return 3;
                case 2: return bits.Read(2) + 4;
                default:
                    var which = bits.Read(4);
                    if (which < 8) return which + 8;
                    if (which < 12) return bits.Read(2) + ((which - 8) << 2) + 0x10;
                    return bits.Read(3) + ((which - 12) << 3) + 0x20;
            }
        }

        // Up to ten leading 1 bits select a range: 0x → x; 100 → 2; 101x → 3+x; 1100x → 5+x; 1101xx → 7+x; then
        // 1110xxx → 11+x, 11110xxx → 19+x, and each further 1 adds a bit: … 11111111110 + 10 bits → 1019+x.
        private static int ReadLength0To2042(BitReader bits)
        {
            var ones = 0;
            while (ones < 10 && bits.Read(1) == 1) ones++;
            return ones switch
            {
                0 => bits.Read(1),
                1 => bits.Read(1) == 0 ? 2 : bits.Read(1) + 3,
                2 => bits.Read(1) == 0 ? bits.Read(1) + 5 : bits.Read(2) + 7,
                3 => bits.Read(3) + 11,
                4 => bits.Read(3) + 19,
                _ => bits.Read(ones) + (1 << ones) - 5,
            };
        }

        // The offset code depends on how much has been written (the largest possible offset). Each family a covers
        // offsets up to about 21·2^a: 0 + a bits → 1+x; 10 + (a+2) bits → 1 + 2^a + x; 11 + k bits → base + x, where
        // k grows with the bytes written (answers table in the disassembly notes, decoders 0x55C–0x1A3E).
        private static readonly uint[] FamilyLimits =
            [0x0A, 0x14, 0x28, 0x50, 0xA0, 0x2A0, 0x3E8, 0xA80, 0x1500, 0x2A00, 0x5400, 0xA800, 0x11170, 0x2A000, uint.MaxValue];

        private static int ReadOffset(int written, BitReader bits)
        {
            var max = (uint)written;
            var family = 0;
            while (max > FamilyLimits[family]) family++;

            if (bits.Read(1) == 0) return bits.Read(family) + 1;
            if (bits.Read(1) == 0) return bits.Read(family + 2) + (1 << family) + 1;

            // Widths 1 … family+3 by threshold; the widest is the code's unconditional else.
            var @base = 1 + (1 << family) + (1 << (family + 2));
            for (var k = 1; k < family + 4; k++)
            {
                var (threshold, width) = Threshold(family, k, @base);
                if (max <= threshold) return bits.Read(width) + @base;
            }
            return bits.Read(family + 4) + @base;
        }

        // Thresholds are `written ≤ T`, regular except three, all as in the 68k code:
        // - family 7, $288 (cmpi.l @0xD34): 3 bits, as the regular rule gives (resource_dasm reads 4); unreachable, since
        //   family 7 runs only from $3E9 written.
        // - family 7, $66C (cmpi.l @0xE00) where the rule gives $680: live, written $66D–$680 reads 11 bits.
        // - family 14, $200C (@0x1B4A) where the rule gives $14080: dead, family 14 runs only above $2A000.
        private static (uint Threshold, int Width) Threshold(int family, int k, int @base) => (family, k) switch
        {
            (7, 10) => (0x66C, 10),
            (14, 7) => (0x200C, 7),
            _ => ((uint)(@base - 1 + (1 << k)), k),
        };

        // Most significant bit first, a byte at a time from the source.
        private sealed class BitReader(BlockCursor cursor)
        {
            private int buffer;
            private int available;

            public int Read(int count)
            {
                var value = 0;
                for (var i = 0; i < count; i++)
                {
                    if (available == 0)
                    {
                        buffer = cursor.ReadByte();
                        available = 8;
                    }
                    available--;
                    value = value << 1 | (buffer >> available & 1);
                }
                return value;
            }
        }
    }
}
