using ClassicMac.Core;
using ClassicMac.Graphics.Pict;
using ClassicMac.Graphics.QuickDraw;

namespace ClassicMac.Graphics.Tests;

// Picture recording (docs/formats/graphics/pict.md §3.3): the port's drawing as version 2 opcodes, lazily with the state
// it needs, as both QuickDraws record it; a recorded picture draws what the port drew.
public class PictureRecordingTests
{
    private static MacRect R(int top, int left, int bottom, int right) => new((short)top, (short)left, (short)bottom, (short)right);

    private static QuickDrawPort Port(QuickDrawVersion version, int size = 100) =>
        new(new RgbaBitmap(size, size), new QuickDrawOptions { Version = version });

    public static TheoryData<QuickDrawVersion> Versions => [QuickDrawVersion.MacOS9, QuickDrawVersion.MacRom];

    // The example the traced code gives (OpenCPicture at 72 dpi, a line, a frame, an oval, text, a 1-bit CopyBits): the
    // same bytes from the ROM's rules and Mac OS 9's.
    [Theory]
    [MemberData(nameof(Versions))]
    public void A_recording_is_the_bytes_QuickDraw_writes(QuickDrawVersion version)
    {
        var port = Port(version);
        var picture = PictureRecorder.OpenCPicture(port, R(0, 0, 100, 100), 72, 72);
        port.MoveTo(10, 10);
        port.LineTo(50, 30);
        port.FrameRect(R(20, 20, 60, 60));
        port.PaintOval(R(30, 30, 70, 80));
        port.DrawString("Hi");
        var bits = Enumerable.Range(0, 16).SelectMany(y => y % 2 == 0 ? new byte[] { 0xAA, 0xAA } : new byte[] { 0x55, 0x55 }).ToArray();
        port.CopyBits(PixMap.FromBitMap(bits, 2, R(0, 0, 16, 16)), R(0, 0, 16, 16), R(40, 40, 56, 56), TransferMode.SrcCopy);
        var bytes = picture.ClosePicture();

        byte[] expected =
        [
            0x00, 0xA2, 0x00, 0x00, 0x00, 0x00, 0x00, 0x64, 0x00, 0x64, 0x00, 0x11, 0x02, 0xFF, 0x0C, 0x00,
            0xFF, 0xFE, 0x00, 0x00, 0x00, 0x48, 0x00, 0x00, 0x00, 0x48, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00,
            0x00, 0x64, 0x00, 0x64, 0x00, 0x00, 0x00, 0x00, 0x00, 0x1E, 0x00, 0x01, 0x00, 0x0A, 0x80, 0x01,
            0x80, 0x01, 0x7F, 0xFF, 0x7F, 0xFF, 0x00, 0x22, 0x00, 0x0A, 0x00, 0x0A, 0x28, 0x14, 0x00, 0x30,
            0x00, 0x14, 0x00, 0x14, 0x00, 0x3C, 0x00, 0x3C, 0x00, 0x51, 0x00, 0x1E, 0x00, 0x1E, 0x00, 0x46,
            0x00, 0x50, 0x00, 0x2E, 0x00, 0x04, 0x00, 0x00, 0x00, 0x00, 0x00, 0x2B, 0x32, 0x1E, 0x02, 0x48,
            0x69, 0x00, 0x00, 0x90, 0x00, 0x02, 0x00, 0x00, 0x00, 0x00, 0x00, 0x10, 0x00, 0x10, 0x00, 0x00,
            0x00, 0x00, 0x00, 0x10, 0x00, 0x10, 0x00, 0x28, 0x00, 0x28, 0x00, 0x38, 0x00, 0x38, 0x00, 0x00,
            .. bits,
            0x00, 0xFF,
        ];
        Assert.Equal(expected, bytes);
        Assert.Equal(0, port.PenVisibility);
    }

