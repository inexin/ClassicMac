using static ClassicMac.Graphics.Tests.TiffTests;

namespace ClassicMac.Graphics.Tests;

// JPEG in TIFF (docs/formats/graphics/tiff.md §2.9): the streams handed to the caller's JPEG decoder, new style
// (compression 7, with JPEGTables) and old style (6, from its interchange stream or rebuilt from its table tags), and
// the decoded strips or tiles placed in the image.
public class TiffJpegTests
{
    private const ushort Undefined = 7;

    // A decoder that keeps what it was given and answers with a solid colour of the size the test says.
    private sealed class FakeJpeg(int width, int height, RgbaColor color)
    {
        public List<byte[]> Streams { get; } = [];

        public RgbaBitmap? Decode(byte[] stream)
        {
            Streams.Add(stream);
            var bitmap = new RgbaBitmap(width, height);
            for (var i = 0; i < bitmap.Pixels.Length; i += 4)
            {
                (bitmap.Pixels[i], bitmap.Pixels[i + 1], bitmap.Pixels[i + 2], bitmap.Pixels[i + 3]) = (color.R, color.G, color.B, color.A);
            }

            return bitmap;
        }
    }

    private static RgbaColor Pixel(RgbaBitmap bitmap, int x, int y)
    {
        var i = (y * bitmap.Width + x) * 4;
        return new RgbaColor(bitmap.Pixels[i], bitmap.Pixels[i + 1], bitmap.Pixels[i + 2], bitmap.Pixels[i + 3]);
    }

    private static readonly RgbaColor Red = new(255, 0, 0);

    [Fact]
    public void New_style_strips_are_joined_to_their_JPEGTables()
    {
        // Tables: SOI, a DQT, EOI. Strips: SOI, SOS…, EOI. The stream is the tables without their EOI, then the
        // strip without its SOI.
        byte[] tables = [0xFF, 0xD8, 0xFF, 0xDB, 0, 3, 7, 0xFF, 0xD9];
        byte[] strip = [0xFF, 0xD8, 0xFF, 0xDA, 1, 2, 0xFF, 0xD9];
        var fake = new FakeJpeg(2, 1, Red);
        var tiff = TiffBuilder.Image(2, 1, 6, [8, 8, 8]).Tag(259, Short, 7).Tag(347, Undefined, [.. tables.Select(b => (uint)b)])
            .Strip(strip).Build();

        var bitmap = TiffFile.Decode(tiff, null, new TiffDecodeOptions { JpegDecoder = fake.Decode });

        Assert.Equal([0xFF, 0xD8, 0xFF, 0xDB, 0, 3, 7, 0xFF, 0xDA, 1, 2, 0xFF, 0xD9], Assert.Single(fake.Streams));
        Assert.Equal(Red, Pixel(bitmap, 1, 0));
    }

    [Fact]
    public void Without_JPEGTables_a_strip_is_its_own_stream()
    {
        byte[] strip = [0xFF, 0xD8, 0xFF, 0xDA, 9, 0xFF, 0xD9];
        var fake = new FakeJpeg(1, 1, Red);
        var tiff = TiffBuilder.Image(1, 1, 2, [8, 8, 8]).Tag(259, Short, 7).Strip(strip).Build();

        TiffFile.Decode(tiff, null, new TiffDecodeOptions { JpegDecoder = fake.Decode });

        Assert.Equal(strip, Assert.Single(fake.Streams));
    }

    [Fact]
    public void Decoded_strips_are_placed_at_their_rows_and_cut_at_the_edge()
    {
        // Two strips of 2 rows for a 3-row image: the second decodes 2 rows but only one is the image's.
        var fake = new FakeJpeg(2, 2, Red);
        var tiff = TiffBuilder.Image(2, 3, 2, [8, 8, 8]).Tag(259, Short, 7).Tag(278, Long, 2)
            .Strip([0xFF, 0xD8, 0xFF, 0xD9]).Strip([0xFF, 0xD8, 0xFF, 0xD9]).Build();

        var bitmap = TiffFile.Decode(tiff, null, new TiffDecodeOptions { JpegDecoder = fake.Decode });

        Assert.Equal(2, fake.Streams.Count);
        Assert.Equal(Red, Pixel(bitmap, 1, 2));
    }

