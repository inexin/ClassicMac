using System;
using System.IO;
using System.Text;
using System.Threading;
using ClassicMac.Core;
using ClassicMac.Graphics;
using ClassicMac.Graphics.QuickDraw;
using ClassicMac.Graphics.QuickTime;

namespace ClassicMac.Graphics.Pict;

/// <summary>
/// Decodes a QuickDraw PICT (v1 / v2 / extended v2) to a <see cref="RgbaBitmap"/>, drawing it the way QuickDraw's
/// DrawPicture does: shapes are rasterized as QuickDraw regions and transferred through the port's patterns and
/// transfer modes, bitmaps are decoded from every PixMap layout, and text is rasterized by an optional
/// <see cref="ITextFallback"/>. Every opcode in Inside Macintosh: Imaging With QuickDraw, Appendix A,
/// Table A-2 is parsed or skipped by its specified operand size; only malformed data and unsupported pixel depths
/// throw.
/// </summary>
public static class PictReader
{
    private static readonly Encoding MacRoman = GetMacRoman();
    private static Encoding GetMacRoman()
    {
        try
        { return CodePagesEncodingProvider.Instance.GetEncoding(10000) ?? Encoding.Latin1; }
        catch { return Encoding.Latin1; }
    }
    internal static string MacRomanString(ReadOnlySpan<byte> bytes) => MacRoman.GetString(bytes);

    /// <summary>
    /// Decodes the picture read from the current position of <paramref name="stream"/> to its end; the stream is
    /// left open.
    /// </summary>
    /// <inheritdoc cref="Decode(ReadOnlyMemory{byte}, PictDecodeOptions, CancellationToken)"/>
    public static RgbaBitmap Decode(Stream stream, PictDecodeOptions? options = null,
        CancellationToken cancellationToken = default) => Read(stream, options, cancellationToken).Bitmap;

    /// <summary>
    /// Decodes the picture read from the current position of <paramref name="stream"/> to its end, with its header
    /// and metadata; the stream is left open.
    /// </summary>
    /// <inheritdoc cref="Decode(ReadOnlyMemory{byte}, PictDecodeOptions, CancellationToken)"/>
    public static PictPicture Read(Stream stream, PictDecodeOptions? options = null,
        CancellationToken cancellationToken = default) => Read(new BigEndianReader(stream), options, cancellationToken);

    /// <summary>
    /// Decodes a picture, either bare (as stored in a <c>PICT</c> resource) or as a <c>.pict</c> file with its
    /// 512-byte application header. The canvas covers <see cref="PictInfo.Bounds"/>, or <see cref="PictInfo.PictureFrame"/>
    /// with <see cref="PictResolution.PictureFrame"/>; pixels the picture never
    /// draws stay transparent.
    /// </summary>
    /// <param name="data">The picture bytes.</param>
    /// <param name="options">Text rasterizer and highlight color; null for defaults.</param>
    /// <param name="cancellationToken">Cancels decoding between opcodes.</param>
    /// <exception cref="NotSupportedException">The picture uses an unsupported pixel format.</exception>
    /// <exception cref="EndOfStreamException">The picture data is truncated.</exception>
    public static RgbaBitmap Decode(ReadOnlyMemory<byte> data, PictDecodeOptions? options = null,
        CancellationToken cancellationToken = default) => Read(data, options, cancellationToken).Bitmap;

    /// <summary>Decodes a picture as <see cref="Decode(ReadOnlyMemory{byte}, PictDecodeOptions, CancellationToken)"/> does, with its header and metadata.</summary>
    /// <inheritdoc cref="Decode(ReadOnlyMemory{byte}, PictDecodeOptions, CancellationToken)"/>
    public static PictPicture Read(ReadOnlyMemory<byte> data, PictDecodeOptions? options = null,
        CancellationToken cancellationToken = default) => Read(new BigEndianReader(data), options, cancellationToken);

    private static PictPicture Read(BigEndianReader b, PictDecodeOptions? options, CancellationToken cancellationToken)
    {
        options ??= PictDecodeOptions.Default;
        var info = PictHeader.Parse(b, out bool v1);
        var bounds = info.BoundsRect;
        var canvasRect = options.Resolution == PictResolution.PictureFrame ? info.FrameRect : bounds;
        var canvas = new RgbaBitmap(Math.Max(1, canvasRect.Width), Math.Max(1, canvasRect.Height));
        var port = new GrafPort(canvas, info.FrameRect, bounds, options);
        Play(b, info, v1, port, options, cancellationToken);
        return new PictPicture(canvas, info);
    }

