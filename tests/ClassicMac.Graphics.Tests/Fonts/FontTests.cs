using System.Text;
using ClassicMac.Core;
using ClassicMac.Graphics.Fonts;
using static ClassicMac.Graphics.Tests.FontBuilder;

namespace ClassicMac.Graphics.Tests;

public class FontTests
{
    [Fact]
    public void Strikes_give_their_metrics_and_glyphs()
    {
        var font = BitmapFont.Read(Sample());

        Assert.Equal(('A', 'C', 4, 1, 5, -1, 1), (font.FirstChar, font.LastChar, font.Ascent, font.Descent, font.RectHeight, font.MaxKern, font.Depth));
        Assert.False(font.HasHeightTable || font.HasWidthTable || font.IsFixedWidth);
        var a = font.Glyph('A')!;
        Assert.Equal((4, 0, 0, 3), (a.Advance, a.Offset, a.StrikeLeft, a.ImageWidth));
        Assert.Equal(".#.#.#####.#...", Pixels(font.Image(a)));
        var c = font.Glyph('C')!;
        Assert.Equal((3, 1, 3, 2), (c.Advance, c.Offset, c.StrikeLeft, c.ImageWidth));
        // 'B' and characters outside the font get the missing symbol.
        Assert.Equal(-1, font.Glyph('B')!.Character);
        Assert.Equal(-1, font.Glyph('z')!.Character);
        Assert.Equal([-1, 'A', 'C'], font.Glyphs.Select(g => g.Character).Order());
    }

    [Fact]
    public void Width_and_height_tables_are_read()
    {
        var font = BitmapFont.Read(Sample(tables: true));

        Assert.True(font.HasHeightTable && font.HasWidthTable);
        var a = font.Glyph('A')!;
        Assert.Equal((4.5, 0, 4), (a.FractionalAdvance, a.Top, a.Rows));
        var box = font.Glyph(-1)!;
        Assert.Equal((0, 3), (box.Top, box.Rows));
    }

    [Fact]
    public void Short_strikes_are_reported_and_tiny_ones_refused()
    {
        var diagnostics = new List<Diagnostic>();
        var data = Sample();

        var font = BitmapFont.Read(data.AsMemory(0, data.Length - 6), diagnostics);

        Assert.Equal(["font.short"], diagnostics.Select(d => d.Code));
        Assert.NotNull(font.Glyph('A'));
        Assert.Throws<InvalidDataException>(() => BitmapFont.Read(new byte[20]));
    }

    [Fact]
    public void The_ROM_reads_a_strike_by_its_own_rules()
    {
        // Four bytes between the strike and the location table: Mac OS 9 finds the table before the offset/width table,
        // the ROM right after the strike (the junk).
        var sample = Sample();
        int rowWords = (sample[24] << 8) | sample[25], strikeEnd = 26 + rowWords * 2 * 5;
        var gap = sample[..strikeEnd].Concat(new byte[] { 0x7F, 0x7F, 0x7F, 0x7F }).Concat(sample[strikeEnd..]).ToArray();
        gap.AsSpan(16).Write16(((gap[16] << 8) | gap[17]) + 2);
        var diagnostics = new List<Diagnostic>();
        var gapReader = new BigEndianReader(gap);
        Assert.Equal(3, BitmapFont.Read(gapReader, diagnostics, rom: false).Locations[1]);
        Assert.Equal(["font.location-table"], diagnostics.Select(d => d.Code));
        Assert.Equal(0x7F7F, ReadRom(gap).Locations[0]);

        // fontType bit 4: a depth code of 4 (16 bits) to the ROM, 0 to Mac OS 9.
        var deep = Sample();
        deep[1] |= 0x10;
        Assert.Equal((1, 16), (BitmapFont.Read(deep).Depth, ReadRom(deep).Depth));

        // rowWords' top bit: masked by Mac OS 9, a rejected strike to the ROM.
        var high = Sample();
        high[24] |= 0x80;
        Assert.Equal(rowWords, BitmapFont.Read(high).RowWords);
        Assert.Throws<InvalidDataException>(() => ReadRom(high));
    }

    private static BitmapFont ReadRom(byte[] data)
    {
        var reader = new BigEndianReader(data);
        return BitmapFont.Read(reader, null, rom: true);
    }

