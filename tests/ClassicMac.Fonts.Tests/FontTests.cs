using System.Text;
using ClassicMac.Core;
using static ClassicMac.Fonts.Tests.FontBuilder;

namespace ClassicMac.Fonts.Tests;

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

        var font = BitmapFont.Read(data.AsSpan(0, data.Length - 6), diagnostics);

        Assert.Equal(["font.short"], diagnostics.Select(d => d.Code));
        Assert.NotNull(font.Glyph('A'));
        Assert.Throws<InvalidDataException>(() => BitmapFont.Read(new byte[20]));
    }

    private static string Pixels(byte[] pixels) => string.Concat(pixels.Select(p => p == 0 ? '.' : '#'));

    [Fact]
    public void Families_give_their_fonts_and_tables()
    {
        var family = FontFamily.Read(Family(), "Example");

        Assert.Equal(("Example", 1024, 32, 127, 2), (family.Name, family.FamilyId, family.FirstChar, family.LastChar, family.Version));
        Assert.Equal((0.75, -0.25, 1.0), (family.Ascent, family.Descent, family.MaxWidth));
        Assert.Equal([0, 0.125, -0x100 / 4096.0], family.StyleExtras.Take(3));
        Assert.Equal([new FontAssociation(0, 0, 1024), new FontAssociation(9, 0, 1033), new FontAssociation(12, 0, 1036)], family.Fonts);
        Assert.Equal([9, 12], family.BitmapSizes);
        Assert.Equal(0.5, Assert.Single(family.WidthTables).Widths['A' - 32]);
        var kerning = Assert.Single(family.KerningTables);
        Assert.Equal([new KerningPair((byte)'A', (byte)'V', -0x100 / 4096.0), new KerningPair((byte)'T', (byte)'o', -0x80 / 4096.0)], kerning.Pairs);
        Assert.Equal(["Example", "-Bold"], family.StyleMapping!.Names);
        Assert.Equal(1, family.StyleMapping.Indexes[0]);
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

        Assert.True(font.IsTrueType);
        Assert.Equal(["glyf", "name"], font.Tables.Select(t => t.Tag.ToString()));
        Assert.Equal(("Test", "Te"), (font.FamilyName, font.FullName));
    }
}
