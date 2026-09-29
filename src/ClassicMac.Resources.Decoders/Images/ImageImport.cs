using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;
using ClassicMac.Graphics;
using ClassicMac.Graphics.Pict;

namespace ClassicMac.Resources.Decoders.Images
{
    /// <summary>
    /// Makes QuickDraw image resources from an RGBA image (a PNG, say): <c>PICT</c>, the icons (<c>ICON</c>, the icon
    /// lists, the 4- and 8-bit icons, <c>cicn</c>) and cursors (<c>CURS</c>, <c>crsr</c>), in the layouts
    /// <see cref="QuickDrawResources"/> reads.
    /// </summary>
    /// <remarks>
    /// The conversion rules are ClassicMac's (no Mac OS code imports a PNG): a pixel is opaque when its alpha is at least
    /// 128 (the mask), black in 1-bit data when its luminance is below 128, and a colour table index is the nearest
    /// entry by RGB distance (the lowest index on a tie), without dithering. An image of another size is scaled to fit
    /// (area average, aspect kept, centred on a transparent field).
    /// </remarks>
    public static class ImageImport
    {
        /// <summary>The types <see cref="Write"/> makes.</summary>
        public static IReadOnlyList<string> Types { get; } =
            ["PICT", "cicn", "ICN#", "icl8", "icl4", "ics#", "ics8", "ics4", "icm#", "icm8", "icm4", "ICON", "CURS", "crsr"];

        /// <summary>The icon types of a Finder icon family, as <see cref="WriteIconFamily"/> makes them.</summary>
        public static IReadOnlyList<string> IconFamilyTypes { get; } = ["ICN#", "icl4", "icl8", "ics#", "ics4", "ics8"];

        /// <summary>A resource of <paramref name="type"/> (one of <see cref="Types"/>) from <paramref name="image"/>; a
        /// cursor's hotspot is its centre.</summary>
        public static byte[] Write(string type, RgbaBitmap image)
        {
            ArgumentNullException.ThrowIfNull(type);
            ArgumentNullException.ThrowIfNull(image);
            return type switch
            {
                "PICT" => WritePicture(image),
                "cicn" => WriteCicn(image),
                "ICON" => WriteIcon(image),
                "ICN#" or "ics#" or "icm#" => WriteIconList(type, image),
                "icl4" or "icl8" or "ics4" or "ics8" or "icm4" or "icm8" => WriteColorIcon(type, image),
                "CURS" => WriteCursor(image, 8, 8),
                "crsr" => WriteColorCursor(image, 8, 8),
                _ => throw new ArgumentException($"'{type}' is not a type images are imported as.", nameof(type)),
            };
        }

        /// <summary>
        /// A <c>PICT</c> resource (no file header): the image over white, as an indexed PixMap at the smallest depth
        /// that holds its colours (up to 256), else 32-bit direct.
        /// </summary>
        public static byte[] WritePicture(RgbaBitmap image)
        {
            ArgumentNullException.ThrowIfNull(image);
            var flat = new RgbaBitmap(image.Width, image.Height);
            var colors = new HashSet<int>();
            for (int i = 0; i < image.Pixels.Length; i += 4)
            {
                int a = image.Pixels[i + 3];
                for (int c = 0; c < 3; c++) flat.Pixels[i + c] = (byte)((image.Pixels[i + c] * a + 255 * (255 - a) + 127) / 255);
                flat.Pixels[i + 3] = 255;
                if (colors.Count <= 256) colors.Add((flat.Pixels[i] << 16) | (flat.Pixels[i + 1] << 8) | flat.Pixels[i + 2]);
            }
            var format = colors.Count switch
            {
                <= 2 => PictPixelFormat.Indexed1,
                <= 4 => PictPixelFormat.Indexed2,
                <= 16 => PictPixelFormat.Indexed4,
                <= 256 => PictPixelFormat.Indexed8,
                _ => PictPixelFormat.Rgb888,
            };
            using var stream = new MemoryStream();
            PictWriter.Write(stream, flat, new PictWriteOptions { Format = format, FileHeader = false });
            return stream.ToArray();
        }

        /// <summary><c>ICON</c>: the 32 × 32 image's 1-bit data (transparent pixels white).</summary>
        public static byte[] WriteIcon(RgbaBitmap image) => Bits(Fit(image, 32, 32), dark: true);

        /// <summary>An icon list (<c>ICN#</c>, <c>ics#</c>, <c>icm#</c>): the 1-bit icon, then its mask.</summary>
        public static byte[] WriteIconList(string type, RgbaBitmap image)
        {
            var (w, h) = IconSize(type);
            var fitted = Fit(image, w, h);
            return [.. Bits(fitted, dark: true), .. Bits(fitted, dark: false)];
        }