    // Plays the opcodes after the header into the play state, to the end opcode or the end of the data.
    internal static void Play(ClassicMac.Core.BigEndianReader b, PictInfo info, bool v1, GrafPort port, PictDecodeOptions options,
        CancellationToken cancellationToken)
    {
        bool macOS9 = options.QuickDraw == QuickDrawVersion.MacOS9;
        {
            PictRect? quickTimeRect = null;           // destination of a QuickTime image drawn by the last opcode
            while (b.Position < b.Length)
            {
                var justDrawnQuickTime = quickTimeRect;
                quickTimeRect = null;
                cancellationToken.ThrowIfCancellationRequested();
                if (!v1 && (b.Position & 1) == 1) // v2 opcodes are word-aligned
                {
                    b.Skip(1);
                }

                if (b.Position >= b.Length)
                {
                    break;
                }

                int op = v1 ? b.ReadByte() : b.ReadUInt16();
                port.Version1 = v1;
                if (macOS9 && (op == 0x0092 || op == 0x0093))
                {
                    op = 0x0094;   // Mac OS 9: reserved
                }

                switch (op)
                {
                    case 0x0011:                        // VersionOp mid-stream: 1 = byte opcodes, 2 = word opcodes
                        v1 = b.ReadByte() == 1;          // (v2's trailing 0xFF is eaten by the word alignment)
                        break;
                    case 0x0090:                        // BitsRect (read like PackBitsRect: the ROM ignores bit 3)
                    case 0x0091:                        // BitsRgn
                    case 0x0098:                        // PackBitsRect
                    case 0x0099:                        // PackBitsRgn
                    case 0x0092:                        // the ROM treats 0x92/0x93 as 0x9A/0x9B
                    case 0x0093:
                    case 0x009A:                        // DirectBitsRect
                    case 0x009B:                        // DirectBitsRgn
                        {
                            var (pm, src, dst, mode, mask) = ReadBits(b, op, macOS9);
                            if (dst != justDrawnQuickTime)
                            {
                                port.CopyBits(pm, src, dst, mode, mask);
                            }

                            break;
                        }
                    case 0x00A0:                        // ShortComment
                        info.AddComment(b.ReadUInt16(), Array.Empty<byte>());
                        break;
                    case 0x00A1:                        // LongComment
                        {
                            int kind = b.ReadUInt16();
                            int size = b.ReadUInt16();
                            info.AddComment(kind, b.ReadBytes(size).ToArray());
                            break;
                        }
                    case 0x00FF:                        // end of picture
                        return;
                    case 0x8200:                        // CompressedQuickTime
                        {
                            // A decoded image skips the picture's fallback for systems without QuickTime: the drawing
                            // after a PnSize marker (SkipQuickTimeFallback), or else a bitmap that immediately follows
                            // into the same rectangle (Photoshop's "QuickTime PICT" placeholder).
                            var block = ReadLengthPrefixedBytes(b);
                            var drawn = port.QuickTime(block);
                            if (drawn != null)
                            {
                                quickTimeRect = drawn;
                                SkipQuickTimeFallback(b);
                            }
                            break;
                        }
                    case 0x8201:                        // UncompressedQuickTime
                        {
                            var drawn = UncompressedQuickTime(port, ReadLengthPrefixedBytes(b), macOS9);
                            if (drawn != null)
                            {
                                quickTimeRect = drawn;
                                SkipQuickTimeFallback(b);
                            }
                            break;
                        }
                    default:
                        if (!HandleDrawingOpcode(port, b, op, macOS9))
                        {
                            SkipOperands(b, op);
                        }

                        break;
                }
            }
        }
    }

