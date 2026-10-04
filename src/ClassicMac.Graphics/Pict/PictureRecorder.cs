using System;
using System.Collections.Generic;
using System.Linq;
using ClassicMac.Core;
using ClassicMac.Graphics.QuickDraw;

namespace ClassicMac.Graphics.Pict;

/// <summary>
/// A picture recorded from a port's drawing (<c>OpenPicture</c>, <c>OpenCPicture</c> … <c>ClosePicture</c>; pict.md §3.3):
/// the calls made on the <see cref="QuickDrawPort"/> while it is open become version 2 opcodes, as the chosen QuickDraw
/// records them. The pen is hidden while it is open, so nothing is drawn unless <see cref="QuickDrawPort.ShowPen"/> is
/// called; a pen hidden once more (an open region or polygon) records nothing but comments.
/// </summary>
public sealed class PictureRecorder : IPictureRecording
{
    // StdBits' transfer-mode table for a BitMap in a picture: the arithmetic modes as Boolean ones.
    private static readonly int[] ArithmeticToBoolean = [0, 3, 2, 1, 1, 3, 2, 1];

    private readonly QuickDrawPort port;
    private readonly bool macOS9;
    private readonly BigEndianWriter w = new();
    private readonly HashSet<int> fonts = [0];

    // picSave: the state the picture last recorded.
    private RgbColor op = RgbColor.Black, hilite = RgbColor.Black, fore = RgbColor.Black, back = RgbColor.White;
    private (int h, int v) origin;
    private Region clip = Region.Empty;
    private (int h, int v) pnLoc, txLoc, pnSize = (1, 1), ovSize, numer = (1, 1), denom = (1, 1);
    private int pnMode = TransferModes.PatCopy, txFont, txFace, txMode = TransferModes.SrcOr, txSize, spExtra, chExtra;
    private uint glyphState = 0x80808080;
    private QuickDrawPattern pnPat = QuickDrawPattern.Black, bkPat = QuickDrawPattern.White, fillPat = QuickDrawPattern.Black;
    private MacRect theRect;

    private PictureRecorder(QuickDrawPort port, MacRect frame)
    {
        this.port = port;
        macOS9 = port.Options.Version == QuickDrawVersion.MacOS9;
        origin = (port.OriginH, port.OriginV);
        w.WriteUInt16(0);                                                           // picSize, set by Close
        frame.Write(w);
        w.WriteUInt16(0x0011);                                                      // VersionOp
        w.WriteUInt16(0x02FF);
    }

    /// <summary>
    /// Opens a picture of <paramref name="frame"/> on <paramref name="port"/> (<c>OpenPicture</c>): in a colour port, a
    /// version 2 picture with the version −1 header.
    /// </summary>
    /// <exception cref="InvalidOperationException">A picture is already open on the port.</exception>
    public static PictureRecorder OpenPicture(QuickDrawPort port, MacRect frame)
    {
        var recorder = Start(port, frame);
        recorder.Op(0x0C00);                                                        // HeaderOp
        recorder.w.WriteInt32(-1);                                                  // version −1, reserved
        foreach (var value in new[] { frame.Left, frame.Top, frame.Right, frame.Bottom })
        {
            recorder.w.WriteInt32(value << 16);                                     // the frame as Fixed: l, t, r, b
        }

        recorder.w.WriteUInt32(0);
        return recorder.Begin();
    }

