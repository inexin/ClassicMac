using System;
using System.Collections.Generic;
using System.Linq;
using ClassicMac.Core;

namespace ClassicMac.Graphics.QuickDraw
{
    /// <summary>QuickDraw's text styles (<c>Style</c>), combinable.</summary>
    [Flags]
    public enum QuickDrawStyle
    {
        /// <summary>Plain text.</summary>
        Plain = 0,
        /// <summary>Bold.</summary>
        Bold = 1,
        /// <summary>Italic.</summary>
        Italic = 2,
        /// <summary>Underlined.</summary>
        Underline = 4,
        /// <summary>Outlined.</summary>
        Outline = 8,
        /// <summary>Shadowed.</summary>
        Shadow = 16,
        /// <summary>Condensed.</summary>
        Condense = 32,
        /// <summary>Extended.</summary>
        Extend = 64,
    }

    /// <summary>
    /// A colour QuickDraw graphics port drawing into an <see cref="RgbaBitmap"/>, pixel for pixel as the chosen Macintosh
    /// QuickDraw draws (<i>Inside Macintosh: Imaging With QuickDraw</i>). Its members are QuickDraw's routines on this
    /// port; coordinates are the canvas's pixels, (0, 0) its top-left. A port is not thread-safe.
    /// </summary>
    public sealed class QuickDrawPort
    {
        private readonly RgbaBitmap canvas;
        private readonly bool macOS9;
        private readonly ScreenDevice? device;

        /// <summary>A port drawing into <paramref name="canvas"/>, in the state <c>OpenCPort</c> gives: black on white, a 1 × 1 black pen in patCopy, a white background pattern, no clipping, the pen at (0, 0).</summary>
        public QuickDrawPort(RgbaBitmap canvas, QuickDrawOptions? options = null)
        {
            ArgumentNullException.ThrowIfNull(canvas);
            this.canvas = canvas;
            Options = options ?? QuickDrawOptions.Default;
            macOS9 = Options.Version == QuickDrawVersion.MacOS9;
            device = ScreenDevice.For(Options.ScreenDepth, macOS9);
            var hilite = Options.HiliteColor ?? (macOS9 ? new RgbColor(0xCCCC, 0xCCCC, 0xFFFF) : new RgbColor(0x9999, 0xCCCC, 0xCCCC));
            (Hilite, hilite16) = (hilite.ToRgba(), hilite);
        }

        /// <summary>The image drawn into.</summary>
        public RgbaBitmap Canvas => canvas;

        /// <summary>How the port draws.</summary>
        public QuickDrawOptions Options { get; }

        /// <summary>The port's rectangle: the canvas, (0, 0) to its size.</summary>
        public MacRect PortRect => new(0, 0, (short)Math.Min(canvas.Height, short.MaxValue), (short)Math.Min(canvas.Width, short.MaxValue));

        internal bool MacOS9 => macOS9;
        internal ScreenDevice? Device => device;

        // ---- colours ----

        internal RgbaColor Fore = new(0, 0, 0), Back = new(255, 255, 255), Hilite;
        internal (ushort r, ushort g, ushort b) Fore16, Back16 = (0xFFFF, 0xFFFF, 0xFFFF), Op;
        private RgbColor hilite16;
        internal bool HilitePending;

        /// <summary>The foreground colour (<c>RGBForeColor</c>).</summary>
        public RgbColor ForeColor
        {
            get => new(Fore16.r, Fore16.g, Fore16.b);
            set => (Fore, Fore16) = (value.ToRgba(), value.Tuple);
        }

        /// <summary>The background colour (<c>RGBBackColor</c>).</summary>
        public RgbColor BackColor
        {
            get => new(Back16.r, Back16.g, Back16.b);
            set => (Back, Back16) = (value.ToRgba(), value.Tuple);
        }

        /// <summary>The arithmetic modes' weight or pin colour (<c>OpColor</c>).</summary>
        public RgbColor OpColor
        {
            get => new(Op.r, Op.g, Op.b);
            set => Op = value.Tuple;
        }

        /// <summary>The highlight colour (<c>HiliteColor</c>); a new port has the system's (<see cref="QuickDrawOptions.HiliteColor"/>).</summary>
        public RgbColor HiliteColor
        {
            get => hilite16;
            set => (Hilite, hilite16) = (value.ToRgba(), value);
        }

