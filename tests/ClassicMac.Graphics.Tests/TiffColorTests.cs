using static ClassicMac.Graphics.Tests.TiffTests;

namespace ClassicMac.Graphics.Tests;

// TIFF's other colour models and sample formats (docs/formats/graphics/tiff.md §2.6–§2.8): YCbCr, subsampled or not;
// CIE L*a*b*; signed and floating-point samples, with the floating-point predictor.
public class TiffColorTests
{
    private static RgbaColor Pixel(RgbaBitmap bitmap, int x, int y)
    {
        var i = (y * bitmap.Width + x) * 4;
        return new RgbaColor(bitmap.Pixels[i], bitmap.Pixels[i + 1], bitmap.Pixels[i + 2], bitmap.Pixels[i + 3]);
    }

    private static void Near(RgbaColor expected, RgbaColor actual)
    {
        Assert.True(Math.Abs(expected.R - actual.R) <= 1 && Math.Abs(expected.G - actual.G) <= 1 && Math.Abs(expected.B - actual.B) <= 1
            && expected.A == actual.A, $"expected {expected}, got {actual}");
    }

    [Fact]
    public void YCbCr_without_subsampling_converts_each_pixel()
    {
        // Y 255, Cb and Cr at their middles: white. Y 76, Cb 85, Cr 255: red (Rec. 601 coefficients, TIFF's default).
        var tiff = TiffBuilder.Image(2, 1, 6, [8, 8, 8]).Tag(530, Short, 1, 1).Strip([255, 128, 128, 76, 85, 255]).Build();

        var bitmap = TiffFile.Decode(tiff);

        Near(new RgbaColor(255, 255, 255), Pixel(bitmap, 0, 0));
        Near(new RgbaColor(254, 0, 0), Pixel(bitmap, 1, 0));
    }

    [Fact]
    public void Subsampled_YCbCr_comes_in_data_units()
    {
        // 2 × 2 subsampling (the default), a 3 × 2 image: two units across, each four Y then Cb and Cr; the second
        // unit's right column lies outside the image.
        byte[] strip = [10, 20, 30, 40, 128, 128, 50, 60, 70, 80, 128, 128];
        var tiff = TiffBuilder.Image(3, 2, 6, [8, 8, 8]).Strip(strip).Build();

        var bitmap = TiffFile.Decode(tiff);

        Near(new RgbaColor(10, 10, 10), Pixel(bitmap, 0, 0));
        Near(new RgbaColor(20, 20, 20), Pixel(bitmap, 1, 0));
        Near(new RgbaColor(30, 30, 30), Pixel(bitmap, 0, 1));
        Near(new RgbaColor(40, 40, 40), Pixel(bitmap, 1, 1));
        Near(new RgbaColor(50, 50, 50), Pixel(bitmap, 2, 0));
        Near(new RgbaColor(70, 70, 70), Pixel(bitmap, 2, 1));
    }

    [Fact]
    public void YCbCr_takes_its_coefficients_and_reference_black_and_white()
    {
        // ReferenceBlackWhite of 16–235 for Y (studio range): Y 235 is white, Y 16 black.
        var tiff = TiffBuilder.Image(2, 1, 6, [8, 8, 8]).Tag(530, Short, 1, 1)
            .Tag(529, Rational, 299, 1000, 587, 1000, 114, 1000)
            .Tag(532, Rational, 16, 1, 235, 1, 128, 1, 240, 1, 128, 1, 240, 1)
            .Strip([235, 128, 128, 16, 128, 128]).Build();

        var bitmap = TiffFile.Decode(tiff);

        Near(new RgbaColor(255, 255, 255), Pixel(bitmap, 0, 0));
        Near(new RgbaColor(0, 0, 0), Pixel(bitmap, 1, 0));
    }

    [Theory]
    [InlineData(8, 255, 0, 0, 255)]      // CIELab: L 100, a 0, b 0 is white
    [InlineData(8, 0, 0, 0, 0)]          // L 0 is black
    [InlineData(9, 255, 128, 128, 255)]  // ICCLab: a and b are unsigned, 128 their zero
    public void Lab_neutral_greys_are_greys(ushort photometric, byte l, byte a, byte b, byte grey)
    {
        var tiff = TiffBuilder.Image(1, 1, photometric, [8, 8, 8]).Strip([l, a, b]).Build();

        Near(new RgbaColor(grey, grey, grey), Pixel(TiffFile.Decode(tiff), 0, 0));
    }