    /// <summary>
    /// Opens a picture of <paramref name="sourceRect"/> at <paramref name="horizontalResolution"/> ×
    /// <paramref name="verticalResolution"/> dpi on <paramref name="port"/> (<c>OpenCPicture</c>): the extended version 2
    /// header; the frame is the rect at 72 dpi from (0, 0).
    /// </summary>
    /// <exception cref="InvalidOperationException">A picture is already open on the port.</exception>
    public static PictureRecorder OpenCPicture(QuickDrawPort port, MacRect sourceRect, double horizontalResolution = 72, double verticalResolution = 72)
    {
        ArgumentNullException.ThrowIfNull(port);
        uint hRes = (uint)Math.Round(horizontalResolution * 65536), vRes = (uint)Math.Round(verticalResolution * 65536);
        // The ROM scales only when both resolutions are set, else keeps srcRect; Mac OS 9 always scales (by zero not
        // traced: ClassicMac keeps srcRect then). The scale truncates.
        var frame = hRes != 0 && vRes != 0
            ? new MacRect(0, 0, (short)((long)(sourceRect.Bottom - sourceRect.Top) * 72 * 65536 / vRes),
                (short)((long)(sourceRect.Right - sourceRect.Left) * 72 * 65536 / hRes))
            : sourceRect;
        var recorder = Start(port, frame);
        recorder.Op(0x0C00);
        recorder.w.WriteInt16(-2);                                                  // version −2
        recorder.w.WriteUInt16(0);
        recorder.w.WriteUInt32(hRes);
        recorder.w.WriteUInt32(vRes);
        sourceRect.Write(recorder.w);
        recorder.w.WriteUInt32(0);
        return recorder.Begin();
    }

    private static PictureRecorder Start(QuickDrawPort port, MacRect frame)
    {
        ArgumentNullException.ThrowIfNull(port);
        if (port.Picture != null)
        {
            throw new InvalidOperationException("A picture is already open on this port.");
        }

        return new PictureRecorder(port, frame);
    }

    private PictureRecorder Begin()
    {
        port.Picture = this;
        port.HidePen();
        return this;
    }

    /// <summary>
    /// Ends the recording (<c>ClosePicture</c>): writes OpEndPic and picSize (the low word of the length), shows the pen
    /// and returns the picture's bytes.
    /// </summary>
    public byte[] ClosePicture()
    {
        if (port.Picture != this)
        {
            throw new InvalidOperationException("The picture is not open.");
        }

        Op(0x00FF);
        w.WriteUInt16At(0, (ushort)w.WrittenSpan.Length);
        port.Picture = null;
        port.ShowPen();
        return w.ToArray();
    }

    // PutPicOp: every opcode starts on an even offset, a zero byte padding it.
    private void Op(int opcode)
    {
        if (w.WrittenSpan.Length % 2 == 1)
        {
            w.WriteByte(0);
        }

        w.WriteUInt16(opcode);
    }

    private static void Rgb(BigEndianWriter writer, RgbColor color)
    {
        writer.WriteUInt16(color.Red);
        writer.WriteUInt16(color.Green);
        writer.WriteUInt16(color.Blue);
    }

    private void Point(int h, int v)
    {
        w.WriteInt16(v);
        w.WriteInt16(h);
    }

    // CheckPic: nothing when the pen is hidden twice; else the port's colours, origin and clip where they changed.
    private bool CheckPic()
    {
        if (port.PenVisibility < -1)
        {
            return false;
        }

        if (port.OpColor != op)
        {
            Op(0x001F);
            Rgb(w, op = port.OpColor);
        }

        if (port.HiliteRgb != hilite)
        {
            hilite = port.HiliteRgb;
            if (hilite == port.SystemHilite)
            {
                Op(0x001E);                                                         // DefHilite
            }
            else
            {
                Op(0x001D);
                Rgb(w, hilite);
            }
        }

        if (port.HilitePending)
        {
            Op(0x001C);
        }

        if (port.ForeColor != fore)
        {
            Op(0x001A);
            Rgb(w, fore = port.ForeColor);
        }

        if (port.BackColor != back)
        {
            Op(0x001B);
            Rgb(w, back = port.BackColor);
        }

        if ((port.OriginH, port.OriginV) != origin)
        {
            Op(0x000C);
            w.WriteInt16(port.OriginH - origin.h);
            w.WriteInt16(port.OriginV - origin.v);
            origin = (port.OriginH, port.OriginV);
        }

        var portClip = port.Clip ?? Region.FromRect(new MacRect(-32767, -32767, 32767, 32767));
        if (clip.IsEmpty != portClip.IsEmpty || !clip.Xor(portClip).IsEmpty || clip.BoundingBox != portClip.BoundingBox)
        {
            Op(0x0001);
            w.WriteBytes(portClip.ToRgnData());
            clip = portClip;
        }

        return true;
    }

