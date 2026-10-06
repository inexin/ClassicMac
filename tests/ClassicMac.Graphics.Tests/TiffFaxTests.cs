namespace ClassicMac.Graphics.Tests;

// CCITT fax compression in TIFF (docs/formats/graphics/tiff.md §2.5): Modified Huffman rows (2), Group 3 one- and
// two-dimensional (3), Group 4 (4) and word-aligned MH (32771), and FillOrder 2. The bits are written out from the
// code tables of ITU-T T.4 and T.6.
public class TiffFaxTests
{
    private const ushort Short = 3, Long = 4;

    // '0' and '1' characters, most significant bit first, padded with zeros to a whole byte.
    private static byte[] Bits(string bits)
    {
        bits = bits.Replace(" ", "", StringComparison.Ordinal);
        var bytes = new byte[(bits.Length + 7) / 8];
        for (var i = 0; i < bits.Length; i++)
        {
            if (bits[i] == '1')
            {
                bytes[i / 8] |= (byte)(0x80 >> (i % 8));
            }
        }

        return bytes;
    }

    // A bilevel image (WhiteIsZero) of the given compression, one strip.
    private static byte[] Fax(int width, int height, ushort compression, byte[] strip, params (ushort Tag, uint Value)[] more)
    {
        var tags = new SortedDictionary<ushort, (ushort Type, uint Value)>
        {
            [256] = (Long, (uint)width), [257] = (Long, (uint)height), [258] = (Short, 1), [259] = (Short, compression),
            [262] = (Short, 0), [273] = (Long, 0), [277] = (Short, 1), [278] = (Long, (uint)height), [279] = (Long, (uint)strip.Length),
        };
        foreach (var (tag, value) in more)
        {
            tags[tag] = (Short, value);
        }

        var ifdSize = 2 + tags.Count * 12 + 4;
        tags[273] = (Long, (uint)(8 + ifdSize));
        var w = new List<byte> { (byte)'M', (byte)'M', 0, 42, 0, 0, 0, 8, (byte)(tags.Count >> 8), (byte)tags.Count };
        foreach (var (tag, (type, value)) in tags)
        {
            w.AddRange([(byte)(tag >> 8), (byte)tag, 0, (byte)type, 0, 0, 0, 1]);
            w.AddRange(type == Short ? [(byte)(value >> 8), (byte)value, 0, 0] : [(byte)(value >> 24), (byte)(value >> 16), (byte)(value >> 8), (byte)value]);
        }

        w.AddRange([0, 0, 0, 0]);
        w.AddRange(strip);
        return [.. w];
    }

    // A row as text: '.' white, '#' black.
    private static string Row(RgbaBitmap bitmap, int y) =>
        new([.. Enumerable.Range(0, bitmap.Width).Select(x => bitmap.Pixels[(y * bitmap.Width + x) * 4] == 0 ? '#' : '.')]);

    [Fact]
    public void Modified_Huffman_rows_start_on_a_byte()
    {
        // Row 1: white 8 (10011). Row 2: white 2 (0111), black 4 (011), white 2 (0111).
        var strip = Bits("10011 000  0111 011 0111");
        var bitmap = TiffFile.Decode(Fax(8, 2, 2, strip));

        Assert.Equal("........", Row(bitmap, 0));
        Assert.Equal("..####..", Row(bitmap, 1));
    }

    [Fact]
    public void Word_aligned_MH_rows_start_on_a_16_bit_word()
    {
        var strip = Bits("10011 000 00000000  0111 011 0111");
        var bitmap = TiffFile.Decode(Fax(8, 2, 32771, strip));

        Assert.Equal("..####..", Row(bitmap, 1));
    }

    [Fact]
    public void Long_runs_take_makeup_codes()
    {
        // Width 2000: white 1984 (extended makeup 000000010010) + 16 (101010). Then white 64 (11011) + 0 (00110101),
        // black 1792 (00000001000) + 0 (0000110111), white 144: 128 (10010) + 16 (101010).
        var strip = Bits("000000010010 101010 000000  11011 00110101 00000001000 0000110111 10010 101010");
        var bitmap = TiffFile.Decode(Fax(2000, 2, 2, strip));

        Assert.DoesNotContain('#', Row(bitmap, 0));
        var row = Row(bitmap, 1);
        Assert.Equal(new string('.', 64) + new string('#', 1792) + new string('.', 144), row);
    }