    // QuickTime writes pictures whose compressed image is followed by drawing for systems without QuickTime
    // ("QuickTime and a ... decompressor are needed"), introduced by a PnSize opcode with v = 0x00AE whose h is the
    // byte count QuickTime skips once it has drawn the image.
    private static void SkipQuickTimeFallback(ClassicMac.Core.BigEndianReader b)
    {
        int start = b.Position + (b.Position & 1);
        if (start + 6 > b.Length)
        {
            return;
        }

        b.Position = start;
        if (b.ReadUInt16() == 0x0007 && b.ReadUInt16() == 0x00AE)
        {
            int skip = b.ReadUInt16();
            b.Position = Math.Min(b.Length, b.Position + skip);
            return;
        }
        b.Position = start;
    }

    // A bitmap opcode's operands (0x90-0x93, 0x98-0x9B): the BitMap/PixMap, srcRect, dstRect, mode, the mask
    // region of the Rgn variants (odd opcodes), and the pixel data.
    private static (PixMap pm, PictRect src, PictRect dst, int mode, Region? mask) ReadBits(ClassicMac.Core.BigEndianReader b, int op,
        bool macOS9)
    {
        bool direct = (op & 0x0A) == 0x0A || op == 0x0092 || op == 0x0093;
        var pm = direct ? PixMap.ReadDirectHeader(b, macOS9) : PixMap.ReadIndexedHeader(b, macOS9);
        var (src, dst, mode, mask) = ReadCopyBitsTail(b, hasRegion: (op & 1) != 0);
        pm.ReadPixData(b);
        return (pm, src, dst, mode, mask);
    }

    // UncompressedQuickTime (0x8201): version, the 3x3 matrix, matte size and rect, the matte (skipped, then
    // word-aligned), then a bitmap opcode with its operands, drawn as QuickTime draws it. Returns where it drew
    // (picture space), or null when the block holds no bitmap opcode.
    private static PictRect? UncompressedQuickTime(GrafPort port, byte[] block, bool macOS9)
    {
        var b = new ClassicMac.Core.BigEndianReader(block);
        try
        {
            b.ReadUInt16();                                        // version
            var matrix = new int[9];
            for (int i = 0; i < 9; i++)
            {
                matrix[i] = b.ReadInt32();
            }

            long matteSize = b.ReadUInt32();
            PictRect.Read(b);                     // matte rect
            if (matteSize > block.Length - b.Position)
            {
                return null;
            }

            b.Position = (int)((b.Position + matteSize + 1) & ~1L);
            int op = b.ReadUInt16();
            if (op < 0x0090 || op > 0x009B || (op > 0x0093 && op < 0x0098))
            {
                return null;
            }

            if (macOS9 && (op == 0x0092 || op == 0x0093))
            {
                return null;
            }

            var (pm, src, dst, mode, mask) = ReadBits(b, op, macOS9);
            return port.UncompressedQuickTime(pm, src, dst, mode, mask, matrix);
        }
        catch (EndOfStreamException)
        {
            return null;
        }
    }

    // srcRect, dstRect, mode and (Rgn variants) maskRgn, which sit between a CopyBits PixMap and its PixData.
    private static (PictRect src, PictRect dst, int mode, Region? mask) ReadCopyBitsTail(ClassicMac.Core.BigEndianReader b, bool hasRegion)
    {
        var src = PictRect.Read(b);
        var dst = PictRect.Read(b);
        int mode = b.ReadUInt16();
        var mask = hasRegion ? Region.Read(b) : null;
        return (src, dst, mode, mask);
    }

