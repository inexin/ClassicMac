using ClassicMac.Core;
using ClassicMac.Graphics.Pict;
using ClassicMac.Graphics.QuickDraw;
using static ClassicMac.Graphics.Tests.TestFont;

namespace ClassicMac.Graphics.Tests;

// The public port draws what the picture player draws: each case once through QuickDrawPort and once as the same
// opcodes in a picture (frame = canvas, so no mapping), under both QuickDraws, compared pixel for pixel.
public class PortTests
{
    private const int Width = 40, Height = 30, Family = 400;

    private static readonly byte[] Font9 = Build(3, 2, 0, 1, new[]
    {
        new Glyph(' ', 2, 0),
        new Glyph('A', 3, 0, "##", "##", "##", "..", ".."),
        new Glyph('g', 3, 0, "..", "##", "##", "##", "##"),
    }, missing: new Glyph('\0', 2, 0, "#", "#", "#"));

    private static FontLibrary Fonts()
    {
        var library = new FontLibrary();
        library.AddFont(Family * 128 + 9, Font9);
        return library;
    }

    private static readonly byte[] Checker = [0xAA, 0x55, 0xAA, 0x55, 0xAA, 0x55, 0xAA, 0x55];
    private static readonly byte[] Stripes = [0xF0, 0xF0, 0xF0, 0xF0, 0x0F, 0x0F, 0x0F, 0x0F];
    private static MacRect R(int top, int left, int bottom, int right) => new((short)top, (short)left, (short)bottom, (short)right);

    public static TheoryData<string, QuickDrawVersion> Cases()
    {
        var data = new TheoryData<string, QuickDrawVersion>();
        foreach (var name in Drawings.Keys)
            foreach (var version in new[] { QuickDrawVersion.MacOS9, QuickDrawVersion.MacRom })
                data.Add(name, version);
        return data;
    }

