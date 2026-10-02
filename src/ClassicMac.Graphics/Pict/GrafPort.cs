using System;
using System.Collections.Generic;
using ClassicMac.Graphics;
using ClassicMac.Graphics.QuickDraw;
using ClassicMac.Graphics.QuickTime;

namespace ClassicMac.Graphics.Pict
{
    // DrawPicture's play state on top of a QuickDrawPort. As DrawPicture does, it keeps picture-space coordinates (pen
    // and text locations, the "same shape" rect/poly/region, the clip) and maps them to the canvas when drawing: from
    // fromRect (the picture's frame, moved by the Origin opcode) to toRect (the canvas), scaling when the two differ in
    // size. The port draws. verb: 0 frame, 1 paint, 2 erase, 3 invert, 4 fill.
    internal sealed class GrafPort
    {
        private readonly QuickDrawPort port;
        private readonly PictDecodeOptions options;
        private PictRect fromRect;
        private readonly PictRect toRect;
        private readonly bool macOS9;

        // Port state as DrawPicture initializes it: black on white, pen 1x1 patCopy with a black pen and fill pattern
        // and a white background pattern, text mode srcOr, OpColor black (a new QuickDrawPort's state). The opcodes
        // set it through these.
        public RgbaColor ForeColor { get => port.Fore; set => port.Fore = value; }
        public RgbaColor BackColor { get => port.Back; set => port.Back = value; }
        public (ushort r, ushort g, ushort b) Fore16 { get => port.Fore16; set => port.Fore16 = value; }   // their exact components
        public (ushort r, ushort g, ushort b) Back16 { get => port.Back16; set => port.Back16 = value; }
        public (ushort r, ushort g, ushort b) OpColor { get => port.Op; set => port.Op = value; }
        public RgbaColor HiliteColor { get => port.Hilite; set => port.SetHilite(value); }

        public QuickDrawPattern BkPat { get => port.BkPat; set => port.BkPat = value; }
        public QuickDrawPattern PnPat { get => port.PnPat; set => port.PnPat = value; }
        public QuickDrawPattern FillPat { get => port.FillPat; set => port.FillPat = value; }
        public int PenMode { get => port.Mode; set => port.Mode = value; }
        private int ovalWidth, ovalHeight;                        // canvas pixels (scaled when set)
        private int penH, penV;                                   // picture space

        private Region? pictureClip;                              // picture space; null = no clip
        private PictRect lastRect;                                // picture space, for the "same shape" opcodes
        private (int h, int v)[]? lastPoly;
        private Region lastRegion = Region.Empty;

        public int TextFace { get => port.Face; set => port.Face = value; }
        public int TextSize { get => port.Size; set => port.Size = value; }
        public int TextMode { get => port.TxMode; set => port.TxMode = value; }
        public int SpaceExtra { get => port.SpaceExtraFixed; set => port.SpaceExtraFixed = value; }   // Fixed
        public int ChExtra { get => port.ChExtra; set => port.ChExtra = value; }   // the color port's chExtra (4.12 per point)
        public bool Version1;                                     // the picture's opcodes are version 1
        private int textFontId, pictureFontId;                    // the port's txFont, and the picture's number for it
        private int pendingFrac = 0x8000;                         // PnLocHFrac for the next text
        private int textH, textV;                                 // text origin, picture space
        private readonly Dictionary<int, string> fontNames = new Dictionary<int, string>();
        private readonly Dictionary<int, int> fontMap = new Dictionary<int, int>();
        private readonly bool intoPort;                           // drawn into a caller's port (DrawPicture), not a fresh canvas
        private readonly Region? callerClip;                      // that port's clip, canvas pixels

        // A picture decoded onto a fresh canvas: picFrame and the drawing rectangle (the header's srcRect for an extended
        // version 2 picture, else picFrame) mapped to the whole canvas. Unlike DrawPicture, which starts with an empty
        // clip until the picture's ClipRgn, a picture without one still draws [ClassicMac].
        public GrafPort(RgbaBitmap canvas, PictRect pictureFrame, PictRect drawingRect, PictDecodeOptions options)
            : this(new QuickDrawPort(canvas, options.ToQuickDrawOptions()), pictureFrame, drawingRect,
                new PictRect(0, 0, canvas.Height, canvas.Width), options, intoPort: false)
        {
        }

