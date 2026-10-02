using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using ClassicMac.Core;
using ClassicMac.Graphics;
using ClassicMac.Graphics.QuickDraw;
using ClassicMac.Resources.Decoders.Images;

namespace ClassicMac.Resources.Decoders.Finder
{
    /// <summary>One item of a Finder window: a file or folder with its name, icon position, Finder flags and icon.</summary>
    /// <param name="Name">The name, drawn under the icon.</param>
    /// <param name="Location">The stored icon position (<c>fdLocation</c>, <c>frLocation</c>); <see cref="Position"/> is what the
    /// Finder makes of it.</param>
    /// <param name="Flags">The Finder flags (<c>fdFlags</c>, <c>frFlags</c>).</param>
    /// <param name="Icon">The icon (see <see cref="FinderIconResolver"/>); null draws a placeholder.</param>
    /// <param name="Kind">What the item is, for its placeholder and the volume root's own folders.</param>
    public sealed record FinderWindowItem(MacString Name, MacPoint Location, ushort Flags, IconSuite? Icon, FinderItemKind Kind = FinderItemKind.Document)
    {
        /// <summary>The Finder flag <c>kIsInvisible</c>.</summary>
        public const ushort InvisibleFlag = 0x4000;

        /// <summary>The Finder flag <c>kIsAlias</c>.</summary>
        public const ushort AliasFlag = 0x8000;

        /// <summary>The Finder flag <c>kHasBeenInited</c>: the Finder has recorded the item's position.</summary>
        public const ushort HasBeenInitedFlag = 0x0100;

        /// <summary>Whether the item is invisible, and not drawn.</summary>
        public bool IsInvisible => (Flags & InvisibleFlag) != 0;

        /// <summary>Whether the item is an alias, whose name the Finder draws in italics.</summary>
        public bool IsAlias => (Flags & AliasFlag) != 0;

        /// <summary>The colour label, 0 (none) to 7: <c>fdFlags</c> bits 1–3.</summary>
        public int Label => (Flags >> 1) & 7;

        /// <summary>Badges drawn over the icon, in order (alias, locked, custom).</summary>
        public IReadOnlyList<IconSuite> Badges { get; init; } = [];

        /// <summary>
        /// The icon's top-left corner in the window's local coordinates, or null when the Finder arranges the item
        /// (docs/formats/file-systems/finder-windows.md §2.2) [Code: Finder 9.2.2]: with <c>kHasBeenInited</c> the location
        /// as stored; without it, the location plus 20000 in each coordinate (16-bit), kept only when −4000 &lt; h &lt; 4000
        /// and v &gt; −4000. (0, 0) and (−1, −1) are no position either way.
        /// </summary>
        public MacPoint? Position
        {
            get
            {
                var point = Location;
                if ((Flags & HasBeenInitedFlag) == 0)
                {
                    short h = unchecked((short)(Location.H + 20000)), v = unchecked((short)(Location.V + 20000));
                    if (h is <= -4000 or >= 4000 || v <= -4000) return null;
                    point = new MacPoint(v, h);
                }
                return point == default || point == new MacPoint(-1, -1) ? null : point;
            }
        }

        /// <summary>Whether the Finder has given the item a place (<see cref="Position"/>).</summary>
        public bool HasLocation => Position is not null;
    }

    /// <summary>A folder's window as the Finder records it: its rectangle and scroll position (<c>DInfo</c>, <c>DXInfo</c>), and its items.</summary>
    public sealed record FinderWindow
    {
        /// <summary>The window's content rectangle in global coordinates (<c>frRect</c>), header and scroll bars included.</summary>
        public MacRect Bounds { get; init; }

        /// <summary>The local coordinates at the icon area's top-left corner (<c>frScroll</c>).</summary>
        public MacPoint ScrollPosition { get; init; }

        /// <summary>The folder's own Finder flags (<c>frFlags</c>): <see cref="Bounds"/> counts only with <c>kHasBeenInited</c>.</summary>
        public ushort Flags { get; init; }

        /// <summary>Whether this is a volume's root window, which leaves out the volume's own files and folders.</summary>
        public bool IsVolumeRoot { get; init; }

        /// <summary>How the Finder shows the window (<see cref="FinderView.Read"/>); a list view is drawn as large icons.</summary>
        public FinderView View { get; init; } = FinderView.LargeIcons;

        /// <summary>The items, in drawing order.</summary>
        public IReadOnlyList<FinderWindowItem> Items { get; init; } = [];

