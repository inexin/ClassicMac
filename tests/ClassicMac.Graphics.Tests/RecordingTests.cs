using ClassicMac.Core;
using ClassicMac.Graphics.QuickDraw;

namespace ClassicMac.Graphics.Tests;

// Region and polygon recording (docs/formats/graphics/quickdraw.md §2.26, §2.27): OpenRgn/CloseRgn take whole shapes
// and lines as inversion points; OpenPoly/ClosePoly take lines' end points; the pen is hidden meanwhile.
public class RecordingTests
{
    private static MacRect R(int top, int left, int bottom, int right) => new((short)top, (short)left, (short)bottom, (short)right);

    private static QuickDrawPort Port(QuickDrawVersion version) =>
        new(new RgbaBitmap(60, 60), new QuickDrawOptions { Version = version });

    public static TheoryData<QuickDrawVersion> Versions => [QuickDrawVersion.MacOS9, QuickDrawVersion.MacRom];

    private static bool SameRegion(Region a, Region b) => a.Xor(b).IsEmpty;

    [Theory]
    [MemberData(nameof(Versions))]
    public void A_framed_rect_records_the_whole_rect_and_overlaps_combine_even_odd(QuickDrawVersion version)
    {
        var port = Port(version);
        port.OpenRgn();
        port.FrameRect(R(10, 10, 30, 30));
        Assert.True(SameRegion(Region.FromRect(R(10, 10, 30, 30)), port.CloseRgn()!));

        port.OpenRgn();
        port.FrameRect(R(10, 10, 30, 30));
        port.FrameRect(R(20, 20, 40, 40));
        var expected = Region.FromRect(R(10, 10, 30, 30)).Xor(Region.FromRect(R(20, 20, 40, 40)));
        Assert.True(SameRegion(expected, port.CloseRgn()!));
        Assert.All(port.Canvas.Pixels.ToArray(), b => Assert.Equal(0, b));                     // the pen was hidden
        Assert.Equal(0, port.PenVisibility);
    }

    [Theory]
    [MemberData(nameof(Versions))]
    public void Lines_record_their_edges_and_ovals_and_regions_their_shapes(QuickDrawVersion version)
    {
        var port = Port(version);
        port.OpenRgn();
        port.MoveTo(10, 10);
        port.LineTo(50, 20);
        port.LineTo(20, 45);
        port.LineTo(10, 10);
        var triangle = port.CloseRgn()!;
        Assert.True(SameRegion(Region.Polygon([new(10, 10), new(20, 50), new(45, 20)]), triangle));

        port.OpenRgn();
        port.FrameOval(R(5, 5, 25, 35));
        port.FrameRgn(Region.FromRect(R(40, 40, 50, 50)));
        port.FrameRoundRect(R(30, 0, 50, 20), 8, 8);
        var expected = Region.Oval(R(5, 5, 25, 35)).Union(Region.FromRect(R(40, 40, 50, 50))).Union(Region.RoundRect(R(30, 0, 50, 20), 8, 8));
        Assert.True(SameRegion(expected, port.CloseRgn()!));
    }

    [Theory]
    [MemberData(nameof(Versions))]
    public void Vertical_lines_add_nothing_and_four_points_are_a_rect(QuickDrawVersion version)
    {
        var port = Port(version);
        port.OpenRgn();
        port.MoveTo(10, 10);
        port.LineTo(10, 40);
        Assert.True(port.CloseRgn()!.IsEmpty);

        // Two horizontal lines: four points, packed as the rect from the first to the last.
        port.OpenRgn();
        port.MoveTo(10, 10);
        port.LineTo(20, 10);
        port.MoveTo(15, 30);
        port.LineTo(25, 30);
        Assert.Equal(R(10, 10, 30, 25), port.CloseRgn()!.BoundingBox);
    }

    [Theory]
    [MemberData(nameof(Versions))]
    public void Fills_text_and_moves_record_nothing_and_CloseRgn_without_OpenRgn_does_nothing(QuickDrawVersion version)
    {
        var port = Port(version);
        Assert.Null(port.CloseRgn());
        port.OpenRgn();
        port.PaintRect(R(0, 0, 10, 10));
        port.PaintOval(R(0, 0, 10, 10));
        port.Move(5, 5);
        port.FrameArc(R(0, 0, 20, 20), 0, 90);
        Assert.True(port.CloseRgn()!.IsEmpty);
    }

    [Theory]
    [InlineData(QuickDrawVersion.MacRom, 4, false)]
    [InlineData(QuickDrawVersion.MacOS9, 4, false)]
    [InlineData(QuickDrawVersion.MacRom, 10, true)]
    [InlineData(QuickDrawVersion.MacOS9, 10, true)]
    public void Too_many_points_overflow_each_QuickDraw_its_own_way(QuickDrawVersion version, int lines, bool overflows)
    {
        // A diagonal of 1,000 × 1,000 is about 2,000 points (8,000 bytes): the ROM refuses a line once its buffer would
        // pass $FE00 bytes and gives an empty region; Mac OS 9 caps it at $7FF8 and leaves the destination unchanged.
        var port = Port(version);
        port.OpenRgn();
        for (var i = 0; i < lines; i++)
        {
            port.MoveTo(i * 3, 0);
            port.LineTo(i * 3 + 1000, 1000);
        }

        var region = port.CloseRgn();
        if (!overflows)
        {
            Assert.False(region!.IsEmpty);
        }
        else if (version == QuickDrawVersion.MacRom)
        {
            Assert.True(region!.IsEmpty);
        }
        else
        {
            Assert.Null(region);
        }

        Assert.Equal(0, port.PenVisibility);
    }

    [Theory]
    [MemberData(nameof(Versions))]
    public void A_polygon_records_line_ends_without_closing_or_dropping_duplicates(QuickDrawVersion version)
    {
        var port = Port(version);
        port.MoveTo(10, 10);
        port.OpenPoly();
        port.LineTo(40, 10);
        port.LineTo(40, 30);
        port.LineTo(40, 30);                                                                      // a duplicate, kept
        port.MoveTo(5, 50);                                                                       // dropped
        port.LineTo(10, 40);
        var polygon = port.ClosePoly()!;

        Assert.Equal([new MacPoint(10, 10), new MacPoint(10, 40), new MacPoint(30, 40), new MacPoint(30, 40), new MacPoint(40, 10)], polygon.Points);
        Assert.Equal(R(10, 10, 40, 40), polygon.BoundingBox);
        Assert.Equal(0, port.PenVisibility);
        Assert.All(port.Canvas.Pixels.ToArray(), b => Assert.Equal(0, b));
        Assert.Null(port.ClosePoly());
    }

    [Theory]
    [MemberData(nameof(Versions))]
    public void An_open_polygon_takes_the_lines_and_an_open_region_the_rest(QuickDrawVersion version)
    {
        var port = Port(version);
        port.OpenRgn();
        port.OpenPoly();
        port.MoveTo(0, 0);
        port.LineTo(20, 20);
        port.FramePoly([new(30, 30), new(30, 40), new(40, 40)]);                          // (v, h)
        port.FrameRect(R(50, 50, 55, 55));
        var polygon = port.ClosePoly()!;
        var region = port.CloseRgn()!;

        Assert.Equal([new MacPoint(0, 0), new MacPoint(20, 20), new MacPoint(30, 40), new MacPoint(40, 40)], polygon.Points);
        Assert.True(SameRegion(Region.FromRect(R(50, 50, 55, 55)), region));
        Assert.Equal(0, port.PenVisibility);
    }
}
