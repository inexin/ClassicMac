using ClassicMac.Graphics.Pict;
using ClassicMac.Graphics.QuickDraw;
using Xunit;

namespace ClassicMac.Graphics.Tests;

// Damaged pictures that once escaped as other exceptions (found by mutation fuzzing): drawn as the Mac draws them,
// or refused as malformed where the Mac traps.
public class MalformedPictureTests
{
    // Scaling from a zero-sized rectangle divides by zero: the 68k's DIVU traps (a "zero divide" system error), so the
    // picture is malformed rather than drawable.
    [Fact]
    public void Scaling_from_a_zero_size_is_refused_as_malformed()
    {
        var empty = new PictRect(0, 0, 10, 0);
        var target = new PictRect(0, 0, 10, 20);

        Assert.Throws<InvalidDataException>(() => PictureMapping.ScaleSize(2, 2, empty, target));
        Assert.Throws<InvalidDataException>(() => PictureMapping.MapPoint(5, 5, empty, target));
    }

    // An 8-bit PackBitsRect whose rowBytes (4) is too small for its 8 pixels: rows overlap as QuickDraw reads them, and
    // what the last row would read past the pixel data is zeros (the Mac reads whatever memory follows) [ClassicMac].
    [Fact]
    public void A_pixmap_with_rowBytes_too_small_for_its_width_is_drawn()
    {
        var b = PictBuilder.V2(0, 0, 2, 8);
        b.U16(0x0098).U16(0x8000 | 4).Rect(0, 0, 2, 8)
            .U16(0).U16(0).U16(0).U16(0).U16(72).U16(0).U16(72).U16(0)          // version, packType, packSize, hRes, vRes
            .U16(0).U16(8).U16(1).U16(8).U16(0).U16(0).U16(0).U16(0).U16(0).U16(0) // pixelType, pixelSize, cmpCount, cmpSize, planeBytes, pmTable, pmReserved
            .U16(0).U16(0).U16(0).U16(1)                                       // ctSeed, ctFlags, ctSize (2 entries)
            .U16(0).Rgb(0xFFFF, 0xFFFF, 0xFFFF).U16(1).Rgb(0, 0, 0)
            .Rect(0, 0, 2, 8).Rect(0, 0, 2, 8).U16(0)
            .Bytes(1, 0, 1, 0, 0, 1, 0, 1)
            .Align().U16(0x00FF);

        var bitmap = PictReader.Decode(b.ToArray());

        Assert.Equal((8, 2), (bitmap.Width, bitmap.Height));
    }

    // A frame too big to draw (found by libFuzzer: -32768…32767 square is 2³² pixels, whose RGBA size overflowed) is
    // refused before anything is allocated; PictDecodeOptions.MaxPixels sets the limit [ClassicMac].
    [Fact]
    public void A_picture_over_the_pixel_limit_is_refused_before_drawing()
    {
        var huge = PictBuilder.V2(-32768, -32768, 32767, 32767).U16(0x00FF).ToArray();
        var small = PictBuilder.V2(0, 0, 100, 100).U16(0x00FF).ToArray();

        var e = Assert.Throws<InvalidDataException>(() => PictReader.Decode(huge));
        Assert.Equal("The picture is 65535 × 65535 pixels, over the 67108864-pixel limit.", e.Message);
        Assert.Equal(64L * 1024 * 1024, PictDecodeOptions.Default.MaxPixels);
        Assert.Throws<InvalidDataException>(() => PictReader.Decode(small, new PictDecodeOptions { MaxPixels = 9_999 }));
        Assert.Equal(100, PictReader.Decode(small, new PictDecodeOptions { MaxPixels = 10_000 }).Width);
    }

    // A region row with an unpaired inversion point (found by libFuzzer: it reached InsetRgn as an odd span list). The
    // point flips every pixel right of it, and QuickDraw scans a region only within its rgnBBox, so the span runs to
    // the box's right edge; a point at or past that edge adds nothing [ClassicMac, from §1.3's rule].
    [Fact]
    public void An_unpaired_inversion_point_runs_to_the_region_box()
    {
        var box = new PictRect(0, 0, 10, 20);
        var region = Region.FromQuickDrawData(box, [0, 5, 0x7FFF, 4, 5, 0x7FFF, 0x7FFF]);

        Assert.True(region.Contains(5, 0));
        Assert.True(region.Contains(19, 3));
        Assert.False(region.Contains(20, 0));
        Assert.False(region.Contains(4, 0));
        Assert.False(region.Contains(5, 4));
        Assert.Equal(new PictRect(0, 5, 4, 20), region.Bounds);
        Assert.Equal(new PictRect(1, 6, 3, 19), region.Inset(1, 1).Bounds);
        Assert.True(Region.FromQuickDrawData(box, [0, 2, 4, 25, 0x7FFF, 4, 2, 4, 25, 0x7FFF, 0x7FFF]).Bands.All(b => b.Spans.Length == 2));
    }
}