        /// <summary>Whether the window has a recorded rectangle: <c>kHasBeenInited</c> and a non-empty <c>frRect</c> [Code: Finder 9.2.2].</summary>
        public bool HasBounds => (Flags & FinderWindowItem.HasBeenInitedFlag) != 0 && !Bounds.IsEmpty;
    }

    /// <summary>Where an item's icon is drawn.</summary>
    /// <param name="Item">The item.</param>
    /// <param name="Location">The icon's top-left corner in the window's local coordinates: its position, or the cell it was arranged in.</param>
    /// <param name="IconRect">The 32 × 32 icon rectangle, in bitmap coordinates.</param>
    /// <param name="Arranged">Whether it was put in a grid cell because it had no position.</param>
    public sealed record FinderWindowPlacement(FinderWindowItem Item, MacPoint Location, MacRect IconRect, bool Arranged);

    /// <summary>How <see cref="FinderWindowRenderer"/> draws.</summary>
    public sealed class FinderWindowOptions
    {
        /// <summary>Geneva 10 labels, a 32-bit screen, Mac OS 9's QuickDraw, no fonts, the default label colours.</summary>
        public static FinderWindowOptions Default { get; } = new();

        /// <summary>The screen depth: 32 (the default) or 1, 2, 4, 8, 16.</summary>
        public int ScreenDepth { get; init; } = 32;

        /// <summary>Which QuickDraw draws the icons.</summary>
        public QuickDrawVersion QuickDraw { get; init; } = QuickDrawVersion.MacOS9;

        /// <summary>The views font's family: 3, Geneva.</summary>
        public int LabelFontId { get; init; } = 3;

        /// <summary>The views font's size: 10.</summary>
        public int LabelFontSize { get; init; } = 10;

        /// <summary>Bitmap fonts for the labels; text no strike draws goes to <see cref="TextFallback"/>.</summary>
        public FontLibrary? Fonts { get; init; }

        /// <summary>Rasterizes labels no bitmap font draws; null draws no such labels.</summary>
        public ITextFallback? TextFallback { get; init; }

        /// <summary>The label colours 0–7 (<see cref="FinderIconResolver.LabelColors"/>); null for <see cref="IconSuite.DefaultLabelColors"/>.</summary>
        public IReadOnlyList<RgbColor>? LabelColors { get; init; }
    }

    /// <summary>
    /// Draws a folder's window in its icon or button view as the Finder lays it out (docs/formats/file-systems/finder-windows.md):
    /// the content area of <c>frRect</c> with the header pane, the icon area on white and the scroll bars' place; each
    /// visible item's icon (and button) at its position less the scroll position, or arranged in a free grid cell; its name
    /// in the views font, through a <see cref="QuickDrawPort"/>. A list view is drawn as large icons.
    /// </summary>
    public static class FinderWindowRenderer
    {
        /// <summary>
        /// The content size of a window that records no rectangle: the Finder's window template (62, 14, 280, 418)
        /// [Code: Finder 9.2.2].
        /// </summary>
        public const int DefaultWidth = 404, DefaultHeight = 218;

        /// <summary>The scroll position of a window that records no rectangle [Code: Finder 9.2.2].</summary>
        public static MacPoint DefaultScrollPosition { get; } = new(-8, -16);

        /// <summary>The header pane above the icon area ("n items", then a line) [Code: Finder 9.2.2; Verified: Mac OS 9.0 Finder].</summary>
        public const int HeaderHeight = 21;

        /// <summary>The scroll bars' width, inside the content rectangle at its right and bottom [Verified: Mac OS 9.0 Finder].</summary>
        public const int ScrollBarSize = 15;

        /// <summary>The large-icon grid's cell [Code: Finder 9.2.2].</summary>
        public const int CellWidth = 128, CellHeight = 64;

        /// <summary>The small-icon view's name pane: names wider than this less 2 are condensed, then truncated [Fitted: Mac OS 9.0 Finder].</summary>
        public const int SmallNameWidth = 167;

        private const int IconSize = 32;

        // The volume's own files and folders the Finder leaves out of the root window [Code: Finder 9.2.2].
        private static readonly HashSet<string> RootFiles = new(StringComparer.Ordinal)
        {
            "AppleShare PDS", "Desktop", "Desktop DB", "Desktop DF", "DesktopPrinters DB", "Finder", "OpenFolderListDF", "Shutdown Check", "VM Storage",
        };