    // Applies the drawing and graphics-state opcodes to the port. Returns false if the opcode isn't one we
    // interpret (the caller then consumes its operands via SkipOperands). Shape blocks: rect 0x30, round rect
    // 0x40, oval 0x50, arc 0x60, poly 0x70, region 0x80; + verb, "same" variants at base + 8.
    private static bool HandleDrawingOpcode(GrafPort port, ClassicMac.Core.BigEndianReader b, int op, bool macOS9)
    {
        switch (op)
        {
            case 0x0001:
                port.SetClip(Region.Read(b));
                return true;                  // ClipRgn
            case 0x0002:
                port.BkPat = QuickDrawPattern.FromMono(b.ReadBytes(8).ToArray());
                return true;    // BkPat
            case 0x0009:
                port.PnPat = QuickDrawPattern.FromMono(b.ReadBytes(8).ToArray());
                return true;    // PnPat
            case 0x000A:
                port.FillPat = QuickDrawPattern.FromMono(b.ReadBytes(8).ToArray());
                return true;  // FillPat
            case 0x0012:
                port.BkPat = QuickDrawPattern.Read(b, macOS9);
                return true;          // BkPixPat
            case 0x0013:
                port.PnPat = QuickDrawPattern.Read(b, macOS9);
                return true;          // PnPixPat
            case 0x0014:
                port.FillPat = QuickDrawPattern.Read(b, macOS9);
                return true;        // FillPixPat
            case 0x0003:
                port.TextFont(b.ReadUInt16());
                return true;                  // TxFont
            case 0x0004:
                port.TextFace = b.ReadByte();
                return true;                  // TxFace
            case 0x0005:
                port.TextMode = b.ReadUInt16();
                return true;                 // TxMode
            case 0x0007:
                { var p = ReadPoint(b); port.PenSize(p.h, p.v); return true; }              // PnSize
            case 0x0008:
                port.PenMode = b.ReadUInt16();
                return true;                  // PnMode
            case 0x000B:
                { var p = ReadPoint(b); port.OvalSize(p.h, p.v); return true; }             // OvSize
            case 0x000C:
                { int dh = b.ReadInt16(), dv = b.ReadInt16(); port.Origin(dh, dv); return true; } // Origin: dh, dv
            case 0x000D:
                port.TextSize = b.ReadUInt16();
                return true;                 // TxSize
            case 0x0006:
                port.SpaceExtra = b.ReadInt32();
                return true;               // SpExtra (Fixed)
            case 0x0015:
                port.PnLocHFrac(b.ReadUInt16());
                return true;                // PnLocHFrac
            case 0x0016:
                port.ChExtra = (short)b.ReadUInt16();
                return true;           // ChExtra (4.12 per point)
            case 0x0010:
                { var n = ReadPoint(b); var d = ReadPoint(b); port.TextRatio(n.h, n.v, d.h, d.v); return true; }   // TxRatio
            case 0x000E:
                (port.ForeColor, port.Fore16) = ClassicColor((int)b.ReadUInt32(), true);
                return true;  // FgColor
            case 0x000F:
                (port.BackColor, port.Back16) = ClassicColor((int)b.ReadUInt32(), false);
                return true; // BkColor
            case 0x001A:
                (port.ForeColor, port.Fore16) = ReadRgbExact(b);
                return true;                          // RGBFgCol
            case 0x001B:
                (port.BackColor, port.Back16) = ReadRgbExact(b);
                return true;                          // RGBBkCol
            case 0x001C:
                port.HiliteMode();
                return true;                             // HiliteMode
            case 0x001D:
                port.HiliteColor = ReadRgb(b);
                return true;                 // HiliteColor
            case 0x001E:
                port.DefaultHilite();
                return true;                          // DefHilite
            case 0x001F:
                port.OpColor = (b.ReadUInt16(), b.ReadUInt16(), b.ReadUInt16());
                return true;   // OpColor
            case 0x0020:
                { var a = ReadPoint(b); var c = ReadPoint(b); port.Line(a.h, a.v, c.h, c.v); return true; }   // Line
            case 0x0021:
                { var c = ReadPoint(b); port.LineTo(c.h, c.v); return true; }               // LineFrom
            case 0x0022:
                { var a = ReadPoint(b); sbyte dh = (sbyte)b.ReadByte(), dv = (sbyte)b.ReadByte(); port.Line(a.h, a.v, a.h + dh, a.v + dv); return true; }   // ShortLine
            case 0x0023:
                { sbyte dh = (sbyte)b.ReadByte(), dv = (sbyte)b.ReadByte(); port.LineBy(dh, dv); return true; }   // ShortLineFrom
            case 0x0028:
                { var p = ReadPoint(b); port.LongText(p.h, p.v, ReadText(b)); return true; }            // LongText
            case 0x0029:
                { int dh = b.ReadByte(); port.OffsetText(dh, 0, ReadText(b)); return true; }            // DHText
            case 0x002A:
                { int dv = b.ReadByte(); port.OffsetText(0, dv, ReadText(b)); return true; }            // DVText
            case 0x002B:
                { int dh = b.ReadByte(), dv = b.ReadByte(); port.OffsetText(dh, dv, ReadText(b)); return true; }   // DHDVText
            case 0x002C:                                                              // fontName
                {
                    var data = b.ReadSubReader(b.ReadUInt16());                          // the old font ID, then a name
                    if (data.Length >= 3 && data.ReadByteAt(2) <= data.Length - 3)
                    {
                        port.FontName(data.ReadUInt16At(0), MacRomanString(data.ReadBytesAt(3, data.ReadByteAt(2))));
                    }

                    return true;
                }
            case 0x002D:                                                              // LineJustify
                {
                    var data = b.ReadSubReader(b.ReadUInt16());                            // interCharSpacing, textExtra
                    if (data.Length >= 4)
                    {
                        port.LineJustify(data.ReadInt32At(0));
                    }

                    return true;
                }
            case 0x002E:                                                              // glyphState
                {
                    var data = b.ReadBytes(b.ReadUInt16()).ToArray();                      // outline preferred, preserve
                    if (data.Length >= 3)                                                 // glyph, fractional widths,
                    {
                        port.GlyphState(data[2] != 0, data.Length >= 4 && data[3] != 0);  // scaling disabled
                    }

                    return true;
                }
        }

        if (op >= 0x0030 && op <= 0x0034)
        {
            port.Rect(PictRect.Read(b), op - 0x0030);
            return true;
        }
        if (op >= 0x0038 && op <= 0x003C)
        {
            port.Rect(null, op - 0x0038);
            return true;
        }
        if (op >= 0x0040 && op <= 0x0044)
        {
            port.RoundRect(PictRect.Read(b), op - 0x0040);
            return true;
        }
        if (op >= 0x0048 && op <= 0x004C)
        {
            port.RoundRect(null, op - 0x0048);
            return true;
        }
        if (op >= 0x0050 && op <= 0x0054)
        {
            port.Oval(PictRect.Read(b), op - 0x0050);
            return true;
        }
        if (op >= 0x0058 && op <= 0x005C)
        {
            port.Oval(null, op - 0x0058);
            return true;
        }
        if (op >= 0x0060 && op <= 0x0064)
        {
            var r = PictRect.Read(b);
            int sa = b.ReadInt16(), aa = b.ReadInt16();
            port.Arc(r, sa, aa, op - 0x0060);
            return true;
        }
        if (op >= 0x0068 && op <= 0x006C)
        {
            int sa = b.ReadInt16(), aa = b.ReadInt16();
            port.Arc(null, sa, aa, op - 0x0068);
            return true;
        }
        if (op >= 0x0070 && op <= 0x0074)
        {
            port.Polygon(ReadPolygon(b), op - 0x0070);
            return true;
        }
        if (op >= 0x0078 && op <= 0x007C)
        {
            port.Polygon(null, op - 0x0078);
            return true;
        }
        if (op >= 0x0080 && op <= 0x0084)
        {
            port.Rgn(Region.Read(b), op - 0x0080);
            return true;
        }
        if (op >= 0x0088 && op <= 0x008C)
        {
            port.Rgn(null, op - 0x0088);
            return true;
        }

        return false;
    }

