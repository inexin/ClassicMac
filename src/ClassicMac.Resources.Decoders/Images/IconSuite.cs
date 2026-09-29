using System;
using System.Collections.Generic;
using System.Linq;
using ClassicMac.Core;
using ClassicMac.Graphics;
using ClassicMac.Graphics.QuickDraw;

namespace ClassicMac.Resources.Decoders.Images
{
    /// <summary>Where an icon sits in its rect (<c>IconAlignmentType</c>): the icon is stretched to the rect, then moved.</summary>
    public enum IconAlignment
    {
        /// <summary>No alignment: the member fills the rect.</summary>
        None = 0,
        /// <summary>Centred vertically.</summary>
        VerticalCenter = 1,
        /// <summary>At the top.</summary>
        Top = 2,
        /// <summary>At the bottom.</summary>
        Bottom = 3,
        /// <summary>Centred horizontally.</summary>
        HorizontalCenter = 4,
        /// <summary>Centred both ways.</summary>
        AbsoluteCenter = 5,
        /// <summary>Centred horizontally, at the top.</summary>
        CenterTop = 6,
        /// <summary>Centred horizontally, at the bottom.</summary>
        CenterBottom = 7,
        /// <summary>At the left.</summary>
        Left = 8,
        /// <summary>At the left, centred vertically.</summary>
        CenterLeft = 9,
        /// <summary>Top left.</summary>
        TopLeft = 10,
        /// <summary>Bottom left.</summary>
        BottomLeft = 11,
        /// <summary>At the right.</summary>
        Right = 12,
        /// <summary>At the right, centred vertically.</summary>
        CenterRight = 13,
        /// <summary>Top right.</summary>
        TopRight = 14,
        /// <summary>Bottom right.</summary>
        BottomRight = 15,
    }

    /// <summary>How an icon is drawn (<c>IconTransformType</c>): one of none, disabled, offline or open, plus selected and a label (1–7).</summary>
    [Flags]
    public enum IconTransform
    {
        /// <summary>As it is.</summary>
        None = 0,
        /// <summary>Dimmed.</summary>
        Disabled = 1,
        /// <summary>Dotted (an offline volume).</summary>
        Offline = 2,
        /// <summary>Outlined and dotted (an open window).</summary>
        Open = 3,
        /// <summary>Label 1; labels 2–7 are 0x200–0x700.</summary>
        Label1 = 0x100,
        /// <summary>Darkened (selected).</summary>
        Selected = 0x4000,
    }

    /// <summary>
    /// An icon suite as the Icon Utilities draw it (<c>PlotIconSuite</c>, <c>PlotIconID</c>): the 1-bit, 4-bit and 8-bit
    /// icons of one ID in three sizes (and, from an icon family, the 48 × 48 and 32-bit ones), drawn into a
    /// <see cref="QuickDrawPort"/> with the member the rect size and screen depth select, aligned and transformed by the
    /// chosen QuickDraw's rules (Mac OS 9's native Icon Utilities, or the ROM's). See ICONS.md.
    /// </summary>
    public sealed class IconSuite
    {
        private static readonly string[] SuiteTypes = ["ICN#", "icl4", "icl8", "ics#", "ics4", "ics8", "icm#", "icm4", "icm8"];

        private readonly Dictionary<string, byte[]> members = new(StringComparer.Ordinal);

        /// <summary>The suite's label (<c>SetSuiteLabel</c>), used when a transform names none: 0 (none) to 7.</summary>
        public int Label { get; set; }

        /// <summary>The members, by type (raw resource data).</summary>
        public IReadOnlyDictionary<string, byte[]> Members => members;

