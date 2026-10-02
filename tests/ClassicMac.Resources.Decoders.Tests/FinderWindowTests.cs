using ClassicMac.Core;
using ClassicMac.Graphics;
using ClassicMac.Graphics.QuickDraw;
using ClassicMac.Resources.Decoders.Finder;
using ClassicMac.Resources.Decoders.Images;

namespace ClassicMac.Resources.Decoders.Tests;

// The Finder's icon view of a folder window (docs/formats/file-systems/finder-windows.md).
public class FinderWindowTests
{
    private static readonly RgbaColor White = new(255, 255, 255), Black = new(0, 0, 0), Header = new(0xDD, 0xDD, 0xDD),
        Trough = new(0xEE, 0xEE, 0xEE);

    private const int Top = FinderWindowRenderer.HeaderHeight;

    // An ICN# whose image and mask are the full 32 x 32 square.
    internal static byte[] SolidIcon() => Enumerable.Repeat((byte)0xFF, 256).ToArray();

    internal static IconSuite Solid() => IconSuite.FromResources((type, _) => type.ToString() == "ICN#" ? SolidIcon() : null, 128);

    // A white image with a full mask.
    private static IconSuite Hollow()
    {
        var hollow = SolidIcon();
        Array.Clear(hollow, 0, 128);
        return IconSuite.FromResources((type, _) => type.ToString() == "ICN#" ? hollow : null, 1);
    }

    private static FinderWindowItem Item(string name, int v, int h, IconSuite? icon = null, ushort flags = FinderWindowItem.HasBeenInitedFlag,
        FinderItemKind kind = FinderItemKind.Document) =>
        new(MacString.FromMacRoman(name), new MacPoint((short)v, (short)h), flags, icon ?? Solid(), kind);

    private static FinderWindowItem Unplaced(string name) => Item(name, 0, 0);

    private static FinderWindow Window(params FinderWindowItem[] items) =>
        new() { Bounds = new MacRect(40, 10, 300, 500), Flags = FinderWindowItem.HasBeenInitedFlag, Items = items };

    // A 400 x 300 window, as the Finder's when it placed items.
    private static FinderWindow Window400(params FinderWindowItem[] items) =>
        new() { Bounds = new MacRect(60, 40, 360, 440), Flags = FinderWindowItem.HasBeenInitedFlag, Items = items };

    // A fallback that draws each character as a block `advance` wide and 9 tall standing on the baseline.
    private sealed class BlockText(int advance = 5) : ITextFallback
    {
        public List<(string Text, TextFallbackStyle Style)> Calls { get; } = [];

        public TextFallbackMask Render(string text, TextFallbackStyle style)
        {
            Calls.Add((text, style));
            int w = text.Length * advance;
            return new TextFallbackMask(w, 9, 0, 9, Enumerable.Repeat((byte)1, w * 9).ToArray(), w);
        }
    }

    private static FinderWindowOptions Blocks(int advance = 5) => new() { TextFallback = new BlockText(advance) };

    [Fact]
    public void The_bitmap_is_the_window_s_content_with_the_header_and_scroll_bars()
    {
        var bitmap = FinderWindowRenderer.Render(Window());

        Assert.Equal((490, 260), (bitmap.Width, bitmap.Height));
        // The header pane: a white top and left edge, grey, a darker bottom edge, then a black line.
        Assert.Equal(White, bitmap[5, 0]);
        Assert.Equal(White, bitmap[0, 10]);
        Assert.Equal(Header, bitmap[5, 10]);
        Assert.Equal(new RgbaColor(0xAA, 0xAA, 0xAA), bitmap[5, 19]);
        Assert.Equal(new RgbaColor(0xAA, 0xAA, 0xAA), bitmap[489, 10]);
        Assert.Equal(Black, bitmap[5, 20]);
        // The icon area on white, then the scroll bars: 15 pixels, a black edge and the trough.
        Assert.Equal(White, bitmap[5, 21]);
        Assert.Equal(White, bitmap[474, 244]);
        Assert.Equal(Black, bitmap[475, 100]);
        Assert.Equal(Trough, bitmap[480, 100]);
        Assert.Equal(Black, bitmap[100, 245]);
        Assert.Equal(Trough, bitmap[100, 250]);
    }

    [Fact]
    public void The_header_says_how_many_items_the_window_shows()
    {
        var text = new BlockText();

        FinderWindowRenderer.Render(Window(Item("a", 20, 30), Item("b", 20, 130), Item("c", 20, 230, flags: FinderWindowItem.InvisibleFlag)),
            new FinderWindowOptions { TextFallback = text });
        FinderWindowRenderer.Render(Window(Item("a", 20, 30)), new FinderWindowOptions { TextFallback = text });

        Assert.Contains(text.Calls, c => c.Text == "2 items");
        Assert.Contains(text.Calls, c => c.Text == "1 item");
    }

