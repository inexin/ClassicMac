using ClassicMac.Core;
using ClassicMac.Graphics;
using ClassicMac.Graphics.QuickDraw;
using ClassicMac.Resources.Decoders.Finder;
using ClassicMac.Resources.Decoders.Images;

namespace ClassicMac.Resources.Decoders.Tests;

// The Finder's icon view of a folder window (docs/formats/file-systems/finder-windows.md).
public class FinderWindowTests
{
    private static readonly RgbaColor White = new(255, 255, 255), Black = new(0, 0, 0);

    // An ICN# whose image and mask are the full 32 x 32 square.
    internal static byte[] SolidIcon() => Enumerable.Repeat((byte)0xFF, 256).ToArray();

    internal static IconSuite Solid() => IconSuite.FromResources((type, _) => type.ToString() == "ICN#" ? SolidIcon() : null, 128);

    private static FinderWindowItem Item(string name, int v, int h, IconSuite? icon = null, ushort flags = 0,
        FinderItemKind kind = FinderItemKind.Document) =>
        new(MacString.FromMacRoman(name), new MacPoint((short)v, (short)h), flags, icon ?? Solid(), kind);

    private static FinderWindow Window(params FinderWindowItem[] items) =>
        new() { Bounds = new MacRect(40, 10, 300, 500), Items = items };

    // A fallback that draws each character as a 5 x 9 block standing on the baseline.
    private sealed class BlockText : ITextFallback
    {
        public List<(string Text, TextFallbackStyle Style)> Calls { get; } = [];

        public TextFallbackMask Render(string text, TextFallbackStyle style)
        {
            Calls.Add((text, style));
            int w = text.Length * 5;
            return new TextFallbackMask(w, 9, 0, 9, Enumerable.Repeat((byte)1, w * 9).ToArray(), w);
        }
    }

    [Fact]
    public void The_bitmap_is_the_window_s_content_on_white()
    {
        var bitmap = FinderWindowRenderer.Render(Window());

        Assert.Equal((490, 260), (bitmap.Width, bitmap.Height));
        Assert.Equal(White, bitmap[0, 0]);
        Assert.Equal(White, bitmap[489, 259]);
    }

    [Fact]
    public void A_window_without_a_rectangle_gets_the_default_size()
    {
        var bitmap = FinderWindowRenderer.Render(new FinderWindow());

        Assert.Equal((FinderWindowRenderer.DefaultWidth, FinderWindowRenderer.DefaultHeight), (bitmap.Width, bitmap.Height));
    }

    [Fact]
    public void An_icon_sits_at_its_location_less_the_scroll_position()
    {
        var window = Window(Item("", 20, 30)) with { ScrollPosition = new MacPoint(10, 5) };

        var bitmap = FinderWindowRenderer.Render(window);

        Assert.Equal(Black, bitmap[25, 10]);
        Assert.Equal(Black, bitmap[56, 41]);
        Assert.Equal(White, bitmap[24, 10]);
        Assert.Equal(White, bitmap[25, 9]);
        Assert.Equal(White, bitmap[57, 20]);
        var placement = Assert.Single(FinderWindowRenderer.Place(window));
        Assert.Equal(new MacRect(10, 25, 42, 57), placement.IconRect);
        Assert.False(placement.Arranged);
    }

    [Fact]
    public void Invisible_items_are_not_drawn()
    {
        var window = Window(Item("Icon\r", 20, 30, flags: FinderWindowItem.InvisibleFlag));

        Assert.Empty(FinderWindowRenderer.Place(window));
        Assert.Equal(White, FinderWindowRenderer.Render(window)[40, 30]);
    }

    [Fact]
    public void Items_without_a_location_are_arranged_in_free_grid_cells()
    {
        // The first cell is taken by a placed icon; the window is three cells wide.
        var window = new FinderWindow
        {
            Bounds = new MacRect(0, 0, 200, 3 * FinderWindowRenderer.GridWidth),
            Items = [Item("a", 0, 0), Item("placed", 10, 30), Item("b", 0, 0), Item("c", 0, 0)],
        };

        var placements = FinderWindowRenderer.Place(window);

        Assert.Equal(["a", "placed", "b", "c"], placements.Select(p => p.Item.Name.ToMacRoman()));
        Assert.Equal(new MacRect(10, 30, 42, 62), placements[1].IconRect);
        Assert.Equal([true, false, true, true], placements.Select(p => p.Arranged));
        int left = (FinderWindowRenderer.GridWidth - 32) / 2, top = FinderWindowRenderer.GridTop;
        Assert.Equal(new MacPoint((short)top, (short)(left + FinderWindowRenderer.GridWidth)), placements[0].IconRect.TopLeft);
        Assert.Equal(new MacPoint((short)top, (short)(left + 2 * FinderWindowRenderer.GridWidth)), placements[2].IconRect.TopLeft);
        Assert.Equal(new MacPoint((short)(top + FinderWindowRenderer.GridHeight), (short)left), placements[3].IconRect.TopLeft);
    }

    [Theory]
    [InlineData(0, 0, false)]
    [InlineData(-1, -1, false)]
    [InlineData(-1, 0, true)]
    [InlineData(0, 1, true)]
    public void Zero_and_minus_one_mean_no_location(int v, int h, bool placed) =>
        Assert.Equal(placed, Item("", v, h).HasLocation);

    [Fact]
    public void An_alias_s_name_is_italic()
    {
        var text = new BlockText();

        FinderWindowRenderer.Render(Window(Item("A", 20, 30, flags: FinderWindowItem.AliasFlag)), new FinderWindowOptions { TextFallback = text });

        Assert.All(text.Calls, c => Assert.Equal((int)QuickDrawStyle.Italic, c.Style.Face));
        Assert.NotEmpty(text.Calls);
    }

