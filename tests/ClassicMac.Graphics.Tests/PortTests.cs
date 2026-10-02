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
        {
            foreach (var version in new[] { QuickDrawVersion.MacOS9, QuickDrawVersion.MacRom })
            {
                data.Add(name, version);
            }
        }

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
        }
        ),

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
        }
        ),

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
        foreach (var (v, h) in points)
        {
            b.Point(v, h);
        }
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

    private static QuickDrawPort Port(QuickDrawVersion version = QuickDrawVersion.MacOS9) =>
        new(new RgbaBitmap(Width, Height), new QuickDrawOptions { Version = version, Fonts = Fonts() }) { TextFont = Family, TextSize = 9 };

    [Theory]
    [InlineData(QuickDrawVersion.MacOS9)]
    [InlineData(QuickDrawVersion.MacRom)]
    public void SetOrigin_moves_drawing_and_the_pattern_phase_but_keeps_the_clip_local(QuickDrawVersion version)
    {
        var plain = Port(version);
        plain.FillRect(R(2, 2, 12, 12), QuickDrawPattern.Gray);

        var moved = Port(version);
        moved.SetOrigin(1, 10);
        Assert.Equal(R(10, 1, 10 + Height, 1 + Width), moved.PortRect);
        moved.FillRect(R(12, 3, 22, 13), QuickDrawPattern.Gray);   // the same canvas pixels, one column later in local h

        // Same pixels covered, pattern phase shifted by one column (h 1; v 10 is a whole pattern period plus 2 rows).
        Assert.Equal(plain.Canvas[2, 2].A, moved.Canvas[2, 2].A);
        Assert.NotEqual(plain.Canvas[2, 2], moved.Canvas[2, 2]);
        Assert.Equal(plain.Canvas[3, 2], moved.Canvas[2, 4]);

        var clipped = Port(version);
        clipped.Clip = Region.FromRect(R(0, 0, 5, 5));
        clipped.SetOrigin(-5, 0);                                   // the clip's local (0, 0) is now canvas (5, 0)
        clipped.PaintRect(R(0, -5, 30, 35));
        Assert.Equal(0, clipped.Canvas[0, 0].A);
        Assert.Equal(255, clipped.Canvas[5, 0].A);
        Assert.Equal(R(0, 0, 5, 5), clipped.Clip!.BoundingBox);
    }

    [Fact]
    public void A_hidden_pen_draws_nothing_but_still_moves()
    {
        var port = Port();
        port.HidePen();
        port.PaintRect(R(0, 0, 30, 40));
        port.MoveTo(2, 8);
        port.DrawString("AA ");
        Assert.Equal(new MacPoint(8, 10), port.PenLocation);
        port.LineTo(30, 20);
        Assert.All(Enumerable.Range(0, Width * Height), i => Assert.Equal(0, port.Canvas.Pixels[4 * i + 3]));

        port.ShowPen();
        port.PaintRect(R(0, 0, 2, 2));
        Assert.Equal(255, port.Canvas[0, 0].A);
    }

    [Theory]
    [InlineData(QuickDrawVersion.MacOS9)]
    [InlineData(QuickDrawVersion.MacRom)]
    public void Text_is_measured_as_the_Font_Manager_measures_it(QuickDrawVersion version)
    {
        var port = Port(version);
        Assert.Equal(3 + 3 + 2 + 3, port.StringWidth("Ag A"));
        Assert.Equal(3, port.CharWidth((byte)'g'));
        Assert.Equal(new FontInfo(3, 2, 3, 1), port.GetFontInfo());

        port.TextFace = QuickDrawStyle.Bold | QuickDrawStyle.Shadow;   // extra 1 + 2 per character; shadow grows the metrics
        Assert.Equal(new FontInfo(4, 4, 6, 1), port.GetFontInfo());
        Assert.Equal((3 + 3) * 2, port.StringWidth("AA"));

        // CharExtra: 2 pixels at 9 points is kept per point, so each character gets 1.9995 more and the width truncates.
        port.TextFace = QuickDrawStyle.Plain;
        port.CharExtra(new Fixed(2 << 16));
        Assert.Equal(9, port.StringWidth("AA"));
        port.TextSize = version == QuickDrawVersion.MacRom ? 9 : 10;    // the ROM clears it on any TextSize, Mac OS 9 on a change
        port.TextSize = 9;
        Assert.Equal(6, port.StringWidth("AA"));
    }

    private static byte[] Picture(bool clip, params (int top, int left, int bottom, int right)[] paints)
    {
        var b = PictBuilder.V2(0, 0, Height, Width);
        if (clip)
        {
            b.Align().U16(0x0001).U16(10).Rect(0, 0, Height, Width);
        }

        b.U16(0x001A).Rgb(0, 0, 0xFFFF);
        foreach (var (top, left, bottom, right) in paints)
        {
            b.U16(0x0031).Rect(top, left, bottom, right);
        }

        return b.Align().U16(0x00FF).ToArray();
    }

    [Theory]
    [InlineData(QuickDrawVersion.MacOS9)]
    [InlineData(QuickDrawVersion.MacRom)]
    public void DrawPicture_plays_the_picture_and_restores_the_port(QuickDrawVersion version)
    {
        var picture = Picture(clip: true, (2, 2, 20, 30), (10, 15, 28, 38));
        var port = Port(version);
        port.ForeColor = new RgbColor(0xFFFF, 0, 0);
        port.PenSize = new MacPoint(3, 3);
        port.TextFace = QuickDrawStyle.Italic;
        port.OpColor = new RgbColor(0x8000, 0x8000, 0x8000);
        port.MoveTo(7, 9);

        port.DrawPicture(picture, R(0, 0, Height, Width));

        Assert.Equal(PictReader.Decode(picture, new PictDecodeOptions { QuickDraw = version }).Pixels, port.Canvas.Pixels);
        Assert.Equal((new RgbColor(0xFFFF, 0, 0), new MacPoint(3, 3), QuickDrawStyle.Italic, new MacPoint(9, 7), (Region?)null),
            (port.ForeColor, port.PenSize, port.TextFace, port.PenLocation, port.Clip));
        Assert.Equal(RgbColor.Black, port.OpColor);
    }

    [Fact]
    public void DrawPicture_draws_nothing_before_a_clip_and_stays_inside_the_ports_clip()
    {
        var port = Port();
        port.DrawPicture(Picture(clip: false, (0, 0, Height, Width)), R(0, 0, Height, Width));
        Assert.All(Enumerable.Range(0, Width * Height), i => Assert.Equal(0, port.Canvas.Pixels[4 * i + 3]));

        port.Clip = Region.FromRect(R(0, 0, Height, 10));
        port.DrawPicture(Picture(clip: true, (0, 0, Height, Width)), R(0, 0, Height, Width));
        Assert.Equal(255, port.Canvas[9, 5].A);
        Assert.Equal(0, port.Canvas[10, 5].A);
    }

    [Fact]
    public void DrawPicture_scales_to_the_destination()
    {
        var port = Port();
        port.SetOrigin(-4, -2);
        port.DrawPicture(Picture(clip: true, (0, 0, Height, Width)), R(0, 0, Height / 2, Width / 2));
        Assert.Equal(255, port.Canvas[4, 2].A);
        Assert.Equal(255, port.Canvas[4 + Width / 2 - 1, 2 + Height / 2 - 1].A);
        Assert.Equal(0, port.Canvas[4 + Width / 2, 2].A);
        Assert.Equal(0, port.Canvas[3, 2].A);
    }

    // Mac OS 9's scaled CopyMask onto a 1-bit screen with colours other than black on white: every masked pixel black.
    [Theory]
    [InlineData(QuickDrawVersion.MacOS9, 16, false)]
    [InlineData(QuickDrawVersion.MacOS9, 8, true)]
    [InlineData(QuickDrawVersion.MacRom, 16, true)]
    public void CopyMask_on_a_one_bit_screen(QuickDrawVersion version, int size, bool imageKept)
    {
        byte[] image = [0xF0, 0xF0, 0xF0, 0xF0, 0x0F, 0x0F, 0x0F, 0x0F];
        byte[] mask = [0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF];
        var port = new QuickDrawPort(new RgbaBitmap(Width, Height), new QuickDrawOptions { Version = version, ScreenDepth = 1 })
        {
            ForeColor = RgbColor.White,
            BackColor = RgbColor.Black,
        };
        port.CopyMask(PixMap.FromBitMap(image, 1, R(0, 0, 8, 8)), PixMap.FromBitMap(mask, 1, R(0, 0, 8, 8)), R(0, 0, 8, 8), R(0, 0, 8, 8),
            R(0, 0, size, size));
        var colours = Enumerable.Range(0, size).Select(x => port.Canvas[x, 0]).Distinct().ToList();
        Assert.Equal(imageKept ? 2 : 1, colours.Count);
        if (!imageKept)
        {
            Assert.Equal(new RgbaColor(0, 0, 0), colours[0]);
        }
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