    [Theory]
    [MemberData(nameof(Versions))]
    public void State_is_written_when_a_drawing_needs_it_and_the_same_rect_once(QuickDrawVersion version)
    {
        var port = Port(version);
        var picture = PictureRecorder.OpenPicture(port, R(0, 0, 100, 100));
        port.ForeColor = new RgbColor(0xFFFF, 0, 0);                               // nothing yet
        port.PenSize = new MacPoint(3, 2);
        port.FrameRect(R(10, 10, 20, 20));
        port.PaintOval(R(10, 10, 20, 20));                                          // the same rect: $59, no rect
        port.PicComment(100, []);
        var bytes = picture.ClosePicture();

        var tail = bytes.AsSpan(40).ToArray();                                      // after the −1 header
        byte[] expected =
        [
            0x00, 0x1E,                                                             // DefHilite
            0x00, 0x1A, 0xFF, 0xFF, 0x00, 0x00, 0x00, 0x00,                         // RGBFgCol
            0x00, 0x01, 0x00, 0x0A, 0x80, 0x01, 0x80, 0x01, 0x7F, 0xFF, 0x7F, 0xFF, // Clip
            0x00, 0x07, 0x00, 0x03, 0x00, 0x02,                                     // PnSize (v, h)
            0x00, 0x30, 0x00, 0x0A, 0x00, 0x0A, 0x00, 0x14, 0x00, 0x14,             // frameRect
            0x00, 0x59,                                                             // paintSameOval
            0x00, 0xA0, 0x00, 0x64,                                                 // ShortComment
            0x00, 0xFF,
        ];
        Assert.Equal(expected, tail);
        Assert.Equal([0x00, 0x11, 0x02, 0xFF, 0x0C, 0x00, 0xFF, 0xFF, 0xFF, 0xFF], bytes.AsSpan(10, 10).ToArray());
    }

    [Theory]
    [MemberData(nameof(Versions))]
    public void A_pen_hidden_twice_records_nothing_but_comments(QuickDrawVersion version)
    {
        var port = Port(version);
        var picture = PictureRecorder.OpenCPicture(port, R(0, 0, 100, 100));
        port.OpenRgn();
        port.FrameRect(R(10, 10, 20, 20));
        port.PicComment(200, [1, 2]);
        port.CloseRgn();
        var bytes = picture.ClosePicture();

        Assert.Equal([0x00, 0xA1, 0x00, 0xC8, 0x00, 0x02, 0x01, 0x02, 0x00, 0xFF], bytes.AsSpan(40).ToArray());
        Assert.Throws<InvalidOperationException>(picture.ClosePicture);
    }

    [Theory]
    [MemberData(nameof(Versions))]
    public void Text_and_line_opcodes_are_chosen_by_the_distance(QuickDrawVersion version)
    {
        var port = Port(version, 400);
        var picture = PictureRecorder.OpenCPicture(port, R(0, 0, 400, 400));
        port.MoveTo(300, 0);
        port.DrawString("A");                                                       // dh 300: LongText
        port.MoveTo(300, 20);
        port.DrawString("B");                                                       // dh 0, dv 20: DVText
        port.MoveTo(5, 5);
        port.LineTo(205, 5);                                                        // too far for a short line: Line
        port.LineTo(215, 10);                                                       // from the last end: ShortLineFrom
        port.LineTo(215, 300);                                                      // far, from the last end: LineFrom
        var bytes = picture.ClosePicture();

        var ops = Opcodes(bytes);
        Assert.Contains(0x0028, ops);
        Assert.Contains(0x002A, ops);
        Assert.Contains(0x0020, ops);
        Assert.Contains(0x0023, ops);
        Assert.Contains(0x0021, ops);
    }

    [Theory]
    [InlineData(QuickDrawVersion.MacOS9, 0)]                                        // transparent becomes srcCopy
    [InlineData(QuickDrawVersion.MacRom, 36)]                                       // recorded as given
    public void A_BitMaps_mode_is_recorded_as_each_QuickDraw_records_it(QuickDrawVersion version, int recorded)
    {
        var port = Port(version);
        var picture = PictureRecorder.OpenCPicture(port, R(0, 0, 100, 100));
        port.CopyBits(PixMap.FromBitMap(new byte[32], 2, R(0, 0, 16, 16)), R(0, 0, 16, 16), R(0, 0, 16, 16), TransferMode.Transparent);
        var bytes = picture.ClosePicture();

        int at = bytes.AsSpan().IndexOf((ReadOnlySpan<byte>)[0x00, 0x90]);
        Assert.Equal(recorded, bytes[at + 2 + 10 + 16] << 8 | bytes[at + 2 + 10 + 17]);
    }

