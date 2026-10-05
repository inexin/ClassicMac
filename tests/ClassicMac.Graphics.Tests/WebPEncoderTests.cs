using ClassicMac.Core;
using ClassicMac.Resources.Decoders.Images;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;
using SkiaSharp;
using Xunit;

namespace ClassicMac.Graphics.Tests;

// Lossless WebP (docs/formats/output/webp.md): VP8L in a RIFF container, read back pixel for pixel by two decoders
// written apart from it, ImageSharp's and libwebp (through SkiaSharp).
public sealed class WebPEncoderTests
{
    private static byte[] ImageSharpPixels(byte[] webp, out int width, out int height)
    {
        using var image = Image.Load<Rgba32>(webp);
        (width, height) = (image.Width, image.Height);
        var pixels = new byte[image.Width * image.Height * 4];
        image.CopyPixelDataTo(pixels);
        return pixels;
    }

    private static byte[] SkiaPixels(byte[] webp, int width, int height)
    {
        using var codec = SKCodec.Create(new SKMemoryStream(webp));
        Assert.Equal(SKEncodedImageFormat.Webp, codec.EncodedFormat);
        var info = new SKImageInfo(width, height, SKColorType.Rgba8888, SKAlphaType.Unpremul);
        using var bitmap = new SKBitmap(info);
        Assert.Equal(SKCodecResult.Success, codec.GetPixels(info, bitmap.GetPixels()));
        return bitmap.Bytes;
    }

    private static void RoundTrips(int width, int height, byte[] rgba)
    {
        var webp = WebPEncoder.Instance.Encode(width, height, rgba);

        Assert.Equal("RIFF", System.Text.Encoding.ASCII.GetString(webp, 0, 4));
        Assert.Equal("WEBPVP8L", System.Text.Encoding.ASCII.GetString(webp, 8, 8));
        Assert.Equal(0, webp.Length % 2);
        Assert.Equal(webp.Length - 8, BitConverter.ToInt32(webp, 4));
        Assert.Equal(rgba, ImageSharpPixels(webp, out var w, out var h));
        Assert.Equal((width, height), (w, h));
        Assert.Equal(rgba, SkiaPixels(webp, width, height));
    }

    private static byte[] Pixels(int width, int height, Func<int, int, (byte R, byte G, byte B, byte A)> at)
    {
        var rgba = new byte[width * height * 4];
        for (var y = 0; y < height; y++)
        {
            for (var x = 0; x < width; x++)
            {
                var (r, g, b, a) = at(x, y);
                var i = (y * width + x) * 4;
                (rgba[i], rgba[i + 1], rgba[i + 2], rgba[i + 3]) = (r, g, b, a);
            }
        }

        return rgba;
    }

    [Fact]
    public void Noise_round_trips()
    {
        var random = new Random(1);
        var rgba = new byte[97 * 61 * 4];
        random.NextBytes(rgba);
        RoundTrips(97, 61, rgba);
    }

    [Fact]
    public void Gradients_flat_areas_and_alpha_round_trip()
    {
        RoundTrips(130, 70, Pixels(130, 70, (x, y) => ((byte)(x * 2), (byte)(y * 3), (byte)(x + y), 255)));
        RoundTrips(64, 64, Pixels(64, 64, (x, y) => x < 32 ? ((byte)200, (byte)10, (byte)10, (byte)255) : ((byte)0, (byte)0, (byte)0, (byte)0)));
        RoundTrips(50, 40, Pixels(50, 40, (x, y) => ((byte)(x * 5), (byte)(255 - y * 6), (byte)(x ^ y), (byte)(x * y % 256))));
    }

    // A picture drawn the Mac's way: few colours, repeated rows and blocks (LZ77 and the colour cache at work).
    [Fact]
    public void Repeated_patterns_round_trip_and_shrink()
    {
        var rgba = Pixels(256, 192, (x, y) => ((x / 8 + y / 8) % 2 == 0 ? ((byte)255, (byte)255, (byte)255, (byte)255) : ((byte)0, (byte)0, (byte)204, (byte)255)));
        RoundTrips(256, 192, rgba);
        var webp = WebPEncoder.Instance.Encode(256, 192, rgba);
        Assert.True(webp.Length < 400, $"{webp.Length} bytes");
        Assert.True(webp.Length < PngEncoder.Instance.Encode(256, 192, rgba).Length);
    }

    [Theory]
    [InlineData(1, 1)]
    [InlineData(1, 300)]
    [InlineData(300, 1)]
    [InlineData(17, 33)]
    public void Edge_sizes_round_trip(int width, int height)
    {
        RoundTrips(width, height, Pixels(width, height, (x, y) => ((byte)(x * 7 + y), (byte)(y * 11), (byte)(x * 13), (byte)(255 - x - y))));
    }

    [Fact]
    public void Sizes_WebP_cannot_hold_are_refused()
    {
        Assert.Equal(("webp", ".webp"), (WebPEncoder.Instance.Name, WebPEncoder.Instance.Extension));
        Assert.Throws<ArgumentOutOfRangeException>(() => WebPEncoder.Instance.Encode(16385, 1, new byte[16385 * 4]));
        Assert.Throws<ArgumentOutOfRangeException>(() => WebPEncoder.Instance.Encode(0, 1, []));
        Assert.Throws<ArgumentException>(() => WebPEncoder.Instance.Encode(2, 2, new byte[15]));
    }
}