    [Fact]
    public void Lab_colour_converts_to_sRGB()
    {
        // L 53.24, a 80.09, b 67.20 is sRGB red (D65): L stored as 53.24 × 255 / 100 ≈ 136, a 80, b 67.
        var tiff = TiffBuilder.Image(1, 1, 8, [8, 8, 8]).Strip([136, 80, 67]).Build();

        var red = Pixel(TiffFile.Decode(tiff), 0, 0);

        Assert.True(red.R > 245 && red.G < 20 && red.B < 20, red.ToString());
    }

    [Fact]
    public void Signed_samples_are_centred()
    {
        // SampleFormat 2: −128 is the darkest, 0 the middle, 127 the brightest.
        var tiff = TiffBuilder.Image(3, 1, 1, [8]).Tag(339, Short, 2).Strip([0x80, 0x00, 0x7F]).Build();

        var bitmap = TiffFile.Decode(tiff);

        Assert.Equal(new RgbaColor(0, 0, 0), Pixel(bitmap, 0, 0));
        Assert.Equal(new RgbaColor(128, 128, 128), Pixel(bitmap, 1, 0));
        Assert.Equal(new RgbaColor(255, 255, 255), Pixel(bitmap, 2, 0));
    }

    [Fact]
    public void Floating_point_samples_of_0_to_1_are_levels()
    {
        // 32-bit floats 0, 0.5, 1 (big-endian).
        var tiff = TiffBuilder.Image(3, 1, 1, [32]).Tag(339, Short, 3)
            .Strip([0, 0, 0, 0, 0x3F, 0, 0, 0, 0x3F, 0x80, 0, 0]).Build();

        var bitmap = TiffFile.Decode(tiff);

        Assert.Equal(new RgbaColor(0, 0, 0), Pixel(bitmap, 0, 0));
        Near(new RgbaColor(128, 128, 128), Pixel(bitmap, 1, 0));
        Assert.Equal(new RgbaColor(255, 255, 255), Pixel(bitmap, 2, 0));
    }

    [Fact]
    public void Floating_point_samples_outside_0_to_1_are_stretched_over_their_range()
    {
        // 64-bit doubles 100, 300, 500.
        static byte[] Double(double v) => [.. BitConverter.GetBytes(v).Reverse()];
        var tiff = TiffBuilder.Image(3, 1, 1, [64]).Tag(339, Short, 3).Strip([.. Double(100), .. Double(300), .. Double(500)]).Build();

        var bitmap = TiffFile.Decode(tiff);

        Assert.Equal(new RgbaColor(0, 0, 0), Pixel(bitmap, 0, 0));
        Near(new RgbaColor(128, 128, 128), Pixel(bitmap, 1, 0));
        Assert.Equal(new RgbaColor(255, 255, 255), Pixel(bitmap, 2, 0));
    }

    [Fact]
    public void Half_floats()
    {
        // 16-bit floats: 0x3800 is 0.5, 0x3C00 is 1.
        var tiff = TiffBuilder.Image(2, 1, 1, [16]).Tag(339, Short, 3).Strip([0x38, 0x00, 0x3C, 0x00]).Build();

        var bitmap = TiffFile.Decode(tiff);

        Near(new RgbaColor(128, 128, 128), Pixel(bitmap, 0, 0));
        Assert.Equal(new RgbaColor(255, 255, 255), Pixel(bitmap, 1, 0));
    }

    [Fact]
    public void The_floating_point_predictor_differences_byte_planes()
    {
        // Floats 1.0 (3F800000) and 0.5 (3F000000): byte planes 3F 3F | 80 00 | 00 00 | 00 00, then each byte
        // differenced from the one before: 3F 00 41 80 00 00 00 00.
        var tiff = TiffBuilder.Image(2, 1, 1, [32]).Tag(339, Short, 3).Tag(317, Short, 3)
            .Strip([0x3F, 0x00, 0x41, 0x80, 0, 0, 0, 0]).Build();

        var bitmap = TiffFile.Decode(tiff);

        Assert.Equal(new RgbaColor(255, 255, 255), Pixel(bitmap, 0, 0));
        Near(new RgbaColor(128, 128, 128), Pixel(bitmap, 1, 0));
    }
}