    [Fact]
    public void A_window_without_a_rectangle_gets_the_default_size()
    {
        var bitmap = FinderWindowRenderer.Render(new FinderWindow());

        Assert.Equal((404, 218), (FinderWindowRenderer.DefaultWidth, FinderWindowRenderer.DefaultHeight));
        Assert.Equal((FinderWindowRenderer.DefaultWidth, FinderWindowRenderer.DefaultHeight), (bitmap.Width, bitmap.Height));
    }

    [Fact]
    public void A_window_without_a_rectangle_has_the_default_scroll_position()
    {
        var window = new FinderWindow { ScrollPosition = new MacPoint(100, 100), Items = [Item("", 20, 20)] };

        var placement = Assert.Single(FinderWindowRenderer.Place(window));
        var bitmap = FinderWindowRenderer.Render(window);

        // (20, 20) less (-8, -16): 28 below the header, 36 across.
        Assert.Equal(new MacRect(Top + 28, 36, Top + 60, 68), placement.IconRect);
        Assert.Equal(Black, bitmap[36, Top + 28]);
        Assert.Equal(new MacPoint(-8, -16), FinderWindowRenderer.DefaultScrollPosition);
    }

    [Fact]
    public void The_rectangle_counts_only_when_the_folder_has_been_inited()
    {
        var notInited = Window() with { Flags = 0 };
        var root = Window() with { IsVolumeRoot = true };

        Assert.Equal((FinderWindowRenderer.DefaultWidth, FinderWindowRenderer.DefaultHeight), Size(FinderWindowRenderer.Render(notInited)));
        Assert.Equal((490, 260), Size(FinderWindowRenderer.Render(root)));
        Assert.False(notInited.HasBounds);
        Assert.True(root.HasBounds);
        Assert.False((Window() with { Bounds = default }).HasBounds);
    }

    private static (int, int) Size(RgbaBitmap bitmap) => (bitmap.Width, bitmap.Height);

    [Fact]
    public void An_icon_sits_at_its_location_less_the_scroll_position_below_the_header()
    {
        var window = Window(Item("", 20, 30)) with { ScrollPosition = new MacPoint(10, 5) };

        var bitmap = FinderWindowRenderer.Render(window);

        Assert.Equal(Black, bitmap[25, Top + 10]);
        Assert.Equal(Black, bitmap[56, Top + 41]);
        Assert.Equal(White, bitmap[24, Top + 10]);
        Assert.Equal(White, bitmap[25, Top + 9]);
        Assert.Equal(White, bitmap[57, Top + 20]);
        var placement = Assert.Single(FinderWindowRenderer.Place(window));
        Assert.Equal(new MacRect(Top + 10, 25, Top + 42, 57), placement.IconRect);
        Assert.Equal(new MacPoint(20, 30), placement.Location);
        Assert.False(placement.Arranged);
    }

    [Fact]
    public void Invisible_items_are_not_drawn()
    {
        var window = Window(Item("Icon\r", 20, 30, flags: FinderWindowItem.InvisibleFlag));

        Assert.Empty(FinderWindowRenderer.Place(window));
        Assert.Equal(White, FinderWindowRenderer.Render(window)[40, Top + 30]);
    }

    [Theory]
    [InlineData("Desktop DB", FinderItemKind.Document)]
    [InlineData("Desktop DF", FinderItemKind.Document)]
    [InlineData("Desktop", FinderItemKind.Document)]
    [InlineData("AppleShare PDS", FinderItemKind.Document)]
    [InlineData("DesktopPrinters DB", FinderItemKind.Document)]
    [InlineData("Finder", FinderItemKind.Document)]
    [InlineData("OpenFolderListDF", FinderItemKind.Document)]
    [InlineData("Shutdown Check", FinderItemKind.Document)]
    [InlineData("VM Storage", FinderItemKind.Document)]
    [InlineData("Temporary Items", FinderItemKind.Folder)]
    [InlineData("Trash", FinderItemKind.Folder)]
    [InlineData("Desktop Folder", FinderItemKind.Folder)]
    [InlineData("Move&Rename", FinderItemKind.Folder)]
    [InlineData("TheVolumeSettingsFolder", FinderItemKind.Folder)]
    public void The_volume_s_own_files_and_folders_are_left_out_of_its_root_window(string name, FinderItemKind kind)
    {
        var invisible = Item(name, 20, 30, flags: FinderWindowItem.HasBeenInitedFlag, kind: kind);
        var other = kind == FinderItemKind.Folder ? FinderItemKind.Document : FinderItemKind.Folder;

        Assert.Empty(FinderWindowRenderer.Place(Window(invisible) with { IsVolumeRoot = true }));
        Assert.Single(FinderWindowRenderer.Place(Window(invisible)));
        Assert.Single(FinderWindowRenderer.Place(Window(invisible with { Kind = other }) with { IsVolumeRoot = true }));
    }