    // PutPicVerb: the pen for frames and paints, the background for erases, the fill pattern for fills.
    private void Verb(int verb)
    {
        if (verb == 0 && (port.PenSize.H, port.PenSize.V) != pnSize)
        {
            Op(0x0007);
            pnSize = (port.PenSize.H, port.PenSize.V);
            Point(pnSize.h, pnSize.v);
        }

        if (verb is 0 or 1)
        {
            if ((int)port.PenMode != pnMode)
            {
                Op(0x0008);
                w.WriteUInt16(pnMode = (int)port.PenMode);
            }

            pnPat = Pattern(port.PenPattern, pnPat, 0x0009, 0x0013);
        }
        else if (verb == 2)
        {
            bkPat = Pattern(port.BackPattern, bkPat, 0x0002, 0x0012);
        }
        else if (verb == 4)
        {
            fillPat = Pattern(port.FillPattern, fillPat, 0x000A, 0x0014);
        }
    }

    // UpdatePat: an old (1-bit) pattern as its 8 bytes; a pixel or RGB pattern as a PixPat.
    private QuickDrawPattern Pattern(QuickDrawPattern current, QuickDrawPattern saved, int oldOp, int pixPatOp)
    {
        if (ReferenceEquals(current, saved) || SamePattern(current, saved))
        {
            return saved;
        }

        if (current.Pixels is null && current.Rgb is null)
        {
            Op(oldOp);
            w.WriteBytes(current.Bits);
            return current;
        }

        Op(pixPatOp);
        if (current.Rgb is not null)
        {
            w.WriteUInt16(2);                                                       // ditherPat: an RGB colour
            w.WriteBytes(current.Bits);
            w.WriteUInt16(current.Rgb16.r);
            w.WriteUInt16(current.Rgb16.g);
            w.WriteUInt16(current.Rgb16.b);
        }
        else
        {
            w.WriteUInt16(1);                                                       // a full PixPat
            w.WriteBytes(current.Bits);
            PixMapRecord(current.Pixels!, current.Pixels!.Bounds, current.Pixels.RowBytes, current.Pixels.RowBytes < 8 ? 1 : 0);
            ColorTable(current.Pixels);
            Rows(current.Pixels, current.Pixels.Bounds, current.Pixels.RowBytes);
        }

        return current;
    }

    private static bool SamePattern(QuickDrawPattern a, QuickDrawPattern b) =>
        a.Pixels is null && b.Pixels is null && a.Rgb == b.Rgb && a.Rgb16 == b.Rgb16 && a.Bits.SequenceEqual(b.Bits);

    void IPictureRecording.Line(int h1, int v1, int h2, int v2)
    {
        if (!CheckPic())
        {
            return;
        }

        Verb(0);
        int dh = h2 - h1, dv = v2 - v1;
        bool from = pnLoc == (h1, v1);
        if (dh is >= -128 and <= 127 && dv is >= -128 and <= 127)
        {
            Op(from ? 0x0023 : 0x0022);
            if (!from)
            {
                Point(h1, v1);
            }

            w.WriteByte((byte)(sbyte)dh);
            w.WriteByte((byte)(sbyte)dv);
        }
        else
        {
            Op(from ? 0x0021 : 0x0020);
            if (!from)
            {
                Point(h1, v1);
            }

            Point(h2, v2);
        }

        pnLoc = (h2, v2);
    }