        /// <summary>A 4- or 8-bit icon (<c>icl4</c>, <c>icl8</c>, <c>ics4</c>, <c>ics8</c>, <c>icm4</c>, <c>icm8</c>)
        /// in the standard colour table; transparent pixels are white (the icon list's mask hides them).</summary>
        public static byte[] WriteColorIcon(string type, RgbaBitmap image)
        {
            var (w, h) = IconSize(type);
            int depth = type[3] == '4' ? 4 : 8;
            return Indices(Fit(image, w, h), StandardColorTables.ForId(depth)!, depth, w * depth / 8);
        }

        /// <summary>
        /// Every icon of a Finder icon family (<see cref="IconFamilyTypes"/>), the small ones from the image scaled to
        /// 16 × 16. <paramref name="small"/>, when given, is used for them instead.
        /// </summary>
        public static IReadOnlyList<(string Type, byte[] Data)> WriteIconFamily(RgbaBitmap image, RgbaBitmap? small = null)
        {
            ArgumentNullException.ThrowIfNull(image);
            var list = new List<(string, byte[])>();
            foreach (var type in IconFamilyTypes)
            {
                var source = type.StartsWith("ics", StringComparison.Ordinal) && small is not null ? small : image;
                list.Add((type, type[3] == '#' ? WriteIconList(type, source) : WriteColorIcon(type, source)));
            }
            return list;
        }

        /// <summary>
        /// <c>cicn</c>: a colour icon of the image's own size (up to 256 × 256): its PixMap at the smallest depth holding
        /// its colours with a table of exactly those (the standard 8-bit table, nearest colours, beyond 256), the mask
        /// and a 1-bit BitMap for 1-bit screens.
        /// </summary>
        public static byte[] WriteCicn(RgbaBitmap image)
        {
            ArgumentNullException.ThrowIfNull(image);
            if (image.Width > 256 || image.Height > 256) image = Fit(image, Math.Min(256, image.Width), Math.Min(256, image.Height));
            int w = image.Width, h = image.Height;
            var (table, depth) = TableFor(image, withWhite: false);
            int rowBytes = (w * depth + 15) / 16 * 2, bitRowBytes = (w + 15) / 16 * 2;
            var pixels = Indices(image, table, depth, rowBytes);

            using var stream = new MemoryStream();
            var b = new BigEndian(stream);
            PixMapRecord(b, w, h, depth, rowBytes, tableAt: 0);
            BitMapRecord(b, w, h, bitRowBytes);                       // maskBMap
            BitMapRecord(b, w, h, bitRowBytes);                       // iconBMap
            b.U32(0);                                                 // iconData handle
            b.Bytes(Bits(image, dark: false, bitRowBytes));           // mask
            b.Bytes(Bits(image, dark: true, bitRowBytes));            // 1-bit icon
            ColorTable(b, table);
            b.Bytes(pixels);
            return stream.ToArray();
        }

        /// <summary><c>CURS</c>: the 16 × 16 image's 1-bit data and mask (transparent pixels leave the screen) and the
        /// hotspot (clamped to 0–15).</summary>
        public static byte[] WriteCursor(RgbaBitmap image, int hotspotH, int hotspotV)
        {
            var fitted = Fit(image, 16, 16);
            using var stream = new MemoryStream();
            var b = new BigEndian(stream);
            b.Bytes(Bits(fitted, dark: true));
            b.Bytes(Bits(fitted, dark: false));
            b.U16(Math.Clamp(hotspotV, 0, 15));
            b.U16(Math.Clamp(hotspotH, 0, 15));
            return stream.ToArray();
        }

        /// <summary>
        /// <c>crsr</c> (type $8001): the 96-byte header (the 1-bit data and mask as for <c>CURS</c>, the hotspot), the
        /// PixMap, the pixels and a colour table as <see cref="WriteCicn"/> makes them. Transparent pixels are white,
        /// which leaves the screen under a colour cursor's mask 0.
        /// </summary>
        public static byte[] WriteColorCursor(RgbaBitmap image, int hotspotH, int hotspotV)
        {
            var fitted = Fit(image, 16, 16);
            var (table, depth) = TableFor(fitted, withWhite: true);
            int rowBytes = 2 * depth;
            const int MapAt = 96, PixelsAt = MapAt + 50;
            int tableAt = PixelsAt + rowBytes * 16;

            using var stream = new MemoryStream();
            var b = new BigEndian(stream);
            b.U16(0x8001);
            b.U32(MapAt);
            b.U32(PixelsAt);
            b.U32(0);                                                 // crsrXData
            b.U16(0);                                                 // crsrXValid
            b.U32(0);                                                 // crsrXHandle
            b.Bytes(Bits(fitted, dark: true));
            b.Bytes(Bits(fitted, dark: false));
            b.U16(Math.Clamp(hotspotV, 0, 15));
            b.U16(Math.Clamp(hotspotH, 0, 15));
            b.U32(0);                                                 // crsrXTable
            b.U32(0);                                                 // crsrID
            PixMapRecord(b, 16, 16, depth, rowBytes, tableAt);
            b.Bytes(Indices(fitted, table, depth, rowBytes));
            ColorTable(b, table);
            return stream.ToArray();
        }