        // DrawPicture into a caller's port: its state reset as DrawPicture resets it (the caller saves and restores it),
        // the picture mapped to destination (canvas pixels), drawing clipped to nothing until the picture's ClipRgn,
        // then to that region within the port's own clip.
        public GrafPort(QuickDrawPort port, PictRect pictureFrame, PictRect drawingRect, PictRect destination, PictDecodeOptions options)
            : this(port, pictureFrame, drawingRect, destination, options, intoPort: true)
        {
        }

        private GrafPort(QuickDrawPort port, PictRect pictureFrame, PictRect drawingRect, PictRect destination,
            PictDecodeOptions options, bool intoPort)
        {
            this.options = options;
            this.port = port;
            this.intoPort = intoPort;
            fromRect = drawingRect;
            toRect = destination;
            macOS9 = port.MacOS9;
            if (intoPort)
            {
                callerClip = port.ClipRegion;
                port.ResetForPicture(SystemHilite);
                port.ClipRegion = Region.Empty;
            }
            // Text scaling: the destination over the drawing rectangle. The pen starts ScalePt((1, 1)) from picFrame
            // (not the extended header's srcRect) to the destination.
            port.TextNumer = (toRect.Width, toRect.Height);
            port.TextDenom = (fromRect.Width, fromRect.Height);
            (port.PenWidth, port.PenHeight) = PictureMapping.ScaleSize(1, 1, pictureFrame, toRect);
        }

        private Region Clipped(Region pictureClip)
        {
            var mapped = MapRegion(pictureClip);
            return intoPort && callerClip != null ? mapped.Intersect(callerClip) : mapped;
        }

        // ---- coordinate mapping ----

        private (int h, int v) MapPoint(int h, int v) => PictureMapping.MapPoint(h, v, fromRect, toRect);
        private PictRect MapRect(PictRect r) => PictureMapping.MapRect(r, fromRect, toRect);
        private Region MapRegion(Region r) => PictureMapping.MapRegion(r, fromRect, toRect);

        // Origin opcode: moves the picture frame by (dh, dv) (so later coordinates land dh, dv further up-left),
        // shifts the pattern alignment by the same amount (ROM only; Mac OS 9 leaves it; DrawPicture starts it at
        // (0, 0)), and re-maps the clip.
        public void Origin(int dh, int dv)
        {
            fromRect = new PictRect(fromRect.Top + dv, fromRect.Left + dh, fromRect.Bottom + dv, fromRect.Right + dh);
            if (!macOS9)
            {
                port.PatternAlign = (port.PatternAlign.h + dh, port.PatternAlign.v + dv);
            }

            if (pictureClip != null)
            {
                port.ClipRegion = Clipped(pictureClip);
            }
        }

        // ---- state ----

        public void SetClip(Region pictureRegion)
        {
            pictureClip = pictureRegion;
            port.ClipRegion = Clipped(pictureRegion);
        }

        public void PenSize(int h, int v) => (port.PenWidth, port.PenHeight) = PictureMapping.ScaleSize(h, v, fromRect, toRect);
        public void OvalSize(int h, int v) => (ovalWidth, ovalHeight) = PictureMapping.ScaleSize(h, v, fromRect, toRect);
        public void HiliteMode() => port.HiliteMode();

        // TxRatio: numer x the destination size over denom x the picture frame size, per axis, both halved together
        // until they fit in 15 bits.
        public void TextRatio(int numerH, int numerV, int denomH, int denomV)
        {
            var (nh, dh) = Reduce(numerH, toRect.Width, denomH, fromRect.Width);
            var (nv, dv) = Reduce(numerV, toRect.Height, denomV, fromRect.Height);
            port.TextNumer = (nh, nv);
            port.TextDenom = (dh, dv);
        }

        private static (int n, int d) Reduce(int numer, int toSize, int denom, int fromSize)
        {
            uint n = (uint)(ushort)numer * (ushort)toSize, d = (uint)(ushort)denom * (ushort)fromSize;
            while (((n | d) & 0xFFFF8000) != 0)
            {
                n >>= 1;
                d >>= 1;
            }
            return ((int)n, (int)d);
        }

        public void DefaultHilite() => port.SetHilite(SystemHilite);

        private RgbaColor SystemHilite =>
            options.HiliteColor ?? (macOS9 ? new RgbaColor(0xCC, 0xCC, 0xFF) : new RgbaColor(0x99, 0xCC, 0xCC));