    [Theory]
    // Inited: the location as it is, except (0, 0) and (-1, -1).
    [InlineData(0x0100, 10, 20, true, 10, 20)]
    [InlineData(0x0100, 0, 0, false, 0, 0)]
    [InlineData(0x0100, -1, -1, false, 0, 0)]
    [InlineData(0x0100, -5000, 9000, true, -5000, 9000)]
    // Not inited: 20000 added to each coordinate (16-bit), kept when -4000 < h < 4000 and v > -4000.
    [InlineData(0, 0, 0, false, 0, 0)]
    [InlineData(0, -19995, -19993, true, 5, 7)]
    [InlineData(0, -19995, -16001, true, 5, 3999)]
    [InlineData(0, -19995, -16000, false, 0, 0)]
    [InlineData(0, -19995, -23999, true, 5, -3999)]
    [InlineData(0, -19995, -24000, false, 0, 0)]
    [InlineData(0, -23999, -20000, true, -3999, 0)]
    [InlineData(0, -24000, -20000, false, 0, 0)]
    [InlineData(0, 12767, -20000, true, 32767, 0)]
    [InlineData(0, 12768, -20000, false, 0, 0)]
    [InlineData(0, -20000, -20000, false, 0, 0)]
    [InlineData(0, -20001, -20001, false, 0, 0)]
    public void An_item_s_position_follows_the_Finder_s_rules(int flags, int v, int h, bool placed, int pv, int ph)
    {
        var item = Item("", v, h, flags: (ushort)flags);

        if (placed) Assert.Equal(new MacPoint((short)pv, (short)ph), item.Position);
        else Assert.Null(item.Position);
        Assert.Equal(placed, item.HasLocation);
    }

    [Fact]
    public void A_folder_that_has_not_been_inited_is_arranged_whatever_its_location()
    {
        var folder = Item("Folder", 20, 20, flags: 0, kind: FinderItemKind.Folder);

        var placements = FinderWindowRenderer.Place(Window400(Item("placed", 300, 300), folder), Blocks());

        Assert.Null(folder.Position);
        Assert.Equal(new MacPoint(64, 129), placements[1].Location);
        Assert.True(placements[1].Arranged);
    }

    [Fact]
    public void A_not_inited_location_out_of_range_horizontally_has_no_position()
    {
        // h 0 + 20000 is out of range even though v would be fine.
        Assert.Null(Item("", -19995, 0, flags: 0).Position);
    }

    [Fact]
    public void Without_placed_items_arranging_starts_at_the_grid_s_origin()
    {
        // 400 wide: 385 visible, so cells at h 1, 129 and 257 fit, and 385 does not.
        var window = Window400(Unplaced("a"), Unplaced("b"), Unplaced("c"), Unplaced("d"));

        var placements = FinderWindowRenderer.Place(window, Blocks());

        Assert.Equal([new MacPoint(0, 1), new MacPoint(0, 129), new MacPoint(0, 257), new MacPoint(64, 1)], placements.Select(p => p.Location));
        Assert.All(placements, p => Assert.True(p.Arranged));
        Assert.Equal(new MacRect(Top + 64, 1, Top + 96, 33), placements[3].IconRect);
    }

    [Fact]
    public void With_placed_items_arranging_starts_one_cell_in_and_skips_cells_near_placed_icons_and_names()
    {
        // The placed icon at (70, 140) and its label take the cell at (64, 129); the next is (64, 257).
        var window = Window400(Item("placed", 70, 140), Unplaced("a"));

        var placements = FinderWindowRenderer.Place(window, Blocks());

        Assert.Equal(new MacPoint(64, 257), placements[1].Location);
    }

    [Fact]
    public void A_placed_icon_s_space_is_widened_by_the_views_font_size()
    {
        // The cell's icon covers h 129..160 at v 64; a placed icon from h 170 reaches 160 once widened by 10, from 171 not.
        var near = FinderWindowRenderer.Place(Window400(Item("", 64, 170), Unplaced("a")), Blocks());
        var far = FinderWindowRenderer.Place(Window400(Item("", 64, 171), Unplaced("a")), Blocks());

        Assert.Equal(new MacPoint(64, 257), near[1].Location);
        Assert.Equal(new MacPoint(64, 129), far[1].Location);
    }

    [Fact]
    public void An_arranged_item_s_name_must_be_clear_too()
    {
        // A placed icon below the cell at (64, 129), where only the arranged item's name (v 96..109) reaches.
        var blocked = FinderWindowRenderer.Place(Window400(Item("", 100, 135), Unplaced("a")), Blocks());
        var clear = FinderWindowRenderer.Place(Window400(Item("", 109, 135), Unplaced("a")), Blocks());

        Assert.Equal(new MacPoint(64, 257), blocked[1].Location);
        Assert.Equal(new MacPoint(64, 129), clear[1].Location);
    }

    [Fact]
    public void Arranged_items_take_space_from_those_arranged_after_them()
    {
        var placements = FinderWindowRenderer.Place(Window400(Item("", 300, 300), Unplaced("a"), Unplaced("b")), Blocks());

        Assert.Equal([new MacPoint(64, 129), new MacPoint(64, 257)], placements.Skip(1).Select(p => p.Location));
    }