        private static readonly HashSet<string> RootFolders = new(StringComparer.Ordinal)
        {
            "Temporary Items", "Trash", "Desktop Folder", "Move&Rename", "TheVolumeSettingsFolder",
        };

        private static readonly RgbColor HeaderFill = new(0xDDDD, 0xDDDD, 0xDDDD), HeaderShadow = new(0xAAAA, 0xAAAA, 0xAAAA),
            Trough = new(0xEEEE, 0xEEEE, 0xEEEE);

        /// <summary>
        /// The rectangle of a label <paramref name="width"/> pixels wide under an icon whose top-left is
        /// <paramref name="icon"/>: 32 to 45 below its top, from <c>16 + ((−width) &gt;&gt; 1) − 2</c> across, 4 wider than the
        /// text [Code: Finder 9.2.2].
        /// </summary>
        public static MacRect LabelRect(MacPoint icon, int width)
        {
            var box = Box.Label(icon.V, icon.H, width);
            return new MacRect((short)box.Top, (short)box.Left, (short)box.Bottom, (short)box.Right);
        }

        /// <summary>Where the visible items' icons go, in drawing order; names are measured with <paramref name="options"/>' font.</summary>
        public static IReadOnlyList<FinderWindowPlacement> Place(FinderWindow window, FinderWindowOptions? options = null)
        {
            ArgumentNullException.ThrowIfNull(window);
            options ??= FinderWindowOptions.Default;
            return Place(window, Port(new RgbaBitmap(1, 1), options), options).Select(p => p.Placement).ToList();
        }

        /// <summary>Draws the window's content.</summary>
        public static RgbaBitmap Render(FinderWindow window, FinderWindowOptions? options = null)
        {
            ArgumentNullException.ThrowIfNull(window);
            options ??= FinderWindowOptions.Default;
            var layout = Layout.Of(window.View.Kind);
            var placed = Place(window, Port(new RgbaBitmap(1, 1), options), options);
            int width = Width(window), height = Height(window);
            if (!window.HasBounds && placed.Any(p => p.Placement.Arranged))
                height = Math.Max(height, HeaderHeight + placed.Where(p => p.Placement.Arranged).Max(p => p.Placement.Location.V) - Scroll(window).V
                    + layout.CellHeight + ScrollBarSize);
            var canvas = new RgbaBitmap(width, height);
            var port = Port(canvas, options);
            port.EraseRect(port.PortRect);
            var colours = options.LabelColors ?? IconSuite.DefaultLabelColors;
            var scroll = Scroll(window);
            foreach (var (placement, name) in placed)
            {
                var item = placement.Item;
                int v = HeaderHeight + placement.Location.V - scroll.V, h = placement.Location.H - scroll.H;
                if (layout.Frame(v, h) is { } frame) BevelButton(port, frame);
                (port.ForeColor, port.BackColor) = (RgbColor.Black, RgbColor.White);
                // PlotIconRef with the label in the transform's bits 8-11 [Code: Finder 9.2.2]; badges composited on it.
                var transform = (IconTransform)(item.Label << 8);
                if (item.Icon?.Plot(port, placement.IconRect, layout.Alignment, transform, colours) != true) Placeholder(port, placement.IconRect, item.Kind);
                foreach (var badge in item.Badges) badge.Plot(port, placement.IconRect, IconAlignment.None, transform, colours);
                if (name.Text.Length == 0) continue;
                TextStyle(port, options, name.Face);
                var (penH, baseline) = layout.Pen(v, h, name.Width);
                port.MoveTo(penH, baseline);
                port.DrawText(name.Text.Bytes);
            }
            Header(port, width, placed.Count, options);
            ScrollBars(port, width, height);
            return canvas;
        }

        private static QuickDrawPort Port(RgbaBitmap canvas, FinderWindowOptions options) => new(canvas, new QuickDrawOptions
        {
            ScreenDepth = options.ScreenDepth, Version = options.QuickDraw, Fonts = options.Fonts, TextFallback = options.TextFallback,
        });

        private static int Width(FinderWindow window) => window.HasBounds ? window.Bounds.Width : DefaultWidth;

        private static int Height(FinderWindow window) => window.HasBounds ? window.Bounds.Height : DefaultHeight;

        // frScroll with the recorded rectangle, else the default window's [Code: Finder 9.2.2].
        private static MacPoint Scroll(FinderWindow window) => window.HasBounds ? window.ScrollPosition : DefaultScrollPosition;

