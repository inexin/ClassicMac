using ClassicMac.Core;

namespace ClassicMac.Graphics.Tests;

// TIFF images (docs/formats/graphics/tiff.md): baseline TIFF 6.0 in either byte order, uncompressed, PackBits and LZW
// strips, the horizontal predictor, bilevel, grey, palette, RGB (with alpha) and CMYK pixels.
public class TiffTests
{
    private const ushort Short = 3, Long = 4;

    // A TIFF built tag by tag: one IFD, then the strips, in either byte order.
    private sealed class TiffBuilder(bool bigEndian = true)
    {
        private readonly SortedDictionary<ushort, (ushort Type, uint[] Values)> tags = [];
        private readonly List<byte[]> strips = [];

        public TiffBuilder Tag(ushort tag, ushort type, params uint[] values)
        {
            tags[tag] = (type, values);
            return this;
        }

        public TiffBuilder Strip(byte[] strip)
        {
            strips.Add(strip);
            return this;
        }

        // An image of width × height, its samples and photometric interpretation, its strips added after.
        public static TiffBuilder Image(int width, int height, ushort photometric, ushort[] bits, bool bigEndian = true) =>
            new TiffBuilder(bigEndian)
                .Tag(256, Long, (uint)width).Tag(257, Long, (uint)height)
                .Tag(258, Short, [.. bits.Select(b => (uint)b)]).Tag(262, Short, photometric)
                .Tag(277, Short, (uint)bits.Length);

        public byte[] Build()
        {
            var entries = new SortedDictionary<ushort, (ushort Type, uint[] Values)>(tags);
            var count = entries.Count + 2;
            var ifdSize = 2 + count * 12 + 4;
            var stripStart = 8 + ifdSize;
            // Values over 4 bytes go after the strips.
            var stripOffsets = new List<uint>();
            var at = (uint)stripStart;
            foreach (var strip in strips)
            {
                stripOffsets.Add(at);
                at += (uint)strip.Length;
            }

            entries[273] = (Long, [.. stripOffsets]);
            entries[279] = (Long, [.. strips.Select(s => (uint)s.Length)]);
            var extra = new List<byte>();
            var extraStart = at;
            var w = new List<byte>();
            void U16(uint v) => w.AddRange(bigEndian ? [(byte)(v >> 8), (byte)v] : [(byte)v, (byte)(v >> 8)]);
            void U32(uint v) => w.AddRange(bigEndian ? [(byte)(v >> 24), (byte)(v >> 16), (byte)(v >> 8), (byte)v] : [(byte)v, (byte)(v >> 8), (byte)(v >> 16), (byte)(v >> 24)]);
            w.AddRange(bigEndian ? "MM"u8.ToArray() : "II"u8.ToArray());
            U16(42);
            U32(8);
            U16((uint)entries.Count);
            foreach (var (tag, (type, values)) in entries)
            {
                U16(tag);
                U16(type);
                U32((uint)values.Length);
                var size = type == Short ? 2 : 4;
                var body = new List<byte>();
                foreach (var v in values)
                {
                    body.AddRange(size == 2
                        ? bigEndian ? [(byte)(v >> 8), (byte)v] : [(byte)v, (byte)(v >> 8)]
                        : bigEndian ? [(byte)(v >> 24), (byte)(v >> 16), (byte)(v >> 8), (byte)v] : [(byte)v, (byte)(v >> 8), (byte)(v >> 16), (byte)(v >> 24)]);
                }

                if (body.Count <= 4)
                {
                    w.AddRange(body);
                    w.AddRange(new byte[4 - body.Count]);
                }
                else
                {
                    U32(extraStart + (uint)extra.Count);
                    extra.AddRange(body);
                }
            }

            U32(0);
            // Two entries fewer than counted (the strip tags were counted in 'count'); pad the IFD to its size.
            while (w.Count < stripStart)
            {
                w.Add(0);
            }

            foreach (var strip in strips)
            {
                w.AddRange(strip);
            }

            w.AddRange(extra);
            return [.. w];
        }
    }

    // TIFF's LZW (TIFF 6.0 §13): codes most significant bit first, 9 to 12 bits, Clear 256, EndOfInformation 257,
    // the width growing one code early (when the next code would be 511, 1023, 2047).
    internal static byte[] Lzw(byte[] data)
    {
        var output = new List<byte>();
        int buffer = 0, filled = 0, width = 9;
        void Emit(int code)
        {
            buffer = (buffer << width) | code;
            filled += width;
            while (filled >= 8)
            {
                output.Add((byte)(buffer >> (filled - 8)));
                filled -= 8;
            }
        }

        var table = new Dictionary<string, int>();
        for (var i = 0; i < 256; i++)
        {
            table[((char)i).ToString()] = i;
        }

        var next = 258;
        Emit(256);
        var current = "";
        foreach (var b in data)
        {
            var extended = current + (char)b;
            if (table.ContainsKey(extended))
            {
                current = extended;
                continue;
            }

            Emit(table[current]);
            table[extended] = next++;
            if (next == (1 << width) - 1 && width < 12)
            {
                width++;
            }

            current = ((char)b).ToString();
        }

        if (current.Length > 0)
        {
            Emit(table[current]);
        }

        Emit(257);
        if (filled > 0)
        {
            output.Add((byte)(buffer << (8 - filled)));
        }

        return [.. output];
    }