    // Each drawing: the port calls, then the same as picture opcodes.
    private static readonly Dictionary<string, (Action<QuickDrawPort> Port, Action<PictBuilder> Picture)> Drawings = new()
    {
        ["rects"] = (p =>
        {
            p.ForeColor = new RgbColor(0xFFFF, 0, 0);
            p.PaintRect(R(0, 0, 30, 40));
            p.ForeColor = RgbColor.Black;
            p.PenSize = new MacPoint(2, 3);
            p.FrameRect(R(2, 2, 20, 30));
            p.PenPattern = QuickDrawPattern.FromBits(Checker);
            p.PaintRect(R(5, 5, 12, 25));
            p.BackPattern = QuickDrawPattern.FromBits(Stripes);
            p.EraseRect(R(14, 6, 26, 18));
            p.InvertRect(R(10, 20, 28, 38));
            p.FillRect(R(22, 1, 29, 12), QuickDrawPattern.Gray);
        }, b => b
            .U16(0x001A).Rgb(0xFFFF, 0, 0).U16(0x0031).Rect(0, 0, 30, 40)
            .U16(0x001A).Rgb(0, 0, 0).U16(0x0007).Point(2, 3).U16(0x0030).Rect(2, 2, 20, 30)
            .U16(0x0009).Bytes(Checker).U16(0x0031).Rect(5, 5, 12, 25)
            .U16(0x0002).Bytes(Stripes).U16(0x0032).Rect(14, 6, 26, 18)
            .U16(0x0033).Rect(10, 20, 28, 38)
            .U16(0x000A).Bytes(QuickDrawPattern.Gray.Bits.ToArray()).U16(0x0034).Rect(22, 1, 29, 12)),

        ["curves"] = (p =>
        {
            p.PenSize = new MacPoint(2, 2);
            p.FrameOval(R(1, 1, 20, 30));
            p.PaintRoundRect(R(8, 10, 28, 38), 12, 8);
            p.PenMode = TransferMode.PatXor;
            p.PaintArc(R(0, 0, 30, 40), 45, 120);
            p.PenMode = TransferMode.PatCopy;
            p.FrameArc(R(4, 4, 26, 36), 200, 100);
            p.FrameRoundRect(R(2, 20, 16, 39), 6, 6);
            p.InvertOval(R(12, 2, 29, 20));
        }, b => b
            .U16(0x0007).Point(2, 2).U16(0x0050).Rect(1, 1, 20, 30)
            .U16(0x000B).Point(8, 12).U16(0x0041).Rect(8, 10, 28, 38)
            .U16(0x0008).U16(10).U16(0x0061).Rect(0, 0, 30, 40).U16(45).U16(120)
            .U16(0x0008).U16(8).U16(0x0060).Rect(4, 4, 26, 36).U16(200).U16(100)
            .U16(0x000B).Point(6, 6).U16(0x0040).Rect(2, 20, 16, 39)
            .U16(0x0053).Rect(12, 2, 29, 20)),

        ["lines, polygons, regions"] = (p =>
        {
            p.PenSize = new MacPoint(1, 2);
            p.MoveTo(1, 1);
            p.LineTo(38, 20);
            p.Line(-30, 8);
            p.FramePoly([new MacPoint(2, 20), new MacPoint(15, 35), new MacPoint(28, 5), new MacPoint(2, 20)]);
            p.PaintPoly([new MacPoint(5, 5), new MacPoint(25, 12), new MacPoint(10, 30), new MacPoint(5, 5)]);
            var region = Region.FromRect(R(3, 3, 12, 12)).Union(Region.FromRect(R(8, 8, 20, 22)));
            p.FrameRgn(region);
            p.FillRgn(region.Offset(15, 5), QuickDrawPattern.LightGray);
        }, b =>
        {
            b.U16(0x0007).Point(1, 2).U16(0x0020).Point(1, 1).Point(20, 38)
                .U16(0x0020).Point(20, 38).Point(28, 8);
            Poly(b, 0x0070, (2, 20), (15, 35), (28, 5), (2, 20));
            Poly(b, 0x0071, (5, 5), (25, 12), (10, 30), (5, 5));
            var region = Region.FromRect(R(3, 3, 12, 12)).Union(Region.FromRect(R(8, 8, 20, 22)));
            b.U16(0x0080).Bytes(region.ToRgnData()).Align();
            b.U16(0x000A).Bytes(QuickDrawPattern.LightGray.Bits.ToArray()).U16(0x0084).Bytes(region.Offset(15, 5).ToRgnData()).Align();
        }),

        ["clip, hilite"] = (p =>
        {
            p.Clip = Region.Oval(R(0, 0, 30, 40));
            p.PaintRect(R(0, 0, 30, 40));
            p.HiliteColor = new RgbColor(0, 0xFFFF, 0);
            p.HiliteMode();
            p.InvertRect(R(5, 5, 25, 35));
        }, b => b
            .U16(0x0001).Bytes(Region.Oval(R(0, 0, 30, 40)).ToRgnData())
            .U16(0x0031).Rect(0, 0, 30, 40)
            .U16(0x001D).Rgb(0, 0xFFFF, 0).U16(0x001C).U16(0x0033).Rect(5, 5, 25, 35)),

        ["copybits"] = (p =>
        {
            byte[] bits = [0xF0, 0x0F, 0xFF, 0x00, 0xAA, 0x55, 0x3C, 0xC3];
            p.ForeColor = new RgbColor(0, 0, 0xFFFF);
            p.CopyBits(PixMap.FromBitMap(bits, 1, R(0, 0, 8, 8)), R(0, 0, 8, 8), R(2, 3, 26, 35), TransferMode.SrcCopy);
            p.CopyBits(PixMap.FromBitMap(bits, 1, R(0, 0, 8, 8)), R(2, 2, 6, 8), R(20, 20, 28, 39), TransferMode.SrcXor, Region.Oval(R(18, 18, 30, 40)));
        }, b =>
        {
            byte[] bits = [0xF0, 0x0F, 0xFF, 0x00, 0xAA, 0x55, 0x3C, 0xC3];
            b.U16(0x001A).Rgb(0, 0, 0xFFFF)
                .U16(0x0090).U16(1).Rect(0, 0, 8, 8).Rect(0, 0, 8, 8).Rect(2, 3, 26, 35).U16(0).Bytes(bits).Align()
                .U16(0x0091).U16(1).Rect(0, 0, 8, 8).Rect(2, 2, 6, 8).Rect(20, 20, 28, 39).U16(2)
                .Bytes(Region.Oval(R(18, 18, 30, 40)).ToRgnData()).Bytes(bits).Align();
        }),

        ["text"] = (p =>
        {
            p.TextFont = Family;
            p.TextSize = 9;
            p.TextFace = QuickDrawStyle.Bold | QuickDrawStyle.Underline;
            p.MoveTo(2, 8);
            p.DrawString("Ag A");
            p.TextFace = QuickDrawStyle.Italic;
            p.TextMode = TransferMode.SrcXor;
            p.MoveTo(4, 20);
            p.DrawString("gAz");
        }, b => b
            .U16(0x0003).U16(Family).U16(0x000D).U16(9).U16(0x0004).U8(5).Align()
            .U16(0x0028).Point(8, 2).Text("Ag A").Align()
            .U16(0x0004).U8(2).Align().U16(0x0005).U16(2)
            .U16(0x0028).Point(20, 4).Text("gAz").Align()),
    };