    private static byte[] ReadText(ClassicMac.Core.BigEndianReader b) => b.ReadBytes(b.ReadByte()).ToArray();

    // QuickDraw Point is (v, h) - vertical first.
    private static (int v, int h) ReadPoint(ClassicMac.Core.BigEndianReader b)
    {
        int v = b.ReadInt16();
        int h = b.ReadInt16();
        return (v, h);
    }

    // RGBColor: three 16-bit channels (use the high byte).
    internal static RgbaColor ReadRgb(ClassicMac.Core.BigEndianReader b)
    {
        int r = b.ReadUInt16(), g = b.ReadUInt16(), bl = b.ReadUInt16();
        return new RgbaColor((byte)(r >> 8), (byte)(g >> 8), (byte)(bl >> 8), 255);
    }

    private static (RgbaColor, (ushort, ushort, ushort)) ReadRgbExact(ClassicMac.Core.BigEndianReader b)
    {
        int r = b.ReadUInt16(), g = b.ReadUInt16(), bl = b.ReadUInt16();
        return (new RgbaColor((byte)(r >> 8), (byte)(g >> 8), (byte)(bl >> 8), 255), ((ushort)r, (ushort)g, (ushort)bl));
    }

    // Classic 1-bit-era color constants (FgColor/BkColor longs): on a color port, the QDColors table (clut 127).
    private static (RgbaColor, (ushort, ushort, ushort)) ClassicColor(int value, bool fore)
    {
        (int r, int g, int b) c = value switch
        {
            30 => (0xFFFF, 0xFFFF, 0xFFFF),     // whiteColor
            33 => (0, 0, 0),                    // blackColor
            69 => (0xFC00, 0xF37D, 0x052F),     // yellowColor
            137 => (0xF2D7, 0x0856, 0x84EC),    // magentaColor
            205 => (0xDD6B, 0x08C2, 0x06A2),    // redColor
            273 => (0x0241, 0xAB54, 0xEAFF),    // cyanColor
            341 => (0x0000, 0x8000, 0x11B0),    // greenColor
            409 => (0x0000, 0x0000, 0xD400),    // blueColor
            _ => fore ? (0, 0, 0) : (0xFFFF, 0xFFFF, 0xFFFF),
        };
        return (new RgbaColor((byte)(c.r >> 8), (byte)(c.g >> 8), (byte)(c.b >> 8), 255), ((ushort)c.r, (ushort)c.g, (ushort)c.b));
    }