    void IPictureRecording.Shape(PictureNoun noun, int verb, MacRect rect, int ovalWidth, int ovalHeight, int startAngle, int arcAngle)
    {
        if (!CheckPic())
        {
            return;
        }

        Verb(verb);
        if (noun == PictureNoun.RoundRect && (ovalWidth, ovalHeight) != ovSize)
        {
            Op(0x000B);
            ovSize = (ovalWidth, ovalHeight);
            Point(ovalWidth, ovalHeight);
        }

        // picTheRect, shared by every noun: the same rect again is the opcode + 8 with no rect.
        if (rect == theRect)
        {
            Op((int)noun + 8 + verb);
        }
        else
        {
            Op((int)noun + verb);
            rect.Write(w);
            theRect = rect;
        }

        if (noun == PictureNoun.Arc)
        {
            w.WriteInt16(startAngle);
            w.WriteInt16(arcAngle);
        }
    }

    void IPictureRecording.Polygon(int verb, IReadOnlyList<MacPoint> points)
    {
        if (!CheckPic())
        {
            return;
        }

        Verb(verb);
        Op(0x0070 + verb);
        w.WriteUInt16(10 + 4 * points.Count);
        var box = points.Count == 0 ? default
            : new MacRect(points.Min(p => p.V), points.Min(p => p.H), points.Max(p => p.V), points.Max(p => p.H));
        box.Write(w);
        foreach (var p in points)
        {
            Point(p.H, p.V);
        }
    }

    void IPictureRecording.Region(int verb, Region region)
    {
        if (!CheckPic())
        {
            return;
        }

        Verb(verb);
        Op(0x0080 + verb);
        w.WriteBytes(region.ToRgnData());
    }

    void IPictureRecording.Text(ReadOnlySpan<byte> text)
    {
        if (!CheckPic())
        {
            return;
        }

        TextState();
        int h = port.PenLocation.H, v = port.PenLocation.V, dh = h - txLoc.h, dv = v - txLoc.v;
        if (dh is >= 0 and <= 255 && dv is >= 0 and <= 255)
        {
            if (dv == 0)
            {
                Op(0x0029);
                w.WriteByte((byte)dh);
            }
            else if (dh == 0)
            {
                Op(0x002A);
                w.WriteByte((byte)dv);
            }
            else
            {
                Op(0x002B);
                w.WriteByte((byte)dh);
                w.WriteByte((byte)dv);
            }
        }
        else
        {
            Op(0x0028);
            Point(h, v);
        }

        w.WriteByte((byte)text.Length);
        w.WriteBytes(text);
        txLoc = (h, v);
    }

    // The text state, in StdText's order.
    private void TextState()
    {
        if (port.FontId != txFont)
        {
            txFont = port.FontId;
            if (fonts.Add(txFont) && port.Options.Fonts?.FamilyName(txFont) is { Length: > 0 } name)
            {
                var bytes = MacRoman.TryEncode(name, out var encoded) ? encoded : [];
                Op(0x002C);                                                         // FontName
                w.WriteUInt16(bytes.Length + 3);
                w.WriteUInt16(txFont);
                w.WriteByte((byte)bytes.Length);
                w.WriteBytes(bytes);
            }

            Op(0x0003);
            w.WriteUInt16(txFont);
        }

        if (port.Face != txFace)
        {
            Op(0x0004);
            w.WriteByte((byte)(txFace = port.Face));
        }

        if (port.TxMode != txMode)
        {
            Op(0x0005);
            w.WriteUInt16(txMode = port.TxMode);
        }

        if (port.Size != txSize)
        {
            Op(0x000D);
            w.WriteUInt16(txSize = port.Size);
        }

        if (port.SpaceExtraFixed != spExtra)
        {
            Op(0x0006);
            w.WriteInt32(spExtra = port.SpaceExtraFixed);
        }

        // The glyph state, high byte first: outline preferred, preserve glyph, FractEnable, FScaleDisable.
        uint glyph = (port.FractEnable ? 0xFFu << 8 : 0) | (port.FScaleDisable ? 0xFFu : 0);
        if (glyph != glyphState)
        {
            Op(0x002E);
            w.WriteUInt16(4);
            w.WriteUInt32(glyphState = glyph);
        }

        if (port.TextNumer != numer || port.TextDenom != denom)
        {
            Op(0x0010);
            (numer, denom) = (port.TextNumer, port.TextDenom);
            Point(numer.h, numer.v);
            Point(denom.h, denom.v);
        }

        if ((port.PenFrac & 0xFFFF) != 0x8000)
        {
            Op(0x0015);
            w.WriteUInt16(port.PenFrac & 0xFFFF);
        }

        if (port.ChExtra != chExtra)
        {
            Op(0x0016);
            w.WriteInt16(chExtra = port.ChExtra);
        }
    }