        // A picture's 8-bit highlight colour.
        internal void SetHilite(RgbaColor color) => (Hilite, hilite16) = (color, RgbColor.FromRgba(color));

        /// <summary>
        /// Makes the next drawing operation highlight (<c>HiliteMode</c>): pattern and source inversions swap the
        /// background and highlight colours instead. Any drawing operation clears it.
        /// </summary>
        public void HiliteMode() => HilitePending = true;

        internal PortColors Colors => new(Fore, Back, Op, Hilite, macOS9, device, Fore16, Back16);

        // ---- pen, patterns, clip ----

        internal QuickDrawPattern PnPat = QuickDrawPattern.Black, BkPat = QuickDrawPattern.White, FillPat = QuickDrawPattern.Black;
        internal int Mode = TransferModes.PatCopy;
        internal int PenWidth = 1, PenHeight = 1;
        internal int PenH, PenV, PenFrac = 0x8000;
        internal Region? ClipRegion;
        internal (int h, int v) PatternAlign;

        /// <summary>The pen pattern (<c>PenPat</c>).</summary>
        public QuickDrawPattern PenPattern { get => PnPat; set => PnPat = value ?? throw new ArgumentNullException(nameof(value)); }

        /// <summary>The background pattern that erasing paints (<c>BackPat</c>).</summary>
        public QuickDrawPattern BackPattern { get => BkPat; set => BkPat = value ?? throw new ArgumentNullException(nameof(value)); }

        /// <summary>The pattern the <c>Fill…</c> routines paint; each sets it to the pattern it is given.</summary>
        public QuickDrawPattern FillPattern { get => FillPat; set => FillPat = value ?? throw new ArgumentNullException(nameof(value)); }

        /// <summary>The pen's transfer mode (<c>PenMode</c>).</summary>
        public TransferMode PenMode { get => (TransferMode)Mode; set => Mode = (int)value; }

        /// <summary>The pen's size: <c>H</c> its width, <c>V</c> its height (<c>PenSize</c>).</summary>
        public MacPoint PenSize
        {
            get => new((short)PenHeight, (short)PenWidth);
            set => (PenWidth, PenHeight) = (value.H, value.V);
        }

        /// <summary>Where the pen is (<c>GetPen</c>).</summary>
        public MacPoint PenLocation => new((short)PenV, (short)PenH);

        /// <summary>The clip region; null draws everywhere on the canvas (<c>SetClip</c>, <c>ClipRect</c>).</summary>
        public Region? Clip { get => ClipRegion; set => ClipRegion = value; }

        /// <summary>The pen back to 1 × 1, patCopy, black (<c>PenNormal</c>).</summary>
        public void PenNormal() => (PenWidth, PenHeight, Mode, PnPat) = (1, 1, TransferModes.PatCopy, QuickDrawPattern.Black);

        // The pen's fraction is kept for text; moving the pen resets it to 1/2 [ClassicMac: not yet checked against the ROM].

        /// <summary>Moves the pen to (<paramref name="h"/>, <paramref name="v"/>) without drawing (<c>MoveTo</c>).</summary>
        public void MoveTo(int h, int v) => (PenH, PenV, PenFrac) = (h, v, 0x8000);

        /// <summary>Moves the pen by (<paramref name="dh"/>, <paramref name="dv"/>) without drawing (<c>Move</c>).</summary>
        public void Move(int dh, int dv) => MoveTo(PenH + dh, PenV + dv);

        /// <summary>Draws a line from the pen to (<paramref name="h"/>, <paramref name="v"/>) and moves the pen there (<c>LineTo</c>).</summary>
        public void LineTo(int h, int v)
        {
            PaintLine(PenH, PenV, h, v);
            MoveTo(h, v);
            Done();
        }

        /// <summary>Draws a line from the pen by (<paramref name="dh"/>, <paramref name="dv"/>) (<c>Line</c>).</summary>
        public void Line(int dh, int dv) => LineTo(PenH + dh, PenV + dv);

        // ---- shapes ----

