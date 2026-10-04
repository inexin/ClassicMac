using System;
using ClassicMac.Graphics;

namespace ClassicMac.Graphics.QuickDraw;

// Transfers a pattern, or a 1-bit mask, through a region onto the canvas. A pattern's phase is the port's local
// coordinates plus patAlign (align: the port origin plus patAlign, which the Origin opcodes accumulate): pixel
// (x, y) takes pattern bit ((x + align.h) & 7) of row ((y + align.v) & 7). The ROM leaves patAlign.v out of a
// pixel pattern's rows (pixPatRomV: the port origin's v alone); Mac OS 9 applies it. A pixel the picture never drew
// (alpha 0) reads as white, the erased background of a fresh port; pixels a mode leaves alone keep their alpha.
internal static class Painter
{
    private static readonly RgbaColor White = new RgbaColor(255, 255, 255);

    // viaStretchBits: the verb draws through StretchBits (rects, regions, polygons, horizontal and vertical lines),
    // not DrawArc / DrawLine's own slab code (ovals, round rects, arcs, slanted lines).
    public static void FillRegion(RgbaBitmap canvas, Region region, Region? clip, QuickDrawPattern pattern, (int h, int v) align,
        int mode, bool hilitePending, in PortColors colors, bool viaStretchBits, int pixPatRomV = 0)
    {
        var area = Visible(canvas, region, clip);
        if (area.IsEmpty)
        {
            return;
        }

        int m = TransferModes.Normalize(mode, hilitePending);
        bool colorPattern = pattern.Pixels != null || pattern.Rgb != null;
        if (colors.Device != null)
        {
            FillRegionOnDevice(canvas, area, pattern, align, m, colors, pixPatRomV);
            return;
        }

        // Hilite through a pattern whose rows are all solid: RgnBlt (a non-rectangular area) hilites the whole area
        // when any row is solid foreground; BitBlt (a rectangle) follows the pattern.
        // (ROM only: Mac OS 9 hilites through the pattern everywhere.)
        if (m == TransferModes.Hilite && !colors.MacOS9 && !colorPattern && viaStretchBits && !IsRectangle(area) &&
            Array.TrueForAll(pattern.Mono, row => row == 0 || row == 0xFF) && Array.IndexOf(pattern.Mono, (byte)0xFF) >= 0)
        {
            pattern = QuickDrawPattern.Black;
        }

        foreach (var r in area.Rectangles())
        {
            for (int y = r.Top; y < r.Bottom; y++)
            {
                for (int x = r.Left; x < r.Right; x++)
                {
                    var dst = ReadPixel(canvas, x, y);
                    bool write;
                    RgbaColor result;
                    if (colorPattern)
                    {
                        // Pixel patterns in Boolean modes act as pixel values with fore = all ones, back = 0:
                        // copy P, or d | P, xor d ^ P, bic d & ~P (P inverted first for the not modes).
                        var src = pattern.Rgb ?? PatternPixel(pattern.Pixels!, x + align.h, y + (colors.MacOS9 ? align.v : pixPatRomV));
                        if (TransferModes.IsArithmetic(m) || m == TransferModes.Hilite)
                        {
                            write = TransferModes.ApplyColor(m, src, dst, colors, out result);
                        }
                        else
                        {
                            if ((m & 4) != 0)
                            {
                                src = TransferModes.Invert(src);
                            }

                            result = (m & 3) switch
                            {
                                0 => src,
                                1 => new RgbaColor((byte)(dst.R | src.R), (byte)(dst.G | src.G), (byte)(dst.B | src.B)),
                                2 => new RgbaColor((byte)(dst.R ^ src.R), (byte)(dst.G ^ src.G), (byte)(dst.B ^ src.B)),
                                _ => new RgbaColor((byte)(dst.R & ~src.R), (byte)(dst.G & ~src.G), (byte)(dst.B & ~src.B)),
                            };
                            write = true;
                        }
                    }
                    else
                    {
                        bool bit = ((pattern.Mono[(y + align.v) & 7] >> (7 - ((x + align.h) & 7))) & 1) != 0;
                        write = TransferModes.ApplyBit(m, bit, dst, colors, out result);
                    }
                    if (write)
                    {
                        WritePixel(canvas, x, y, result);
                    }
                }
            }
        }
    }