        // Shown: not invisible, and at a volume's root not one of the volume's own files or folders [Code: Finder 9.2.2].
        private static bool Shown(FinderWindowItem item, bool root)
        {
            if (item.IsInvisible) return false;
            if (!root) return true;
            var name = item.Name.ToMacRoman();
            return !(item.Kind == FinderItemKind.Folder ? RootFolders : RootFiles).Contains(name);
        }

        // A name as drawn: its text (a small icon's may be truncated), face and width.
        private sealed record Name(MacString Text, QuickDrawStyle Face, int Width);

        // Placed items at their positions; the others arranged in the view's grid, in order (finder-windows.md §2.5).
        private static List<(FinderWindowPlacement Placement, Name Name)> Place(FinderWindow window, QuickDrawPort port, FinderWindowOptions options)
        {
            var layout = Layout.Of(window.View.Kind);
            var scroll = Scroll(window);
            var items = window.Items.Where(i => Shown(i, window.IsVolumeRoot)).ToList();
            var names = items.Select(i => FitName(port, i, layout, options)).ToList();
            var occupied = new List<Box>();
            for (int i = 0; i < items.Count; i++)
                if (items[i].Position is { } position) Occupy(occupied, layout, position, names[i].Width, options.LabelFontSize);
            bool placed = occupied.Count > 0;
            // Small icons keep clear of all the placed items together [Fitted: Mac OS 9.0 Finder].
            if (placed && layout.Columns) occupied.Add(occupied.Aggregate((a, b) => a.Union(b)));
            var start = layout.Start(placed, scroll);
            int visibleRight = scroll.H + Width(window) - ScrollBarSize;
            int visibleBottom = scroll.V + Height(window) - HeaderHeight - ScrollBarSize;
            var result = new List<(FinderWindowPlacement, Name)>(items.Count);
            for (int i = 0; i < items.Count; i++)
            {
                var item = items[i];
                bool arranged = item.Position is null;
                var location = item.Position ?? Arrange(occupied, layout, start, visibleRight, visibleBottom, names[i].Width);
                if (arranged) Occupy(occupied, layout, location, names[i].Width, options.LabelFontSize);
                var icon = layout.Icon(HeaderHeight + location.V - scroll.V, location.H - scroll.H);
                var rect = new MacRect((short)icon.Top, (short)icon.Left, (short)icon.Bottom, (short)icon.Right);
                result.Add((new FinderWindowPlacement(item, location, rect, arranged), names[i]));
            }
            return result;
        }

        // A rectangle in ints, so cells and widened rectangles near the coordinate limits do not wrap.
        private readonly record struct Box(int Top, int Left, int Bottom, int Right)
        {
            public static Box Label(int v, int h, int nameWidth)
            {
                int left = h + IconSize / 2 + ((-nameWidth) >> 1) - 2;
                return new(v + 32, left, v + 45, left + nameWidth + 4);
            }

            public Box Widen(int by) => this with { Left = Left - by, Right = Right + by };

            public Box Union(Box o) => new(Math.Min(Top, o.Top), Math.Min(Left, o.Left), Math.Max(Bottom, o.Bottom), Math.Max(Right, o.Right));

            public bool Intersects(Box o) => Left < o.Right && o.Left < Right && Top < o.Bottom && o.Top < Bottom;
        }

        // A view's geometry, for an item whose position is (v, h): its icon, its button, its name's pen and rectangle, its
        // grid (finder-windows.md §2.7, §2.8).
        private sealed record Layout(FinderViewKind Kind, int CellWidth, int CellHeight, int OriginH, bool Columns)
        {
            public static Layout Of(FinderViewKind kind) => kind switch
            {
                FinderViewKind.SmallIcon => new(kind, 192, 24, 2, true),
                FinderViewKind.Button => new(kind, 128, 86, 1, false),
                FinderViewKind.SmallButton => new(kind, 128, 62, 1, false),
                _ => new(FinderViewKind.LargeIcon, FinderWindowRenderer.CellWidth, FinderWindowRenderer.CellHeight, 1, false),
            };

            public Box Icon(int v, int h) => Kind switch
            {
                FinderViewKind.SmallIcon => new(v, h, v + 16, h + 16),
                FinderViewKind.Button => new(v + 8, h, v + 40, h + 32),
                FinderViewKind.SmallButton => new(v + 6, h + 8, v + 22, h + 24),
                _ => new(v, h, v + IconSize, h + IconSize),
            };