        /// <summary>Outlines a rectangle with the pen (<c>FrameRect</c>).</summary>
        public void FrameRect(MacRect rect) => RectShape(PictRect.From(rect), 0);
        /// <summary>Fills a rectangle with the pen pattern and mode (<c>PaintRect</c>).</summary>
        public void PaintRect(MacRect rect) => RectShape(PictRect.From(rect), 1);
        /// <summary>Fills a rectangle with the background pattern (<c>EraseRect</c>).</summary>
        public void EraseRect(MacRect rect) => RectShape(PictRect.From(rect), 2);
        /// <summary>Inverts a rectangle's pixels (<c>InvertRect</c>).</summary>
        public void InvertRect(MacRect rect) => RectShape(PictRect.From(rect), 3);
        /// <summary>Fills a rectangle with <paramref name="pattern"/> (<c>FillRect</c>).</summary>
        public void FillRect(MacRect rect, QuickDrawPattern pattern) { FillPattern = pattern; RectShape(PictRect.From(rect), 4); }

        /// <summary>Outlines the oval inscribed in a rectangle (<c>FrameOval</c>).</summary>
        public void FrameOval(MacRect rect) => OvalShape(PictRect.From(rect), 0);
        /// <summary>Paints an oval (<c>PaintOval</c>).</summary>
        public void PaintOval(MacRect rect) => OvalShape(PictRect.From(rect), 1);
        /// <summary>Erases an oval (<c>EraseOval</c>).</summary>
        public void EraseOval(MacRect rect) => OvalShape(PictRect.From(rect), 2);
        /// <summary>Inverts an oval (<c>InvertOval</c>).</summary>
        public void InvertOval(MacRect rect) => OvalShape(PictRect.From(rect), 3);
        /// <summary>Fills an oval with <paramref name="pattern"/> (<c>FillOval</c>).</summary>
        public void FillOval(MacRect rect, QuickDrawPattern pattern) { FillPattern = pattern; OvalShape(PictRect.From(rect), 4); }

        /// <summary>Outlines a rounded rectangle with corner ovals <paramref name="ovalWidth"/> × <paramref name="ovalHeight"/> (<c>FrameRoundRect</c>).</summary>
        public void FrameRoundRect(MacRect rect, int ovalWidth, int ovalHeight) => RoundRectShape(PictRect.From(rect), ovalWidth, ovalHeight, 0);
        /// <summary>Paints a rounded rectangle (<c>PaintRoundRect</c>).</summary>
        public void PaintRoundRect(MacRect rect, int ovalWidth, int ovalHeight) => RoundRectShape(PictRect.From(rect), ovalWidth, ovalHeight, 1);
        /// <summary>Erases a rounded rectangle (<c>EraseRoundRect</c>).</summary>
        public void EraseRoundRect(MacRect rect, int ovalWidth, int ovalHeight) => RoundRectShape(PictRect.From(rect), ovalWidth, ovalHeight, 2);
        /// <summary>Inverts a rounded rectangle (<c>InvertRoundRect</c>).</summary>
        public void InvertRoundRect(MacRect rect, int ovalWidth, int ovalHeight) => RoundRectShape(PictRect.From(rect), ovalWidth, ovalHeight, 3);
        /// <summary>Fills a rounded rectangle with <paramref name="pattern"/> (<c>FillRoundRect</c>).</summary>
        public void FillRoundRect(MacRect rect, int ovalWidth, int ovalHeight, QuickDrawPattern pattern)
        {
            FillPattern = pattern;
            RoundRectShape(PictRect.From(rect), ovalWidth, ovalHeight, 4);
        }

        /// <summary>Outlines an arc of the oval in <paramref name="rect"/>, from <paramref name="startAngle"/> (degrees clockwise from 12 o'clock) through <paramref name="arcAngle"/> (<c>FrameArc</c>).</summary>
        public void FrameArc(MacRect rect, int startAngle, int arcAngle) => ArcShape(PictRect.From(rect), startAngle, arcAngle, 0);
        /// <summary>Paints a wedge of an oval (<c>PaintArc</c>).</summary>
        public void PaintArc(MacRect rect, int startAngle, int arcAngle) => ArcShape(PictRect.From(rect), startAngle, arcAngle, 1);
        /// <summary>Erases a wedge of an oval (<c>EraseArc</c>).</summary>
        public void EraseArc(MacRect rect, int startAngle, int arcAngle) => ArcShape(PictRect.From(rect), startAngle, arcAngle, 2);
        /// <summary>Inverts a wedge of an oval (<c>InvertArc</c>).</summary>
        public void InvertArc(MacRect rect, int startAngle, int arcAngle) => ArcShape(PictRect.From(rect), startAngle, arcAngle, 3);
        /// <summary>Fills a wedge of an oval with <paramref name="pattern"/> (<c>FillArc</c>).</summary>
        public void FillArc(MacRect rect, int startAngle, int arcAngle, QuickDrawPattern pattern)
        {
            FillPattern = pattern;
            ArcShape(PictRect.From(rect), startAngle, arcAngle, 4);
        }