    [Fact]
    public void Arranging_starts_from_the_scroll_position()
    {
        // Visible from (100, 50): the first grid point at or past (104, 66) is (128, 129); 129 + 128 <= 50 + 385.
        var window = Window400(Item("", 600, 600), Unplaced("a"), Unplaced("b"), Unplaced("c")) with { ScrollPosition = new MacPoint(100, 50) };

        var placements = FinderWindowRenderer.Place(window, Blocks());

        Assert.Equal([new MacPoint(128, 129), new MacPoint(128, 257), new MacPoint(192, 129)], placements.Skip(1).Select(p => p.Location));
        Assert.Equal(new MacRect(Top + 28, 79, Top + 60, 111), placements[1].IconRect);
    }

    [Fact]
    public void A_window_narrower_than_a_cell_takes_one_item_per_row()
    {
        var window = new FinderWindow { Bounds = new MacRect(0, 0, 300, 100), Flags = FinderWindowItem.HasBeenInitedFlag, Items = [Unplaced("a"), Unplaced("b")] };

        Assert.Equal([new MacPoint(0, 1), new MacPoint(64, 1)], FinderWindowRenderer.Place(window, Blocks()).Select(p => p.Location));
    }

    // An equivalent of a folder the Mac OS 9.0 Finder arranged: placed items, then two that had no position.
    [Fact]
    public void Two_unplaced_items_go_below_the_placed_ones_as_the_Finder_put_them()
    {
        FinderWindowItem P(string name, int h, int v, FinderItemKind kind = FinderItemKind.Document) => Item(name, v, h, kind: kind);
        var window = Window400(
            P("A", 10, 10), P("B", 100, 13), P("C", 37, 90), P("D", 250, 40), P("E", 5, 200), P(" ", 300, 180),
            P("Long name with spaces, 31 chrs.", 150, 95), P("LongNameWithoutAnySpaces_31chrs", 150, 140), P("Fourteen chars", 175, 13),
            P("Generic Doc", 340, 10), P("Generic App", 340, 90), P("Custom Folder", 90, 230, FinderItemKind.Folder),
            P("Plain Folder", 160, 230, FinderItemKind.Folder), P("Locked", 230, 230), P("Stationery", 300, 240), P("Alias", 360, 240),
            P("Label 2", 280, 110), P("Label 6", 350, 150),
            Item("Auto 1", 0, 0, flags: 0x0400), Item("Auto 2", 0, 0, flags: 0x0400));

        var placements = FinderWindowRenderer.Place(window, Blocks(6));

        Assert.Equal([new MacPoint(320, 129), new MacPoint(320, 257)], placements.Where(p => p.Arranged).Select(p => p.Location));
    }

    [Fact]
    public void A_window_without_a_rectangle_grows_to_hold_its_arranged_items()
    {
        var items = Enumerable.Range(0, 40).Select(i => Unplaced($"{i}")).ToArray();

        var bitmap = FinderWindowRenderer.Render(new FinderWindow { Items = items });

        // Visible from h -16 to 373: two cells a row (1, 129), so 20 rows; the last at v 1216, drawn 8 lower.
        Assert.Equal(FinderWindowRenderer.DefaultWidth, bitmap.Width);
        Assert.Equal(Top + 1216 + 8 + FinderWindowRenderer.CellHeight + FinderWindowRenderer.ScrollBarSize, bitmap.Height);
    }

    [Fact]
    public void The_label_rectangle_is_centred_under_the_icon_by_an_arithmetic_shift()
    {
        Assert.Equal(new MacRect(52, 39, 65, 53), FinderWindowRenderer.LabelRect(new MacPoint(20, 30), 10));
        // (-5) >> 1 is -3: odd widths lean left.
        Assert.Equal(new MacRect(52, 41, 65, 50), FinderWindowRenderer.LabelRect(new MacPoint(20, 30), 5));
        Assert.Equal(new MacRect(52, 44, 65, 48), FinderWindowRenderer.LabelRect(new MacPoint(20, 30), 0));
    }

    [Fact]
    public void The_name_is_drawn_two_pixels_into_its_rectangle_on_a_baseline_42_below_the_icon_s_top()
    {
        var text = new BlockText();
        var bitmap = FinderWindowRenderer.Render(Window(Item("AB", 20, 30), Item("A", 120, 30)), new FinderWindowOptions { TextFallback = text });

        // Measured, then drawn: Geneva (3) 10, plain.
        Assert.All(text.Calls.Where(c => c.Text is "AB" or "A"), c => Assert.Equal(new TextFallbackStyle(3, 0, 10), c.Style with { FontName = null }));
        // "AB", 10 wide: the rectangle from 30 + 16 - 5 - 2 = 39, the text from 41 to 50, in the 9 rows above the
        // baseline at 21 + 20 + 42.
        int baseline = Top + 20 + 42;
        Assert.Equal(Black, bitmap[41, baseline - 1]);
        Assert.Equal(Black, bitmap[50, baseline - 9]);
        Assert.Equal(White, bitmap[40, baseline - 1]);
        Assert.Equal(White, bitmap[51, baseline - 1]);
        Assert.Equal(White, bitmap[41, baseline]);
        Assert.Equal(White, bitmap[41, baseline - 10]);
        // "A", 5 wide: from 30 + 16 - 3 - 2 + 2 = 43 to 47.
        Assert.Equal(Black, bitmap[43, baseline + 99]);
        Assert.Equal(Black, bitmap[47, baseline + 99]);
        Assert.Equal(White, bitmap[48, baseline + 99]);
        Assert.Equal(White, bitmap[42, baseline + 99]);
    }