            // The button's bevel [Verified: Mac OS 9.0 Finder].
            public Box? Frame(int v, int h) => Kind switch
            {
                FinderViewKind.Button => new Box(v, h - 8, v + 48, h + 40),
                FinderViewKind.SmallButton => new Box(v, h + 2, v + 28, h + 30),
                _ => null,
            };

            // The pen: a large icon's [Code: Finder 9.2.2]; small icons' flush left, buttons' centred [Verified: Mac OS 9.0 Finder].
            public (int H, int V) Pen(int v, int h, int width) => Kind switch
            {
                FinderViewKind.SmallIcon => (h + 18, v + 11),
                FinderViewKind.Button => (h + 16 + ((-width) >> 1), v + 60),
                FinderViewKind.SmallButton => (h + 16 + ((-width) >> 1), v + 40),
                _ => (Box.Label(v, h, width).Left + 2, v + 42),
            };

            // The name's rectangle: a large icon's [Code: Finder 9.2.2]; the others from the ascent above the baseline to
            // 3 below it, 2 either side of the text [ClassicMac], a small icon's from h + 17 [Verified: Mac OS 9.0 Finder].
            public Box LabelBox(int v, int h, int width)
            {
                if (Kind == FinderViewKind.LargeIcon) return Box.Label(v, h, width);
                var (penH, baseline) = Pen(v, h, width);
                int left = Kind == FinderViewKind.SmallIcon ? h + 17 : penH - 2;
                return new Box(baseline - 10, left, baseline + 3, left + width + (Kind == FinderViewKind.SmallIcon ? 2 : 4));
            }

            // A button centres its icon by the icon's mask [Fitted: Mac OS 9.0 Finder, a shaped icon sits a pixel higher];
            // the icon views plot it as it is [Code: Finder 9.2.2].
            public IconAlignment Alignment => Frame(0, 0) is null ? IconAlignment.None : IconAlignment.AbsoluteCenter;

            // What an item takes: its button or icon, and its name.
            public Box Body(int v, int h) => Frame(v, h) ?? Icon(v, h);

            // Where arranging starts: the first grid point at or past (visTop + 4, visLeft + 16) [Code: Finder 9.2.2], for
            // small icons at or past (visTop, visLeft + 16) [Fitted: Mac OS 9.0 Finder]; the grid's origin when nothing is placed.
            public MacPoint Start(bool placed, MacPoint scroll) => !placed
                ? new MacPoint(0, (short)OriginH)
                : new MacPoint((short)GridAtOrAfter(scroll.V + (Columns ? 0 : 4), 0, CellHeight), (short)GridAtOrAfter(scroll.H + 16, OriginH, CellWidth));
        }

        // The first free cell from `start`: one whose icon (or button) and name are clear of every occupied rectangle. Large
        // icons and buttons scan rows, a cell fitting the visible width [Code: Finder 9.2.2]; small icons scan columns, a
        // cell fitting the visible height [Code: Finder 9.2.2; Fitted: Mac OS 9.0 Finder]. The first cell of a row or column
        // always counts as fitting, so a window smaller than a cell still takes one a row or column [ClassicMac].
        private static MacPoint Arrange(List<Box> occupied, Layout layout, MacPoint start, int visibleRight, int visibleBottom, int nameWidth)
        {
            int v = start.V, h = start.H;
            while (true)
            {
                if (!layout.Columns && h != start.H && h + layout.CellWidth > visibleRight)
                {
                    (v, h) = (v + layout.CellHeight, start.H);
                    continue;
                }
                if (layout.Columns && v != start.V && v + layout.CellHeight > visibleBottom)
                {
                    (v, h) = (start.V, h + layout.CellWidth);
                    continue;
                }
                Box body = layout.Body(v, h), label = layout.LabelBox(v, h, nameWidth);
                if (!occupied.Any(o => o.Intersects(body) || o.Intersects(label))) return new MacPoint(unchecked((short)v), unchecked((short)h));
                if (layout.Columns) v += layout.CellHeight;
                else h += layout.CellWidth;
            }
        }