    [Fact]
    public void A_strip_the_decoder_cannot_read_is_left_white_and_counted()
    {
        var diagnostics = new List<ClassicMac.Core.Diagnostic>();
        var tiff = TiffBuilder.Image(1, 1, 2, [8, 8, 8]).Tag(259, Short, 7).Strip([0xFF, 0xD8, 0xFF, 0xD9]).Build();

        var bitmap = TiffFile.Decode(tiff, diagnostics, new TiffDecodeOptions { JpegDecoder = _ => null });

        Assert.Equal(new RgbaColor(255, 255, 255), Pixel(bitmap, 0, 0));
        Assert.Equal("tiff.short-strip", Assert.Single(diagnostics).Code);
    }

    [Fact]
    public void Without_a_JPEG_decoder_JPEG_is_not_read()
    {
        var tiff = TiffBuilder.Image(1, 1, 2, [8, 8, 8]).Tag(259, Short, 7).Strip([0xFF, 0xD8, 0xFF, 0xD9]).Build();

        var e = Assert.Throws<NotSupportedException>(() => TiffFile.Decode(tiff));

        Assert.Contains("JpegDecoder", e.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Old_style_JPEG_with_an_interchange_stream_decodes_that_stream()
    {
        // JPEGInterchangeFormat (513) and its length (514) name a whole JPEG file inside the TIFF: here the strip.
        byte[] stream = [0xFF, 0xD8, 0xFF, 0xC0, 0xFF, 0xD9];
        var fake = new FakeJpeg(2, 1, Red);
        // Built once to find where the strip lands, then again pointing at it (the layout does not change).
        byte[] Build(uint at) => TiffBuilder.Image(2, 1, 6, [8, 8, 8]).Tag(259, Short, 6).Tag(513, Long, at).Tag(514, Long, (uint)stream.Length)
            .Strip(stream).Build();
        var patched = Build((uint)Build(0).AsSpan().IndexOf(stream));

        var bitmap = TiffFile.Decode(patched, null, new TiffDecodeOptions { JpegDecoder = fake.Decode });

        Assert.Equal(stream, Assert.Single(fake.Streams));
        Assert.Equal(Red, Pixel(bitmap, 1, 0));
    }

    [Fact]
    public void Old_style_JPEG_without_one_is_rebuilt_from_its_tables()
    {
        // One component (grey), 8 × 8, one table each: a quantisation table of 64 ones, DC and AC tables with one
        // code each. The stream: SOI, DQT, DHT (DC, then AC), SOF0, SOS, the strip's entropy-coded data, EOI.
        var q = Enumerable.Repeat((uint)1, 64).ToArray();
        uint[] dc = [0, 1, .. Enumerable.Repeat((uint)0, 14), 5];           // one 2-bit code for category 5
        uint[] ac = [0, 1, .. Enumerable.Repeat((uint)0, 14), 0xE7];        // one 2-bit code (a symbol found nowhere else)
        var fake = new FakeJpeg(8, 8, Red);
        // The tag values are offsets to the tables, one per component: the tables ride in private tags (65000–65002),
        // found once built, then pointed at (the layout does not change).
        byte[] Build(uint qAt, uint dcAt, uint acAt) => TiffBuilder.Image(8, 8, 1, [8]).Tag(259, Short, 6).Tag(512, Short, 1)
            .Tag(519, Long, qAt).Tag(520, Long, dcAt).Tag(521, Long, acAt)
            .Tag(65000, Undefined, q).Tag(65001, Undefined, dc).Tag(65002, Undefined, ac).Strip([0x12, 0x34]).Build();
        var draft = Build(0, 0, 0);
        int Find(uint[] table) => draft.AsSpan().IndexOf([.. table.Select(v => (byte)v)]);
        var tiff = Build((uint)Find(q), (uint)Find(dc), (uint)Find(ac));

        TiffFile.Decode(tiff, null, new TiffDecodeOptions { JpegDecoder = fake.Decode });

        var s = Assert.Single(fake.Streams);
        byte[] expected =
        [
            0xFF, 0xD8,
            0xFF, 0xDB, 0, 67, 0, .. Enumerable.Repeat((byte)1, 64),
            0xFF, 0xC4, 0, 20, 0x00, 0, 1, .. Enumerable.Repeat((byte)0, 14), 5,
            0xFF, 0xC4, 0, 20, 0x10, 0, 1, .. Enumerable.Repeat((byte)0, 14), 0xE7,
            0xFF, 0xC0, 0, 11, 8, 0, 8, 0, 8, 1, 1, 0x11, 0,
            0xFF, 0xDA, 0, 8, 1, 1, 0x00, 0, 63, 0,
            0x12, 0x34,
            0xFF, 0xD9,
        ];
        Assert.Equal(expected, s);
    }
}
