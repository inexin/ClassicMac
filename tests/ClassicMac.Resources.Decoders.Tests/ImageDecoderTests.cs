using System.Text.Json;
using ClassicMac.Core;
using ClassicMac.Resources.Export;

namespace ClassicMac.Resources.Decoders.Tests;

public class ImageDecoderTests
{
    private static (IReadOnlyList<DecodedFile> Files, List<Diagnostic> Diagnostics) Decode(
        Resource resource, DecodeOptions? options = null, params Resource[] others)
    {
        var fork = new ResourceFork();
        fork.Add(resource);
        foreach (var other in others)
        {
            fork.Add(other);
        }

        var diagnostics = new List<Diagnostic>();
        var decoder = ResourceDecoders.Create(options).Single(d => d.CanDecode(resource.Type));
        return (decoder.Decode(new DecodeInput(resource, resource.GetData(), fork, diagnostics: diagnostics)), diagnostics);
    }

    private static Resource Res(string type, short id, byte[] data) => new(FourCC.FromString(type), id, data);

    // RGBA of pixel (x, y).
    private static (byte R, byte G, byte B, byte A) Pixel((int Width, int Height, byte[] Rgba) image, int x, int y)
    {
        var at = (y * image.Width + x) * 4;
        return (image.Rgba[at], image.Rgba[at + 1], image.Rgba[at + 2], image.Rgba[at + 3]);
    }

    // A 32×32 1-bit image: the top row black, the rest white.
    private static byte[] TopRow() => [.. Enumerable.Repeat((byte)0xFF, 4), .. new byte[124]];

    [Fact]
    public void ICON_is_black_on_white()
    {
        var (files, diagnostics) = Decode(Res("ICON", 128, TopRow()));

        Assert.Empty(diagnostics);
        var image = TestPng.Read(Assert.Single(files).Content.Span);
        Assert.Equal((32, 32), (image.Width, image.Height));
        Assert.Equal((0, 0, 0, 255), Pixel(image, 5, 0));
        Assert.Equal((255, 255, 255, 255), Pixel(image, 5, 1));
    }

    [Fact]
    public void Icon_lists_use_their_mask_as_transparency()
    {
        // Image: top row black. Mask: only the top two rows.
        byte[] mask = [.. Enumerable.Repeat((byte)0xFF, 8), .. new byte[120]];
        var (files, _) = Decode(Res("ICN#", 128, [.. TopRow(), .. mask]));

        var image = TestPng.Read(files[0].Content.Span);
        Assert.Equal((0, 0, 0, 255), Pixel(image, 0, 0));
        Assert.Equal(255, Pixel(image, 0, 1).A);
        Assert.Equal(0, Pixel(image, 0, 2).A);
    }

    [Fact]
    public void Colour_icons_take_the_mask_of_their_icon_list()
    {
        var icl8 = Res("icl8", 128, Enumerable.Repeat((byte)0xFF, 1024).ToArray()); // colour 255: black
        byte[] mask = [.. Enumerable.Repeat((byte)0xFF, 4), .. new byte[124]];
        var list = Res("ICN#", 128, [.. new byte[128], .. mask]);

        var (files, diagnostics) = Decode(icl8, others: list);
        Assert.Empty(diagnostics);
        var image = TestPng.Read(files[0].Content.Span);
        Assert.Equal((0, 0, 0, 255), Pixel(image, 3, 0));
        Assert.Equal(0, Pixel(image, 3, 1).A);

        (files, diagnostics) = Decode(Res("icl8", 128, Enumerable.Repeat((byte)0xFF, 1024).ToArray())); // no ICN#: opaque, noted
        Assert.Equal(255, Pixel(TestPng.Read(files[0].Content.Span), 3, 1).A);
        Assert.Equal("image.no-mask", Assert.Single(diagnostics).Code);
    }

    [Fact]
    public void Lists_become_numbered_images()
    {
        var (files, _) = Decode(Res("SICN", 128, new byte[64])); // two 16×16 icons
        Assert.Equal([".1.png", ".2.png"], files.Select(f => f.Extension));
        Assert.Equal(16, TestPng.Read(files[1].Content.Span).Width);
    }

    [Fact]
    public void Cursors_come_with_their_hotspot_and_inverted_pixels()
    {
        // CURS: 16×16 data, 16×16 mask, hotspot (v, h). Data all set, mask only the first row: the rest inverts.
        byte[] curs = [.. Enumerable.Repeat((byte)0xFF, 32), 0xFF, 0xFF, .. new byte[30], 0, 3, 0, 5];

        var (files, diagnostics) = Decode(Res("CURS", 128, curs));

        Assert.Empty(diagnostics);
        Assert.Equal([".png", ".json"], files.Select(f => f.Extension));
        var json = JsonDocument.Parse(files[1].Content).RootElement;
        Assert.Equal((5, 3), (json.GetProperty("hotspot").GetProperty("h").GetInt32(), json.GetProperty("hotspot").GetProperty("v").GetInt32()));
        var rows = json.GetProperty("inverted").EnumerateArray().Select(r => r.GetString()).ToList();
        Assert.Equal(new string('0', 16), rows[0]);
        Assert.Equal(new string('1', 16), rows[1]);
    }

    [Fact]
    public void Patterns_are_eight_by_eight()
    {
        var (files, _) = Decode(Res("PAT ", 128, [0xAA, 0x55, 0xAA, 0x55, 0xAA, 0x55, 0xAA, 0x55]));
        var image = TestPng.Read(files[0].Content.Span);
        Assert.Equal((8, 8), (image.Width, image.Height));
        Assert.Equal((0, 0, 0, 255), Pixel(image, 0, 0));
        Assert.Equal((255, 255, 255, 255), Pixel(image, 1, 0));
    }

    // A version 1 picture: size, frame (0,0)–(4,4), picVersion 1, PaintRect (0,0)–(2,2) with the black pen, end.
    private static byte[] Picture(short bottom = 4, short right = 4) =>
        [0, 0, 0, 0, 0, 0, (byte)(bottom >> 8), (byte)bottom, (byte)(right >> 8), (byte)right, 0x11, 0x01, 0x31, 0, 0, 0, 0, 0, 2, 0, 2, 0xFF];

    [Fact]
    public void Pictures_are_drawn()
    {
        var (files, diagnostics) = Decode(Res("PICT", 128, Picture()));

        Assert.Empty(diagnostics);
        var image = TestPng.Read(Assert.Single(files).Content.Span);
        Assert.Equal((4, 4), (image.Width, image.Height));
        Assert.Equal((0, 0, 0, 255), Pixel(image, 1, 1));
        Assert.NotEqual((0, 0, 0, 255), Pixel(image, 3, 3));
    }

    [Fact]
    public void Pictures_over_the_limit_and_damaged_resources_are_left_raw()
    {
        var (files, diagnostics) = Decode(Res("PICT", 128, Picture(30000, 30000)), DecodeOptions.Default with { MaxImagePixels = 1_000_000 });
        Assert.Empty(files);
        Assert.Equal("image.too-large", Assert.Single(diagnostics).Code);

        (files, diagnostics) = Decode(Res("ICN#", 128, new byte[10]));
        Assert.Empty(files);
        Assert.Equal("image.undecodable", Assert.Single(diagnostics).Code);
    }
}
