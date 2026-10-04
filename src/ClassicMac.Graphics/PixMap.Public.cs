using System;
using System.Collections.Generic;
using System.Linq;
using ClassicMac.Core;

namespace ClassicMac.Graphics;

/// <summary>
/// A QuickDraw <c>BitMap</c> or <c>PixMap</c>: pixels in QuickDraw's in-memory layout, the source of a CopyBits. Rows
/// are <c>rowBytes</c> long; indexed pixels (1, 2, 4 or 8 bits) are packed most significant first and index a colour
/// table; 16-bit pixels are big-endian x-5-5-5; 32-bit pixels are (unused, red, green, blue).
/// </summary>
public sealed partial class PixMap
{
    /// <summary>The pixel map's bounds: its coordinate system (<c>bounds</c>).</summary>
    public MacRect BoundsRect => Bounds.ToMacRect();

    /// <summary>Bits per pixel: 1, 2, 4, 8, 16 or 32.</summary>
    public int Depth => PixelSize;

    /// <summary>
    /// A 1-bit <c>BitMap</c>: CopyBits draws its 1 bits in the port's foreground colour and its 0 bits in the
    /// background colour.
    /// </summary>
    /// <exception cref="ArgumentException">The data is shorter than <paramref name="rowBytes"/> × the height.</exception>
    public static PixMap FromBitMap(ReadOnlySpan<byte> bits, int rowBytes, MacRect bounds)
    {
        Check(bits, rowBytes, bounds, 1);
        return new PixMap
        {
            Bounds = PictRect.From(bounds),
            RowBytes = rowBytes,
            PixelSize = 1,
            IsPixMap = false,
            Palette = [new RgbaColor(255, 255, 255), new RgbaColor(0, 0, 0)],
            Data = bits.ToArray(),
        };
    }

    /// <summary>An indexed <c>PixMap</c> (1, 2, 4 or 8 bits per pixel) with its colour table.</summary>
    /// <exception cref="ArgumentException">A bad depth, or data shorter than <paramref name="rowBytes"/> × the height.</exception>
    public static PixMap Indexed(ReadOnlySpan<byte> pixels, int rowBytes, MacRect bounds, int depth, IReadOnlyList<RgbColor> colors)
    {
        ArgumentNullException.ThrowIfNull(colors);
        if (depth is not (1 or 2 or 4 or 8))
        {
            throw new ArgumentException("An indexed pixel map has 1, 2, 4 or 8 bits per pixel.", nameof(depth));
        }

        Check(pixels, rowBytes, bounds, depth);
        return new PixMap
        {
            Bounds = PictRect.From(bounds),
            RowBytes = rowBytes,
            PixelSize = depth,
            IsPixMap = true,
            Palette = colors.Select(c => c.ToRgba()).ToArray(),
            Palette16 = colors.Select(c => c.Tuple).ToArray(),
            Data = pixels.ToArray(),
        };
    }

    /// <summary>A direct <c>PixMap</c> (16 bits x-5-5-5, or 32 bits unused-red-green-blue).</summary>
    /// <exception cref="ArgumentException">A bad depth, or data shorter than <paramref name="rowBytes"/> × the height.</exception>
    public static PixMap Direct(ReadOnlySpan<byte> pixels, int rowBytes, MacRect bounds, int depth)
    {
        if (depth is not (16 or 32))
        {
            throw new ArgumentException("A direct pixel map has 16 or 32 bits per pixel.", nameof(depth));
        }

        Check(pixels, rowBytes, bounds, depth);
        return new PixMap
        {
            Bounds = PictRect.From(bounds),
            RowBytes = rowBytes,
            PixelSize = depth,
            CmpCount = 3,
            PixelType = 16,
            IsPixMap = true,
            Data = pixels.ToArray(),
        };
    }

    /// <summary>A 32-bit <c>PixMap</c> of an RGBA image, bounds (0, 0, height, width); its alpha rides in the unused byte.</summary>
    public static PixMap FromBitmap(RgbaBitmap bitmap)
    {
        ArgumentNullException.ThrowIfNull(bitmap);
        var data = new byte[bitmap.Width * bitmap.Height * 4];
        var px = bitmap.Pixels;
        for (int i = 0; i < bitmap.Width * bitmap.Height; i++)
        {
            data[4 * i] = px[4 * i + 3];
            data[4 * i + 1] = px[4 * i];
            data[4 * i + 2] = px[4 * i + 1];
            data[4 * i + 3] = px[4 * i + 2];
        }
        return new PixMap
        {
            Bounds = new PictRect(0, 0, bitmap.Height, bitmap.Width),
            RowBytes = bitmap.Width * 4,
            PixelSize = 32,
            CmpCount = 4,
            IsPixMap = true,
            Data = data,
        };
    }

    private static void Check(ReadOnlySpan<byte> data, int rowBytes, MacRect bounds, int depth)
    {
        if (rowBytes < 0 || (long)rowBytes * 8 < (long)Math.Max(0, bounds.Width) * depth)
        {
            throw new ArgumentException($"{rowBytes} bytes per row cannot hold {bounds.Width} pixels of {depth} bits.", nameof(rowBytes));
        }

        if ((long)rowBytes * Math.Max(0, bounds.Height) > data.Length)
        {
            throw new ArgumentException($"{bounds.Height} rows of {rowBytes} bytes need {rowBytes * bounds.Height} bytes; there are {data.Length}.", nameof(data));
        }
    }
}