        /// <summary>Draws lines from each point of a polygon to the next, not closing it (<c>FramePoly</c>).</summary>
        public void FramePoly(IReadOnlyList<MacPoint> points) => PolyShape(Points(points), 0);
        /// <summary>Paints a polygon (<c>PaintPoly</c>).</summary>
        public void PaintPoly(IReadOnlyList<MacPoint> points) => PolyShape(Points(points), 1);
        /// <summary>Erases a polygon (<c>ErasePoly</c>).</summary>
        public void ErasePoly(IReadOnlyList<MacPoint> points) => PolyShape(Points(points), 2);
        /// <summary>Inverts a polygon (<c>InvertPoly</c>).</summary>
        public void InvertPoly(IReadOnlyList<MacPoint> points) => PolyShape(Points(points), 3);
        /// <summary>Fills a polygon with <paramref name="pattern"/> (<c>FillPoly</c>).</summary>
        public void FillPoly(IReadOnlyList<MacPoint> points, QuickDrawPattern pattern) { FillPattern = pattern; PolyShape(Points(points), 4); }

        /// <summary>Outlines a region with the pen, inside its edge (<c>FrameRgn</c>).</summary>
        public void FrameRgn(Region region) => RgnShape(region ?? throw new ArgumentNullException(nameof(region)), 0);
        /// <summary>Paints a region (<c>PaintRgn</c>).</summary>
        public void PaintRgn(Region region) => RgnShape(region ?? throw new ArgumentNullException(nameof(region)), 1);
        /// <summary>Erases a region (<c>EraseRgn</c>).</summary>
        public void EraseRgn(Region region) => RgnShape(region ?? throw new ArgumentNullException(nameof(region)), 2);
        /// <summary>Inverts a region (<c>InvertRgn</c>).</summary>
        public void InvertRgn(Region region) => RgnShape(region ?? throw new ArgumentNullException(nameof(region)), 3);
        /// <summary>Fills a region with <paramref name="pattern"/> (<c>FillRgn</c>).</summary>
        public void FillRgn(Region region, QuickDrawPattern pattern)
        {
            ArgumentNullException.ThrowIfNull(region);
            FillPattern = pattern;
            RgnShape(region, 4);
        }

        private static (int h, int v)[] Points(IReadOnlyList<MacPoint> points)
        {
            ArgumentNullException.ThrowIfNull(points);
            return points.Select(p => ((int)p.H, (int)p.V)).ToArray();
        }

        // ---- bits ----

        /// <summary>
        /// Copies <paramref name="sourceRect"/> of <paramref name="source"/> to <paramref name="destinationRect"/>,
        /// scaling to fit, through <paramref name="mode"/>, the optional <paramref name="mask"/> and the clip region
        /// (<c>CopyBits</c>).
        /// </summary>
        public void CopyBits(PixMap source, MacRect sourceRect, MacRect destinationRect, TransferMode mode, Region? mask = null)
        {
            ArgumentNullException.ThrowIfNull(source);
            CopyBits(source, PictRect.From(sourceRect), PictRect.From(destinationRect), (int)mode, mask);
        }

        // ---- text ----

        internal int FontId, Face, Size, TxMode = TransferModes.SrcOr;
        internal int SpaceExtraFixed, ChExtra, InterCharSpacing;
        internal bool FractEnable, FScaleDisable;
        internal (int h, int v) TextNumer = (1, 1), TextDenom = (1, 1);

        /// <summary>The font family number (<c>TextFont</c>): 0 the system font, 1 the application font.</summary>
        public int TextFont { get => FontId; set => FontId = value; }

        /// <summary>The text style (<c>TextFace</c>).</summary>
        public QuickDrawStyle TextFace { get => (QuickDrawStyle)Face; set => Face = (int)value; }

        /// <summary>The point size (<c>TextSize</c>); 0 is the font's default, 12.</summary>
        public int TextSize { get => Size; set => Size = value; }

