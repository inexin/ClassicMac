using System;
using System.Collections.Generic;
using System.Linq;
using ClassicMac.Core;
using ClassicMac.Graphics;
using ClassicMac.Graphics.QuickDraw;
using ClassicMac.Resources.Decoders.Images;

namespace ClassicMac.Resources.Decoders.Finder
{
    /// <summary>One item of a Finder window: a file or folder with its name, icon position, Finder flags and icon.</summary>
    /// <param name="Name">The name, drawn under the icon.</param>
    /// <param name="Location">The icon's top-left corner in the window's local coordinates (<c>fdLocation</c>,
    /// <c>frLocation</c>); (0, 0) or (−1, −1) when the Finder has not placed it.</param>
    /// <param name="Flags">The Finder flags (<c>fdFlags</c>, <c>frFlags</c>).</param>
    /// <param name="Icon">The icon (see <see cref="FinderIconResolver"/>); null draws a placeholder.</param>
    /// <param name="Kind">What the item is, for its placeholder.</param>
    public sealed record FinderWindowItem(MacString Name, MacPoint Location, ushort Flags, IconSuite? Icon, FinderItemKind Kind = FinderItemKind.Document)
    {
        /// <summary>The Finder flag <c>kIsInvisible</c>.</summary>
        public const ushort InvisibleFlag = 0x4000;

        /// <summary>The Finder flag <c>kIsAlias</c>.</summary>
        public const ushort AliasFlag = 0x8000;

        /// <summary>Whether the item is invisible, and not drawn.</summary>
        public bool IsInvisible => (Flags & InvisibleFlag) != 0;

        /// <summary>Whether the item is an alias, whose name the Finder draws in italics.</summary>
        public bool IsAlias => (Flags & AliasFlag) != 0;

        /// <summary>
        /// Whether the Finder has given the item a place: a location other than (0, 0) and (−1, −1)
        /// [Fitted: items Apple's installers wrote, never shown in a window, are at (−1, −1)].
        /// </summary>
        public bool HasLocation => Location != default && Location != new MacPoint(-1, -1);
    }

    /// <summary>A folder's window as the Finder records it: its rectangle and scroll position (<c>DInfo</c>, <c>DXInfo</c>), and its items.</summary>
    public sealed record FinderWindow
    {
        /// <summary>The window's content rectangle in global coordinates (<c>frRect</c>); empty when not recorded.</summary>
        public MacRect Bounds { get; init; }

        /// <summary>The local coordinates at the content's top-left corner (<c>frScroll</c>).</summary>
        public MacPoint ScrollPosition { get; init; }

        /// <summary>The items, in drawing order.</summary>
        public IReadOnlyList<FinderWindowItem> Items { get; init; } = [];
    }

    /// <summary>Where an item's icon is drawn.</summary>
    /// <param name="Item">The item.</param>
    /// <param name="IconRect">The 32 × 32 icon rectangle, in bitmap coordinates.</param>
    /// <param name="Arranged">Whether it was put in a grid cell because it had no location.</param>
    public sealed record FinderWindowPlacement(FinderWindowItem Item, MacRect IconRect, bool Arranged);

    /// <summary>How <see cref="FinderWindowRenderer"/> draws.</summary>
    public sealed class FinderWindowOptions
    {
        /// <summary>Geneva 9 labels, a 32-bit screen, Mac OS 9's QuickDraw, no fonts.</summary>
        public static FinderWindowOptions Default { get; } = new();

        /// <summary>The screen depth: 32 (the default) or 1, 2, 4, 8, 16.</summary>
        public int ScreenDepth { get; init; } = 32;

        /// <summary>Which QuickDraw draws the icons.</summary>
        public QuickDrawVersion QuickDraw { get; init; } = QuickDrawVersion.MacOS9;

        /// <summary>The labels' font family: 3, Geneva.</summary>
        public int LabelFontId { get; init; } = 3;

        /// <summary>The labels' size: 9.</summary>
        public int LabelFontSize { get; init; } = 9;

        /// <summary>Bitmap fonts for the labels; text no strike draws goes to <see cref="TextFallback"/>.</summary>
        public FontLibrary? Fonts { get; init; }

        /// <summary>Rasterizes labels no bitmap font draws; null draws no such labels.</summary>
        public ITextFallback? TextFallback { get; init; }
    }

    /// <summary>
    /// Draws a folder's window in icon view as the Finder lays it out (docs/formats/file-systems/finder-windows.md §5): the
    /// content area on white, each visible item's 32 × 32 icon at its location less the scroll position, its name centred
    /// under it, through a <see cref="QuickDrawPort"/>. No window frame, header or scroll bars.
    /// </summary>
    public static class FinderWindowRenderer
    {
        /// <summary>The size of a window that records no rectangle [ClassicMac].</summary>
        public const int DefaultWidth = 480, DefaultHeight = 300;