    private static RgbaColor Pixel(RgbaBitmap bitmap, int x, int y)
    {
        var i = (y * bitmap.Width + x) * 4;
        return new RgbaColor(bitmap.Pixels[i], bitmap.Pixels[i + 1], bitmap.Pixels[i + 2], bitmap.Pixels[i + 3]);
    }

    private static readonly RgbaColor Red = new(255, 0, 0, 255), Blue = new(0, 0, 255, 255),
        White = new(255, 255, 255, 255), Black = new(0, 0, 0, 255);

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void Uncompressed_RGB_reads_in_either_byte_order(bool bigEndian)
    {
        var tiff = TiffBuilder.Image(2, 1, 2, [8, 8, 8], bigEndian).Strip([255, 0, 0, 0, 0, 255]).Build();

        Assert.True(TiffFile.IsTiffFile(tiff));
        var bitmap = TiffFile.Decode(tiff);

        Assert.Equal((2, 1), (bitmap.Width, bitmap.Height));
        Assert.Equal(Red, Pixel(bitmap, 0, 0));
        Assert.Equal(Blue, Pixel(bitmap, 1, 0));
    }

    [Theory]
    [InlineData(0, 0b1010_0000, 0, 255, 0)]    // WhiteIsZero: a 1 bit is black
    [InlineData(1, 0b1010_0000, 255, 0, 255)]  // BlackIsZero: a 1 bit is white
    public void Bilevel_rows_end_on_a_byte(ushort photometric, byte row, byte first, byte second, byte third)
    {
        // Three pixels in a row, two rows: each row starts on its own byte.
        var tiff = TiffBuilder.Image(3, 2, photometric, [1]).Strip([row, row]).Build();

        var bitmap = TiffFile.Decode(tiff);

        Assert.Equal(new RgbaColor(first, first, first, 255), Pixel(bitmap, 0, 1));
        Assert.Equal(new RgbaColor(second, second, second, 255), Pixel(bitmap, 1, 1));
        Assert.Equal(new RgbaColor(third, third, third, 255), Pixel(bitmap, 2, 1));
    }

    [Fact]
    public void Grey_of_eight_and_sixteen_bits()
    {
        var eight = TiffFile.Decode(TiffBuilder.Image(2, 1, 1, [8]).Strip([0x00, 0x80]).Build());
        var sixteen = TiffFile.Decode(TiffBuilder.Image(1, 1, 1, [16]).Strip([0x80, 0x7F]).Build());

        Assert.Equal(Black, Pixel(eight, 0, 0));
        Assert.Equal(new RgbaColor(0x80, 0x80, 0x80, 255), Pixel(eight, 1, 0));
        Assert.Equal(new RgbaColor(0x80, 0x80, 0x80, 255), Pixel(sixteen, 0, 0));
    }

    [Fact]
    public void A_palette_image_looks_up_its_color_map()
    {
        // 4 bits: 16 entries of red, then green, then blue, 16 bits each.
        var map = new uint[48];
        map[2] = 0xFFFF;          // entry 2 red
        map[16 + 3] = 0xFFFF;     // entry 3 green
        var tiff = TiffBuilder.Image(2, 1, 3, [4]).Tag(320, Short, map).Strip([0x23]).Build();

        var bitmap = TiffFile.Decode(tiff);

        Assert.Equal(Red, Pixel(bitmap, 0, 0));
        Assert.Equal(new RgbaColor(0, 255, 0, 255), Pixel(bitmap, 1, 0));
    }

    [Fact]
    public void PackBits_strips()
    {
        // Six bytes: a run of three 0xFF (-2), then a literal of three (2).
        var tiff = TiffBuilder.Image(2, 1, 2, [8, 8, 8]).Tag(259, Short, 32773).Strip([0xFE, 0xFF, 0x02, 0x00, 0x00, 0xFF]).Build();

        var bitmap = TiffFile.Decode(tiff);

        Assert.Equal(White, Pixel(bitmap, 0, 0));
        Assert.Equal(Blue, Pixel(bitmap, 1, 0));
    }