        /// <summary>The text's transfer mode (<c>TextMode</c>).</summary>
        public TransferMode TextMode { get => (TransferMode)TxMode; set => TxMode = (int)value; }

        /// <summary>Extra width for each space (<c>SpaceExtra</c>).</summary>
        public Fixed SpaceExtra { get => new(SpaceExtraFixed); set => SpaceExtraFixed = value.Raw; }

        /// <summary>Whether the Font Manager uses fractional character widths (<c>SetFractEnable</c>).</summary>
        public bool FractionalWidths { get => FractEnable; set => FractEnable = value; }

        /// <summary>Whether the Font Manager avoids scaling bitmap fonts (<c>SetFScaleDisable</c>).</summary>
        public bool ScaleDisable { get => FScaleDisable; set => FScaleDisable = value; }

        /// <summary>Draws a string at the pen in the port's font, and moves the pen past it (<c>DrawString</c>). Characters outside Mac OS Roman draw as '?'.</summary>
        public void DrawString(string text)
        {
            ArgumentNullException.ThrowIfNull(text);
            DrawText(MacRoman.Encode(text));
        }

        /// <summary>Draws Mac OS Roman text at the pen, and moves the pen past it (<c>DrawText</c>).</summary>
        public void DrawText(ReadOnlySpan<byte> text) => DrawTextAt(text, PenH, PenV, FontId, FontId, null, movePen: true);

        /// <summary>Draws one character at the pen, and moves the pen past it (<c>DrawChar</c>).</summary>
        public void DrawChar(byte character) => DrawText([character]);

        // ---- the engine, in canvas pixels (the picture player maps its coordinates first) ----

        // Color QuickDraw resets the highlight bit after every drawing operation.
        internal void Done() => HilitePending = false;

        internal void RectShape(PictRect r, int verb)
        {
            if (r.IsEmpty) { Done(); return; }
            Shape(verb, () => RegionShapes.Rect(r), () => new[] { RegionShapes.FrameRect(r, PenWidth, PenHeight) }, true);
        }

        internal void RoundRectShape(PictRect r, int ovalWidth, int ovalHeight, int verb)
        {
            if (r.IsEmpty) { Done(); return; }
            int ow = ovalWidth, oh = ovalHeight;
            if (macOS9)
            {
                // Mac OS 9 clamps the corner to the rect; a zero corner is a plain rect.
                ow = Math.Clamp(ow, 0, r.Width);
                oh = Math.Clamp(oh, 0, r.Height);
                if (ow == 0 || oh == 0)
                {
                    Shape(verb, () => RegionShapes.Rect(r), () => new[] { RegionShapes.FrameRect(r, PenWidth, PenHeight) }, false);
                    return;
                }
            }
            Shape(verb, () => RegionShapes.RoundRect(r, ow, oh),
                () => RegionShapes.FrameRoundRectParts(r, ow, oh, PenWidth, PenHeight, macOS9), false);
        }

        internal void OvalShape(PictRect r, int verb)
        {
            if (r.IsEmpty) { Done(); return; }
            Shape(verb, () => RegionShapes.Oval(r), () => RegionShapes.FrameOvalParts(r, PenWidth, PenHeight, macOS9), false);
        }

        internal void ArcShape(PictRect r, int startAngle, int arcAngle, int verb)
        {
            if (r.IsEmpty) { Done(); return; }
            Shape(verb, () => RegionShapes.Arc(r, startAngle, arcAngle, macOS9),
                () => RegionShapes.FrameArcParts(r, startAngle, arcAngle, PenWidth, PenHeight, macOS9), false);
        }

        // Framing draws each edge as a line and does not close the polygon.
        internal void PolyShape((int h, int v)[] points, int verb)
        {
            if (points.Length < 2) { Done(); return; }
            if (verb == 0)
            {
                for (int i = 1; i < points.Length; i++)
                    PaintLine(points[i - 1].h, points[i - 1].v, points[i].h, points[i].v);
                Done();
                return;
            }
            Shape(verb, () => RegionShapes.Polygon(points), () => new[] { Region.Empty }, true);
        }

        internal void RgnShape(Region region, int verb) =>
            Shape(verb, () => region, () => new[] { RegionShapes.FrameRegion(region, PenWidth, PenHeight) }, true);

