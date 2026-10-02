namespace ClassicMac.App.Tests;

// A font family for the FOND preview's tests (design/boards/font-family.md, P6): 'FOND' 128 "Tester" with bitmap strikes
// 'NFNT' 1001 (9 pt), 1002 (12 pt plain) and 1003 (12 pt bold), a TrueType 'sfnt' 1005, and associations whose
// resources are not in the file ('NFNT' 1004, 14 pt 4-bit; 1006, 12 pt bold condensed); a kerning table of ten pairs,
// a style-mapping table and the metrics 0.75 / −0.25 / 0.0625 / 1.0 em.
internal static class FontFixtures
{
    public const string SampleChars = "AVTo ";

    // Glyphs: each a width and rows of '#' from the ascent's top; the bold strike's are a pixel wider.
    public static byte[] Strike(int ascent, int descent, bool bold = false)
    {
        var height = ascent + descent;
        var glyphs = new SortedDictionary<int, (int Width, string[] Rows)>();
        foreach (var c in SampleChars)
        {
            var width = c == ' ' ? 3 : ascent / 2 + 2 + (bold ? 1 : 0);
            var rows = Enumerable.Range(0, height).Select(r => c == ' ' || r >= ascent ? new string('.', width)
                : new string('#', width - 1) + ".").ToArray();
            glyphs[c] = (width, rows);
        }

        int first = glyphs.Keys.Min(), last = glyphs.Keys.Max();
        var all = new List<(int Width, string[] Rows)?>();
        for (var c = first; c <= last; c++)
        {
            all.Add(glyphs.TryGetValue(c, out var g) ? g : null);
        }

        // The missing symbol: a hollow box.
        var box = Enumerable.Range(0, height).Select(r => r >= ascent ? "....." : r is 0 || r == ascent - 1 ? "####." : "#..#.").ToArray();
        all.Add((5, box));
        var stripWidth = all.Sum(g => g?.Width ?? 0);
        var rowWords = Math.Max(1, (stripWidth + 15) / 16);
        var strike = new byte[rowWords * 2 * height];
        var locations = new List<int>();
        var offsetWidths = new List<int>();
        var x = 0;
        foreach (var g in all)
        {
            locations.Add(x);
            if (g is not { } glyph)
            {
                offsetWidths.Add(-1);
                continue;
            }

            for (var r = 0; r < height; r++)
            {
                for (var i = 0; i < glyph.Rows[r].Length; i++)
                {
                    if (glyph.Rows[r][i] == '#')
                    {
                        strike[r * rowWords * 2 + ((x + i) >> 3)] |= (byte)(0x80 >> ((x + i) & 7));
                    }
                }
            }

            offsetWidths.Add(glyph.Width);
            x += glyph.Width;
        }

        locations.Add(x);
        offsetWidths.Add(-1);
        var b = new List<byte>();
        void W(int v)
        {
            b.Add((byte)(v >> 8));
            b.Add((byte)v);
        }

        var entries = last - first + 3;
        W(0x9000);
        W(first);
        W(last);
        W(all.Max(g => g?.Width ?? 0));
        W(0);
        W(-descent);
        W(stripWidth);
        W(height);
        W((26 - 16 + strike.Length + entries * 2) / 2);
        W(ascent);
        W(descent);
        W(0);
        W(rowWords);
        b.AddRange(strike);
        foreach (var l in locations)
        {
            W(l);
        }

        foreach (var o in offsetWidths)
        {
            W(o);
        }

        return [.. b];
    }

    public static readonly (byte First, byte Second, short Kern)[] Pairs =
    [
        ((byte)'A', (byte)'V', -0x0200), ((byte)'T', (byte)'o', -0x0100), ((byte)'V', (byte)'A', -0x01C0), ((byte)'A', (byte)'T', -0x0080),
        ((byte)'o', (byte)'V', -0x0040), ((byte)'V', (byte)'o', -0x00C0), ((byte)'T', (byte)'A', -0x0140), ((byte)'o', (byte)'T', -0x0020),
        ((byte)'A', (byte)'o', 0x0010), ((byte)'o', (byte)'A', -0x0030),
    ];

    // The family record: header, associations, then the kerning table and the style-mapping table.
    public static byte[] Family(bool withTables = true)
    {
        (int Size, int Style, int Id)[] associations =
        [
            (0, 0, 1005), (9, 0, 1001), (12, 0, 1002), (12, 1, 1003), (12, 0x21, 1006), (14, 0x0200, 1004),
        ];
        var b = new List<byte>();
        void W(int v)
        {
            b.Add((byte)(v >> 8));
            b.Add((byte)v);
        }

        void L(int v)
        {
            W(v >> 16);
            W(v);
        }

        W(0x8002);                    // fixed width, has width tables (none stored)
        W(128);
        W(0x20);
        W(0x7E);
        W(0x0C00);                    // ascent 0.75
        W(unchecked((short)0xFC00));  // descent −0.25
        W(0x0100);                    // leading 0.0625
        W(0x1000);                    // max width 1.0
        var kernAt = 54 + 6 * associations.Length;
        var kern = new List<byte>();
        if (withTables)
        {
            kern.AddRange([0, 0, 0, 0, 0, (byte)Pairs.Length]);
            foreach (var (first, second, value) in Pairs)
            {
                kern.AddRange([first, second, (byte)(value >> 8), (byte)value]);
            }
        }

        var styleAt = kernAt + kern.Count;
        L(0);
        L(withTables ? kernAt : 0);
        L(withTables ? styleAt : 0);
        short[] extras = [0, 0x0080, 0, 0, 0x0100, 0x0100, unchecked((short)0x8040), 0x0040, 0];
        foreach (var e in extras)
        {
            W(e);
        }

        W(0);
        W(0);
        W(0);                         // version 0: no offset table after the associations
        W(associations.Length - 1);
        foreach (var (size, style, id) in associations)
        {
            W(size);
            W(style);
            W(id);
        }

        b.AddRange(kern);
        if (withTables)
        {
            W(0x0001);                // font class
            L(0);                     // no glyph encoding
            L(0);
            var indexes = new byte[48];
            (indexes[0], indexes[1], indexes[2], indexes[3]) = (1, 5, 6, 7);
            b.AddRange(indexes);
            string[] names = ["Tester", "-", "Bold", "Italic", "\u0002\u0003", "\u0002\u0004", "\u0002\u0003\u0004"];
            W(names.Length);
            foreach (var name in names)
            {
                b.Add((byte)name.Length);
                b.AddRange(name.Select(c => (byte)c));
            }
        }

        return [.. b];
    }

    // The fork: the family, its strikes and the TrueType font, and a 'TEXT' 128 (not a font).
    public static byte[] Fork() => PreviewTests.Fork(
        ("FOND", 128, "Tester", Family()),
        ("NFNT", 1001, null, Strike(7, 2)),
        ("NFNT", 1002, null, Strike(9, 3)),
        ("NFNT", 1003, null, Strike(9, 3, bold: true)),
        ("sfnt", 1005, null, [0, 1, 0, 0]),
        ("TEXT", 128, null, [1]));
}
