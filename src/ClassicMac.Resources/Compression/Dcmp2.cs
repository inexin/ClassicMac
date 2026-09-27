using ClassicMac.Core;

namespace ClassicMac.Resources.Compression
{
    // System 'dcmp' 2 ("GreggyBits"): 16-bit words from a 256-word table, optionally mixed with literal words.
    // Read in the Mac OS 9.0 System's dcmp 2 (68k, version-9 form) and checked by emulation:
    // - param2 bit 0: (param1 + 1) words follow and replace the start of the table (_BlockMove @0x4C into a table
    //   inside the code at 0x2D6). The real table is never cleared, so entries above param1 keep an earlier resource's
    //   words; we model a freshly loaded dcmp (zeros) and report when such an entry is used.
    // - param2 bit 1 clear: every source byte is a table index. A do-while loop, so sizes 0 and 1 still write a word.
    // - param2 bit 1 set: one flag byte per 8 words, most significant bit first; 1 = index byte, 0 = two literal
    //   bytes. Leftover words (count & 7) use the top bits of one more flag byte.
    // - An odd final byte is copied raw. Indexes are not checked against param1.
    internal sealed class Dcmp2 : IResourceDecompressor
    {
        // The default table (code 0xD6–0x2D5), identical to resource_dasm's.
        private static readonly ushort[] DefaultTable =
        [
            0x0000, 0x0008, 0x4EBA, 0x206E, 0x4E75, 0x000C, 0x0004, 0x7000, 0x0010, 0x0002, 0x486E, 0xFFFC, 0x6000, 0x0001, 0x48E7, 0x2F2E,
            0x4E56, 0x0006, 0x4E5E, 0x2F00, 0x6100, 0xFFF8, 0x2F0B, 0xFFFF, 0x0014, 0x000A, 0x0018, 0x205F, 0x000E, 0x2050, 0x3F3C, 0xFFF4,
            0x4CEE, 0x302E, 0x6700, 0x4CDF, 0x266E, 0x0012, 0x001C, 0x4267, 0xFFF0, 0x303C, 0x2F0C, 0x0003, 0x4ED0, 0x0020, 0x7001, 0x0016,
            0x2D40, 0x48C0, 0x2078, 0x7200, 0x588F, 0x6600, 0x4FEF, 0x42A7, 0x6706, 0xFFFA, 0x558F, 0x286E, 0x3F00, 0xFFFE, 0x2F3C, 0x6704,
            0x598F, 0x206B, 0x0024, 0x201F, 0x41FA, 0x81E1, 0x6604, 0x6708, 0x001A, 0x4EB9, 0x508F, 0x202E, 0x0007, 0x4EB0, 0xFFF2, 0x3D40,
            0x001E, 0x2068, 0x6606, 0xFFF6, 0x4EF9, 0x0800, 0x0C40, 0x3D7C, 0xFFEC, 0x0005, 0x203C, 0xFFE8, 0xDEFC, 0x4A2E, 0x0030, 0x0028,
            0x2F08, 0x200B, 0x6002, 0x426E, 0x2D48, 0x2053, 0x2040, 0x1800, 0x6004, 0x41EE, 0x2F28, 0x2F01, 0x670A, 0x4840, 0x2007, 0x6608,
            0x0118, 0x2F07, 0x3028, 0x3F2E, 0x302B, 0x226E, 0x2F2B, 0x002C, 0x670C, 0x225F, 0x6006, 0x00FF, 0x3007, 0xFFEE, 0x5340, 0x0040,
            0xFFE4, 0x4A40, 0x660A, 0x000F, 0x4EAD, 0x70FF, 0x22D8, 0x486B, 0x0022, 0x204B, 0x670E, 0x4AAE, 0x4E90, 0xFFE0, 0xFFC0, 0x002A,
            0x2740, 0x6702, 0x51C8, 0x02B6, 0x487A, 0x2278, 0xB06E, 0xFFE6, 0x0009, 0x322E, 0x3E00, 0x4841, 0xFFEA, 0x43EE, 0x4E71, 0x7400,
            0x2F2C, 0x206C, 0x003C, 0x0026, 0x0050, 0x1880, 0x301F, 0x2200, 0x660C, 0xFFDA, 0x0038, 0x6602, 0x302C, 0x200C, 0x2D6E, 0x4240,
            0xFFE2, 0xA9F0, 0xFF00, 0x377C, 0xE580, 0xFFDC, 0x4868, 0x594F, 0x0034, 0x3E1F, 0x6008, 0x2F06, 0xFFDE, 0x600A, 0x7002, 0x0032,
            0xFFCC, 0x0080, 0x2251, 0x101F, 0x317C, 0xA029, 0xFFD8, 0x5240, 0x0100, 0x6710, 0xA023, 0xFFCE, 0xFFD4, 0x2006, 0x4878, 0x002E,
            0x504F, 0x43FA, 0x6712, 0x7600, 0x41E8, 0x4A6E, 0x20D9, 0x005A, 0x7FFF, 0x51CA, 0x005C, 0x2E00, 0x0240, 0x48C7, 0x6714, 0x0C80,
            0x2E9F, 0xFFD6, 0x8000, 0x1000, 0x4842, 0x4A6B, 0xFFD2, 0x0048, 0x4A47, 0x4ED1, 0x206F, 0x0041, 0x600C, 0x2A78, 0x422E, 0x3200,
            0x6574, 0x6716, 0x0044, 0x486D, 0x2008, 0x486C, 0x0B7C, 0x2640, 0x0400, 0x0068, 0x206D, 0x000D, 0x2A40, 0x000B, 0x003E, 0x0220,
        ];

        public short Id => 2;

        public int Decompress(DecompressionContext context)
        {
            var header = context.Header;
            if (header.IsVersion8)
                throw new DecompressionFormException("'dcmp' 2 has the version-9 entry points but the resource uses a version-8 header.");

            var cursor = new BlockCursor(context);
            var table = DefaultTable;
            var customCount = 256;
            if ((header.Param2 & 1) != 0)
            {
                table = new ushort[256];
                customCount = header.Param1 + 1;
                for (var i = 0; i < customCount; i++) table[i] = cursor.ReadU16();
            }

            var staleReported = false;
            void Word()
            {
                var index = cursor.ReadByte();
                if (index >= customCount && !staleReported)
                {
                    staleReported = true;
                    context.Report(DiagnosticSeverity.Info, "resource.dcmp2-stale-table",
                        $"uses table entry {index}, beyond the {customCount}-word custom table; a real Mac would use a " +
                        "word left by an earlier resource (zero here, as after a fresh load).");
                }
                cursor.WriteU16(table[index]);
            }

            var words = header.DecompressedSize >> 1;
            if ((header.Param2 & 2) == 0)
            {
                // do { … } while (--words > 0): at least one word.
                var count = words == 0 ? 1 : words;
                for (uint i = 0; i < count; i++) Word();
            }
            else
            {
                for (uint group = 0; group < words >> 3; group++) Mixed(cursor, 8, Word);
                if ((words & 7) != 0) Mixed(cursor, (int)(words & 7), Word);
            }

            if ((header.DecompressedSize & 1) != 0) cursor.WriteByte(cursor.ReadByte());
            return cursor.Written;
        }

        private static void Mixed(BlockCursor cursor, int count, System.Action word)
        {
            var flags = cursor.ReadByte();
            for (var i = 0; i < count; i++, flags <<= 1)
            {
                if ((flags & 0x80) != 0) word();
                else cursor.CopyLiteral(2);
            }
        }
    }
}