        // TxFont goes through the font map the fontName opcodes build.
        public void TextFont(int fontId)
        {
            pictureFontId = fontId;
            textFontId = fontMap.TryGetValue(fontId, out int mapped) ? mapped : fontId;
        }

        // fontName: when the library has a family of that name (GetFNum, which never maps to 0) numbered differently,
        // later TxFont opcodes for the picture's number select it. The name also goes to the outline fallback.
        public void FontName(int fontId, string name)
        {
            fontNames[fontId] = name;
            if (options.Fonts is { } library && library.TryGetFamilyByName(name, out int family) && family != 0
                && family != fontId)
            {
                fontMap.TryAdd(fontId, family);
            }
        }

        public void PnLocHFrac(int fraction) => pendingFrac = fraction & 0xFFFF;
        // (Mac OS 9 skips LineJustify: its character extra stays 0.)
        public void LineJustify(int interCharacterSpacing)
        {
            if (!macOS9)
            {
                port.InterCharSpacing = interCharacterSpacing;
            }
        }
        public void GlyphState(bool fractionalWidths, bool scalingDisabled) =>
            (port.FractEnable, port.FScaleDisable) = (fractionalWidths, scalingDisabled);

        // ---- shapes (mapped to the canvas, drawn by the port) ----

        public void Rect(PictRect? pictureRect, int verb)
        {
            if (pictureRect is { } pr)
            {
                lastRect = pr;
            }

            port.RectShape(MapRect(lastRect), verb);
        }

        public void RoundRect(PictRect? pictureRect, int verb)
        {
            if (pictureRect is { } pr)
            {
                lastRect = pr;
            }

            port.RoundRectShape(MapRect(lastRect), ovalWidth, ovalHeight, verb);
        }

        public void Oval(PictRect? pictureRect, int verb)
        {
            if (pictureRect is { } pr)
            {
                lastRect = pr;
            }

            port.OvalShape(MapRect(lastRect), verb);
        }

        public void Arc(PictRect? pictureRect, int startAngle, int arcAngle, int verb)
        {
            if (pictureRect is { } pr)
            {
                lastRect = pr;
            }

            port.ArcShape(MapRect(lastRect), startAngle, arcAngle, verb);
        }

        // Polygons (picture-space points).
        public void Polygon((int h, int v)[]? picturePoints, int verb)
        {
            if (picturePoints != null)
            {
                lastPoly = picturePoints;
            }

            if (lastPoly == null || lastPoly.Length < 2)
            {
                port.Done();
                return;
            }
            var pts = new (int h, int v)[lastPoly.Length];
            for (int i = 0; i < pts.Length; i++)
            {
                pts[i] = MapPoint(lastPoly[i].h, lastPoly[i].v);
            }

            port.PolyShape(pts, verb);
        }

        public void Rgn(Region? pictureRegion, int verb)
        {
            if (pictureRegion != null)
            {
                lastRegion = pictureRegion;
            }

            port.RgnShape(MapRegion(lastRegion), verb);
        }

        // ---- lines ----

        public void Line(int h1, int v1, int h2, int v2)
        {
            penH = h1;
            penV = v1;
            LineTo(h2, v2);
        }

        public void LineTo(int h, int v)
        {
            var (x1, y1) = MapPoint(penH, penV);
            var (x2, y2) = MapPoint(h, v);
            port.PaintLine(x1, y1, x2, y2);
            penH = h;
            penV = v;
            port.Done();
        }

        public void LineBy(int dh, int dv) => LineTo(penH + dh, penV + dv);

        // ---- bitmaps ----

        // BitsRect / BitsRgn / PackBitsRect / PackBitsRgn / DirectBitsRect / DirectBitsRgn: the destination rect and
        // mask region are mapped like any other picture coordinates.
        public void CopyBits(PixMap source, PictRect srcRect, PictRect pictureDstRect, int mode, Region? pictureMask) =>
            port.CopyBits(source, srcRect, MapRect(pictureDstRect), mode, pictureMask == null ? null : MapRegion(pictureMask));

        // ---- QuickTime ----