    private static string Pixels(byte[] pixels) => string.Concat(pixels.Select(p => p == 0 ? '.' : '#'));

    [Fact]
    public void Families_give_their_fonts_and_tables()
    {
        var family = FontFamily.Read(Family(), "Example");

        Assert.Equal(("Example", 1024, 32, 127, 4, 2), (family.Name, family.FamilyId, family.FirstChar, family.LastChar, family.Version, family.Language));
        Assert.Equal(new FamilyBounds(0, -0x100 / 4096.0, -0.25, 0x1100 / 4096.0, 0.75), Assert.Single(family.Bounds));
        Assert.Equal((0.75, -0.25, 1.0), (family.Ascent, family.Descent, family.MaxWidth));
        Assert.Equal([0, 0.125, -0x100 / 4096.0], family.StyleExtras.Take(3));
        Assert.Equal([new FontAssociation(0, 0, 1024), new FontAssociation(9, 0, 1033), new FontAssociation(12, 0, 1036)], family.Fonts);
        Assert.Equal([9, 12], family.BitmapSizes);
        Assert.Equal(0.5, Assert.Single(family.WidthTables).Widths['A' - 32]);
        var kerning = Assert.Single(family.KerningTables);
        Assert.Equal([new KerningPair((byte)'A', (byte)'V', -0x100 / 4096.0), new KerningPair((byte)'T', (byte)'o', -0x12F / 4096.0)], kerning.Pairs); // $812F: sign and magnitude
        Assert.Equal(["Example", "Bold"], family.StyleMapping!.Names);
        Assert.Equal(1, family.StyleMapping.Indexes[0]);
        Assert.Equal([((byte)0x80, "Adieresis"), ((byte)0x81, "Aring")], family.StyleMapping.Encoding);
        Assert.Equal(1, family.StyleMapping.EncodingOffset % 2); // odd
    }

    [Fact]
    public void A_familys_strikes_are_found_as_NFNT_then_FONT()
    {
        var family = FontFamily.Read(Family(), "Example");
        var nfnt = Sample();
        ReadOnlyMemory<byte>? Lookup(FourCC type, short id) => (type.ToString(), id) switch
        {
            ("FONT", 1033) => nfnt,
            ("NFNT", 1036) => nfnt,
            _ => null,
        };

        Assert.Null(FontFamily.Strike(family.Fonts[0], Lookup)); // the outline font
        Assert.NotNull(FontFamily.Strike(family.Fonts[1], Lookup)); // from 'FONT'
        Assert.NotNull(FontFamily.Strike(family.Fonts[2], Lookup)); // from 'NFNT'
        Assert.Null(FontFamily.Strike(new FontAssociation(24, 0, 9999), Lookup));
    }

    [Fact]
    public void Outline_fonts_give_their_tables_and_names()
    {
        var sfnt = Sfnt();

        var font = OutlineFont.Read(sfnt);
        var prefixed = new byte[sfnt.Length + 2];
        sfnt.CopyTo(prefixed, 2);
        var reader = new ClassicMac.Core.BigEndianReader(prefixed) { Position = 2 };
        var fromReader = OutlineFont.Read(reader);

        Assert.True(font.IsTrueType);
        Assert.Equal(["glyf", "name"], font.Tables.Select(t => t.Tag.ToString()));
        Assert.Equal(("Test", "Te"), (font.FamilyName, font.FullName));
        Assert.Equal(font.Tables, fromReader.Tables);
        Assert.Equal(prefixed.Length, reader.Position);
    }

    [Fact]
    public void Font_color_table_reads_sequentially_with_the_big_endian_reader()
    {
        var data = new byte[16];
        var writer = new ClassicMac.Core.BigEndianWriter(data);
        writer.WriteUInt32(0); // seed
        writer.WriteUInt16(0); // flags
        writer.WriteInt16(0); // one entry
        writer.WriteInt16(7);
        writer.WriteUInt16(0x1234);
        writer.WriteUInt16(0x5678);
        writer.WriteUInt16(0x9ABC);
        var reader = new ClassicMac.Core.BigEndianReader(data);

        var entries = FontColorTable.Read(reader);

        Assert.Equal([new FontColorEntry(7, 0x1234, 0x5678, 0x9ABC)], entries);
        Assert.Equal(data.Length, reader.Position);
    }
}