        /// <summary>
        /// Mac OS 9's label colours, from the System's <c>'rgb '</c> −16392 + n (0 none; 1 Project 2, 2 Project 1, 3 Personal,
        /// 4 Cool, 5 In Progress, 6 Hot, 7 Essential). The Finder's Labels settings change them.
        /// </summary>
        public static IReadOnlyList<RgbColor> DefaultLabelColors { get; } =
        [
            new(0x0000, 0x0000, 0x0000), new(0x5600, 0x2C9D, 0x0524), new(0x0000, 0x64AF, 0x11B0), new(0x0000, 0x0000, 0xD400),
            new(0x0241, 0xAB54, 0xEAFF), new(0xF2D7, 0x0856, 0x84EC), new(0xDD6B, 0x08C2, 0x06A2), new(0xFFFF, 0x648A, 0x028C),
        ];

        /// <summary>The suite of a resource ID, as <c>PlotIconID</c> reads it: <c>ICN#</c>, <c>icl4</c>, <c>icl8</c>, <c>ics#</c>, <c>ics4</c>, <c>ics8</c>, <c>icm#</c>, <c>icm4</c>, <c>icm8</c>.</summary>
        public static IconSuite FromResources(Func<FourCC, short, ReadOnlyMemory<byte>?> lookup, short id)
        {
            ArgumentNullException.ThrowIfNull(lookup);
            var suite = new IconSuite();
            foreach (var type in SuiteTypes)
                if (lookup(FourCC.FromString(type), id) is { Length: > 0 } data) suite.members[type] = data.ToArray();
            return suite;
        }

        /// <summary>The suite of an icon family (<c>IconFamilyToIconSuite</c>): every member but <c>it32</c> and <c>t8mk</c>.</summary>
        public static IconSuite FromFamily(IconFamily family)
        {
            ArgumentNullException.ThrowIfNull(family);
            var suite = new IconSuite();
            foreach (var (type, data) in family.Members)
                if (type is not ("it32" or "t8mk")) suite.members[type] = data;
            return suite;
        }

        /// <summary>
        /// Draws the suite into <paramref name="port"/> (<c>PlotIconSuite</c>): the member for the rect's size and the
        /// port's screen depth, stretched to <paramref name="rect"/> (local coordinates) and moved by
        /// <paramref name="alignment"/>, through its mask, transformed. Returns false when the suite has no 1-bit member
        /// to mask with (noMaskFoundErr); nothing is drawn then.
        /// </summary>
        public bool Plot(QuickDrawPort port, MacRect rect, IconAlignment alignment = IconAlignment.None,
            IconTransform transform = IconTransform.None, IReadOnlyList<RgbColor>? labelColors = null)
        {
            ArgumentNullException.ThrowIfNull(port);
            labelColors ??= DefaultLabelColors;
            bool native = port.Options.Version == QuickDrawVersion.MacOS9;
            int depth = port.Options.ScreenDepth;
            int t = (int)transform;
            if ((t & 0xF00) == 0) t |= (Label & 7) << 8;
            int tf = t & 3, label = (t >> 8) & 15;
            bool selected = (t & 0x4000) != 0;
            if (label >= 8) label = 0;                            // GetLabel fails for 8-15 [ClassicMac: the colour is undefined there]

            if (Prepare(rect, alignment, native) is not { } p) return false;
            if (p.Empty) return true;
            var (group, gw, gh, image, mask, place) = (p.Group, p.Width, p.Height, p.Image, p.Mask, p.Place);
            int rowBytes = gw / 8;

            var saved = (port.ForeColor, port.BackColor, port.PenPattern, port.PenMode, port.FillPattern);
            try
            {
                bool colour = (depth > 4 && tf != 3) || (depth == 4 && !selected && tf is 0 or 2);
                string? data = colour ? ColourData(group, depth, native) : null;
                var maskMap = PixMap.FromBitMap(mask, rowBytes, new MacRect(0, 0, (short)gh, (short)gw));
                if (data != null) PlotDeep(port, data, maskMap, place, tf, label, selected, labelColors, native, gw, gh);
                else
                {
                    var imageMap = PixMap.FromBitMap(image, rowBytes, new MacRect(0, 0, (short)gh, (short)gw));
                    PlotShallow(port, imageMap, maskMap, mask, place, tf, label, selected, depth, labelColors, native, gw, gh);
                }
            }
            finally
            {
                (port.ForeColor, port.BackColor, port.PenPattern, port.PenMode, port.FillPattern) = saved;
            }
            return true;
        }