    [Fact]
    public void An_alias_s_name_is_italic()
    {
        var text = new BlockText();

        FinderWindowRenderer.Render(Window(Item("A", 20, 30, flags: FinderWindowItem.AliasFlag | FinderWindowItem.HasBeenInitedFlag)),
            new FinderWindowOptions { TextFallback = text });

        Assert.All(text.Calls.Where(c => c.Text == "A"), c => Assert.Equal((int)QuickDrawStyle.Italic, c.Style.Face));
        Assert.Contains(text.Calls, c => c.Text == "A");
    }

    [Fact]
    public void The_label_uses_a_bitmap_font_when_the_library_has_it()
    {
        var fonts = new FontLibrary();
        fonts.AddFont(3 * 128 + 10, ClassicMac.Graphics.Tests.FontBuilder.Sample());
        var text = new BlockText();

        var bitmap = FinderWindowRenderer.Render(Window(Item("AC", 20, 30)), new FinderWindowOptions { Fonts = fonts, TextFallback = text });

        Assert.DoesNotContain(text.Calls, c => c.Text == "AC");
        // "AC" advances 7: the rectangle from 30 + 16 - 4 - 2 = 40, the pen at 42, kerned 1 to the left; ascent 4 above the
        // baseline.
        int baseline = Top + 20 + 42, inside = 0, outside = 0;
        for (int y = Top + 52; y < Top + 70; y++)
            for (int x = 30; x < 62; x++)
                if (bitmap[x, y] == Black)
                {
                    if (x is >= 41 and < 49 && y >= baseline - 4 && y < baseline) inside++;
                    else outside++;
                }
        Assert.True(inside > 0);
        Assert.Equal(0, outside);
    }

    [Fact]
    public void A_labelled_icon_is_tinted_with_its_label_s_colour()
    {
        var item = Item("", 20, 30, flags: FinderWindowItem.HasBeenInitedFlag | (6 << 1));
        var colours = Enumerable.Repeat(new RgbColor(0, 0, 0), 8).ToArray();
        colours[6] = new RgbColor(0xFFFF, 0, 0);

        var system = FinderWindowRenderer.Render(Window(item));
        var custom = FinderWindowRenderer.Render(Window(item), new FinderWindowOptions { LabelColors = colours });

        Assert.Equal(6, item.Label);
        Assert.Equal(new RgbaColor(0xDD, 0x08, 0x06), system[40, Top + 30]);
        Assert.Equal(new RgbaColor(0xFF, 0, 0), custom[40, Top + 30]);
    }

    [Fact]
    public void Badges_are_drawn_over_the_icon()
    {
        var leftHalf = SolidIcon();
        for (int row = 0; row < 32; row++)
        {
            leftHalf[row * 4 + 2] = leftHalf[row * 4 + 3] = 0;           // image
            leftHalf[128 + row * 4 + 2] = leftHalf[128 + row * 4 + 3] = 0; // mask
        }
        var badge = IconSuite.FromResources((type, _) => type.ToString() == "ICN#" ? leftHalf : null, 2);
        var item = Item("", 20, 30, Hollow()) with { Badges = [badge] };

        var bitmap = FinderWindowRenderer.Render(Window(item));

        Assert.Equal(Black, bitmap[35, Top + 30]);
        Assert.Equal(White, bitmap[55, Top + 30]);
    }

    [Theory]
    [InlineData(FinderItemKind.Document)]
    [InlineData(FinderItemKind.Application)]
    [InlineData(FinderItemKind.Folder)]
    public void An_item_without_an_icon_gets_a_placeholder_for_its_kind(FinderItemKind kind)
    {
        var withoutIcon = new FinderWindowItem(MacString.FromMacRoman(""), new MacPoint(20, 30), FinderWindowItem.HasBeenInitedFlag, null, kind);
        var noMask = withoutIcon with { Icon = IconSuite.FromResources((_, _) => null, 0) };

        var a = FinderWindowRenderer.Render(Window(withoutIcon));
        var b = FinderWindowRenderer.Render(Window(noMask));

        var inked = Ink(a, 30, Top + 20);
        Assert.InRange(inked, 1, 32 * 32 - 1);
        Assert.Equal(a.Pixels, b.Pixels);
    }

    [Fact]
    public void The_placeholders_differ_by_kind()
    {
        byte[] Draw(FinderItemKind kind) =>
            FinderWindowRenderer.Render(Window(new FinderWindowItem(MacString.FromMacRoman(""), new MacPoint(20, 30), FinderWindowItem.HasBeenInitedFlag, null, kind))).Pixels;

        Assert.NotEqual(Draw(FinderItemKind.Document), Draw(FinderItemKind.Folder));
        Assert.NotEqual(Draw(FinderItemKind.Document), Draw(FinderItemKind.Application));
        Assert.NotEqual(Draw(FinderItemKind.Folder), Draw(FinderItemKind.Application));
    }

