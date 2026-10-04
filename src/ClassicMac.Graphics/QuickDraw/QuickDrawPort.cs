using System;
using System.Collections.Generic;
using System.Linq;
using ClassicMac.Core;

namespace ClassicMac.Graphics.QuickDraw;

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

/// <summary>A font's metrics as <c>GetFontInfo</c> gives them, in pixels.</summary>
public readonly record struct FontInfo(int Ascent, int Descent, int WidMax, int Leading);

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

    /// <summary>The port's rectangle in its own coordinates: the canvas, its top-left at the origin (<c>portRect</c>).</summary>
    public MacRect PortRect => new((short)OriginV, (short)OriginH, (short)(OriginV + Math.Min(canvas.Height, short.MaxValue)),
        (short)(OriginH + Math.Min(canvas.Width, short.MaxValue)));

    // The local coordinates of the canvas's top-left pixel (SetOrigin). The public routines take local coordinates;
    // the engine below works in canvas pixels.
    internal int OriginH, OriginV;

    /// <summary>
    /// Gives the canvas's top-left pixel the local coordinates (<paramref name="h"/>, <paramref name="v"/>)
    /// (<c>SetOrigin</c>). The clip region, the pen and the pattern alignment keep their local coordinates, so on the
    /// canvas they move, and patterns shift with the origin.
    /// </summary>
    public void SetOrigin(int h, int v)
    {
        ClipRegion = ClipRegion?.Offset(OriginH - h, OriginV - v);   // the same local region, elsewhere on the canvas
        (OriginH, OriginV) = (h, v);
    }

    internal PictRect ToCanvas(MacRect r) => new(r.Top - OriginV, r.Left - OriginH, r.Bottom - OriginV, r.Right - OriginH);
    private Region ToCanvas(Region r) => r.Offset(-OriginH, -OriginV);

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
    internal int PenH, PenV, PenFrac = 0x8000;                // local coordinates; the fraction is pnLocHFrac
    internal int PenVis;                                      // pnVis: drawing is hidden while negative
    internal Region? ClipRegion;                              // canvas pixels (local clip less the origin)
    internal (int h, int v) PatternAlign;                     // patAlign

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

    /// <summary>The clip region, in local coordinates; null draws everywhere on the canvas (<c>SetClip</c>, <c>ClipRect</c>).</summary>
    public Region? Clip
    {
        get => ClipRegion?.Offset(OriginH, OriginV);
        set => ClipRegion = value == null ? null : ToCanvas(value);
    }

    /// <summary>
    /// Hides the pen (<c>HidePen</c>): until as many <see cref="ShowPen"/> calls, lines, shapes, text and CopyBits
    /// into the port draw nothing, though the pen still moves.
    /// </summary>
    public void HidePen() => PenVis--;

    /// <summary>The pen's visibility (<c>pnVis</c>): drawing is hidden while it is negative.</summary>
    public int PenVisibility => PenVis;

    // OpenRgn's and OpenPoly's recordings, while open (quickdraw.md §2.26, §2.27).
    private RegionRecording? regionRecording;
    private PolygonRecording? polygonRecording;

    /// <summary>
    /// Starts recording a region (<c>OpenRgn</c>, quickdraw.md §2.26) and hides the pen: lines, and the frames of
    /// rects, ovals, round rects, regions and polygons, add their inversion points until <see cref="CloseRgn"/>. Opening
    /// again starts over and hides the pen once more.
    /// </summary>
    public void OpenRgn()
    {
        regionRecording = new RegionRecording(macOS9);
        HidePen();
    }

    /// <summary>
    /// Ends the region recording and shows the pen (<c>CloseRgn</c>): the region the points make (local). After an
    /// overflow the ROM gives an empty region and Mac OS 9 null (its destination is left as it was); null without a
    /// recording.
    /// </summary>
    public Region? CloseRgn()
    {
        if (regionRecording is not { } recording)
        {
            return null;
        }

        regionRecording = null;
        ShowPen();
        return recording.Close();
    }

    /// <summary>
    /// Starts recording a polygon (<c>OpenPoly</c>, quickdraw.md §2.27) and hides the pen: lines, and FramePoly's edges,
    /// add their ends until <see cref="ClosePoly"/>. Lines go to an open polygon rather than an open region.
    /// </summary>
    public void OpenPoly()
    {
        HidePen();
        polygonRecording = new PolygonRecording();
    }

    /// <summary>Ends the polygon recording and shows the pen (<c>ClosePoly</c>): the polygon, not closed; null without a recording.</summary>
    public Polygon? ClosePoly()
    {
        if (polygonRecording is not { } recording)
        {
            return null;
        }

        polygonRecording = null;
        ShowPen();
        return recording.Close();
    }

    // A line to an open polygon, else to an open region (DoLine).
    private void RecordLine(int h1, int v1, int h2, int v2)
    {
        if (polygonRecording != null)
        {
            polygonRecording.Line(h1, v1, h2, v2);
        }
        else
        {
            regionRecording?.Line(h1, v1, h2, v2);
        }
    }

    /// <summary>Undoes one <see cref="HidePen"/> (<c>ShowPen</c>).</summary>
    public void ShowPen() => PenVis++;

    /// <summary>The pen back to 1 × 1, patCopy, black (<c>PenNormal</c>).</summary>
    public void PenNormal() => (PenWidth, PenHeight, Mode, PnPat) = (1, 1, TransferModes.PatCopy, QuickDrawPattern.Black);

    // The pen's fraction (pnLocHFrac) is kept for text; MoveTo, Move and the line routines reset it to 1/2 (ROM, Mac OS 9).

    /// <summary>Moves the pen to (<paramref name="h"/>, <paramref name="v"/>) without drawing (<c>MoveTo</c>).</summary>
    public void MoveTo(int h, int v) => (PenH, PenV, PenFrac) = (h, v, 0x8000);

    /// <summary>Moves the pen by (<paramref name="dh"/>, <paramref name="dv"/>) without drawing (<c>Move</c>).</summary>
    public void Move(int dh, int dv) => MoveTo(PenH + dh, PenV + dv);

    /// <summary>Draws a line from the pen to (<paramref name="h"/>, <paramref name="v"/>) and moves the pen there (<c>LineTo</c>).</summary>
    public void LineTo(int h, int v)
    {
        RecordLine(PenH, PenV, h, v);
        PaintLine(PenH - OriginH, PenV - OriginV, h - OriginH, v - OriginV);
        MoveTo(h, v);
        Done();
    }

    /// <summary>Draws a line from the pen by (<paramref name="dh"/>, <paramref name="dv"/>) (<c>Line</c>).</summary>
    public void Line(int dh, int dv) => LineTo(PenH + dh, PenV + dv);

    // ---- shapes ----

    /// <summary>Outlines a rectangle with the pen (<c>FrameRect</c>).</summary>
    public void FrameRect(MacRect rect)
    {
        regionRecording?.Rect(rect);
        RectShape(ToCanvas(rect), 0);
    }
    /// <summary>Fills a rectangle with the pen pattern and mode (<c>PaintRect</c>).</summary>
    public void PaintRect(MacRect rect) => RectShape(ToCanvas(rect), 1);
    /// <summary>Fills a rectangle with the background pattern (<c>EraseRect</c>).</summary>
    public void EraseRect(MacRect rect) => RectShape(ToCanvas(rect), 2);
    /// <summary>Inverts a rectangle's pixels (<c>InvertRect</c>).</summary>
    public void InvertRect(MacRect rect) => RectShape(ToCanvas(rect), 3);
    /// <summary>Fills a rectangle with <paramref name="pattern"/> (<c>FillRect</c>).</summary>
    public void FillRect(MacRect rect, QuickDrawPattern pattern) { FillPattern = pattern; RectShape(ToCanvas(rect), 4); }

    /// <summary>Outlines the oval inscribed in a rectangle (<c>FrameOval</c>).</summary>
    public void FrameOval(MacRect rect)
    {
        regionRecording?.Shape(Region.Oval(rect));
        OvalShape(ToCanvas(rect), 0);
    }
    /// <summary>Paints an oval (<c>PaintOval</c>).</summary>
    public void PaintOval(MacRect rect) => OvalShape(ToCanvas(rect), 1);
    /// <summary>Erases an oval (<c>EraseOval</c>).</summary>
    public void EraseOval(MacRect rect) => OvalShape(ToCanvas(rect), 2);
    /// <summary>Inverts an oval (<c>InvertOval</c>).</summary>
    public void InvertOval(MacRect rect) => OvalShape(ToCanvas(rect), 3);
    /// <summary>Fills an oval with <paramref name="pattern"/> (<c>FillOval</c>).</summary>
    public void FillOval(MacRect rect, QuickDrawPattern pattern) { FillPattern = pattern; OvalShape(ToCanvas(rect), 4); }

    /// <summary>Outlines a rounded rectangle with corner ovals <paramref name="ovalWidth"/> × <paramref name="ovalHeight"/> (<c>FrameRoundRect</c>).</summary>
    public void FrameRoundRect(MacRect rect, int ovalWidth, int ovalHeight)
    {
        regionRecording?.Shape(Region.RoundRect(rect, ovalWidth, ovalHeight));
        RoundRectShape(ToCanvas(rect), ovalWidth, ovalHeight, 0);
    }
    /// <summary>Paints a rounded rectangle (<c>PaintRoundRect</c>).</summary>
    public void PaintRoundRect(MacRect rect, int ovalWidth, int ovalHeight) => RoundRectShape(ToCanvas(rect), ovalWidth, ovalHeight, 1);
    /// <summary>Erases a rounded rectangle (<c>EraseRoundRect</c>).</summary>
    public void EraseRoundRect(MacRect rect, int ovalWidth, int ovalHeight) => RoundRectShape(ToCanvas(rect), ovalWidth, ovalHeight, 2);
    /// <summary>Inverts a rounded rectangle (<c>InvertRoundRect</c>).</summary>
    public void InvertRoundRect(MacRect rect, int ovalWidth, int ovalHeight) => RoundRectShape(ToCanvas(rect), ovalWidth, ovalHeight, 3);
    /// <summary>Fills a rounded rectangle with <paramref name="pattern"/> (<c>FillRoundRect</c>).</summary>
    public void FillRoundRect(MacRect rect, int ovalWidth, int ovalHeight, QuickDrawPattern pattern)
    {
        FillPattern = pattern;
        RoundRectShape(ToCanvas(rect), ovalWidth, ovalHeight, 4);
    }

    /// <summary>Outlines an arc of the oval in <paramref name="rect"/>, from <paramref name="startAngle"/> (degrees clockwise from 12 o'clock) through <paramref name="arcAngle"/> (<c>FrameArc</c>).</summary>
    public void FrameArc(MacRect rect, int startAngle, int arcAngle) => ArcShape(ToCanvas(rect), startAngle, arcAngle, 0);
    /// <summary>Paints a wedge of an oval (<c>PaintArc</c>).</summary>
    public void PaintArc(MacRect rect, int startAngle, int arcAngle) => ArcShape(ToCanvas(rect), startAngle, arcAngle, 1);
    /// <summary>Erases a wedge of an oval (<c>EraseArc</c>).</summary>
    public void EraseArc(MacRect rect, int startAngle, int arcAngle) => ArcShape(ToCanvas(rect), startAngle, arcAngle, 2);
    /// <summary>Inverts a wedge of an oval (<c>InvertArc</c>).</summary>
    public void InvertArc(MacRect rect, int startAngle, int arcAngle) => ArcShape(ToCanvas(rect), startAngle, arcAngle, 3);
    /// <summary>Fills a wedge of an oval with <paramref name="pattern"/> (<c>FillArc</c>).</summary>
    public void FillArc(MacRect rect, int startAngle, int arcAngle, QuickDrawPattern pattern)
    {
        FillPattern = pattern;
        ArcShape(ToCanvas(rect), startAngle, arcAngle, 4);
    }

    /// <summary>Draws lines from each point of a polygon to the next, not closing it (<c>FramePoly</c>).</summary>
    public void FramePoly(IReadOnlyList<MacPoint> points)
    {
        ArgumentNullException.ThrowIfNull(points);
        for (int i = 1; i < points.Count; i++)
        {
            RecordLine(points[i - 1].H, points[i - 1].V, points[i].H, points[i].V);       // no closing edge
        }

        PolyShape(Points(points), 0);
        PenFrac = 0x8000;                                     // the line routine's reset
    }
    /// <summary>Paints a polygon (<c>PaintPoly</c>).</summary>
    public void PaintPoly(IReadOnlyList<MacPoint> points) => PolyShape(Points(points), 1);
    /// <summary>Erases a polygon (<c>ErasePoly</c>).</summary>
    public void ErasePoly(IReadOnlyList<MacPoint> points) => PolyShape(Points(points), 2);
    /// <summary>Inverts a polygon (<c>InvertPoly</c>).</summary>
    public void InvertPoly(IReadOnlyList<MacPoint> points) => PolyShape(Points(points), 3);
    /// <summary>Fills a polygon with <paramref name="pattern"/> (<c>FillPoly</c>).</summary>
    public void FillPoly(IReadOnlyList<MacPoint> points, QuickDrawPattern pattern) { FillPattern = pattern; PolyShape(Points(points), 4); }

    /// <summary>Outlines a region with the pen, inside its edge (<c>FrameRgn</c>).</summary>
    public void FrameRgn(Region region)
    {
        ArgumentNullException.ThrowIfNull(region);
        regionRecording?.Shape(region);
        RgnShape(ToCanvas(region), 0);
    }
    /// <summary>Paints a region (<c>PaintRgn</c>).</summary>
    public void PaintRgn(Region region) => RgnShape(ToCanvas(region ?? throw new ArgumentNullException(nameof(region))), 1);
    /// <summary>Erases a region (<c>EraseRgn</c>).</summary>
    public void EraseRgn(Region region) => RgnShape(ToCanvas(region ?? throw new ArgumentNullException(nameof(region))), 2);
    /// <summary>Inverts a region (<c>InvertRgn</c>).</summary>
    public void InvertRgn(Region region) => RgnShape(ToCanvas(region ?? throw new ArgumentNullException(nameof(region))), 3);
    /// <summary>Fills a region with <paramref name="pattern"/> (<c>FillRgn</c>).</summary>
    public void FillRgn(Region region, QuickDrawPattern pattern)
    {
        ArgumentNullException.ThrowIfNull(region);
        FillPattern = pattern;
        RgnShape(ToCanvas(region), 4);
    }

    private (int h, int v)[] Points(IReadOnlyList<MacPoint> points)
    {
        ArgumentNullException.ThrowIfNull(points);
        return points.Select(p => (p.H - OriginH, p.V - OriginV)).ToArray();
    }

    // ---- bits ----

    /// <summary>
    /// Copies <paramref name="sourceRect"/> of <paramref name="source"/> (in the source's coordinates) to
    /// <paramref name="destinationRect"/> (local), scaling to fit, through <paramref name="mode"/>, the optional
    /// <paramref name="mask"/> (local) and the clip region (<c>CopyBits</c>).
    /// </summary>
    public void CopyBits(PixMap source, MacRect sourceRect, MacRect destinationRect, TransferMode mode, Region? mask = null)
    {
        ArgumentNullException.ThrowIfNull(source);
        CopyBits(source, PictRect.From(sourceRect), ToCanvas(destinationRect), (int)mode, mask == null ? null : ToCanvas(mask));
    }

    /// <summary>
    /// Scrolls <paramref name="rect"/> (local) by <paramref name="dh"/>, <paramref name="dv"/> (<c>ScrollRect</c>,
    /// quickdraw.md §2.25): the pixels of the rect inside the screen and the clip region move by CopyBits srcCopy in
    /// black on white (so nothing is colorized), cut to where they land inside the same area. With
    /// <paramref name="updateRegion"/>, what they leave is erased with the port's background and returned (local);
    /// without it nothing is erased and null is returned. A hidden pen, or no move, does nothing (an empty region).
    /// </summary>
    public Region? ScrollRect(MacRect rect, int dh, int dv, bool updateRegion)
    {
        if (PenVis < 0 || (dh == 0 && dv == 0))
        {
            return updateRegion ? Region.Empty : null;
        }

        // srcRgn = the rect ∩ visRgn (the canvas) ∩ clipRgn; maskRgn = srcRgn ∩ srcRgn moved.
        var source = Region.FromRect(ToCanvas(rect)).Intersect(Region.FromRect(new PictRect(0, 0, canvas.Height, canvas.Width)));
        if (ClipRegion != null)
        {
            source = source.Intersect(ClipRegion);
        }

        var mask = source.Intersect(source.Offset(dh, dv));
        var (fore, back) = (ForeColor, BackColor);
        (ForeColor, BackColor) = (RgbColor.Black, RgbColor.White);
        var from = ToCanvas(rect);
        var to = new PictRect(from.Top + dv, from.Left + dh, from.Bottom + dv, from.Right + dh);
        CopyBits(PixMap.FromBitmap(canvas), from, to, TransferModes.SrcCopy, mask);
        (ForeColor, BackColor) = (fore, back);
        if (!updateRegion)
        {
            return null;
        }

        var update = source.Difference(mask);
        RgnShape(update, 2);
        return update.Offset(OriginH, OriginV);
    }

    /// <summary>
    /// Copies <paramref name="sourceRect"/> of <paramref name="source"/> to <paramref name="destinationRect"/> (local)
    /// where the 1-bit <paramref name="mask"/>'s <paramref name="maskRect"/> is set, both stretched to the destination
    /// (<c>CopyMask</c>): srcCopy with the port's colours, inside the clip region. A hidden pen does not stop it.
    /// </summary>
    public void CopyMask(PixMap source, PixMap mask, MacRect sourceRect, MacRect maskRect, MacRect destinationRect)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(mask);
        var destination = ToCanvas(destinationRect);
        // [ClassicMac: the mask is stretched by its own StretchBits, as the ROM's fallback stretches it; CopyMask's
        // single pass is taken to sample both alike.]
        var region = StretchedMask(mask, PictRect.From(maskRect), destination);
        if (ClipRegion != null)
        {
            region = region.Intersect(ClipRegion);
        }

        var source1 = PictRect.From(sourceRect);
        var colors = Colors;
        // Mac OS 9's general stretch path (a scaled 1-bit source onto a 1-bit screen, with a 1-bit mask) applies its
        // pixel-value colorizing op in RGB and maps the result back, so with any colours but black on white every
        // masked pixel comes out black [Code] [Verified]. The ROM colorizes on pixel values.
        if (macOS9 && device is { Depth: 1 } && source.PixelSize == 1 && mask.PixelSize == 1
            && (source1.Width != destination.Width || source1.Height != destination.Height)
            && (colors.FgIndex != 1 || colors.BkIndex != 0))
        {
            var black = new PortColors(new RgbaColor(0, 0, 0), new RgbaColor(255, 255, 255), (0, 0, 0), Hilite, macOS9, device);
            Painter.FillRegion(canvas, region, null, QuickDrawPattern.Black, Align, TransferModes.PatCopy, false, black, true, OriginV);
            Done();
            return;
        }
        Bits.CopyBits(canvas, source, PictRect.From(sourceRect), destination, TransferModes.SrcCopy, region, HilitePending, Colors,
            Options.PreserveAlpha);
        Done();
    }

    // A 1-bit mask's rect stretched to the destination (canvas pixels) as a region of its set pixels.
    private Region StretchedMask(PixMap mask, PictRect maskRect, PictRect destination)
    {
        int w = destination.Width, h = destination.Height;
        if (w <= 0 || h <= 0)
        {
            return Region.Empty;
        }

        var scratch = new RgbaBitmap(w, h);
        var colors = new PortColors(new RgbaColor(0, 0, 0), new RgbaColor(255, 255, 255), (0, 0, 0), new RgbaColor(0, 0, 0), macOS9);
        Bits.CopyBits(scratch, mask, maskRect, new PictRect(0, 0, h, w), TransferModes.SrcCopy, null, false, colors, false);
        var set = new bool[w * h];
        for (int y = 0; y < h; y++)
        {
            for (int x = 0; x < w; x++)
            {
                var c = scratch[x, y];
                set[y * w + x] = c.A != 0 && c.R == 0 && c.G == 0 && c.B == 0;
            }
        }

        var region = Region.Empty;
        for (int y = 0; y < h; y++)
        {
            for (int x = 0; x < w; x++)
            {
                if (!set[y * w + x])
                {
                    continue;
                }

                int end = x;
                while (end < w && set[y * w + end])
                {
                    end++;
                }

                region = region.Union(Region.FromRect(new PictRect(destination.Top + y, destination.Left + x, destination.Top + y + 1, destination.Left + end)));
                x = end;
            }
        }

        return region;
    }

    // GetGray: the realized midpoint of the background and foreground (16-bit components averaged, + 2 below $8000),
    // good when it is nearer the midpoint than half its distance to either colour; the colours are first realized
    // on the screen (Color2Index, Index2Color).
    internal bool GetGray(RgbColor back, RgbColor fore, out RgbColor gray)
    {
        (int r, int g, int b) Realize((int r, int g, int b) c) => device == null
            ? ((c.r >> 8) * 257, (c.g >> 8) * 257, (c.b >> 8) * 257)
            : device.Index2Color16(device.Color2Index(c.r, c.g, c.b));
        var bk = Realize((back.Red, back.Green, back.Blue));
        var fg = Realize((fore.Red, fore.Green, fore.Blue));
        int Mid(int a, int b)
        {
            int m = (a + b) >> 1;
            return m < 0x8000 ? m + 2 : m;
        }
        (int r, int g, int b) mid = (Mid(fg.r, bk.r), Mid(fg.g, bk.g), Mid(fg.b, bk.b));
        var real = Realize(mid);
        int Distance((int r, int g, int b) a, (int r, int g, int b) b) =>
            Math.Max(Math.Abs(a.r - b.r), Math.Max(Math.Abs(a.g - b.g), Math.Abs(a.b - b.b)));
        gray = new RgbColor((ushort)real.r, (ushort)real.g, (ushort)real.b);
        return Distance(real, mid) < Distance(real, bk) / 2 && Distance(real, mid) < Distance(real, fg) / 2;
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

    /// <summary>
    /// The point size (<c>TextSize</c>); 0 is the font's default, 12. Setting it clears the character extra (the ROM
    /// always; Mac OS 9 when the size changes).
    /// </summary>
    public int TextSize
    {
        get => Size;
        set
        {
            if (!macOS9 || value != Size)
            {
                ChExtra = 0;
            }

            Size = value;
        }
    }

    /// <summary>
    /// Extra width after each character but spaces, <paramref name="extra"/> pixels at the current size (<c>CharExtra</c>).
    /// The port keeps it per point (4.12), so a later size scales it; <see cref="TextSize"/> clears it.
    /// </summary>
    public void CharExtra(Fixed extra)
    {
        // chExtra = FixDiv(extra, size) >> 4 (the ROM rounds the divide; Mac OS 9 truncates it and clamps to
        // +-$7FFF). Size 0 is the system font size, 12 [ClassicMac: the system font size is taken as 12].
        long size = (Size != 0 ? Size : 12) << 16;
        long quotient = ((long)extra.Raw << 16) / size;
        if (!macOS9)
        {
            long remainder = ((long)extra.Raw << 16) % size;
            if (Math.Abs(remainder) * 2 >= size)
            {
                quotient += Math.Sign(remainder);   // [ClassicMac: FixDiv's tie rule not checked]
            }

            ChExtra = (short)(quotient >> 4);
        }
        else
        {
            ChExtra = (int)Math.Clamp(quotient >> 4, -0x7FFF, 0x7FFF);
        }
    }

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
    public void DrawText(ReadOnlySpan<byte> text) => DrawTextAt(text, PenH - OriginH, PenV - OriginV, FontId, FontId, null, movePen: true);

    /// <summary>Draws one character at the pen, and moves the pen past it (<c>DrawChar</c>).</summary>
    public void DrawChar(byte character) => DrawText([character]);

    /// <summary>
    /// The width of Mac OS Roman text in the port's font, in whole pixels (<c>TextWidth</c>): the characters' widths,
    /// with style, space and character extras, truncated, then stretched by the Font Manager to the requested size and
    /// truncated again. 0 when no bitmap font draws the text.
    /// </summary>
    public int TextWidth(ReadOnlySpan<byte> text)
    {
        if (Select((1, 1), (1, 1)) is not { } font)
        {
            return 0;
        }

        int width = TextDrawer.Measure(font, text, unchecked(((short)ChExtra << 4) + InterCharSpacing)) >> 16;
        if (font.Numer == font.Denom)
        {
            return width;
        }
        // The ROM multiplies and divides unsigned (MULU, DIVU); Mac OS 9 signed.
        return macOS9
            ? (int)((long)width * font.Numer.h / font.Denom.h)
            : (short)((uint)(ushort)width * (ushort)font.Numer.h / (ushort)font.Denom.h);
    }

    /// <summary>The width of a string (<c>StringWidth</c>); see <see cref="TextWidth"/>.</summary>
    public int StringWidth(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        return TextWidth(MacRoman.Encode(text));
    }

    /// <summary>The width of one character (<c>CharWidth</c>); see <see cref="TextWidth"/>.</summary>
    public int CharWidth(byte character) => TextWidth([character]);

    /// <summary>
    /// The port font's metrics (<c>GetFontInfo</c>), from the Font Manager's output: widMax with the style's extra
    /// width, ascent and descent grown for shadow and outline, all stretched to the requested size (rounded).
    /// Zeros when no bitmap font draws the port's font.
    /// </summary>
    public FontInfo GetFontInfo()
    {
        if (Select((1, 1), (1, 1)) is not { } font)
        {
            return default;
        }

        int ascent = font.Ascent, descent = font.Descent, widMax = font.WidMax + (sbyte)font.Extra, leading = font.Leading;
        if (font.Shadow != 0)
        {
            (ascent, descent) = (ascent + 1, descent + (byte)font.Shadow);
        }

        if (font.Numer != font.Denom)
        {
            int Scale(int value, int numer, int denom) =>
                (int)(((uint)value * (ushort)numer + (uint)(ushort)denom / 2) / (ushort)denom);
            (ascent, descent, leading) = (Scale(ascent, font.Numer.v, font.Denom.v), Scale(descent, font.Numer.v, font.Denom.v),
                Scale(leading, font.Numer.v, font.Denom.v));
            widMax = Scale(widMax, font.Numer.h, font.Denom.h);
        }
        return new FontInfo(ascent, descent, widMax, leading);
    }

    // The Font Manager's choice for the port's text state at a text scale.
    private FontSelection? Select((int h, int v) numer, (int h, int v) denom) =>
        Options.Fonts is { } library
            ? FontManager.Swap(library, FontId, Size, Face, numer, denom, SpaceExtraFixed, FractEnable, FScaleDisable, macOS9)
            : null;

    // ---- DrawPicture's save and restore ----

    // Everything DrawPicture saves at entry and restores at exit: the whole port record (pen, patterns, text, colours,
    // clip), patAlign, the character extras and the Font Manager's two settings. Not the highlight bit.
    internal sealed record State(RgbaColor Fore, RgbaColor Back, (ushort, ushort, ushort) Fore16, (ushort, ushort, ushort) Back16,
        QuickDrawPattern PnPat, QuickDrawPattern BkPat, QuickDrawPattern FillPat, int Mode, int PenWidth, int PenHeight,
        int PenH, int PenV, int PenFrac, int PenVis, Region? Clip, (int, int) PatternAlign, int FontId, int Face, int Size,
        int TxMode, int SpaceExtra, int ChExtra, int InterCharSpacing, bool FractEnable, bool FScaleDisable,
        (int, int) TextNumer, (int, int) TextDenom, RgbaColor Hilite, RgbColor Hilite16);

    internal State Save() => new(Fore, Back, Fore16, Back16, PnPat, BkPat, FillPat, Mode, PenWidth, PenHeight, PenH, PenV,
        PenFrac, PenVis, ClipRegion, PatternAlign, FontId, Face, Size, TxMode, SpaceExtraFixed, ChExtra, InterCharSpacing,
        FractEnable, FScaleDisable, TextNumer, TextDenom, Hilite, hilite16);

    internal void Restore(State s, bool hilite)
    {
        (Fore, Back, Fore16, Back16, PnPat, BkPat, FillPat, Mode) = (s.Fore, s.Back, s.Fore16, s.Back16, s.PnPat, s.BkPat, s.FillPat, s.Mode);
        (PenWidth, PenHeight, PenH, PenV, PenFrac, PenVis, ClipRegion, PatternAlign) =
            (s.PenWidth, s.PenHeight, s.PenH, s.PenV, s.PenFrac, s.PenVis, s.Clip, s.PatternAlign);
        (FontId, Face, Size, TxMode, SpaceExtraFixed, ChExtra, InterCharSpacing) =
            (s.FontId, s.Face, s.Size, s.TxMode, s.SpaceExtra, s.ChExtra, s.InterCharSpacing);
        (FractEnable, FScaleDisable, TextNumer, TextDenom) = (s.FractEnable, s.FScaleDisable, s.TextNumer, s.TextDenom);
        if (hilite)
        {
            (Hilite, hilite16) = (s.Hilite, s.Hilite16);
        }
    }

    // DrawPicture's starting state (the pen visibility and the Font Manager's settings are kept).
    internal void ResetForPicture(RgbaColor systemHilite)
    {
        (PnPat, FillPat, BkPat, Mode, PenH, PenV, PenFrac) =
            (QuickDrawPattern.Black, QuickDrawPattern.Black, QuickDrawPattern.White, TransferModes.PatCopy, 0, 0, 0x8000);
        (FontId, Face, Size, TxMode, SpaceExtraFixed, ChExtra, InterCharSpacing) = (0, 0, 0, TransferModes.SrcOr, 0, 0, 0);
        (Fore, Back, Fore16, Back16, Op) = (new RgbaColor(0, 0, 0), new RgbaColor(255, 255, 255), (0, 0, 0), (0xFFFF, 0xFFFF, 0xFFFF), (0, 0, 0));
        (PatternAlign, HilitePending) = ((0, 0), false);
        SetHilite(systemHilite);
    }

    // ---- the engine, in canvas pixels (the picture player maps its coordinates first) ----

    // Color QuickDraw resets the highlight bit after every drawing operation.
    internal void Done() => HilitePending = false;

    internal void RectShape(PictRect r, int verb)
    {
        if (r.IsEmpty)
        {
            Done();
            return;
        }
        Shape(verb, () => RegionShapes.Rect(r), () => new[] { RegionShapes.FrameRect(r, PenWidth, PenHeight) }, true);
    }

    internal void RoundRectShape(PictRect r, int ovalWidth, int ovalHeight, int verb)
    {
        if (r.IsEmpty)
        {
            Done();
            return;
        }
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
        if (r.IsEmpty)
        {
            Done();
            return;
        }
        Shape(verb, () => RegionShapes.Oval(r), () => RegionShapes.FrameOvalParts(r, PenWidth, PenHeight, macOS9), false);
    }

    internal void ArcShape(PictRect r, int startAngle, int arcAngle, int verb)
    {
        if (r.IsEmpty)
        {
            Done();
            return;
        }
        Shape(verb, () => RegionShapes.Arc(r, startAngle, arcAngle, macOS9),
            () => RegionShapes.FrameArcParts(r, startAngle, arcAngle, PenWidth, PenHeight, macOS9), false);
    }

    // Framing draws each edge as a line and does not close the polygon.
    internal void PolyShape((int h, int v)[] points, int verb)
    {
        if (points.Length < 2)
        {
            Done();
            return;
        }
        if (verb == 0)
        {
            for (int i = 1; i < points.Length; i++)
            {
                PaintLine(points[i - 1].h, points[i - 1].v, points[i].h, points[i].v);
            }

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
        if (PenVis < 0)
        {
            Done();
            return;
        }
        var colors = Colors;
        // DrawArc (ovals, round rects, arcs) takes the pen mode with bit 3 forced and draws only pattern modes
        // 8-15, arithmetic modes 40-47 and hilite 58; any other mode (16-31, 49, 64 and up, ...) draws nothing
        // (ROM $FFC93E8C; Mac OS 9 the same after dropping bit 6).
        if (!viaStretchBits && verb <= 1)
        {
            int m = (macOS9 ? Mode & ~TransferModes.DitherCopy : Mode) | 8;
            if (!(m <= 15 || (m >= 40 && m <= 47) || m == 58))
            {
                Done();
                return;
            }
        }
        switch (verb)
        {
            case 0:
                // (Mac OS 9 paints a crossed frame's two slabs one after the other.)
                foreach (var part in frame())
                {
                    Painter.FillRegion(canvas, part, ClipRegion, PnPat, Align, Mode, HilitePending, colors, viaStretchBits, OriginV);
                }

                break;
            case 1:
                Painter.FillRegion(canvas, interior(), ClipRegion, PnPat, Align, Mode, HilitePending, colors, viaStretchBits, OriginV);
                break;
            case 2:
                Painter.FillRegion(canvas, interior(), ClipRegion, BkPat, Align, TransferModes.PatCopy, false, colors, viaStretchBits, OriginV);
                break;
            case 3:
                Painter.FillRegion(canvas, interior(), ClipRegion, QuickDrawPattern.Black, Align, TransferModes.PatXor, HilitePending, colors, viaStretchBits, OriginV);
                break;
            case 4:
                Painter.FillRegion(canvas, interior(), ClipRegion, FillPat, Align, TransferModes.PatCopy, false, colors, viaStretchBits, OriginV);
                break;
        }
        Done();
    }

    // The pattern phase: local coordinates plus patAlign.
    private (int h, int v) Align => (PatternAlign.h + OriginH, PatternAlign.v + OriginV);

    // StdLine paints the pen-swept region with the pen pattern; Boolean pen modes act as pattern modes.
    internal void PaintLine(int x1, int y1, int x2, int y2)
    {
        if (PenVis < 0)
        {
            return;
        }

        var region = RegionShapes.Line(x1, y1, x2, y2, PenWidth, PenHeight);
        int mode = Mode < TransferModes.Blend ? (Mode % 0x40) | 8 : Mode;
        Painter.FillRegion(canvas, region, ClipRegion, PnPat, Align, mode, HilitePending, Colors, x1 == x2 || y1 == y2, OriginV);
    }

    // The mask and the clip together limit the copy.
    internal void CopyBits(PixMap source, PictRect sourceRect, PictRect destinationRect, int mode, Region? mask)
    {
        if (PenVis < 0)
        {
            Done();
            return;
        }
        if (ClipRegion != null)
        {
            mask = mask == null ? ClipRegion : mask.Intersect(ClipRegion);
        }

        Bits.CopyBits(canvas, source, sourceRect, destinationRect, mode, mask, HilitePending, Colors, Options.PreserveAlpha);
        Done();
    }

    // StdText at (x, y) with the pen's fraction: bitmap fonts from the library when it has the family (or a stand-in the
    // Font Manager would use), else the outline text fallback, which gets fallbackFontId and fallbackName. With
    // movePen, the pen ends past the text.
    internal void DrawTextAt(ReadOnlySpan<byte> text, int x, int y, int fontId, int fallbackFontId, string? fallbackName, bool movePen)
    {
        if (text.Length == 0)
        {
            Done();
            return;
        }
        int mode = TxMode;
        // Mac OS 9 draws transparent and ditherCopy text as srcOr (grayishTextOr stays the gray srcOr below).
        if (macOS9 && (mode == TransferModes.Transparent || mode == TransferModes.DitherCopy))
        {
            mode = TransferModes.SrcOr;
        }

        int advance = 0, startFrac = PenFrac & 0xFFFF;
        if (Options.Fonts is { } library)
        {
            var font = FontManager.Swap(library, fontId, Size, Face, TextNumer, TextDenom, SpaceExtraFixed, FractEnable,
                FScaleDisable, macOS9);
            if (font != null)
            {
                int charExtra = unchecked(((short)ChExtra << 4) + InterCharSpacing);
                if (PenVis < 0)
                {
                    // A hidden pen still moves.
                    advance = TextDrawer.Advance(font, text, charExtra);
                    PenFrac = (startFrac + advance) & 0xFFFF;
                    Moved();
                    Done();
                    return;
                }
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
            if (mask != null && mask.Width > 0 && mask.Height > 0 && PenVis >= 0)
            {
                Painter.FillMask(canvas, x - mask.OriginX, y - mask.OriginY, mask.Width, mask.Height, mask.Bits,
                    ClipRegion, mode, HilitePending, Colors);
            }

            if (mask != null)
            {
                advance = (int)Math.Round(mask.Advance * 65536.0);
            }
        }
        // (The picture player keeps its fraction across fallback text; a port's pen moves by the fallback's advance.)
        if (movePen)
        {
            PenFrac = (startFrac + advance) & 0xFFFF;
        }

        Moved();
        Done();

        void Moved()
        {
            if (!movePen)
            {
                return;
            }

            long pen = ((long)(x + OriginH) << 16) + startFrac + advance;
            // Mac OS 9 stops the pen at 32752.0.
            if (macOS9 && pen > 32752L << 16)
            {
                (pen, PenFrac) = (32752L << 16, 0);
            }

            (PenH, PenV) = ((int)(pen >> 16), y + OriginV);
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
        int Mid(int a, int b)
        {
            int m = (a + b) >> 1;
            return m < 0x8000 ? m + 2 : m;
        }
        (int r, int g, int b) mid = (Mid(fg.r, bk.r), Mid(fg.g, bk.g), Mid(fg.b, bk.b));
        var gray = new RgbaColor((byte)(mid.r >> 8), (byte)(mid.g >> 8), (byte)(mid.b >> 8));
        var grayWide = Wide(gray);
        int Distance((int r, int g, int b) a, (int r, int g, int b) b) =>
            Math.Max(Math.Abs(a.r - b.r), Math.Max(Math.Abs(a.g - b.g), Math.Abs(a.b - b.b)));
        if (Distance(grayWide, mid) < Distance(grayWide, bk) / 2 && Distance(grayWide, mid) < Distance(grayWide, fg) / 2)
        {
            return TextDrawer.Draw(canvas, font, text, x, y, PenFrac, charExtra, TransferModes.SrcOr, ClipRegion,
                HilitePending, new PortColors(gray, Back, Op, Hilite, macOS9, device), out advance);
        }

        int width = (short)(TextDrawer.Measure(font, text, charExtra) >> 16);
        int ascent = font.Ascent, descent = font.Descent;
        if (font.Shadow != 0)
        {
            (ascent, descent) = (ascent + 1, descent + (byte)font.Shadow);
        }

        if (font.Numer != font.Denom)
        {
            uint n = (ushort)font.Numer.v, d = (ushort)font.Denom.v;
            ascent = (int)(((uint)ascent * n + d / 2) / d);
            descent = (int)(((uint)descent * n + d / 2) / d);
        }
        int frac = TextDrawer.Draw(canvas, font, text, x, y, PenFrac, charExtra, TransferModes.SrcOr, ClipRegion,
            HilitePending, Colors, out advance);
        var box = new PictRect(y - ascent, x, y + descent, x + width);
        Painter.FillRegion(canvas, RegionShapes.Rect(box), ClipRegion, Gray, Align, TransferModes.PatBic, false, Colors, true, OriginV);
        return frac;
    }

    private static readonly QuickDrawPattern Gray = QuickDrawPattern.FromMono(new byte[] { 0xAA, 0x55, 0xAA, 0x55, 0xAA, 0x55, 0xAA, 0x55 });
}