    // A 1-bit mask (text) placed with its top-left at (left, top), transferred with a source mode.
    public static void FillMask(RgbaBitmap canvas, int left, int top, int width, int height, byte[] bits,
        Region? clip, int mode, bool hilitePending, in PortColors colors)
    {
        var area = Visible(canvas, Region.FromRect(new PictRect(top, left, top + height, left + width)), clip);
        if (area.IsEmpty)
        {
            return;
        }

        int m = TransferModes.Normalize(mode, hilitePending);
        foreach (var r in area.Rectangles())
        {
            for (int y = r.Top; y < r.Bottom; y++)
            {
                for (int x = r.Left; x < r.Right; x++)
                {
                    bool bit = bits[(y - top) * width + (x - left)] != 0;
                    if (colors.Device is { } device)
                    {
                        if (DeviceModes.Bit(m, bit, device.Read(canvas, x, y), colors, out int value))
                        {
                            device.Write(canvas, x, y, value);
                        }
                    }
                    else if (TransferModes.ApplyBit(m, bit, ReadPixel(canvas, x, y), colors, out var result))
                    {
                        WritePixel(canvas, x, y, result);
                    }
                }
            }
        }
    }

    // On an indexed or 16-bit screen: 1-bit patterns draw fg / bk indices; a pixel pattern's colors become device
    // values (Color2Index for indexed patterns, the inverse table for direct ones; an RGB pattern is PatDither's 2 x 2
    // cell, solid only at 32 bits) drawn with fg all ones and bk 0.
    private static void FillRegionOnDevice(RgbaBitmap canvas, Region area, QuickDrawPattern pattern, (int h, int v) align, int m,
        in PortColors colors, int pixPatRomV)
    {
        var device = colors.Device!;
        int[]? cell = pattern.Rgb != null ? device.PatDither(pattern.Rgb16) : null;
        var pixels = pattern.Rgb == null ? pattern.Pixels : null;
        foreach (var r in area.Rectangles())
        {
            for (int y = r.Top; y < r.Bottom; y++)
            {
                for (int x = r.Left; x < r.Right; x++)
                {
                    int dst = device.Read(canvas, x, y), value;
                    bool write;
                    if (cell != null)
                    {
                        int p = cell[(((y + align.v) & 1) << 1) | ((x + align.h) & 1)];
                        write = DeviceModes.PatternValue(m, p, device.ColorOf(p), dst, colors, out value);
                    }
                    else if (pixels != null)
                    {
                        var c = PatternPixel(pixels, x + align.h, y + (colors.MacOS9 ? align.v : pixPatRomV));
                        int p = pixels.IsDirect ? device.Lookup(c) : device.Color2Index(c);
                        write = DeviceModes.PatternValue(m, p, c, dst, colors, out value);
                    }
                    else
                    {
                        bool bit = ((pattern.Mono[(y + align.v) & 7] >> (7 - ((x + align.h) & 7))) & 1) != 0;
                        write = DeviceModes.Bit(m, bit, dst, colors, out value);
                    }
                    if (write)
                    {
                        device.Write(canvas, x, y, value);
                    }
                }
            }
        }
    }

    private static bool IsRectangle(Region r)
    {
        using var e = r.Rectangles().GetEnumerator();
        return e.MoveNext() && !e.MoveNext();
    }

    private static Region Visible(RgbaBitmap canvas, Region region, Region? clip)
    {
        var area = region.Intersect(Region.FromRect(new PictRect(0, 0, canvas.Height, canvas.Width)));
        return clip == null ? area : area.Intersect(clip);
    }

    private static RgbaColor PatternPixel(PixMap pm, int x, int y)
    {
        int w = Math.Max(1, pm.Width), h = Math.Max(1, pm.Height);
        return pm.GetPixel(((x % w) + w) % w, ((y % h) + h) % h);
    }

    public static RgbaColor ReadPixel(RgbaBitmap canvas, int x, int y)
    {
        int i = (y * canvas.Width + x) * 4;
        var p = canvas.Pixels;
        return p[i + 3] == 0 ? White : new RgbaColor(p[i], p[i + 1], p[i + 2], p[i + 3]);
    }

    public static void WritePixel(RgbaBitmap canvas, int x, int y, RgbaColor c, byte alpha = 255)
    {
        int i = (y * canvas.Width + x) * 4;
        var p = canvas.Pixels;
        p[i] = c.R;
        p[i + 1] = c.G;
        p[i + 2] = c.B;
        p[i + 3] = alpha;
    }
}