        // An item's icon (or button) and name rectangles, each widened left and right by the views font size, and their
        // union [Code: Finder 9.2.2].
        private static void Occupy(List<Box> occupied, Layout layout, MacPoint position, int nameWidth, int fontSize)
        {
            Box body = layout.Body(position.V, position.H).Widen(fontSize), label = layout.LabelBox(position.V, position.H, nameWidth).Widen(fontSize);
            occupied.Add(body);
            occupied.Add(label);
            occupied.Add(body.Union(label));
        }

        // The smallest grid point at or past `value`, on a grid of `step` from `origin`.
        private static int GridAtOrAfter(int value, int origin, int step) => origin + (int)Math.Ceiling((value - origin) / (double)step) * step;

        private static QuickDrawStyle Face(FinderWindowItem item) => item.IsAlias ? QuickDrawStyle.Italic : QuickDrawStyle.Plain;

        private static void TextStyle(QuickDrawPort port, FinderWindowOptions options, QuickDrawStyle face)
        {
            port.TextFont = options.LabelFontId;
            port.TextSize = options.LabelFontSize;
            port.TextFace = face;
            port.TextMode = TransferMode.SrcOr;
            port.ForeColor = RgbColor.Black;
        }

        // The name as the view draws it: whole, except a small icon's, which is condensed when it and 2 pixels are wider
        // than the name pane, then truncated in the middle to the pane less 2 [Code: Finder 9.2.2; Fitted: the pane's
        // width]. Names in other views are never truncated [Code: Finder 9.2.2].
        private static Name FitName(QuickDrawPort port, FinderWindowItem item, Layout layout, FinderWindowOptions options)
        {
            var face = Face(item);
            int width = TextWidth(port, item.Name, face, options);
            if (layout.Kind != FinderViewKind.SmallIcon || width + 2 <= SmallNameWidth) return new Name(item.Name, face, width);
            face |= QuickDrawStyle.Condense;
            width = TextWidth(port, item.Name, face, options);
            if (width + 2 <= SmallNameWidth) return new Name(item.Name, face, width);
            var text = TruncateMiddle(port, item.Name, face, options, SmallNameWidth - 2);
            return new Name(text, face, TextWidth(port, text, face, options));
        }

        // TruncString(width, smTruncMiddle) approximated: the most characters, the first half's extra one first, kept about
        // an ellipsis [ClassicMac: not compared with the Script Manager's TruncText].
        private static MacString TruncateMiddle(QuickDrawPort port, MacString name, QuickDrawStyle face, FinderWindowOptions options, int width)
        {
            var bytes = name.Bytes;
            for (int keep = bytes.Length - 1; keep > 0; keep--)
            {
                int head = (keep + 1) / 2, tail = keep / 2;
                var candidate = new MacString([.. bytes[..head], 0xC9, .. bytes[^tail..]]);
                if (TextWidth(port, candidate, face, options) <= width) return candidate;
            }
            return new MacString([0xC9]);
        }

        // StringWidth in the views font; text no strike draws is measured by the fallback [ClassicMac]. Leaves the port
        // set to draw it.
        private static int TextWidth(QuickDrawPort port, MacString text, QuickDrawStyle face, FinderWindowOptions options)
        {
            TextStyle(port, options, face);
            if (text.Length == 0) return 0;
            int width = port.TextWidth(text.Bytes);
            if (width == 0 && options.TextFallback?.Render(text.ToMacRoman(), new TextFallbackStyle(options.LabelFontId, (int)face, options.LabelFontSize)) is { } mask)
                width = (int)Math.Round(mask.Advance);
            return width;
        }

        // A Platinum bevel button: a $CCCC face in three rings, each lighter at the top and left, darker at the bottom and
        // right, its top-right and bottom-left corners between ($6666/$3333/$5555, $CCCC/$7777/$AAAA, $FFFF/$9999/$CCCC
        // from the outside) [Verified: Mac OS 9.0 Finder, the Appearance Manager's bevel; ClassicMac: drawn here, not by
        // ApplyThemeBackground].
        private static void BevelButton(QuickDrawPort port, Box r)
        {
            void Fill(int top, int left, int bottom, int right, ushort grey)
            {
                port.ForeColor = new RgbColor(grey, grey, grey);
                port.PaintRect(new MacRect((short)top, (short)left, (short)bottom, (short)right));
            }
            port.PenNormal();
            Fill(r.Top, r.Left, r.Bottom, r.Right, 0xCCCC);
            ReadOnlySpan<(ushort Light, ushort Dark, ushort Corner)> rings = [(0x6666, 0x3333, 0x5555), (0xCCCC, 0x7777, 0xAAAA), (0xFFFF, 0x9999, 0xCCCC)];
            for (int i = 0; i < rings.Length; i++)
            {
                int t = r.Top + i, l = r.Left + i, b = r.Bottom - 1 - i, rt = r.Right - 1 - i;
                var (light, dark, corner) = rings[i];
                Fill(t, l, t + 1, rt, light);              // top
                Fill(t, l, b, l + 1, light);               // left
                Fill(b, l + 1, b + 1, rt + 1, dark);       // bottom
                Fill(t + 1, rt, b + 1, rt + 1, dark);      // right
                Fill(t, rt, t + 1, rt + 1, corner);        // top right
                Fill(b, l, b + 1, l + 1, corner);          // bottom left
            }
        }