    void IPictureRecording.Bits(PixMap source, MacRect sourceRect, MacRect destinationRect, int mode, Region? mask)
    {
        if (!CheckPic())
        {
            return;
        }

        // The source trimmed to the rows of srcRect and to whole bytes around its columns; rowBytes even.
        var bounds = source.Bounds;
        int pixelsPerByte = Math.Max(1, 8 / source.PixelSize);
        int top = Math.Max(bounds.Top, (int)sourceRect.Top), bottom = Math.Min(bounds.Bottom, (int)sourceRect.Bottom);
        int left = bounds.Left + (Math.Max(0, sourceRect.Left - bounds.Left) / pixelsPerByte * pixelsPerByte);
        int right = Math.Min(bounds.Right, bounds.Left + ((Math.Max(0, sourceRect.Right - bounds.Left) + pixelsPerByte - 1) / pixelsPerByte * pixelsPerByte));
        if (right <= left || bottom <= top)
        {
            if (!macOS9)
            {
                return;                                                             // the ROM records nothing
            }

            right = Math.Max(right, left);
            bottom = Math.Max(bottom, top);
        }

        var trimmed = new PictRect(top, left, bottom, right);
        int rowBytes = ((right - left) * source.PixelSize + 15) / 16 * 2;
        bool bitMap = !source.IsPixMap;
        bool direct = !bitMap && source.IsDirect;
        int packed = rowBytes >= 8 ? 8 : 0;
        if (direct)
        {
            Op(0x009A + (mask is null ? 0 : 1));
            w.WriteUInt32(0x000000FF);
        }
        else
        {
            Op(0x0090 + packed + (mask is null ? 0 : 1));
        }

        if (bitMap)
        {
            w.WriteUInt16(rowBytes);
            trimmed.ToMacRect().Write(w);
        }
        else
        {
            int packType = rowBytes < 8 ? 1 : !direct ? 0 : source.PixelSize == 16 ? 3 : 4;
            PixMapRecord(source, trimmed, rowBytes, packType);
            if (!direct)
            {
                ColorTable(source);
            }
        }

        sourceRect.Write(w);
        destinationRect.Write(w);
        // Mac OS 9 records a BitMap's mode as Boolean and without ditherCopy; the ROM records it as given.
        if (bitMap && macOS9)
        {
            mode &= ~TransferModes.DitherCopy;
            if (mode is >= 32 and <= 39)
            {
                mode = ArithmeticToBoolean[(mode - 32) & 3];
            }
        }

        w.WriteUInt16(mode);
        if (mask is not null)
        {
            w.WriteBytes(mask.ToRgnData());
        }

        Rows(source, trimmed, rowBytes, direct && rowBytes >= 8 ? (source.PixelSize == 16 ? 3 : 4) : 0);
    }

    // A PixMap record without its baseAddr: rowBytes with the PixMap flag, bounds, version, packing, resolution, pixel
    // type and size, components, planeBytes, pmTable and pmReserved.
    private void PixMapRecord(PixMap pixMap, PictRect bounds, int rowBytes, int packType)
    {
        w.WriteUInt16(rowBytes | 0x8000);
        bounds.ToMacRect().Write(w);
        w.WriteUInt16(0);                                                           // pmVersion
        w.WriteUInt16(packType);
        w.WriteUInt32(0);                                                           // packSize
        w.WriteUInt32(72 << 16);
        w.WriteUInt32(72 << 16);
        bool direct = pixMap.IsDirect;
        w.WriteUInt16(direct ? 16 : 0);
        w.WriteUInt16(pixMap.PixelSize);
        w.WriteUInt16(direct ? 3 : 1);
        w.WriteUInt16(direct ? (pixMap.PixelSize == 16 ? 5 : 8) : pixMap.PixelSize);
        w.WriteUInt32(0);                                                           // planeBytes
        w.WriteUInt32(0);                                                           // pmTable
        w.WriteUInt32(0);                                                           // pmReserved
    }