    // A Polygon: u16 polySize + bounding Rect + (polySize - 10) / 4 Points, returned as (h, v) picture points.
    // An empty polygon (no points or an empty bounding box) draws nothing (Executor C_StdPoly).
    private static (int h, int v)[] ReadPolygon(ClassicMac.Core.BigEndianReader b)
    {
        int size = b.ReadUInt16();
        var bbox = PictRect.Read(b);
        int count = Math.Max(0, (size - 10) / 4);
        var pts = new (int h, int v)[count];
        for (int i = 0; i < count; i++)
        {
            var p = ReadPoint(b);
            pts[i] = (p.h, p.v);
        }
        if (size > 10 && (size - 10) % 4 != 0)
        {
            b.Skip((size - 10) % 4);
        }

        return bbox.IsEmpty ? Array.Empty<(int h, int v)>() : pts;
    }

    // A QuickDraw Region or Polygon: u16 total size (including itself) + bounding Rect +
    // optional run data. We only need to skip past it.
    private static void SkipRegion(ClassicMac.Core.BigEndianReader b)
    {
        int size = b.ReadUInt16();
        if (size >= 2)
        {
            b.Skip(size - 2);
        }
    }

    // var16/var32: a u16/u32 byte-length prefix followed by that many data bytes.
    private static void SkipVar16(ClassicMac.Core.BigEndianReader b) => b.Skip(b.ReadUInt16());
    private static void SkipVar32(ClassicMac.Core.BigEndianReader b)
    {
        uint count = b.ReadUInt32();
        if (count > b.Remaining)
        {
            throw new EndOfStreamException();
        }

        b.Skip((int)count);
    }

    private static byte[] ReadLengthPrefixedBytes(ClassicMac.Core.BigEndianReader b)
    {
        uint count = b.ReadUInt32();
        if (count > b.Remaining)
        {
            throw new EndOfStreamException();
        }

        return b.ReadBytes((int)count).ToArray();
    }

    // Text opcodes: positioning bytes, then a u8 char count, then that many chars.
    private static void SkipText(ClassicMac.Core.BigEndianReader b, int positionBytes)
    {
        b.Skip(positionBytes);
        int count = b.ReadByte();
        b.Skip(count);
    }