        /// <summary>
        /// The region the suite's mask covers when plotted in <paramref name="rect"/> (<c>IconSuiteToRgn</c>,
        /// <c>IconIDToRgn</c>): the member the rect selects, aligned, its mask mapped to the placed rect. Null when the
        /// suite has no 1-bit member.
        /// </summary>
        public Region? ToRegion(MacRect rect, IconAlignment alignment = IconAlignment.None, QuickDrawVersion version = QuickDrawVersion.MacOS9)
        {
            if (Prepare(rect, alignment, version == QuickDrawVersion.MacOS9) is not { } p) return null;
            if (p.Empty) return Region.Empty;
            var region = Region.FromBitMap(PixMap.FromBitMap(p.Mask, p.Width / 8, new MacRect(0, 0, (short)p.Height, (short)p.Width)));
            return MapRegion(region, p.Width, p.Height, p.Place);
        }

        /// <summary>
        /// Draws an <c>ICON</c> or <c>ICN#</c> (<c>PlotIconHandle</c>): always as the 32 × 32 member, stretched to the rect,
        /// by the 1-bit rules (an <c>ICON</c>, having no mask half, gets CalcMask's silhouette).
        /// </summary>
        public static bool PlotIconHandle(QuickDrawPort port, MacRect rect, byte[] icon, IconAlignment alignment = IconAlignment.None,
            IconTransform transform = IconTransform.None, IReadOnlyList<RgbColor>? labelColors = null)
        {
            ArgumentNullException.ThrowIfNull(icon);
            var suite = new IconSuite();
            suite.members["ICN#"] = icon;
            return suite.Plot(port, rect, alignment, transform, labelColors);
        }

        /// <summary>
        /// Draws a <c>SICN</c> list (<c>PlotSICNHandle</c>) as the small or mini member: one entry gets CalcMask, two or
        /// more take the data from the list's second half as the mask; a mini rect re-centres it 16 rows tall.
        /// </summary>
        public static bool PlotSICNHandle(QuickDrawPort port, MacRect rect, byte[] sicn, IconAlignment alignment = IconAlignment.None,
            IconTransform transform = IconTransform.None, IReadOnlyList<RgbColor>? labelColors = null)
        {
            ArgumentNullException.ThrowIfNull(sicn);
            var suite = new IconSuite();
            suite.members["ics#"] = sicn;
            suite.members["icm#"] = sicn;
            return suite.Plot(port, rect, alignment, transform, labelColors);
        }

        /// <summary>
        /// Draws an <c>ICON</c> as <c>PlotIcon</c> does: CopyBits srcCopy of its 32 × 32 bits, unmasked, stretched to
        /// <paramref name="rect"/>, in the port's colours; no alignment or transform.
        /// </summary>
        public static void PlotIcon(QuickDrawPort port, MacRect rect, byte[] icon)
        {
            ArgumentNullException.ThrowIfNull(port);
            ArgumentNullException.ThrowIfNull(icon);
            var bounds = new MacRect(0, 0, 32, 32);
            port.CopyBits(PixMap.FromBitMap(Pad(icon, 0, 128), 4, bounds), bounds, rect, TransferMode.SrcCopy);
        }

        private sealed record Prepared(string Group, int Width, int Height, byte[] Image, byte[] Mask, PictRect Place, bool Empty);

