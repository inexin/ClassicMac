using System.Buffers.Binary;
using System.Text;

namespace ClassicMac.Graphics.Tests;

// Fonts made in code, laid out as Inside Macintosh: Text describes them (no Apple data).
internal static class FontBuilder
{
    // A glyph: its advance, offset, and rows of pixels ('#' ink) all rectHeight tall; null for a character the font lacks.
    public sealed record Glyph(int Advance, int Offset, string[] Rows);

    // An NFNT: glyphs for firstChar.., then the missing symbol; a width table (fontType bit 1) and a height table (bit 0)
    // when asked; nDescent as the high word of owTLoc when the tables are pushed past 128 KiB (not used here).
    public static byte[] Strike(int firstChar, IReadOnlyList<Glyph?> glyphs, Glyph missing, int ascent, int descent, int kernMax = 0,
        bool widthTable = false, bool heightTable = false, int leading = 1)
    {
        var all = glyphs.Append(missing).ToList();
        var height = ascent + descent;
        var locations = new List<int> { 0 };
        foreach (var g in all) locations.Add(locations[^1] + (g?.Rows[0].Length ?? 0));
        var width = locations[^1];
        var rowWords = (width + 15) / 16;
        var strike = new byte[rowWords * 2 * height];
        for (var i = 0; i < all.Count; i++)
        {
            if (all[i] is not { } g) continue;
            for (var y = 0; y < height; y++)
                for (var x = 0; x < g.Rows[y].Length; x++)
                    if (g.Rows[y][x] == '#')
                    {
                        var column = locations[i] + x;
                        strike[y * rowWords * 2 + column / 8] |= (byte)(0x80 >> (column % 8));
                    }
        }
        var entries = all.Count + 1;
        var fontType = (ushort)(0x9000 | (widthTable ? 2 : 0) | (heightTable ? 1 : 0));
        var header = new byte[26];
        void W(int at, int v) => BinaryPrimitives.WriteInt16BigEndian(header.AsSpan(at), (short)v);
        W(0, fontType);
        W(2, firstChar);
        W(4, firstChar + glyphs.Count - 1);
        W(6, all.Max(g => g?.Advance ?? 0));
        W(8, kernMax);
        W(10, -descent);
        W(12, all.Max(g => g?.Rows[0].Length ?? 0));
        W(14, height);
        W(18, ascent);
        W(20, descent);
        W(22, leading);
        W(24, rowWords);
        // owTLoc: words from +16 to the offset/width table, which follows the strike and the location table.
        var owTable = 26 + strike.Length + 2 * entries;
        W(16, (owTable - 16) / 2);
        var body = new List<byte>(header);
        body.AddRange(strike);
        foreach (var l in locations) body.AddRange(BE16(l));
        body.AddRange(all.SelectMany(g => g is null ? BE16(-1) : BE16(g.Offset << 8 | g.Advance)));
        body.AddRange(BE16(-1));
        if (widthTable) foreach (var g in all.Append(null)) body.AddRange(BE16((g?.Advance ?? 0) * 256 + 128)); // advance + 0.5
        if (heightTable)
        {
            foreach (var g in all.Append(null))
            {
                if (g is null)
                {
                    body.AddRange(BE16(0));
                    continue;
                }
                var top = Array.FindIndex(g.Rows, r => r.Contains('#'));
                var bottom = Array.FindLastIndex(g.Rows, r => r.Contains('#'));
                body.AddRange(BE16(top < 0 ? 0 : top << 8 | (bottom - top + 1)));
            }
        }
        return [.. body];
    }

    // 'A' (3 wide, advance 4), 'B' missing, 'C' (2 wide, offset 1, advance 3) in a 5-row font (ascent 4, descent 1, kernMax
    // −1), and a missing symbol (a 3 × 3 box).
    public static readonly Glyph A = new(4, 0, [".#.", "#.#", "###", "#.#", "..."]);
    public static readonly Glyph C = new(3, 1, ["##", "#.", "#.", "##", ".."]);
    public static readonly Glyph Box = new(4, 0, ["###", "#.#", "###", "...", "..."]);

    public static byte[] Sample(bool tables = false) => Strike('A', [A, null, C], Box, ascent: 4, descent: 1, kernMax: -1, widthTable: tables, heightTable: tables);

    public static byte[] BE16(int v) => [(byte)(v >> 8), (byte)v];

    public static byte[] BE32(int v) => [(byte)(v >> 24), (byte)(v >> 16), (byte)(v >> 8), (byte)v];