        /// <summary>The grid for items without a location: cells this wide and tall, from this far down [ClassicMac].</summary>
        public const int GridWidth = 80, GridHeight = 64, GridTop = 8;

        /// <summary>The gap between an icon's bottom and its label's top [ClassicMac].</summary>
        public const int LabelGap = 2;

        private const int IconSize = 32;

        /// <summary>Where the visible items' icons go, in drawing order.</summary>
        public static IReadOnlyList<FinderWindowPlacement> Place(FinderWindow window)
        {
            ArgumentNullException.ThrowIfNull(window);
            var (width, _) = Size(window, 0);
            return Place(window, width);
        }

        /// <summary>Draws the window's content.</summary>
        public static RgbaBitmap Render(FinderWindow window, FinderWindowOptions? options = null)
        {
            ArgumentNullException.ThrowIfNull(window);
            options ??= FinderWindowOptions.Default;
            var (width, _) = Size(window, 0);
            var placements = Place(window, width);
            int lowest = placements.Where(p => p.Arranged).Select(p => p.IconRect.Top - GridTop + GridHeight).DefaultIfEmpty(0).Max();
            var (_, height) = Size(window, GridTop + lowest);
            var canvas = new RgbaBitmap(width, height);
            var port = new QuickDrawPort(canvas, new QuickDrawOptions
            {
                ScreenDepth = options.ScreenDepth, Version = options.QuickDraw, Fonts = options.Fonts, TextFallback = options.TextFallback,
            });
            port.EraseRect(port.PortRect);
            foreach (var placement in placements)
            {
                if (placement.Item.Icon?.Plot(port, placement.IconRect) != true) Placeholder(port, placement.IconRect, placement.Item.Kind);
                Label(port, placement, options);
            }
            return canvas;
        }

        // The bitmap's size: the window's, or the default grown to hold the arranged items.
        private static (int Width, int Height) Size(FinderWindow window, int arrangedHeight)
        {
            if (!window.Bounds.IsEmpty) return (window.Bounds.Width, window.Bounds.Height);
            return (DefaultWidth, Math.Max(DefaultHeight, arrangedHeight));
        }

        // Placed items where they are; the others in the free cells of a grid across the width, in order [ClassicMac].
        private static List<FinderWindowPlacement> Place(FinderWindow window, int width)
        {
            var scroll = window.ScrollPosition;
            var visible = window.Items.Where(i => !i.IsInvisible).ToList();
            var taken = visible.Where(i => i.HasLocation).Select(i => IconRect(i.Location.V - scroll.V, i.Location.H - scroll.H)).ToList();
            int columns = Math.Max(1, width / GridWidth), cell = 0;
            var result = new List<FinderWindowPlacement>(visible.Count);
            foreach (var item in visible)
            {
                if (item.HasLocation)
                {
                    result.Add(new FinderWindowPlacement(item, IconRect(item.Location.V - scroll.V, item.Location.H - scroll.H), false));
                    continue;
                }
                MacRect rect;
                do
                {
                    rect = IconRect(GridTop + cell / columns * GridHeight, cell % columns * GridWidth + (GridWidth - IconSize) / 2);
                    cell++;
                }
                while (taken.Any(t => Intersects(t, rect)));
                taken.Add(rect);
                result.Add(new FinderWindowPlacement(item, rect, true));
            }
            return result;
        }

        private static MacRect IconRect(int top, int left) =>
            new((short)top, (short)left, (short)(top + IconSize), (short)(left + IconSize));

        private static bool Intersects(MacRect a, MacRect b) =>
            a.Left < b.Right && b.Left < a.Right && a.Top < b.Bottom && b.Top < a.Bottom;

        // The name in the label font, black, centred under the icon, its top LabelGap below it [ClassicMac]; an alias's
        // name in italics [Doc: Macintosh Toolbox Essentials, Finder Interface].
        private static void Label(QuickDrawPort port, FinderWindowPlacement placement, FinderWindowOptions options)
        {
            var name = placement.Item.Name;
            if (name.Length == 0) return;
            port.TextFont = options.LabelFontId;
            port.TextSize = options.LabelFontSize;
            var face = placement.Item.IsAlias ? QuickDrawStyle.Italic : QuickDrawStyle.Plain;
            port.TextFace = face;
            port.TextMode = TransferMode.SrcOr;
            port.ForeColor = RgbColor.Black;
            int width = port.TextWidth(name.Bytes), ascent = port.GetFontInfo().Ascent;
            if (width == 0 && options.TextFallback?.Render(name.ToMacRoman(), new TextFallbackStyle(options.LabelFontId, (int)face, options.LabelFontSize)) is { } mask)
                width = (int)Math.Round(mask.Advance);
            if (ascent == 0) ascent = options.LabelFontSize;          // [ClassicMac: no strike, so the size stands in]
            var rect = placement.IconRect;
            port.MoveTo(rect.Left + IconSize / 2 - width / 2, rect.Bottom + LabelGap + ascent);
            port.DrawText(name.Bytes);
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