        // SetupParamBlock, MakeBoundary and PerformAlignment: the member, its image and mask, and where it goes.
        private Prepared? Prepare(MacRect rect, IconAlignment alignment, bool native)
        {
            // The mask group: the first 1-bit member of the rect size's list.
            int w = rect.Width, h = rect.Height;
            string[] groups = native
                ? w >= 48 || h >= 48 ? ["ich#", "ICN#", "ics#", "icm#"]
                  : w < 32 && h < 32 && h > 12 ? ["ics#", "ICN#", "icm#", "ich#"]
                  : w < 32 && h < 32 ? ["icm#", "ics#", "ICN#", "ich#"]
                  : ["ICN#", "ich#", "ics#", "icm#"]
                : w >= 32 || h >= 32 ? ["ICN#", "ics#", "icm#"]
                  : h > 12 ? ["ics#", "icm#", "ICN#"]
                  : ["icm#", "ics#", "ICN#"];
            if (groups.FirstOrDefault(members.ContainsKey) is not { } group) return null;
            var (gw, gh) = group switch { "ICN#" => (32, 32), "ics#" => (16, 16), "icm#" => (16, 12), _ => (48, 48) };
            var list = members[group];
            int rowBytes = gw / 8;
            var place = PictRect.From(rect);

            // An icm# longer than one icon and its mask is taken for SICNs: 16 rows, the rect re-centred to 16 tall.
            int size = list.Length;
            if (group == "icm#" && (size > 48 || (size < 48 && size > 24)))
            {
                int c = (place.Top + place.Bottom) >> 1;
                place = new PictRect(c - 8, place.Left, c + 8, place.Right);
                gh = 16;
            }
            int bytes = rowBytes * gh;
            if (group == "ich#" && native && size < 2 * bytes) return null;   // a short ich# fails the call
            var image = Pad(list, 0, bytes);
            // The mask is the member's second half (at half its size, so a list of SICNs gives the rows from there), or
            // CalcMask of the image when the member is too short for one.
            var mask = size >= 2 * bytes ? Pad(list, size / 2, bytes) : QuickDrawResources.CalcMask(image, gw, gh);

            // MakeBoundary: nothing is drawn for an empty mask.
            var boundary = Boundary(mask, gw, gh);
            if (boundary.IsEmpty) return new Prepared(group, gw, gh, image, mask, place, Empty: true);
            if (alignment != IconAlignment.None) place = Align(place, boundary, gw, gh, (int)alignment);
            return new Prepared(group, gw, gh, image, mask, place, Empty: false);
        }

        // ---- choosing the colour member ----

        private string? ColourData(string group, int depth, bool native)
        {
            var (x4, x8, x32) = group switch
            {
                "ICN#" => ("icl4", "icl8", "il32"),
                "ics#" => ("ics4", "ics8", "is32"),
                "icm#" => ("icm4", "icm8", (string?)null),
                _ => ("ich4", "ich8", "ih32"),
            };
            string?[] order = !native ? [depth >= 8 ? x8 : null, depth >= 4 ? x4 : null]
                : depth >= 16 ? [x32, x8, x4]
                : depth == 8 ? [x8, x32, x4]
                : depth == 4 ? [x4] : [];
            foreach (var type in order)
                if (type != null && members.TryGetValue(type, out var bytes) && bytes.Length >= IconFamily.MemberType(type)!.RawSize) return type;
            return null;
        }

        // ---- colour data (PlotDeep) ----

        private void PlotDeep(QuickDrawPort port, string type, PixMap maskMap, PictRect place, int tf, int label, bool selected,
            IReadOnlyList<RgbColor> labelColors, bool native, int gw, int gh)
        {
            var member = IconFamily.MemberType(type)!;
            port.ForeColor = RgbColor.Black;
            port.BackColor = RgbColor.White;
            bool disabled = tf == 1;
            PixMap dataMap = member.Depth == 32
                ? Direct(members[type], gw, gh, label, selected, disabled, labelColors)
                : PixMap.Indexed(members[type], gw * member.Depth / 8, new MacRect(0, 0, (short)gh, (short)gw), member.Depth,
                    Clut(member.Depth, label, selected, disabled, labelColors, native));
            var bounds = new MacRect(0, 0, (short)gh, (short)gw);
            port.CopyMask(dataMap, maskMap, bounds, bounds, place.ToMacRect());
            if (tf == 2) Transform(port, null, maskMap, place, tf, native, gw, gh, onePass: false);
        }