        // StdRgn: frame paints the frame with the pen, paint uses the pen pattern and mode, erase the background
        // pattern (patCopy), invert XORs with black (hilite when pending), fill the fill pattern (patCopy).
        // viaStretchBits: rects, regions and polygons (not ovals, round rects and arcs, which DrawArc draws itself).
        private void Shape(int verb, Func<Region> interior, Func<Region[]> frame, bool viaStretchBits)
        {
            var colors = Colors;
            // DrawArc (ovals, round rects, arcs) takes the pen mode with bit 3 forced and draws only pattern modes
            // 8-15, arithmetic modes 40-47 and hilite 58; any other mode (16-31, 49, 64 and up, ...) draws nothing
            // (ROM $FFC93E8C; Mac OS 9 the same after dropping bit 6).
            if (!viaStretchBits && verb <= 1)
            {
                int m = (macOS9 ? Mode & ~TransferModes.DitherCopy : Mode) | 8;
                if (!(m <= 15 || (m >= 40 && m <= 47) || m == 58)) { Done(); return; }
            }
            switch (verb)
            {
                case 0:
                    // (Mac OS 9 paints a crossed frame's two slabs one after the other.)
                    foreach (var part in frame())
                        Painter.FillRegion(canvas, part, ClipRegion, PnPat, PatternAlign, Mode, HilitePending, colors, viaStretchBits);
                    break;
                case 1: Painter.FillRegion(canvas, interior(), ClipRegion, PnPat, PatternAlign, Mode, HilitePending, colors, viaStretchBits); break;
                case 2: Painter.FillRegion(canvas, interior(), ClipRegion, BkPat, PatternAlign, TransferModes.PatCopy, false, colors, viaStretchBits); break;
                case 3: Painter.FillRegion(canvas, interior(), ClipRegion, QuickDrawPattern.Black, PatternAlign, TransferModes.PatXor, HilitePending, colors, viaStretchBits); break;
                case 4: Painter.FillRegion(canvas, interior(), ClipRegion, FillPat, PatternAlign, TransferModes.PatCopy, false, colors, viaStretchBits); break;
            }
            Done();
        }

        // StdLine paints the pen-swept region with the pen pattern; Boolean pen modes act as pattern modes.
        internal void PaintLine(int x1, int y1, int x2, int y2)
        {
            var region = RegionShapes.Line(x1, y1, x2, y2, PenWidth, PenHeight);
            int mode = Mode < TransferModes.Blend ? (Mode % 0x40) | 8 : Mode;
            Painter.FillRegion(canvas, region, ClipRegion, PnPat, PatternAlign, mode, HilitePending, Colors, x1 == x2 || y1 == y2);
        }

        // The mask and the clip together limit the copy.
        internal void CopyBits(PixMap source, PictRect sourceRect, PictRect destinationRect, int mode, Region? mask)
        {
            if (ClipRegion != null) mask = mask == null ? ClipRegion : mask.Intersect(ClipRegion);
            Bits.CopyBits(canvas, source, sourceRect, destinationRect, mode, mask, HilitePending, Colors, Options.PreserveAlpha);
            Done();
        }

        // StdText at (x, y) with the pen's fraction: bitmap fonts from the library when it has the family (or a stand-in the
        // Font Manager would use), else the outline text fallback, which gets fallbackFontId and fallbackName. With
        // movePen, the pen ends past the text.
        internal void DrawTextAt(ReadOnlySpan<byte> text, int x, int y, int fontId, int fallbackFontId, string? fallbackName, bool movePen)
        {
            if (text.Length == 0) { Done(); return; }
            int mode = TxMode;
            // Mac OS 9 draws transparent and ditherCopy text as srcOr (grayishTextOr stays the gray srcOr below).
            if (macOS9 && (mode == TransferModes.Transparent || mode == TransferModes.DitherCopy))
                mode = TransferModes.SrcOr;
            int advance = 0, startFrac = PenFrac & 0xFFFF;
            if (Options.Fonts is { } library)
            {
                var font = FontManager.Swap(library, fontId, Size, Face, TextNumer, TextDenom, SpaceExtraFixed, FractEnable,
                    FScaleDisable, macOS9);
                if (font != null)
                {
                    int charExtra = unchecked(((short)ChExtra << 4) + InterCharSpacing);
                    int frac = mode == TransferModes.GrayishTextOr
                        ? GrayishText(font, text, x, y, charExtra, out advance)
                        : TextDrawer.Draw(canvas, font, text, x, y, PenFrac, charExtra, mode, ClipRegion, HilitePending, Colors, out advance);
                    PenFrac = frac;
                    Moved();
                    Done();
                    return;
                }
            }
            if (Options.TextFallback is { } fallback)
            {
                var mask = fallback.Render(MacRoman.Decode(text), new TextFallbackStyle(fallbackFontId, Face, Size, fallbackName));
                if (mask != null && mask.Width > 0 && mask.Height > 0)
                    Painter.FillMask(canvas, x - mask.OriginX, y - mask.OriginY, mask.Width, mask.Height, mask.Bits,
                        ClipRegion, mode, HilitePending, Colors);
                if (mask != null) advance = (int)Math.Round(mask.Advance * 65536.0);
            }
            // (The picture player keeps its fraction across fallback text; a port's pen moves by the fallback's advance.)
            if (movePen) PenFrac = (startFrac + advance) & 0xFFFF;
            Moved();
            Done();

            void Moved()
            {
                if (!movePen) return;
                long pen = ((long)x << 16) + startFrac + advance;
                (PenH, PenV) = ((int)(pen >> 16), y);
            }
        }

