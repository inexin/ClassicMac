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
}
