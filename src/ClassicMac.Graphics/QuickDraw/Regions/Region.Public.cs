using System;
using System.Collections.Generic;
using System.Linq;
using System.IO;
using ClassicMac.Core;

namespace ClassicMac.Graphics.QuickDraw
{
    public sealed partial class Region
    {
        /// <summary>The smallest rectangle enclosing the region (<c>rgnBBox</c>); empty for the empty region.</summary>
        public MacRect BoundingBox => Bounds.ToMacRect();

        /// <summary>A rectangular region (<c>RectRgn</c>); empty for an empty rectangle.</summary>
        public static Region FromRect(MacRect rect) => FromRect(PictRect.From(rect));

        /// <summary>
        /// A region from QuickDraw's stored form (a <c>'RGN '</c> resource, a picture's region operand): <c>rgnSize</c>,
        /// <c>rgnBBox</c>, then the inversion-point rows. A 10-byte region is its bounding rectangle.
        /// </summary>
        /// <exception cref="InvalidDataException">Shorter than its 10-byte header, or than its <c>rgnSize</c>.</exception>
        public static Region FromRgnData(ReadOnlyMemory<byte> data)
        {
            if (data.Length < 10) throw new InvalidDataException($"A region needs a 10-byte header; this is {data.Length} bytes.");
            var size = new ClassicMac.Core.BigEndianReader(data).ReadUInt16At(0) & 0x7FFF;
            if (size > data.Length) throw new InvalidDataException($"The region says it is {size} bytes; there are {data.Length}.");
            var reader = new ClassicMac.Core.BigEndianReader(data[..Math.Max(10, size)]);
            return Read(reader);
        }

        /// <summary>The region in QuickDraw's stored form: <c>rgnSize</c>, <c>rgnBBox</c> and, unless it is a rectangle, its rows.</summary>
        public byte[] ToRgnData()
        {
            var rows = ToQuickDrawData();
            var writer = new BigEndianWriter(10 + 2 * rows.Length);
            writer.WriteUInt16((ushort)(10 + 2 * rows.Length));
            BoundingBox.Write(writer);
            foreach (var row in rows) writer.WriteInt16(row);
            return writer.ToArray();
        }

        /// <summary>The pixels an oval inscribed in <paramref name="rect"/> covers, as <c>PaintOval</c> paints it.</summary>
        public static Region Oval(MacRect rect) => rect.IsEmpty ? Empty : RegionShapes.Oval(PictRect.From(rect));

        /// <summary>The pixels a rounded rectangle covers, as <c>PaintRoundRect</c> paints it (corner oval <paramref name="ovalWidth"/> × <paramref name="ovalHeight"/>).</summary>
        public static Region RoundRect(MacRect rect, int ovalWidth, int ovalHeight) =>
            rect.IsEmpty ? Empty : RegionShapes.RoundRect(PictRect.From(rect), ovalWidth, ovalHeight);

        /// <summary>
        /// The pixels a wedge of the oval in <paramref name="rect"/> covers, as <c>PaintArc</c> paints it: from
        /// <paramref name="startAngle"/> (degrees clockwise from 12 o'clock) through <paramref name="arcAngle"/>, by the
        /// chosen QuickDraw's rules.
        /// </summary>
        public static Region Arc(MacRect rect, int startAngle, int arcAngle, QuickDrawVersion version = QuickDrawVersion.MacOS9) =>
            rect.IsEmpty ? Empty : RegionShapes.Arc(PictRect.From(rect), startAngle, arcAngle, version == QuickDrawVersion.MacOS9);

        /// <summary>
        /// The set pixels of a 1-bit bitmap, in its own coordinates (<c>BitMapToRegion</c>). For a deeper pixel map, the
        /// pixels whose value is not 0.
        /// </summary>
        public static Region FromBitMap(PixMap bitmap)
        {
            ArgumentNullException.ThrowIfNull(bitmap);
            var bounds = bitmap.Bounds;
            var region = Empty;
            for (int y = 0; y < bitmap.Height; y++)
                for (int x = 0; x < bitmap.Width; x++)
                {
                    if (bitmap.GetIndex(x, y) == 0) continue;
                    int end = x;
                    while (end < bitmap.Width && bitmap.GetIndex(end, y) != 0) end++;
                    region = region.Union(FromRect(new PictRect(bounds.Top + y, bounds.Left + x, bounds.Top + y + 1, bounds.Left + end)));
                    x = end;
                }
            return region;
        }

        /// <summary>The pixels a polygon through <paramref name="points"/> encloses, as <c>PaintPoly</c> paints it.</summary>
        public static Region Polygon(IReadOnlyList<MacPoint> points)
        {
            ArgumentNullException.ThrowIfNull(points);
            return RegionShapes.Polygon(points.Select(p => ((int)p.H, (int)p.V)).ToArray());
        }

        /// <summary>Whether <paramref name="point"/> is in the region (<c>PtInRgn</c>).</summary>
        public bool Contains(MacPoint point) => Contains(point.H, point.V);
    }
}