    [Fact]
    public void Items_are_drawn_in_order_so_later_ones_cover_earlier_ones()
    {
        var bitmap = FinderWindowRenderer.Render(Window(Item("", 20, 30), Item("", 20, 46, Hollow())));

        Assert.Equal(Black, bitmap[40, Top + 30]);
        Assert.Equal(White, bitmap[50, Top + 30]);
    }

    [Fact]
    public void Default_options_draw_Geneva_10_on_a_32_bit_Mac_OS_9_screen()
    {
        var options = FinderWindowOptions.Default;

        Assert.Equal((3, 10, 32, QuickDrawVersion.MacOS9), (options.LabelFontId, options.LabelFontSize, options.ScreenDepth, options.QuickDraw));
        Assert.Null(options.Fonts);
        Assert.Null(options.TextFallback);
        Assert.Null(options.LabelColors);
    }

    [Theory]
    [InlineData(0x0100, 0, 0, FinderViewKind.LargeIcon, 0, 0)]
    [InlineData(0x0107, 0, 0, FinderViewKind.LargeIcon, 0, 7)]
    [InlineData(0x0147, 0, 0, FinderViewKind.LargeIcon, 0, 7)]      // frView's 0x40 counts only with frScript's 0x40
    [InlineData(0x0000, 0, 0, FinderViewKind.LargeIcon, 0, 0)]
    [InlineData(0x0144, 0x50, 0, FinderViewKind.SmallIcon, 0, 4)]   // as the Finder writes it
    [InlineData(0x0104, 0x50, 0, FinderViewKind.LargeIcon, 0, 4)]
    [InlineData(0x0140, 0x10, 0, FinderViewKind.LargeIcon, 0, 0)]
    [InlineData(0x0140, 0xC0, 0, FinderViewKind.SmallIcon, 0, 0)]
    [InlineData(0x0100, 0x20, 0, FinderViewKind.Button, 0, 0)]
    [InlineData(0x0100, 0x28, 0, FinderViewKind.Button, 0, 0)]      // small buttons need 0x40 too
    [InlineData(0x0140, 0x60, 0, FinderViewKind.Button, 0, 0)]
    [InlineData(0x0005, 0x68, 0, FinderViewKind.SmallButton, 0, 5)] // as the Finder writes it
    [InlineData(0x0100, 0xA0, 0, FinderViewKind.LargeIcon, 0, 0)]   // bit 7: not buttons
    [InlineData(0x0340, 0x20, 0, FinderViewKind.List, 3, 0)]
    [InlineData(0x0806, 0, 0, FinderViewKind.List, 8, 6)]
    [InlineData(0x0900, 0, 0, FinderViewKind.List, 2, 0)]
    [InlineData(0x0F00, 0, 0, FinderViewKind.List, 2, 0)]
    [InlineData(0x0200, 0, 0x00140000, FinderViewKind.List, 5, 0)]
    [InlineData(0x0200, 0, 0x7FC3FFFF, FinderViewKind.List, 0, 0)]
    [InlineData(0x1100, 0x20, 0, FinderViewKind.Button, 0, 0)]
    public void The_view_comes_from_frView_frScript_and_frOpenChain(int view, int script, int openChain, FinderViewKind kind, int style, int arrange)
    {
        var read = FinderView.Read((short)view, (sbyte)script, openChain);

        Assert.Equal(new FinderView(kind, style, arrange), read);
    }

    [Theory]
    [InlineData(FinderViewKind.LargeIcon, "icon view")]
    [InlineData(FinderViewKind.SmallIcon, "small icon view")]
    [InlineData(FinderViewKind.Button, "button view")]
    [InlineData(FinderViewKind.SmallButton, "small button view")]
    [InlineData(FinderViewKind.List, "list view")]
    public void Views_have_names(FinderViewKind kind, string name) => Assert.Equal(name, new FinderView(kind, 0, 0).Name);

    [Fact]
    public void A_window_s_view_is_large_icons_unless_said()
    {
        Assert.Equal(FinderViewKind.LargeIcon, new FinderWindow().View.Kind);
    }

    private static FinderWindow In(FinderViewKind kind, FinderWindow window) => window with { View = new FinderView(kind, 0, 0) };

    private static RgbaColor Grey(int v) => new((byte)v, (byte)v, (byte)v);

    // A fallback whose condensed text is a pixel a character narrower, as the Font Manager's condense.
    private sealed class CondensingText : ITextFallback
    {
        public List<(string Text, TextFallbackStyle Style)> Calls { get; } = [];

        public TextFallbackMask Render(string text, TextFallbackStyle style)
        {
            Calls.Add((text, style));
            int advance = (style.Face & (int)QuickDrawStyle.Condense) != 0 ? 4 : 5, w = text.Length * advance;
            return new TextFallbackMask(w, 9, 0, 9, Enumerable.Repeat((byte)1, w * 9).ToArray(), w);
        }
    }