    // The colour table; none gives the minimal 16-byte table.
    private void ColorTable(PixMap pixMap)
    {
        w.WriteUInt32(0);                                                           // ctSeed
        w.WriteUInt16(0);                                                           // ctFlags
        if (pixMap.Palette16.Length == 0)
        {
            w.WriteBytes([0x00, 0x00, 0x4B, 0x4F, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00]);
            return;
        }

        w.WriteUInt16(pixMap.Palette16.Length - 1);
        for (int i = 0; i < pixMap.Palette16.Length; i++)
        {
            w.WriteUInt16(i);
            w.WriteUInt16(pixMap.Palette16[i].r);
            w.WriteUInt16(pixMap.Palette16[i].g);
            w.WriteUInt16(pixMap.Palette16[i].b);
        }
    }

    // The rows of the trimmed area: under 8 bytes raw; else each packed (PackBits; 16-bit pixels by words; 32-bit as
    // its red, green and blue planes), its length a byte when rowBytes ≤ 250, else a word.
    private void Rows(PixMap pixMap, PictRect area, int rowBytes, int packType = 0)
    {
        for (int y = area.Top; y < area.Bottom; y++)
        {
            var row = Row(pixMap, area, y, rowBytes);
            if (rowBytes < 8)
            {
                w.WriteBytes(row);
                continue;
            }

            var packed = packType switch
            {
                3 => PackBits.Pack(row, 2),
                4 => PackBits.Pack(Planes(row), 1),
                _ => PackBits.Pack(row, 1),
            };
            if (rowBytes > 250)
            {
                w.WriteUInt16(packed.Length);
            }
            else
            {
                w.WriteByte((byte)packed.Length);
            }

            w.WriteBytes(packed);
        }
    }

    // One row of the trimmed area, rowBytes long, its pixels shifted to start at the area's left.
    private static byte[] Row(PixMap pixMap, PictRect area, int y, int rowBytes)
    {
        var row = new byte[rowBytes];
        int bits = pixMap.PixelSize, start = (area.Left - pixMap.Bounds.Left) * bits, sourceRow = (y - pixMap.Bounds.Top) * pixMap.RowBytes;
        for (int bit = 0; bit < (area.Right - area.Left) * bits; bit++)
        {
            int from = start + bit;
            if (sourceRow + from / 8 < pixMap.Data.Length && (pixMap.Data[sourceRow + from / 8] & (0x80 >> (from % 8))) != 0)
            {
                row[bit / 8] |= (byte)(0x80 >> (bit % 8));
            }
        }

        return row;
    }

    // A 32-bit row (x, R, G, B per pixel) as its red, green and blue planes.
    private static byte[] Planes(byte[] row)
    {
        int pixels = row.Length / 4;
        var planes = new byte[pixels * 3];
        for (int i = 0; i < pixels; i++)
        {
            planes[i] = row[4 * i + 1];
            planes[pixels + i] = row[4 * i + 2];
            planes[2 * pixels + i] = row[4 * i + 3];
        }

        return planes;
    }

    void IPictureRecording.Comment(int kind, ReadOnlySpan<byte> data)
    {
        if (data.Length == 0)
        {
            Op(0x00A0);
            w.WriteUInt16(kind);
            return;
        }

        Op(0x00A1);
        w.WriteUInt16(kind);
        w.WriteUInt16(data.Length);
        w.WriteBytes(data);
    }
}