        // 32-bit data transformed on a copy: label c * Brighten(L) >> 16 per channel, selected halves every channel,
        // disabled (c + 255) >> 1.
        private static PixMap Direct(byte[] argb, int gw, int gh, int label, bool selected, bool disabled, IReadOnlyList<RgbColor> labelColors)
        {
            var copy = argb.ToArray();
            var bright = label != 0 ? Brighten(labelColors[label]) : default;
            for (int i = 0; i < gw * gh; i++)
                for (int c = 1; c <= 3; c++)
                {
                    int v = copy[4 * i + c];
                    if (label != 0)
                    {
                        int l = c == 1 ? bright.Red : c == 2 ? bright.Green : bright.Blue;
                        v = (int)(((long)(v << 8) * l) >> 24);
                    }
                    if (selected) v >>= 1;
                    if (disabled) v = (v + 255) >> 1;
                    copy[4 * i + c] = (byte)v;
                }
            return PixMap.Direct(copy, gw * 4, new MacRect(0, 0, (short)gh, (short)gw), 32);
        }

        // MakeClut: the system table with the label, selection and dimming baked in, in that order per entry. 8-bit
        // label: entry 0 (c + L) >> 1, others (c * L) >> 16 with L = Brighten(label colour); 4-bit label: entry 15 (black)
        // becomes the raw label colour. The ROM changes only the entries its 'indl' lists; Mac OS 9 every entry.
        private static RgbColor[] Clut(int depth, int label, bool selected, bool disabled, IReadOnlyList<RgbColor> labelColors, bool native)
        {
            var table = StandardColorTables.Exact(depth)!.Select(c => new RgbColor(c.r, c.g, c.b)).ToArray();
            if (label == 0 && !selected && !disabled) return table;
            var romEntries = depth == 8 ? Indl8 : Indl4;
            var bright = Brighten(labelColors[label]);
            for (int i = 0; i < table.Length; i++)
            {
                var c = table[i];
                bool listed = native || romEntries.Contains(i);
                if (label != 0 && listed)
                {
                    if (depth == 8)
                        c = i == 0
                            ? new((ushort)((c.Red + bright.Red) >> 1), (ushort)((c.Green + bright.Green) >> 1), (ushort)((c.Blue + bright.Blue) >> 1))
                            : new((ushort)((c.Red * bright.Red) >> 16), (ushort)((c.Green * bright.Green) >> 16), (ushort)((c.Blue * bright.Blue) >> 16));
                    else if (i == 15) c = labelColors[label];
                }
                if (selected && listed) c = Darken(c);
                if (disabled) c = new((ushort)((c.Red + 0xFFFF) >> 1), (ushort)((c.Green + 0xFFFF) >> 1), (ushort)((c.Blue + 0xFFFF) >> 1));
                table[i] = c;
            }
            return table;
        }

        // The ROM's 'indl' -16392 and -16391: the entries labels and selection change.
        private static readonly HashSet<int> Indl8 = [0, 1, 5, 8, 0x13, 0x16, 0x2A, 0x2B, 0x33, 0x48, 0x54, 0x5C, 0x69, 0x7F, 0x92, 0x9F, 0xA5, 0xAB,
            0xB0, 0xC0, 0xD8, 0xE3, 0xEC, 0xF5, 0xF6, 0xF7, 0xF8, 0xF9, 0xFA, 0xFB, 0xFC, 0xFD, 0xFE, 0xFF];
        private static readonly HashSet<int> Indl4 = [0, 1, 7, 8, 0xB, 0xC, 0xD, 0xE, 0xF];