        // grayishTextOr on a color port (GetGray): the realized midpoint of the fore and back colors (16-bit
        // components averaged, + 2 below 0x8000) drawn srcOr when it is nearer the midpoint than half its distance to
        // either color; else the text srcOr with the gray pattern patBic over pen.h .. + the StdTxMeas width (integer,
        // unscaled), pen.v - ascent .. + descent - GetFontInfo's metrics: the strike's, + 1 / + shadow for shadowed
        // text, scaled by the Font Manager's stretch (rounded half up) but not by the text ratio.
        private int GrayishText(FontSelection font, ReadOnlySpan<byte> text, int x, int y, int charExtra, out int advance)
        {
            (int r, int g, int b) Wide(RgbaColor c) => (c.R * 257, c.G * 257, c.B * 257);
            var fg = Wide(Fore);
            var bk = Wide(Back);
            int Mid(int a, int b) { int m = (a + b) >> 1; return m < 0x8000 ? m + 2 : m; }
            (int r, int g, int b) mid = (Mid(fg.r, bk.r), Mid(fg.g, bk.g), Mid(fg.b, bk.b));
            var gray = new RgbaColor((byte)(mid.r >> 8), (byte)(mid.g >> 8), (byte)(mid.b >> 8));
            var grayWide = Wide(gray);
            int Distance((int r, int g, int b) a, (int r, int g, int b) b) =>
                Math.Max(Math.Abs(a.r - b.r), Math.Max(Math.Abs(a.g - b.g), Math.Abs(a.b - b.b)));
            if (Distance(grayWide, mid) < Distance(grayWide, bk) / 2 && Distance(grayWide, mid) < Distance(grayWide, fg) / 2)
                return TextDrawer.Draw(canvas, font, text, x, y, PenFrac, charExtra, TransferModes.SrcOr, ClipRegion,
                    HilitePending, new PortColors(gray, Back, Op, Hilite, macOS9, device), out advance);
            int width = (short)(TextDrawer.Measure(font, text, charExtra) >> 16);
            int ascent = font.Ascent, descent = font.Descent;
            if (font.Shadow != 0) (ascent, descent) = (ascent + 1, descent + (byte)font.Shadow);
            if (font.Numer != font.Denom)
            {
                uint n = (ushort)font.Numer.v, d = (ushort)font.Denom.v;
                ascent = (int)(((uint)ascent * n + d / 2) / d);
                descent = (int)(((uint)descent * n + d / 2) / d);
            }
            int frac = TextDrawer.Draw(canvas, font, text, x, y, PenFrac, charExtra, TransferModes.SrcOr, ClipRegion,
                HilitePending, Colors, out advance);
            var box = new PictRect(y - ascent, x, y + descent, x + width);
            Painter.FillRegion(canvas, RegionShapes.Rect(box), ClipRegion, Gray, PatternAlign, TransferModes.PatBic, false, Colors, true);
            return frac;
        }

        private static readonly QuickDrawPattern Gray = QuickDrawPattern.FromMono(new byte[] { 0xAA, 0x55, 0xAA, 0x55, 0xAA, 0x55, 0xAA, 0x55 });
    }
}