    private static void Poly(PictBuilder b, int opcode, params (int v, int h)[] points)
    {
        int top = points.Min(p => p.v), left = points.Min(p => p.h), bottom = points.Max(p => p.v), right = points.Max(p => p.h);
        b.U16(opcode).U16(10 + 4 * points.Length).Rect(top, left, bottom, right);
        foreach (var (v, h) in points) b.Point(v, h);
    }

    [Theory]
    [MemberData(nameof(Cases))]
    public void The_port_draws_what_the_picture_player_draws(string drawing, QuickDrawVersion version)
    {
        var (portCalls, opcodes) = Drawings[drawing];
        var fonts = Fonts();
        var canvas = new RgbaBitmap(Width, Height);
        portCalls(new QuickDrawPort(canvas, new QuickDrawOptions { Version = version, Fonts = fonts }));

        var picture = PictBuilder.V2(0, 0, Height, Width);
        opcodes(picture);
        picture.Align().U16(0x00FF);
        var expected = PictReader.Decode(picture.ToArray(), new PictDecodeOptions { QuickDraw = version, Fonts = fonts });

        Assert.Equal(expected.Pixels, canvas.Pixels);
        Assert.Contains(canvas.Pixels, b => b != 0);
    }

    [Fact]
    public void Text_moves_the_pen_by_its_width()
    {
        var port = new QuickDrawPort(new RgbaBitmap(Width, Height), new QuickDrawOptions { Fonts = Fonts() })
        {
            TextFont = Family,
            TextSize = 9,
        };
        port.MoveTo(2, 8);
        port.DrawString("AA ");

        Assert.Equal(new MacPoint(8, 2 + 3 + 3 + 2), port.PenLocation);
    }

    [Fact]
    public void Regions_round_trip_through_their_stored_form()
    {
        var region = Region.Oval(R(0, 0, 13, 17)).Difference(Region.FromRect(R(4, 4, 8, 9)));

        var copy = Region.FromRgnData(region.ToRgnData());

        Assert.Equal(region.ToRgnData(), copy.ToRgnData());
        Assert.Equal(R(0, 0, 13, 17), copy.BoundingBox);
        Assert.False(copy.Contains(new MacPoint(5, 5)));
        Assert.True(copy.Contains(new MacPoint(6, 1)));
        Assert.Equal(10, Region.FromRect(R(1, 2, 3, 4)).ToRgnData().Length);
    }
}