        // ---- 1-bit data (PlotShallow) ----

        private static void PlotShallow(QuickDrawPort port, PixMap imageMap, PixMap maskMap, byte[] mask, PictRect place, int tf, int label,
            bool selected, int depth, IReadOnlyList<RgbColor> labelColors, bool native, int gw, int gh)
        {
            if (depth >= 2)
            {
                // Label (raw), then the disabled grey, then Darken for selection.
                var fg = depth > 2 && label != 0 ? labelColors[label] : port.ForeColor;
                var bk = tf == 3 ? new RgbColor(0xCC2A, 0xCC2A, 0xFF2A) : port.BackColor;
                if (tf == 1 && (depth > 4 || label == 0) && port.GetGray(bk, fg, out var gray)) (fg, tf) = (gray, 0);
                if (selected)
                {
                    if (depth > 4) fg = Darken(fg);
                    bk = Darken(bk);
                }
                (port.ForeColor, port.BackColor) = (fg, bk);
            }
            else if (selected) (port.ForeColor, port.BackColor) = (port.BackColor, port.ForeColor);

            var bounds = new MacRect(0, 0, (short)gh, (short)gw);
            if (tf == 0)
            {
                port.CopyMask(imageMap, maskMap, bounds, bounds, place.ToMacRect());
                return;
            }
            Transform(port, imageMap, maskMap, place, tf, native, gw, gh, onePass: true);
        }

        // ---- transforms ----

        // The bitmap path (DoBitMapTransform) draws icon-aligned patterns; the region path (DoRegionTransform) port-aligned
        // ones. Mac OS 9 takes the bitmap path only for a rect of exactly 32 x 32; the ROM for any rect up to 32 x 32,
        // prescaling the mask to the rect when the sizes differ.
        private static void Transform(QuickDrawPort port, PixMap? imageMap, PixMap maskMap, PictRect place, int tf, bool native,
            int gw, int gh, bool onePass)
        {
            int w = place.Width, h = place.Height;
            bool useRegions = native ? !(w == 32 && h == 32) : w > 32 || h > 32;
            var rect = place.ToMacRect();
            if (useRegions)
            {
                var region = MapRegion(Region.FromBitMap(maskMap), gw, gh, place);
                var bounds = new MacRect(0, 0, (short)gh, (short)gw);
                if (onePass && tf != 3) port.CopyMask(imageMap!, maskMap, bounds, bounds, rect);
                switch (tf)
                {
                    case 1: port.PenPattern = QuickDrawPattern.Gray; port.PenMode = TransferMode.PatBic; port.PaintRgn(region); break;
                    case 2: port.PenPattern = QuickDrawPattern.LightGray; port.PenMode = TransferMode.PatOr; port.PaintRgn(region); break;
                    case 3:
                        port.FillRgn(region, QuickDrawPattern.Black);
                        port.FillRgn(region.Inset(1, 1), QuickDrawPattern.LightGray);
                        break;
                }
                return;
            }

            // Bitmap path: T (and the mask) at the member's size, or prescaled to the rect's.
            bool prescale = w != gw || h != gh;
            int tw = prescale ? w : gw, th = prescale ? h : gh;
            var maskBits = prescale ? Stretch(maskMap, gw, gh, tw, th) : Bits(maskMap, gw, gh);
            var imageBits = imageMap == null ? new bool[tw * th] : prescale ? Stretch(imageMap, gw, gh, tw, th) : Bits(imageMap, gw, gh);
            var t = new bool[tw * th];
            for (int y = 0; y < th; y++)
                for (int x = 0; x < tw; x++)
                {
                    int i = y * tw + x;
                    bool m = maskBits[i];
                    bool v = onePass && tf != 3 && imageBits[i];
                    if (tf == 3 && m)
                    {
                        // DoOutline: the mask less the pixels whose four neighbours are all set.
                        bool Inside(int xx, int yy) => xx >= 0 && yy >= 0 && xx < tw && yy < th && maskBits[yy * tw + xx];
                        v = !(Inside(x - 1, y) && Inside(x + 1, y) && Inside(x, y - 1) && Inside(x, y + 1));
                    }
                    if (tf == 1) v &= (y & 1) == 0 ? (x & 1) == 0 : (x & 1) == 1;            // $AAAAAAAA / $55555555
                    else if (tf is 2 or 3) v |= m && ((y & 1) == 0 ? (x & 3) == 0 : (x & 3) == 2);   // $88888888 / $22222222
                    t[i] = v;
                }
            var tMap = ToBitMap(t, tw, th);
            var tBounds = new MacRect(0, 0, (short)th, (short)tw);
            if (!onePass)
            {
                // Colour data: the dots in black over what was drawn.
                port.ForeColor = RgbColor.Black;
                port.CopyBits(tMap, tBounds, rect, TransferMode.SrcOr);
                return;
            }
            var maskOut = ToBitMap(maskBits, tw, th);
            if (!prescale)
            {
                port.CopyMask(tMap, maskOut, tBounds, tBounds, rect);
                return;
            }
            // The prescaled mask no longer matches the data: the ROM paints the mask srcBic then T srcOr; Mac OS 9 copies
            // T srcCopy through the mask as a region.
            if (native) port.CopyBits(tMap, tBounds, rect, TransferMode.SrcCopy, Region.FromBitMap(maskOut).Offset(rect.Left, rect.Top));
            else
            {
                port.CopyBits(maskOut, tBounds, rect, TransferMode.SrcBic);
                port.CopyBits(tMap, tBounds, rect, TransferMode.SrcOr);
            }
        }