    [Fact]
    public void A_window_without_a_rectangle_grows_to_hold_its_arranged_items()
    {
        var items = Enumerable.Range(0, 40).Select(i => Item($"{i}", 0, 0)).ToArray();

        var bitmap = FinderWindowRenderer.Render(new FinderWindow { Items = items });

        int columns = FinderWindowRenderer.DefaultWidth / FinderWindowRenderer.GridWidth;
        int rows = (40 + columns - 1) / columns;
        Assert.Equal(FinderWindowRenderer.DefaultWidth, bitmap.Width);
        Assert.Equal(FinderWindowRenderer.GridTop + rows * FinderWindowRenderer.GridHeight, bitmap.Height);
    }

    [Fact]
    public void The_name_is_centred_under_the_icon_in_the_label_font()
    {
        var text = new BlockText();
        var window = Window(Item("AB", 20, 30));

        var bitmap = FinderWindowRenderer.Render(window, new FinderWindowOptions { TextFallback = text });

        // Measured, then drawn: Geneva (3) 9, plain.
        Assert.All(text.Calls, c => Assert.Equal(("AB", new TextFallbackStyle(3, 0, 9)), (c.Text, c.Style with { FontName = null })));
        // 10 pixels wide, centred on the icon (30..62): 41..51, from the line below the icon to the baseline.
        int top = 20 + 32 + FinderWindowRenderer.LabelGap;
        Assert.Equal(Black, bitmap[41, top + 4]);
        Assert.Equal(Black, bitmap[50, top + 4]);
        Assert.Equal(White, bitmap[40, top + 4]);
        Assert.Equal(White, bitmap[51, top + 4]);
    }

    [Fact]
    public void The_label_uses_a_bitmap_font_when_the_library_has_it()
    {
        var fonts = new FontLibrary();
        fonts.AddFont(3 * 128 + 9, ClassicMac.Graphics.Tests.FontBuilder.Sample());
        var text = new BlockText();

        var bitmap = FinderWindowRenderer.Render(Window(Item("AC", 20, 30)), new FinderWindowOptions { Fonts = fonts, TextFallback = text });

        Assert.Empty(text.Calls);
        // "AC" advances 4 + 3 = 7 pixels, centred on the icon's middle (46); the strike's ascent is 4.
        int top = 20 + 32 + FinderWindowRenderer.LabelGap;
        int inside = 0, outside = 0;
        for (int y = top - 2; y < top + 8; y++)
            for (int x = 30; x < 62; x++)
                if (bitmap[x, y] == Black)
                {
                    if (x is >= 41 and < 51 && y >= top && y < top + 5) inside++;
                    else outside++;
                }
        Assert.True(inside > 0);
        Assert.Equal(0, outside);
    }

    [Theory]
    [InlineData(FinderItemKind.Document)]
    [InlineData(FinderItemKind.Application)]
    [InlineData(FinderItemKind.Folder)]
    public void An_item_without_an_icon_gets_a_placeholder_for_its_kind(FinderItemKind kind)
    {
        var withoutIcon = new FinderWindowItem(MacString.FromMacRoman(""), new MacPoint(20, 30), 0, null, kind);
        var noMask = withoutIcon with { Icon = IconSuite.FromResources((_, _) => null, 0) };

        var a = FinderWindowRenderer.Render(Window(withoutIcon));
        var b = FinderWindowRenderer.Render(Window(noMask));

        var inked = Ink(a, 30, 20);
        Assert.InRange(inked, 1, 32 * 32 - 1);
        Assert.Equal(a.Pixels, b.Pixels);
    }

    [Fact]
    public void The_placeholders_differ_by_kind()
    {
        byte[] Draw(FinderItemKind kind) =>
            FinderWindowRenderer.Render(Window(new FinderWindowItem(MacString.FromMacRoman(""), new MacPoint(20, 30), 0, null, kind))).Pixels;

        Assert.NotEqual(Draw(FinderItemKind.Document), Draw(FinderItemKind.Folder));
        Assert.NotEqual(Draw(FinderItemKind.Document), Draw(FinderItemKind.Application));
        Assert.NotEqual(Draw(FinderItemKind.Folder), Draw(FinderItemKind.Application));
    }

    [Fact]
    public void Items_are_drawn_in_order_so_later_ones_cover_earlier_ones()
    {
        var hollow = SolidIcon();
        for (int i = 0; i < 128; i++) hollow[i] = 0;   // a white image with a full mask
        var white = IconSuite.FromResources((type, _) => type.ToString() == "ICN#" ? hollow : null, 1);

        var bitmap = FinderWindowRenderer.Render(Window(Item("", 20, 30), Item("", 20, 46, white)));

        Assert.Equal(Black, bitmap[40, 30]);
        Assert.Equal(White, bitmap[50, 30]);
    }

    [Fact]
    public void Default_options_draw_Geneva_9_on_a_32_bit_Mac_OS_9_screen()
    {
        var options = FinderWindowOptions.Default;

        Assert.Equal((3, 9, 32, QuickDrawVersion.MacOS9), (options.LabelFontId, options.LabelFontSize, options.ScreenDepth, options.QuickDraw));
        Assert.Null(options.Fonts);
        Assert.Null(options.TextFallback);
    }

    [Fact]
    public void Arguments_must_be_given()
    {
        Assert.Throws<ArgumentNullException>(() => FinderWindowRenderer.Render(null!));
        Assert.Throws<ArgumentNullException>(() => FinderWindowRenderer.Place(null!));
    }

    private static int Ink(RgbaBitmap bitmap, int left, int top)
    {
        int n = 0;
        for (int y = top; y < top + 32; y++)
            for (int x = left; x < left + 32; x++)
                if (bitmap[x, y] != White) n++;
        return n;
    }
}