    [Fact]
    public void Small_icons_are_16_pixels_with_the_name_flush_left_beside_them()
    {
        var window = In(FinderViewKind.SmallIcon, Window(Item("AB", 20, 30)));

        var bitmap = FinderWindowRenderer.Render(window, Blocks());
        var placement = Assert.Single(FinderWindowRenderer.Place(window, Blocks()));

        Assert.Equal(new MacRect(Top + 20, 30, Top + 36, 46), placement.IconRect);
        Assert.Equal(Black, bitmap[30, Top + 20]);
        Assert.Equal(Black, bitmap[45, Top + 35]);
        Assert.Equal(White, bitmap[30, Top + 36]);
        Assert.Equal(White, bitmap[46, Top + 30]);
        // The pen at h + 18 on a baseline 11 below the icon's top: "AB" from 48 to 57, in the 9 rows above it.
        int baseline = Top + 20 + 11;
        Assert.Equal(Black, bitmap[48, baseline - 1]);
        Assert.Equal(Black, bitmap[57, baseline - 9]);
        Assert.Equal(White, bitmap[47, baseline - 1]);
        Assert.Equal(White, bitmap[58, baseline - 1]);
        Assert.Equal(White, bitmap[48, baseline]);
    }

    [Fact]
    public void A_small_icon_s_name_too_wide_for_its_pane_is_condensed()
    {
        var text = new CondensingText();
        var name = new string('x', 34);   // 170 plain, 136 condensed; the pane takes 165

        FinderWindowRenderer.Render(In(FinderViewKind.SmallIcon, Window(Item(name, 20, 30))), new FinderWindowOptions { TextFallback = text });

        Assert.Contains(text.Calls, c => c.Text == name && c.Style.Face == (int)QuickDrawStyle.Condense);
        Assert.DoesNotContain(text.Calls, c => c.Text.Contains('…'));
    }

    [Fact]
    public void A_small_icon_s_name_too_wide_even_condensed_is_truncated_in_the_middle()
    {
        var text = new BlockText();
        var name = "ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefgh";   // 34 characters, 170 pixels either way

        FinderWindowRenderer.Render(In(FinderViewKind.SmallIcon, Window(Item(name, 20, 30))), new FinderWindowOptions { TextFallback = text });

        var drawn = text.Calls.Last(c => c.Text.Contains('…')).Text;
        Assert.Equal("ABCDEFGHIJKLMNOP…STUVWXYZabcdefgh", drawn);
        Assert.True(drawn.Length * 5 <= FinderWindowRenderer.SmallNameWidth - 2);
    }

    [Fact]
    public void Small_icons_are_arranged_down_columns_from_the_grid_s_origin()
    {
        var window = In(FinderViewKind.SmallIcon, new FinderWindow
        {
            Bounds = new MacRect(0, 0, 84, 340), Flags = FinderWindowItem.HasBeenInitedFlag,   // 48 visible: two rows
            Items = [Unplaced("a"), Unplaced("b"), Unplaced("c")],
        });

        Assert.Equal([new MacPoint(0, 2), new MacPoint(24, 2), new MacPoint(0, 194)], FinderWindowRenderer.Place(window, Blocks()).Select(p => p.Location));
    }

    [Fact]
    public void A_window_lower_than_a_small_icon_row_takes_one_item_a_column()
    {
        var window = In(FinderViewKind.SmallIcon, new FinderWindow
        {
            Bounds = new MacRect(0, 0, 50, 340), Flags = FinderWindowItem.HasBeenInitedFlag,   // 14 visible
            Items = [Unplaced("a"), Unplaced("b")],
        });

        Assert.Equal([new MacPoint(0, 2), new MacPoint(0, 194)], FinderWindowRenderer.Place(window, Blocks()).Select(p => p.Location));
    }

    [Fact]
    public void Small_icons_are_arranged_clear_of_all_the_placed_items_together()
    {
        // Placed at (10, 200) and (150, 400): together they cover v 10..166, so column 194 is free only from v 168,
        // although no item is near (48, 194).
        var window = In(FinderViewKind.SmallIcon, new FinderWindow
        {
            Bounds = new MacRect(0, 0, 400, 340), Flags = FinderWindowItem.HasBeenInitedFlag,
            Items = [Item("A", 10, 200), Item("B", 150, 400), Unplaced("a")],
        });

        Assert.Equal(new MacPoint(168, 194), FinderWindowRenderer.Place(window, Blocks())[2].Location);
    }