    // A version 4 FOND: family 1024, characters 32–127, language 2; fonts 9 and 12 (plain) and an outline font; style
    // extras with a sign-magnitude negative; a bounding box; a width table; a kerning table (one kern sign-magnitude); a
    // style-mapping table with two names and a two-glyph encoding subtable at an odd offset.
    public static byte[] Family()
    {
        var header = new byte[52];
        void W(int at, int v) => header.AsSpan(at).Write16(v);
        W(0, 0x6000);
        W(2, 1024);
        W(4, 32);
        W(6, 127);
        W(8, 0x0C00);   // ascent 0.75
        W(10, -0x0400); // descent −0.25
        W(12, 0x0100);
        W(14, 0x1000);  // max width 1.0
        W(28, 0);
        W(30, 0x0200);  // bold extra 0.125
        W(32, 0x8100);  // italic −(0x100)/4096, sign-magnitude
        W(44, 2);       // language (ffProperty[8], version 4)
        W(50, 4);
        byte[] associations = [0, 2, .. BE16(0), .. BE16(0), .. BE16(1024), .. BE16(9), .. BE16(0), .. BE16(1033), .. BE16(12), .. BE16(0), .. BE16(1036)];
        byte[] bounds = [.. BE16(0), .. BE32(6), .. BE16(0), .. BE16(0), .. BE16(-0x0100), .. BE16(-0x0400), .. BE16(0x1100), .. BE16(0x0C00)];
        var entries = 127 - 32 + 3;
        var widths = new List<byte>(BE16(0)); // one table
        widths.AddRange(BE16(0));             // plain
        for (var i = 0; i < entries; i++) widths.AddRange(BE16(0x0800));
        byte[] kerning = [.. BE16(0), .. BE16(0), .. BE16(2), (byte)'A', (byte)'V', .. BE16(-0x0100), (byte)'T', (byte)'o', 0x81, 0x2F];
        var style = new List<byte>(BE16(1));
        style.AddRange(BE32(0));
        style.AddRange(BE32(0));
        style.AddRange(Enumerable.Range(0, 48).Select(i => (byte)(i == 0 ? 1 : 0)));
        style.AddRange(BE16(2));
        foreach (var name in new[] { "Example", "Bold" }) style.AddRange([(byte)name.Length, .. Encoding.ASCII.GetBytes(name)]);
        var encodingAt = style.Count; // 73: odd
        style.AddRange(BE16(2));
        foreach (var (code, glyph) in new[] { (0x80, "Adieresis"), (0x81, "Aring") }) style.AddRange([(byte)code, (byte)glyph.Length, .. Encoding.ASCII.GetBytes(glyph)]);
        style[2] = (byte)(encodingAt >> 24);
        style[3] = (byte)(encodingAt >> 16);
        style[4] = (byte)(encodingAt >> 8);
        style[5] = (byte)encodingAt;

        var widthAt = header.Length + associations.Length + bounds.Length;
        var kernAt = widthAt + widths.Count;
        var styleAt = kernAt + kerning.Length;
        header.AsSpan(16).Write32(widthAt);
        header.AsSpan(20).Write32(kernAt);
        header.AsSpan(24).Write32(styleAt);
        return [.. header, .. associations, .. bounds, .. widths, .. kerning, .. style];
    }

    // An sfnt with 'name' (a Macintosh Roman family name and a Unicode full name) and an empty 'glyf'.
    public static byte[] Sfnt()
    {
        var names = new List<byte>(BE16(0));
        names.AddRange(BE16(2));
        names.AddRange(BE16(6 + 24));
        names.AddRange([.. BE16(1), .. BE16(0), .. BE16(0), .. BE16(1), .. BE16(4), .. BE16(0)]);
        names.AddRange([.. BE16(3), .. BE16(1), .. BE16(0x409), .. BE16(4), .. BE16(4), .. BE16(4)]);
        names.AddRange("Test"u8.ToArray());
        names.AddRange(Encoding.BigEndianUnicode.GetBytes("Te"));
        var directory = 12 + 2 * 16;
        byte[] sfnt = [0, 1, 0, 0, .. BE16(2), 0, 0, 0, 0, 0, 0,
            .. "glyf"u8, 0, 0, 0, 0, .. BE32(directory), .. BE32(0),
            .. "name"u8, 0, 0, 0, 0, .. BE32(directory), .. BE32(names.Count), .. names];
        return sfnt;
    }
}

internal static class SpanWrites
{
    public static void Write16(this Span<byte> span, int v) => System.Buffers.Binary.BinaryPrimitives.WriteInt16BigEndian(span, (short)v);

    public static void Write32(this Span<byte> span, int v) => System.Buffers.Binary.BinaryPrimitives.WriteInt32BigEndian(span, v);
}