    [Theory]
    [InlineData(QuickDrawVersion.MacOS9, 0, 0, 100, 50)]                            // 144 dpi: the frame at 72
    [InlineData(QuickDrawVersion.MacRom, 0, 0, 100, 50)]
    public void OpenCPicture_frames_the_rect_at_72_dpi(QuickDrawVersion version, int top, int left, int bottom, int right)
    {
        var port = Port(version);
        var bytes = PictureRecorder.OpenCPicture(port, R(10, 10, 210, 110), 144, 144).ClosePicture();

        Assert.Equal(R(top, left, bottom, right), new MacRect(
            (short)(bytes[2] << 8 | bytes[3]), (short)(bytes[4] << 8 | bytes[5]), (short)(bytes[6] << 8 | bytes[7]), (short)(bytes[8] << 8 | bytes[9])));
        var open = PictureRecorder.OpenPicture(port, R(0, 0, 1, 1));
        Assert.Throws<InvalidOperationException>(() => PictureRecorder.OpenPicture(port, R(0, 0, 1, 1)));       // no nesting
        open.ClosePicture();
    }

    // What was recorded draws what the port drew.
    [Theory]
    [MemberData(nameof(Versions))]
    public void A_recorded_picture_draws_what_the_port_drew(QuickDrawVersion version)
    {
        void Draw(QuickDrawPort p)
        {
            p.ForeColor = new RgbColor(0, 0, 0xFFFF);
            p.PaintRect(R(5, 5, 40, 60));
            p.ForeColor = RgbColor.Black;
            p.PenSize = new MacPoint(2, 3);
            p.FrameOval(R(10, 10, 50, 70));
            p.PenPattern = QuickDrawPattern.Gray;
            p.PaintRoundRect(R(45, 5, 75, 45), 12, 8);
            p.BackPattern = QuickDrawPattern.LightGray;
            p.EraseArc(R(40, 40, 90, 90), 45, 120);
            p.InvertPoly([new(60, 10), new(95, 30), new(80, 60)]);
            p.FillRgn(Region.FromRect(R(70, 70, 95, 95)), QuickDrawPattern.DarkGray);
            p.MoveTo(0, 99);
            p.LineTo(99, 0);
            var image = Enumerable.Range(0, 12 * 20).Select(i => (byte)(i * 37)).ToArray();
            p.CopyBits(PixMap.FromBitMap(image, 12, R(0, 0, 20, 96)), R(2, 3, 18, 90), R(80, 2, 96, 89), TransferMode.SrcOr);
        }

        var direct = Port(version);
        Draw(direct);

        var recording = Port(version);
        var picture = PictureRecorder.OpenCPicture(recording, R(0, 0, 100, 100));
        Draw(recording);
        var bytes = picture.ClosePicture();
        var played = PictReader.Decode(bytes, new PictDecodeOptions { QuickDraw = version });

        Assert.Equal(direct.Canvas.Pixels.ToArray(), played.Pixels.ToArray());
    }

    [Theory]
    [InlineData(QuickDrawVersion.MacOS9, 8)]
    [InlineData(QuickDrawVersion.MacRom, 8)]
    [InlineData(QuickDrawVersion.MacOS9, 4)]
    [InlineData(QuickDrawVersion.MacOS9, 16)]
    [InlineData(QuickDrawVersion.MacOS9, 32)]
    [InlineData(QuickDrawVersion.MacRom, 32)]
    public void Recorded_pixel_maps_draw_what_the_port_drew(QuickDrawVersion version, int depth)
    {
        const int Width = 40, Height = 20;
        int rowBytes = depth <= 8 ? (Width * depth + 15) / 16 * 2 : Width * depth / 8;
        var data = Enumerable.Range(0, rowBytes * Height).Select(i => (byte)((i / 7 * 29 + i % 3) & 0xFF)).ToArray();
        var colors = Enumerable.Range(0, 1 << Math.Min(depth, 8)).Select(i => new RgbColor((ushort)(i * 997), (ushort)(i * 3001), (ushort)(i * 211))).ToArray();
        var source = depth <= 8 ? PixMap.Indexed(data, rowBytes, R(0, 0, Height, Width), depth, colors) : PixMap.Direct(data, rowBytes, R(0, 0, Height, Width), depth);
        void Draw(QuickDrawPort p) => p.CopyBits(source, R(1, 3, 19, 37), R(10, 10, 46, 78), TransferMode.SrcCopy, Region.Oval(R(10, 10, 46, 78)));

        var direct = Port(version);
        Draw(direct);
        var recording = Port(version);
        var picture = PictureRecorder.OpenCPicture(recording, R(0, 0, 100, 100));
        Draw(recording);
        var played = PictReader.Decode(picture.ClosePicture(), new PictDecodeOptions { QuickDraw = version });

        Assert.Equal(direct.Canvas.Pixels.ToArray(), played.Pixels.ToArray());
    }