    [Fact]
    public void Large_buttons_are_bevelled_48_pixel_buttons_with_the_icon_centred_and_the_name_below()
    {
        var window = In(FinderViewKind.Button, Window(Item("AB", 20, 30)));

        var bitmap = FinderWindowRenderer.Render(window, Blocks());
        var placement = Assert.Single(FinderWindowRenderer.Place(window, Blocks()));

        // The button from (41, 22) to (88, 69): three rings, light at the top and left, dark at the bottom and right.
        Assert.Equal((Grey(0x66), Grey(0x55), Grey(0x55), Grey(0x33)), (bitmap[22, 41], bitmap[69, 41], bitmap[22, 88], bitmap[69, 88]));
        Assert.Equal((Grey(0x66), Grey(0x33), Grey(0x66), Grey(0x33)), (bitmap[40, 41], bitmap[40, 88], bitmap[22, 60], bitmap[69, 60]));
        Assert.Equal((Grey(0xCC), Grey(0xAA), Grey(0xAA), Grey(0x77)), (bitmap[23, 42], bitmap[68, 42], bitmap[23, 87], bitmap[68, 87]));
        Assert.Equal((Grey(0xFF), Grey(0xCC), Grey(0xCC), Grey(0x99)), (bitmap[24, 43], bitmap[67, 43], bitmap[24, 86], bitmap[67, 86]));
        Assert.Equal(Grey(0xCC), bitmap[26, 46]);
        Assert.Equal(White, bitmap[21, 60]);
        Assert.Equal(White, bitmap[70, 60]);
        // The icon 8 down in it, at the item's h.
        Assert.Equal(new MacRect(Top + 28, 30, Top + 60, 62), placement.IconRect);
        Assert.Equal(Black, bitmap[30, Top + 28]);
        Assert.Equal(Grey(0xCC), bitmap[29, Top + 28]);
        // The name centred on h + 16, on a baseline 60 below the item's top: "AB" from 41 to 50.
        int baseline = Top + 20 + 60;
        Assert.Equal(Black, bitmap[41, baseline - 1]);
        Assert.Equal(Black, bitmap[50, baseline - 9]);
        Assert.Equal(White, bitmap[40, baseline - 1]);
        Assert.Equal(White, bitmap[51, baseline - 1]);
    }

    [Theory]
    [InlineData(FinderViewKind.Button)]
    [InlineData(FinderViewKind.SmallButton)]
    public void A_button_s_icon_is_centred_by_its_mask(FinderViewKind kind)
    {
        // An ICN# whose image and mask leave the top 4 rows empty: centred, its 28 rows start 2 down.
        var lower = SolidIcon();
        Array.Clear(lower, 0, 16);
        Array.Clear(lower, 128, 16);
        var icon = IconSuite.FromResources((type, _) => type.ToString() == "ICN#" ? lower : null, 1);
        var window = In(kind, Window(Item("", 20, 30, icon)));

        var bitmap = FinderWindowRenderer.Render(window);
        var rect = Assert.Single(FinderWindowRenderer.Place(window)).IconRect;

        int size = rect.Height, top = rect.Top + size * 2 / 32, x = rect.Left + size / 2;
        Assert.Equal(Black, bitmap[x, top]);
        Assert.Equal(Grey(0xCC), bitmap[x, top - 1]);
        Assert.Equal(Grey(0xCC), bitmap[x, rect.Bottom - 1]);
    }

    [Fact]
    public void Small_buttons_are_28_pixels_with_a_16_pixel_icon()
    {
        var window = In(FinderViewKind.SmallButton, Window(Item("AB", 20, 30)));

        var bitmap = FinderWindowRenderer.Render(window, Blocks());
        var placement = Assert.Single(FinderWindowRenderer.Place(window, Blocks()));

        // The button from (41, 32) to (68, 59); the icon at (47, 38).
        Assert.Equal((Grey(0x66), Grey(0x33)), (bitmap[32, 41], bitmap[59, 68]));
        Assert.Equal(White, bitmap[31, 50]);
        Assert.Equal(White, bitmap[60, 50]);
        Assert.Equal(new MacRect(Top + 26, 38, Top + 42, 54), placement.IconRect);
        Assert.Equal(Black, bitmap[38, Top + 26]);
        Assert.Equal(Black, bitmap[53, Top + 41]);
        Assert.Equal(Grey(0xCC), bitmap[37, Top + 26]);
        // The name on a baseline 40 below the item's top.
        Assert.Equal(Black, bitmap[41, Top + 20 + 40 - 1]);
        Assert.Equal(White, bitmap[41, Top + 20 + 40]);
    }

    [Theory]
    [InlineData(FinderViewKind.Button, 86)]
    [InlineData(FinderViewKind.SmallButton, 62)]
    public void Buttons_are_arranged_in_rows_of_their_cells(FinderViewKind kind, int height)
    {
        var window = In(kind, Window400(Item("", 300, 300), Unplaced("a"), Unplaced("b"), Unplaced("c")));

        Assert.Equal([new MacPoint((short)height, 129), new MacPoint((short)height, 257), new MacPoint((short)(2 * height), 129)],
            FinderWindowRenderer.Place(window, Blocks()).Skip(1).Select(p => p.Location));
    }

    [Fact]
    public void A_list_view_is_drawn_as_large_icons()
    {
        var items = new[] { Item("AB", 20, 30), Unplaced("c") };

        Assert.Equal(FinderWindowRenderer.Render(Window(items), Blocks()).Pixels,
            FinderWindowRenderer.Render(In(FinderViewKind.List, Window(items)), Blocks()).Pixels);
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