        /// <summary>
        /// The image scaled to fit <paramref name="width"/> × <paramref name="height"/>, aspect kept and centred on a
        /// transparent field, by area average of premultiplied colour; the image itself when it is that size.
        /// </summary>
        public static RgbaBitmap Fit(RgbaBitmap image, int width, int height)
        {
            ArgumentNullException.ThrowIfNull(image);
            if (image.Width == width && image.Height == height) return image;
            double scale = Math.Min((double)width / image.Width, (double)height / image.Height);
            int w = Math.Max(1, (int)Math.Round(image.Width * scale)), h = Math.Max(1, (int)Math.Round(image.Height * scale));
            int left = (width - w) / 2, top = (height - h) / 2;
            var result = new RgbaBitmap(width, height);
            var src = image.Pixels;
            for (int y = 0; y < h; y++)
            {
                double y0 = (double)y * image.Height / h, y1 = (double)(y + 1) * image.Height / h;
                for (int x = 0; x < w; x++)
                {
                    double x0 = (double)x * image.Width / w, x1 = (double)(x + 1) * image.Width / w;
                    double r = 0, g = 0, bl = 0, a = 0, area = 0;
                    for (int sy = (int)y0; sy < Math.Min(image.Height, (int)Math.Ceiling(y1)); sy++)
                    {
                        double fy = Math.Min(y1, sy + 1) - Math.Max(y0, sy);
                        for (int sx = (int)x0; sx < Math.Min(image.Width, (int)Math.Ceiling(x1)); sx++)
                        {
                            double f = fy * (Math.Min(x1, sx + 1) - Math.Max(x0, sx));
                            int i = (sy * image.Width + sx) * 4;
                            double pa = src[i + 3] * f;
                            r += src[i] * pa; g += src[i + 1] * pa; bl += src[i + 2] * pa; a += pa; area += f;
                        }
                    }
                    int o = ((top + y) * width + left + x) * 4;
                    if (a > 0)
                    {
                        result.Pixels[o] = (byte)Math.Round(r / a);
                        result.Pixels[o + 1] = (byte)Math.Round(g / a);
                        result.Pixels[o + 2] = (byte)Math.Round(bl / a);
                    }
                    result.Pixels[o + 3] = (byte)Math.Round(a / area);
                }
            }
            return result;
        }

        // ---- helpers ----

        private static bool Opaque(byte[] p, int i) => p[i + 3] >= 128;

        private static bool Dark(byte[] p, int i) => Opaque(p, i) && (299 * p[i] + 587 * p[i + 1] + 114 * p[i + 2]) / 1000 < 128;

        // 1-bit rows (rowBytes each, default w/8): the dark pixels, or the opaque ones (a mask).
        private static byte[] Bits(RgbaBitmap image, bool dark, int rowBytes = 0)
        {
            int w = image.Width, h = image.Height;
            if (rowBytes == 0) rowBytes = w / 8;
            var bits = new byte[rowBytes * h];
            for (int y = 0; y < h; y++)
                for (int x = 0; x < w; x++)
                {
                    int i = (y * w + x) * 4;
                    if (dark ? Dark(image.Pixels, i) : Opaque(image.Pixels, i)) bits[y * rowBytes + (x >> 3)] |= (byte)(0x80 >> (x & 7));
                }
            return bits;
        }

        // Pixel indices at `depth` into `table` (nearest colour; transparent pixels white, else index 0).
        private static byte[] Indices(RgbaBitmap image, RgbaColor[] table, int depth, int rowBytes)
        {
            int w = image.Width, h = image.Height;
            var data = new byte[rowBytes * h];
            int white = Nearest(table, 255, 255, 255);
            var cache = new Dictionary<int, int>();
            for (int y = 0; y < h; y++)
                for (int x = 0; x < w; x++)
                {
                    int i = (y * w + x) * 4;
                    var p = image.Pixels;
                    int index = white;
                    if (Opaque(p, i))
                    {
                        int key = (p[i] << 16) | (p[i + 1] << 8) | p[i + 2];
                        if (!cache.TryGetValue(key, out index)) cache[key] = index = Nearest(table, p[i], p[i + 1], p[i + 2]);
                    }
                    int bit = x * depth;
                    data[y * rowBytes + (bit >> 3)] |= (byte)(index << (8 - depth - (bit & 7)));
                }
            return data;
        }