    [Fact]
    public void Group_3_two_dimensional_rows_follow_their_tag_bit()
    {
        // EOL, 1 (one-dimensional), white 8; EOL, 0 (two-dimensional): horizontal (001) white 2, black 4, then V0 (1).
        var strip = Bits("000000000001 1 10011  000000000001 0 001 0111 011 1");
        var bitmap = TiffFile.Decode(Fax(8, 2, 3, strip, (292, 1)));

        Assert.Equal("........", Row(bitmap, 0));
        Assert.Equal("..####..", Row(bitmap, 1));
    }

    [Fact]
    public void Group_3_EOLs_may_be_padded_to_a_byte()
    {
        // T4Options bit 2: zeros before each EOL so it ends on a byte boundary.
        var strip = Bits("0000 000000000001 10011 0000000 000000000001 0111 011 0111");
        var bitmap = TiffFile.Decode(Fax(8, 2, 3, strip, (292, 4)));

        Assert.Equal("..####..", Row(bitmap, 1));
    }

    [Fact]
    public void Group_4_rows_code_their_changes_against_the_row_above()
    {
        // Row 1 against all white: horizontal white 2, black 4, then V0. Row 2 the same as row 1: V0 three times.
        // Row 3: VR1 (011) to 3, VL1 (010) to 5, V0 to the end: white 3, black 2, white 3.
        var strip = Bits("001 0111 011 1  1 1 1  011 010 1");
        var bitmap = TiffFile.Decode(Fax(8, 3, 4, strip));

        Assert.Equal("..####..", Row(bitmap, 0));
        Assert.Equal("..####..", Row(bitmap, 1));
        Assert.Equal("...##...", Row(bitmap, 2));
    }

    [Fact]
    public void Group_4_pass_mode_skips_a_reference_run()
    {
        // Row 1: horizontal white 2, black 2 (11), V0: "..##....". Row 2 all white: pass (0001) over the black run,
        // then V0 at the end.
        var strip = Bits("001 0111 11 1  0001 1");
        var bitmap = TiffFile.Decode(Fax(8, 2, 4, strip));

        Assert.Equal("..##....", Row(bitmap, 0));
        Assert.Equal("........", Row(bitmap, 1));
    }

    [Fact]
    public void FillOrder_2_reverses_each_byte()
    {
        var strip = Bits("10011 000  0111 011 0111").Select(b => (byte)(((b * 0x0202020202UL) & 0x010884422010UL) % 1023)).ToArray();
        var bitmap = TiffFile.Decode(Fax(8, 2, 2, strip, (266, 2)));

        Assert.Equal("..####..", Row(bitmap, 1));
    }

    [Fact]
    public void A_black_is_zero_fax_reads_its_bits_the_other_way()
    {
        // The codes give the bits (a white run is zeros); BlackIsZero then shows them inverted, as libtiff does.
        var strip = Bits("10011 000  0111 011 0111");
        var tiff = Fax(8, 2, 2, strip);
        tiff[8 + 2 + 4 * 12 + 9] = 1;       // PhotometricInterpretation (the fifth entry) to BlackIsZero

        var bitmap = TiffFile.Decode(tiff);

        Assert.Equal("##....##", Row(bitmap, 1));
    }

    [Fact]
    public void Damaged_fax_data_leaves_the_rest_white_and_says_so()
    {
        var diagnostics = new List<ClassicMac.Core.Diagnostic>();
        // Row 1 fine; row 2 starts with a code that is none (0000000 0...).
        var strip = Bits("10011 000  00000000 00000000");

        var bitmap = TiffFile.Decode(Fax(8, 2, 2, strip), diagnostics);

        Assert.Equal("........", Row(bitmap, 1));
        Assert.Equal("tiff.short-strip", Assert.Single(diagnostics).Code);
    }
}