        // CompressedQuickTime: decompress (built-in codecs, then the caller's) and draw the image where its matrix
        // puts the source rect, through its transfer mode and mask. Returns that destination (picture space), or null
        // when the image could not be decoded.
        public PictRect? QuickTime(byte[] block)
        {
            var q = QuickTimeImage.Parse(block);
            if (q == null)
            {
                return null;
            }

            var image = QuickTimeCodecs.Decode(q.Description, q.Data) ?? options.ImageCodec?.Decode(q.Description, q.Data);
            if (image == null)
            {
                return null;
            }

            var source = q.SourceRect.IsEmpty ? new PictRect(0, 0, image.Height, image.Width) : q.SourceRect;
            var destination = q.DestinationRect();
            port.CopyBits(QuickTimeImage.ToPixMap(image), source, MapRect(destination), q.Mode, q.Mask == null ? null : MapRegion(q.Mask));
            return destination;
        }

        // UncompressedQuickTime: the embedded bitmap opcode's image drawn to its dstRect, or, when the matrix is not
        // the identity, to where the matrix puts its srcRect (bounding box; the same placement as a compressed image).
        public PictRect UncompressedQuickTime(PixMap source, PictRect srcRect, PictRect dstRect, int mode, Region? pictureMask,
            int[] matrix)
        {
            bool identity = matrix[0] == 0x10000 && matrix[1] == 0 && matrix[3] == 0 && matrix[4] == 0x10000 &&
                matrix[6] == 0 && matrix[7] == 0;
            var destination = identity ? dstRect : QuickTimeImage.Place(matrix, srcRect);
            CopyBits(source, srcRect, destination, mode, pictureMask);
            return destination;
        }

        // ---- text ----

        // LongText sets the text origin; DH/DV/DHDV text move it from the previous origin. Drawing does not move it.
        // The pen goes to the mapped origin: in a version 2 picture through MapFixPt, from (v + 1/2, h + the pending
        // PnLocHFrac fraction), keeping h's fraction as the pen's; in a version 1 picture through MapPt, the pen
        // keeping the fraction the last text left.
        public void LongText(int h, int v, byte[] text)
        {
            textH = h;
            textV = v;
            DrawText(text);
        }

        public void OffsetText(int dh, int dv, byte[] text)
        {
            textH += dh;
            textV += dv;
            DrawText(text);
        }

        // StdText through the port; a fontName opcode maps the picture's font number to a family by name, and the
        // outline fallback gets the picture's number and name.
        private void DrawText(byte[] text)
        {
            int fraction = pendingFrac;
            pendingFrac = 0x8000;
            int x, y;
            if (Version1)
            {
                (x, y) = MapPoint(textH, textV);
            }
            else
            {
                int fh = MapFixed(unchecked((textH << 16) | fraction), fromRect.Left, fromRect.Right, toRect.Left, toRect.Right, macOS9);
                int fv = MapFixed(unchecked((textV << 16) | 0x8000), fromRect.Top, fromRect.Bottom, toRect.Top, toRect.Bottom, macOS9);
                (x, y, port.PenFrac) = (fh >> 16, fv >> 16, fh & 0xFFFF);
            }
            fontNames.TryGetValue(pictureFontId, out var name);
            port.DrawTextAt(text, x, y, textFontId, pictureFontId, name, movePen: false);
        }

        // MapFixPt on one axis: (c - from) x toSize / fromSize (a truncating 64/32-bit divide) + to, in Fixed; an
        // axis of equal sizes only moves. Mac OS 9 multiplies by the truncated ratio (toSize << 16) / fromSize instead,
        // rounding half up (so 3 -> 1 maps 3.0 to 0.99998).
        private static int MapFixed(int c, int fromLo, int fromHi, int toLo, int toHi, bool macOS9)
        {
            int fromSize = unchecked(((short)fromHi << 16) - ((short)fromLo << 16));
            int toSize = unchecked(((short)toHi << 16) - ((short)toLo << 16));
            int d = unchecked(c - ((short)fromLo << 16));
            if (fromSize != toSize && fromSize != 0)
            {
                if (macOS9)
                {
                    long ratio = ((long)(short)(toHi - toLo) << 16) / (short)(fromHi - fromLo);
                    d = FixedMath.FixMulHalfUp(d, (int)Math.Clamp(ratio, int.MinValue, int.MaxValue));
                }
                else
                {
                    d = unchecked((int)((long)d * toSize / fromSize));
                }
            }
            return unchecked(d + ((short)toLo << 16));
        }
    }
}