        private static int Nearest(RgbaColor[] table, int r, int g, int b)
        {
            int best = 0, bestDistance = int.MaxValue;
            for (int i = 0; i < table.Length; i++)
            {
                int dr = table[i].R - r, dg = table[i].G - g, db = table[i].B - b, d = dr * dr + dg * dg + db * db;
                if (d < bestDistance) (best, bestDistance) = (i, d);
            }
            return best;
        }

        // The image's opaque colours as a table at the smallest depth holding them (white added first when asked);
        // the standard 8-bit table beyond 256.
        private static (RgbaColor[] Table, int Depth) TableFor(RgbaBitmap image, bool withWhite)
        {
            var colors = new List<RgbaColor>();
            var seen = new HashSet<int>();
            if (withWhite) { colors.Add(new RgbaColor(255, 255, 255)); seen.Add(0xFFFFFF); }
            var p = image.Pixels;
            for (int i = 0; i < p.Length && colors.Count <= 256; i += 4)
                if (Opaque(p, i) && seen.Add((p[i] << 16) | (p[i + 1] << 8) | p[i + 2])) colors.Add(new RgbaColor(p[i], p[i + 1], p[i + 2]));
            if (colors.Count == 0) colors.Add(new RgbaColor(255, 255, 255));
            if (colors.Count > 256) return (StandardColorTables.ForId(8)!, 8);
            int depth = colors.Count switch { <= 2 => 1, <= 4 => 2, <= 16 => 4, _ => 8 };
            return (colors.ToArray(), depth);
        }

        // A 50-byte PixMap record; pmTable is the colour table's offset in the resource (0 when it follows by layout).
        private static void PixMapRecord(BigEndian b, int w, int h, int depth, int rowBytes, int tableAt)
        {
            b.U32(0);                                                 // baseAddr
            b.U16(0x8000 | rowBytes);
            b.U16(0); b.U16(0); b.U16(h); b.U16(w);                   // bounds
            b.U16(0);                                                 // pmVersion
            b.U16(0);                                                 // packType
            b.U32(0);                                                 // packSize
            b.U32(0x00480000); b.U32(0x00480000);                     // hRes, vRes: 72 dpi
            b.U16(0);                                                 // pixelType: indexed
            b.U16(depth);                                             // pixelSize
            b.U16(1);                                                 // cmpCount
            b.U16(depth);                                             // cmpSize
            b.U32(0);                                                 // planeBytes
            b.U32(tableAt);                                           // pmTable
            b.U32(0);                                                 // pmReserved
        }

        private static void BitMapRecord(BigEndian b, int w, int h, int rowBytes)
        {
            b.U32(0);
            b.U16(rowBytes);
            b.U16(0); b.U16(0); b.U16(h); b.U16(w);
        }

        // ctSeed 0, ctFlags 0 (a PixMap's table), ctSize, then value/r/g/b per entry (16-bit, each byte repeated).
        private static void ColorTable(BigEndian b, RgbaColor[] table)
        {
            b.U32(0);
            b.U16(0);
            b.U16(table.Length - 1);
            for (int i = 0; i < table.Length; i++)
            {
                b.U16(i);
                b.U16(table[i].R * 257); b.U16(table[i].G * 257); b.U16(table[i].B * 257);
            }
        }

        private static (int w, int h) IconSize(string type) => type switch
        {
            "ICN#" or "icl4" or "icl8" => (32, 32),
            "ics#" or "ics4" or "ics8" => (16, 16),
            "icm#" or "icm4" or "icm8" => (16, 12),
            _ => throw new ArgumentException($"'{type}' is not an icon type.", nameof(type)),
        };

        private sealed class BigEndian(Stream stream)
        {
            public void U16(int value) { Span<byte> s = stackalloc byte[2]; BinaryPrimitives.WriteUInt16BigEndian(s, (ushort)value); stream.Write(s); }
            public void U32(uint value) { Span<byte> s = stackalloc byte[4]; BinaryPrimitives.WriteUInt32BigEndian(s, value); stream.Write(s); }
            public void U32(int value) => U32((uint)value);
            public void Bytes(ReadOnlySpan<byte> bytes) => stream.Write(bytes);
        }
    }
}