        // The header pane in the Platinum appearance's colours, with "n items" centred in the views font [Verified: Mac OS
        // 9.0 Finder, the pane's geometry and colours; ClassicMac: the text's place, and no free space].
        private static void Header(QuickDrawPort port, int width, int count, FinderWindowOptions options)
        {
            void Fill(int top, int left, int bottom, int right, RgbColor colour)
            {
                port.ForeColor = colour;
                port.PaintRect(new MacRect((short)top, (short)left, (short)bottom, (short)right));
            }
            port.PenNormal();
            Fill(0, 0, HeaderHeight - 1, width, HeaderFill);
            Fill(0, 0, 1, width - 1, RgbColor.White);
            Fill(0, 0, HeaderHeight - 2, 1, RgbColor.White);
            Fill(1, width - 1, HeaderHeight - 1, width, HeaderShadow);
            Fill(HeaderHeight - 2, 1, HeaderHeight - 1, width, HeaderShadow);
            Fill(HeaderHeight - 1, 0, HeaderHeight, width, RgbColor.Black);
            var text = MacString.FromMacRoman(string.Create(CultureInfo.InvariantCulture, $"{count} item{(count == 1 ? "" : "s")}"));
            int textWidth = TextWidth(port, text, QuickDrawStyle.Plain, options);
            port.MoveTo((width - textWidth) / 2, 14);
            port.DrawText(text.Bytes);
        }

        // The scroll bars' place: a black edge and an empty trough, 15 pixels at the right and the bottom [Verified: Mac OS
        // 9.0 Finder, the geometry; ClassicMac: no arrows, thumb or grow box].
        private static void ScrollBars(QuickDrawPort port, int width, int height)
        {
            port.PenNormal();
            port.ForeColor = Trough;
            port.PaintRect(new MacRect(HeaderHeight, (short)(width - ScrollBarSize), (short)height, (short)width));
            port.PaintRect(new MacRect((short)(height - ScrollBarSize), 0, (short)height, (short)width));
            port.ForeColor = RgbColor.Black;
            port.PaintRect(new MacRect(HeaderHeight, (short)(width - ScrollBarSize), (short)height, (short)(width - ScrollBarSize + 1)));
            port.PaintRect(new MacRect((short)(height - ScrollBarSize), 0, (short)(height - ScrollBarSize + 1), (short)width));
        }

        // A neutral outline for an item with no icon [ClassicMac]: a page with a turned corner, a folder, or a diamond.
        private static void Placeholder(QuickDrawPort port, MacRect r, FinderItemKind kind)
        {
            port.PenNormal();
            port.ForeColor = RgbColor.Black;
            int t = r.Top, l = r.Left;
            MacPoint P(int v, int h) => new((short)(t + v), (short)(l + h));
            IReadOnlyList<MacPoint> outline = kind switch
            {
                FinderItemKind.Folder => [P(6, 1), P(3, 4), P(3, 12), P(6, 15), P(6, 30), P(27, 30), P(27, 1), P(6, 1)],
                FinderItemKind.Application => [P(1, 16), P(16, 31), P(31, 16), P(16, 1), P(1, 16)],
                _ => [P(1, 4), P(1, 20), P(9, 28), P(31, 28), P(31, 4), P(1, 4)],
            };
            port.ForeColor = RgbColor.White;
            port.PaintPoly(outline);
            port.ForeColor = RgbColor.Black;
            port.FramePoly(outline);
            if (kind == FinderItemKind.Document)
            {
                port.MoveTo(l + 20, t + 1);
                port.LineTo(l + 20, t + 9);
                port.LineTo(l + 28, t + 9);
            }
        }
    }
}
