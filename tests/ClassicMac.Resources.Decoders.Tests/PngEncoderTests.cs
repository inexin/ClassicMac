using ClassicMac.Resources.Decoders.Images;

namespace ClassicMac.Resources.Decoders.Tests;

public class PngEncoderTests
{
    [Fact]
    public void Pixels_round_trip_through_PNG()
    {
        var rgba = Enumerable.Range(0, 5 * 3 * 4).Select(i => (byte)(i * 37 + 11)).ToArray();

        var png = PngEncoder.Instance.Encode(5, 3, rgba);

        var (width, height, read) = TestPng.Read(png);
        Assert.Equal((5, 3), (width, height));
        Assert.Equal(rgba, read);
        Assert.Equal((".png", "png"), (PngEncoder.Instance.Extension, PngEncoder.Instance.Name));
    }

    [Fact]
    public void Too_few_pixels_are_refused()
    {
        Assert.Throws<ArgumentException>(() => PngEncoder.Instance.Encode(4, 4, new byte[10]));
    }
}