        private static Region MapRegion(Region region, int gw, int gh, PictRect place) =>
            gw == place.Width && gh == place.Height
                ? region.Offset(place.Left, place.Top)
                : PictureMapping.MapRegion(region, new PictRect(0, 0, gh, gw), place);

        private static bool[] Bits(PixMap map, int w, int h)
        {
            var bits = new bool[w * h];
            for (int y = 0; y < h; y++)
                for (int x = 0; x < w; x++) bits[y * w + x] = map.GetIndex(x, y) != 0;
            return bits;
        }

        // CopyBits srcCopy of a 1-bit map into a w x h buffer (StretchBits).
        private static bool[] Stretch(PixMap map, int gw, int gh, int w, int h)
        {
            var scratch = new RgbaBitmap(w, h);
            var port = new QuickDrawPort(scratch);
            port.CopyBits(map, new MacRect(0, 0, (short)gh, (short)gw), new MacRect(0, 0, (short)h, (short)w), TransferMode.SrcCopy);
            var bits = new bool[w * h];
            for (int i = 0; i < w * h; i++) bits[i] = scratch.Pixels[4 * i] == 0 && scratch.Pixels[4 * i + 3] != 0;
            return bits;
        }

        private static PixMap ToBitMap(bool[] bits, int w, int h)
        {
            int rowBytes = (w + 15) / 16 * 2;
            var data = new byte[rowBytes * h];
            for (int y = 0; y < h; y++)
                for (int x = 0; x < w; x++)
                    if (bits[y * w + x]) data[y * rowBytes + (x >> 3)] |= (byte)(0x80 >> (x & 7));
            return PixMap.FromBitMap(data, rowBytes, new MacRect(0, 0, (short)h, (short)w));
        }

        // ---- geometry ----

        private static byte[] Pad(byte[] data, int at, int length)
        {
            var result = new byte[length];
            if (at < data.Length) data.AsSpan(at, Math.Min(length, data.Length - at)).CopyTo(result);
            return result;
        }