    // Consumes the operands of an opcode that is not interpreted, by its size in Inside Macintosh: Imaging With
    // QuickDraw, Appendix A, Table A-2 (cross-checked with Executor's wparray, qPicstuff.cpp). Reserved opcodes
    // "for Apple use" are skipped, as QuickDraw does.
    private static void SkipOperands(ClassicMac.Core.BigEndianReader b, int op)
    {
        switch (op)
        {
            case 0x0000:
                return;                                    // NOP
            case 0x0001:
                SkipRegion(b);
                return;                // clip region
            case 0x0002:
            case 0x0009:
            case 0x000A:
            case 0x0010:    // BkPat/PnPat/FillPat/TxRatio
                b.Skip(8);
                return;
            case 0x0003:
            case 0x0005:
            case 0x0008:
            case 0x000D:    // TxFont/TxMode/PnMode/TxSize
            case 0x0015:
            case 0x0016:
            case 0x0023:                 // PnLocHFrac/ChExtra/ShortLineFrom
                b.Skip(2);
                return;
            case 0x0004:
                b.Skip(1);
                return;                        // TxFace
            case 0x0006:
            case 0x0007:
            case 0x000B:
            case 0x000C:    // SpExtra/PnSize/OvSize/Origin
            case 0x000E:
            case 0x000F:
            case 0x0021:                 // Fg/BkColor/LineFrom
                b.Skip(4);
                return;
            case 0x0017:
            case 0x0018:
            case 0x0019:
                return;         // reserved (no data)
            case 0x001A:
            case 0x001B:
            case 0x001D:
            case 0x001F:    // RGB Fg/Bk/Hilite/OpColor
                b.Skip(6);
                return;
            case 0x001C:
            case 0x001E:
                return;                      // HiliteMode/DefHilite
            case 0x0020:
                b.Skip(8);
                return;                        // Line
            case 0x0022:
                b.Skip(6);
                return;                        // ShortLine
            case 0x0028:
                SkipText(b, 4);
                return;               // LongText
            case 0x0029:
            case 0x002A:
                SkipText(b, 1);
                return;  // DH/DV Text
            case 0x002B:
                SkipText(b, 2);
                return;               // DHDV Text
            case 0x02FF:
                b.Skip(2);
                return;                        // Version (mid-stream)
        }

        if (op >= 0x0024 && op <= 0x0027)
        { SkipVar16(b); return; } // reserved: length + data
        if (op >= 0x002C && op <= 0x002F)
        { SkipVar16(b); return; } // fontName/lineJustify/glyphState/reserved
        if (op >= 0x0030 && op <= 0x0037)
        { b.Skip(8); return; }        // rect (incl. reserved)
        if (op >= 0x0038 && op <= 0x003F)
        {
            return;                       // same rect (no data)
        }

        if (op >= 0x0040 && op <= 0x0047)
        { b.Skip(8); return; }        // round rect
        if (op >= 0x0048 && op <= 0x004F)
        {
            return;
        }

        if (op >= 0x0050 && op <= 0x0057)
        { b.Skip(8); return; }        // oval
        if (op >= 0x0058 && op <= 0x005F)
        {
            return;
        }

        if (op >= 0x0060 && op <= 0x0067)
        { b.Skip(12); return; }       // arc (rect + angles)
        if (op >= 0x0068 && op <= 0x006F)
        { b.Skip(4); return; }        // same arc (angles)
        if (op >= 0x0070 && op <= 0x0077)
        { SkipRegion(b); return; } // poly (size includes itself)
        if (op >= 0x0078 && op <= 0x007F)
        {
            return;                       // same poly (no data)
        }

        if (op >= 0x0080 && op <= 0x0087)
        { SkipRegion(b); return; } // region
        if (op >= 0x0088 && op <= 0x008F)
        {
            return;                       // same region (no data)
        }

        if (op >= 0x0094 && op <= 0x0097)
        { SkipVar16(b); return; } // reserved
        if (op >= 0x009C && op <= 0x009F)
        { SkipVar16(b); return; } // reserved
        if (op >= 0x00A2 && op <= 0x00AF)
        { SkipVar16(b); return; } // reserved
        if (op >= 0x00B0 && op <= 0x00CF)
        {
            return;                       // reserved (no data)
        }

        if (op >= 0x00D0 && op <= 0x00DF)
        { SkipVar16(b); return; } // reserved: the ROM reads a u16 length
        if (op >= 0x00E0 && op <= 0x00FE)
        { SkipVar32(b); return; } // reserved (u32 length + data)
        if (op >= 0x0100 && op <= 0x7FFF)
        { b.Skip(2 * (op >> 8)); return; }   // $nnXX: 2 * nn bytes
        if (op >= 0x8000 && op <= 0x80FF)
        {
            return;                       // reserved (no data)
        }
        // 0x8100-0xFFFF: u32 length + data, including 0x8200/0x8201 QuickTime and 0xFFFF.
        SkipVar32(b);
    }
}