    // UpdatePat: an old pattern as its 8 bytes ($09/$02/$0A); a pixel or RGB pattern as a PixPat ($13/$12/$14).
    [Theory]
    [MemberData(nameof(Versions))]
    public void Patterns_are_recorded_old_or_as_pixel_patterns(QuickDrawVersion version)
    {
        var pixels = PixMap.Indexed(Enumerable.Range(0, 8 * 8).Select(i => (byte)(i % 3)).ToArray(), 8, R(0, 0, 8, 8), 8,
            [new RgbColor(0xFFFF, 0, 0), new RgbColor(0, 0xFFFF, 0), new RgbColor(0, 0, 0xFFFF)]);
        var pixelPattern = QuickDrawPattern.FromMono([0xAA, 0x55, 0xAA, 0x55, 0xAA, 0x55, 0xAA, 0x55]);
        pixelPattern.Pixels = pixels;
        var rgbPattern = QuickDrawPattern.FromMono([0xFF, 0, 0xFF, 0, 0xFF, 0, 0xFF, 0]);
        (rgbPattern.Rgb, rgbPattern.Rgb16) = (new RgbaColor(0x80, 0x40, 0x20), (0x8080, 0x4040, 0x2020));
        void Draw(QuickDrawPort p)
        {
            p.PenPattern = QuickDrawPattern.Gray;
            p.PaintRect(R(0, 0, 20, 20));
            p.PenPattern = pixelPattern;
            p.PaintRect(R(20, 20, 50, 50));
            p.FillOval(R(50, 0, 90, 40), rgbPattern);
            p.BackPattern = QuickDrawPattern.DarkGray;
            p.EraseRect(R(60, 60, 90, 90));
        }

        var direct = Port(version);
        Draw(direct);
        var recording = Port(version);
        var picture = PictureRecorder.OpenCPicture(recording, R(0, 0, 100, 100));
        Draw(recording);
        var bytes = picture.ClosePicture();

        Assert.True(bytes.AsSpan().IndexOf((ReadOnlySpan<byte>)[0x00, 0x09, 0xAA, 0x55]) > 0);                        // PnPat gray
        Assert.True(bytes.AsSpan().IndexOf((ReadOnlySpan<byte>)[0x00, 0x13, 0x00, 0x01]) > 0);                        // PnPixPat, type 1
        Assert.True(bytes.AsSpan().IndexOf((ReadOnlySpan<byte>)[0x00, 0x14, 0x00, 0x02]) > 0);                        // FillPixPat, type 2
        Assert.True(bytes.AsSpan().IndexOf((ReadOnlySpan<byte>)[0x00, 0x02, 0x77, 0xDD]) > 0);                        // BkPat dark gray
        var played = PictReader.Decode(bytes, new PictDecodeOptions { QuickDraw = version });
        Assert.Equal(direct.Canvas.Pixels.ToArray(), played.Pixels.ToArray());
    }

    private static List<int> Opcodes(byte[] picture)
    {
        // Walk only what the tests write: fixed-size state, lines and text, until OpEndPic.
        var ops = new List<int>();
        int at = 40;
        while (at + 1 < picture.Length)
        {
            at += at % 2;
            int op = picture[at] << 8 | picture[at + 1];
            ops.Add(op);
            at += 2;
            at += op switch
            {
                0x001E or 0x00FF => 0,
                0x0001 => picture[at] << 8 | picture[at + 1],
                0x001A or 0x001B => 6,
                0x0028 => 4 + 1 + picture[at + 4],
                0x0029 or 0x002A => 1 + 1 + picture[at + 1],
                0x002B => 2 + 1 + picture[at + 2],
                0x002E => 6,
                0x0020 => 8,
                0x0021 => 4,
                0x0022 => 6,
                0x0023 => 2,
                _ => throw new InvalidOperationException($"opcode {op:X4}"),
            };
            if (op == 0x00FF)
            {
                break;
            }
        }

        return ops;
    }
}
