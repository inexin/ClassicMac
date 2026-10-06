using static ClassicMac.Graphics.Tests.TiffTests;

namespace ClassicMac.Graphics.Tests;

// SGILog high dynamic range images (docs/formats/graphics/tiff.md §2.10): LogL (luminance) and LogLuv32 (luminance
// and chromaticity), each row's byte planes run-length coded, tone-mapped for display.
public class TiffLogLuvTests
{
    private static RgbaColor Pixel(RgbaBitmap bitmap, int x, int y)
    {
        var i = (y * bitmap.Width + x) * 4;
        return new RgbaColor(bitmap.Pixels[i], bitmap.Pixels[i + 1], bitmap.Pixels[i + 2], bitmap.Pixels[i + 3]);
    }

    // A row's byte planes, most significant first, each as literal runs (a count, then the bytes).
    private static byte[] Literal(params uint[][] planes) =>
        [.. planes.SelectMany(plane => (byte[])[(byte)plane.Length, .. plane.Select(b => (byte)b)])];

    private static byte[] LogL(int width, byte[] strip) =>
        TiffBuilder.Image(width, 1, 32844, [16]).Tag(259, Short, 34676).Tag(339, Short, 2).Strip(strip).Build();

    [Fact]
    public void A_uniform_LogL_image_is_a_middle_grey()
    {
        // Le 0x4000 (Y ≈ 1) everywhere; the high plane as a run (130: two copies), the low plane literal.
        var bitmap = TiffFile.Decode(LogL(2, [130, 0x40, 2, 0, 0]));

        // Tone-mapped to the key 0.18: 0.18 / 1.18 ≈ 0.153, 109 after the sRGB curve.
        var p = Pixel(bitmap, 0, 0);
        Assert.InRange(p.R, 105, 113);
        Assert.Equal(p, Pixel(bitmap, 1, 0));
        Assert.Equal(p.R, p.G);
    }

    [Fact]
    public void Brighter_luminance_is_brighter_and_zero_is_black()
    {
        // Le 0, 0x4000 (Y ≈ 1), 0x4200 (Y ≈ 4).
        var bitmap = TiffFile.Decode(LogL(3, Literal([0x00, 0x40, 0x42], [0x00, 0x00, 0x00])));

        Assert.Equal(new RgbaColor(0, 0, 0), Pixel(bitmap, 0, 0));
        Assert.True(Pixel(bitmap, 2, 0).R > Pixel(bitmap, 1, 0).R);
    }

    [Fact]
    public void LogLuv32_carries_chromaticity()
    {
        // Two pixels of Le 0x4000: the equal-energy white (u' 0.2105, v' 0.4737: ue 86, ve 194), and a red
        // (x 0.64, y 0.33: u' 0.4507, v' 0.5229: ue 184, ve 214).
        var strip = Literal([0x40, 0x40], [0x00, 0x00], [86, 184], [194, 214]);
        var tiff = TiffBuilder.Image(2, 1, 32845, [16, 16, 16]).Tag(259, Short, 34676).Tag(339, Short, 2, 2, 2).Strip(strip).Build();

        var bitmap = TiffFile.Decode(tiff);

        var white = Pixel(bitmap, 0, 0);
        Assert.True(Math.Abs(white.R - white.G) <= 3 && Math.Abs(white.G - white.B) <= 3, white.ToString());
        var red = Pixel(bitmap, 1, 0);
        Assert.True(red.R > red.G + 50 && red.R > red.B + 50, red.ToString());
    }

    // A LogLuv24 pixel: 10-bit log luminance, then a 14-bit index into the (u′, v′) grid, three bytes most significant first.
    private static byte[] Luv24(int logL, int index)
    {
        var p = logL << 14 | index;
        return [(byte)(p >> 16), (byte)(p >> 8), (byte)p];
    }

    [Fact]
    public void LogLuv24_indexes_the_chromaticity_grid()
    {
        // Le 768 (Y ≈ 1): the equal-energy white's cell (12266), then a red's (14740, about x 0.64, y 0.33).
        var tiff = TiffBuilder.Image(2, 1, 32845, [16, 16, 16]).Tag(259, Short, 34677)
            .Strip([.. Luv24(768, 12266), .. Luv24(768, 14740)]).Build();

        var bitmap = TiffFile.Decode(tiff);

        var white = Pixel(bitmap, 0, 0);
        Assert.True(Math.Abs(white.R - white.G) <= 3 && Math.Abs(white.G - white.B) <= 3, white.ToString());
        var red = Pixel(bitmap, 1, 0);
        Assert.True(red.R > red.G + 50 && red.R > red.B + 50, red.ToString());
    }

    [Fact]
    public void LogLuv24_luminance_zero_is_black_and_an_index_past_the_grid_is_neutral()
    {
        var tiff = TiffBuilder.Image(3, 1, 32845, [16, 16, 16]).Tag(259, Short, 34677)
            .Strip([.. Luv24(0, 12266), .. Luv24(768, 16383), .. Luv24(768, 12266)]).Build();

        var bitmap = TiffFile.Decode(tiff);

        Assert.Equal(new RgbaColor(0, 0, 0), Pixel(bitmap, 0, 0));
        var neutral = Pixel(bitmap, 1, 0);
        Assert.True(Math.Abs(neutral.R - Pixel(bitmap, 2, 0).R) <= 3, neutral.ToString());
    }

    [Fact]
    public void A_short_LogLuv_row_is_reported()
    {
        var diagnostics = new List<ClassicMac.Core.Diagnostic>();

        TiffFile.Decode(LogL(4, [2, 0x40, 0x40]), diagnostics);

        Assert.Equal("tiff.short-strip", Assert.Single(diagnostics).Code);
    }
}