        private static PictRect Boundary(byte[] mask, int w, int h)
        {
            int top = int.MaxValue, left = int.MaxValue, bottom = int.MinValue, right = int.MinValue;
            int rowBytes = w / 8;
            for (int y = 0; y < h; y++)
                for (int x = 0; x < w; x++)
                    if (((mask[y * rowBytes + (x >> 3)] >> (7 - (x & 7))) & 1) != 0)
                        (top, left, bottom, right) = (Math.Min(top, y), Math.Min(left, x), Math.Max(bottom, y + 1), Math.Max(right, x + 1));
            return top == int.MaxValue ? default : new PictRect(top, left, bottom, right);
        }

        // PerformAlignment: the mask's bounding box mapped into the rect (MapPt: |v - from| * to + from / 2, divided,
        // the sign restored), then the rect moved so the box is centred (a floor half) or on an edge.
        private static PictRect Align(PictRect rect, PictRect boundary, int gw, int gh, int align)
        {
            static int Map(int v, int fromLo, int fromSize, int toLo, int toSize)
            {
                if (fromSize == toSize) return v - fromLo + toLo;
                int d = v - fromLo;
                int scaled = (int)(((uint)Math.Abs(d) * (uint)toSize + (uint)(fromSize >> 1)) / (uint)fromSize);
                return (d < 0 ? -scaled : scaled) + toLo;
            }
            var b = new PictRect(Map(boundary.Top, 0, gh, rect.Top, rect.Height), Map(boundary.Left, 0, gw, rect.Left, rect.Width),
                Map(boundary.Bottom, 0, gh, rect.Top, rect.Height), Map(boundary.Right, 0, gw, rect.Left, rect.Width));
            int dx = (align & 0xC) switch
            {
                4 => ((rect.Left + rect.Right) - (b.Left + b.Right)) >> 1,
                8 => rect.Left - b.Left,
                12 => rect.Right - b.Right,
                _ => 0,
            };
            int dy = (align & 3) switch
            {
                1 => ((rect.Top + rect.Bottom) - (b.Top + b.Bottom)) >> 1,
                2 => rect.Top - b.Top,
                3 => rect.Bottom - b.Bottom,
                _ => 0,
            };
            return new PictRect(rect.Top + dy, rect.Left + dx, rect.Bottom + dy, rect.Right + dx);
        }

        // ---- colour arithmetic ----

        // Darken: halve every component; unless all three are then equal (within $200), halve the smallest, then the
        // smaller of the other two unless those are equal.
        internal static RgbColor Darken(RgbColor c)
        {
            int[] v = [c.Red >> 1, c.Green >> 1, c.Blue >> 1];
            static bool Equal(int a, int b) => Math.Abs(a - b) <= 0x200;
            if (!(Equal(v[0], v[1]) && Equal(v[1], v[2]) && Equal(v[0], v[2])))
            {
                int min = v[0] <= v[1] && v[0] <= v[2] ? 0 : v[1] <= v[2] ? 1 : 2;
                v[min] >>= 1;
                int a = (min + 1) % 3, b = (min + 2) % 3;
                if (!Equal(v[a], v[b])) v[v[a] < v[b] ? a : b] >>= 1;
            }
            return new((ushort)v[0], (ushort)v[1], (ushort)v[2]);
        }

        // Brighten: the colour scaled up to full intensity, then mixed with white towards luminance (L >> 1) + (L >> 3) +
        // $6000, where L = (5r + 9g + 2b) >> 4.
        internal static RgbColor Brighten(RgbColor c)
        {
            int max = Math.Max(c.Red, Math.Max(c.Green, c.Blue));
            if (max == 0) return RgbColor.White;
            int lum = (5 * c.Red + 9 * c.Green + 2 * c.Blue) >> 4;
            long target = (lum >> 1) + (lum >> 3) + 0x6000;
            ushort One(int v)
            {
                long scaled = max < 0xFFFF ? (long)v * 0xFFFF / max : v;
                return (ushort)(scaled * target / 0xFFFF + (0xFFFF - target));
            }
            return new(One(c.Red), One(c.Green), One(c.Blue));
        }
    }
}