    [Fact]
    public void LZW_strips_with_the_horizontal_predictor()
    {
        // Two rows, a strip each; rows 600 pixels wide so the codes grow past 9 bits.
        const int width = 600;
        var rows = new byte[2][];
        for (var y = 0; y < 2; y++)
        {
            var row = new byte[width * 3];
            for (var x = 0; x < width; x++)
            {
                row[x * 3] = (byte)(x * 7 + y);
                row[x * 3 + 1] = (byte)(x * 13);
                row[x * 3 + 2] = (byte)(255 - x);
            }

            rows[y] = row;
        }

        static byte[] Differenced(byte[] row)
        {
            var d = (byte[])row.Clone();
            for (var i = d.Length - 1; i >= 3; i--)
            {
                d[i] = (byte)(row[i] - row[i - 3]);
            }

            return d;
        }

        var tiff = TiffBuilder.Image(width, 2, 2, [8, 8, 8]).Tag(259, Short, 5).Tag(317, Short, 2).Tag(278, Long, 1)
            .Strip(Lzw(Differenced(rows[0]))).Strip(Lzw(Differenced(rows[1]))).Build();

        var bitmap = TiffFile.Decode(tiff);

        for (var y = 0; y < 2; y++)
        {
            for (var x = 0; x < width; x += 37)
            {
                Assert.Equal(new RgbaColor(rows[y][x * 3], rows[y][x * 3 + 1], rows[y][x * 3 + 2], 255), Pixel(bitmap, x, y));
            }
        }
    }

    [Theory]
    [InlineData(2, 0x80, 0x40)]   // unassociated: kept as it is
    [InlineData(1, 0x80, 0x80)]   // associated (premultiplied): divided out
    public void Alpha_is_an_extra_sample(uint extra, byte alpha, byte red)
    {
        var tiff = TiffBuilder.Image(1, 1, 2, [8, 8, 8, 8]).Tag(338, Short, extra).Strip([0x40, 0, 0, alpha]).Build();

        var bitmap = TiffFile.Decode(tiff);

        Assert.Equal(new RgbaColor(red, 0, 0, alpha), Pixel(bitmap, 0, 0));
    }

    [Fact]
    public void Sixteen_bit_RGB_keeps_the_high_byte()
    {
        var tiff = TiffBuilder.Image(1, 1, 2, [16, 16, 16]).Strip([0xFF, 0x10, 0x00, 0xFF, 0x80, 0x00]).Build();

        Assert.Equal(new RgbaColor(0xFF, 0x00, 0x80, 255), Pixel(TiffFile.Decode(tiff), 0, 0));
    }

    [Fact]
    public void CMYK_is_converted_by_subtraction()
    {
        var tiff = TiffBuilder.Image(2, 1, 5, [8, 8, 8, 8]).Strip([0, 0, 0, 0, 255, 0, 0, 0]).Build();

        var bitmap = TiffFile.Decode(tiff);

        Assert.Equal(White, Pixel(bitmap, 0, 0));
        Assert.Equal(new RgbaColor(0, 255, 255, 255), Pixel(bitmap, 1, 0));
    }

    [Fact]
    public void A_strip_shorter_than_its_rows_leaves_them_white_and_says_so()
    {
        var diagnostics = new List<Diagnostic>();
        var tiff = TiffBuilder.Image(1, 2, 1, [8]).Strip([0x00]).Build();

        var bitmap = TiffFile.Decode(tiff, diagnostics);

        Assert.Equal(Black, Pixel(bitmap, 0, 0));
        Assert.Equal(White, Pixel(bitmap, 0, 1));
        Assert.Equal("tiff.short-strip", Assert.Single(diagnostics).Code);
    }

    [Theory]
    [InlineData(259, 7, "JPEG")]
    [InlineData(259, 4, "CCITT")]
    [InlineData(322, 16, "tiles")]
    [InlineData(284, 2, "planar")]
    public void What_is_not_read_is_named(ushort tag, uint value, string what)
    {
        var tiff = TiffBuilder.Image(1, 1, 2, [8, 8, 8]).Tag(tag, Short, value).Strip([0, 0, 0]).Build();

        var e = Assert.Throws<NotSupportedException>(() => TiffFile.Decode(tiff));

        Assert.Contains(what, e.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(new byte[] { (byte)'M', (byte)'M', 0, 42, 0, 0, 0, 100 })]   // the IFD past the end
    [InlineData(new byte[] { (byte)'I', (byte)'I', 42, 0, 8, 0, 0, 0, 0, 0 })] // an IFD with no entries
    public void Damage_is_an_InvalidDataException(byte[] tiff)
    {
        Assert.True(TiffFile.IsTiffFile(tiff));
        Assert.Throws<InvalidDataException>(() => TiffFile.Decode(tiff));
    }

    [Theory]
    [InlineData(new byte[] { (byte)'M', (byte)'M', 0, 43 })]
    [InlineData(new byte[] { (byte)'I', (byte)'M', 42, 0 })]
    [InlineData(new byte[] { 0xFF, 0xD8, 0xFF })]
    public void Other_files_are_no_TIFF(byte[] start) => Assert.False(TiffFile.IsTiffFile(start));
}
