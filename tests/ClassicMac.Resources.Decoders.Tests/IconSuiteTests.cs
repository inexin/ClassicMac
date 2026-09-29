using ClassicMac.Core;
using ClassicMac.Graphics;
using ClassicMac.Graphics.QuickDraw;
using ClassicMac.Resources.Decoders.Images;

namespace ClassicMac.Resources.Decoders.Tests;

// Icon suites drawn as the Icon Utilities draw them (PlotIconSuite).
public class IconSuiteTests
{
    private static MacRect R(int top, int left, int bottom, int right) => new((short)top, (short)left, (short)bottom, (short)right);

    // 32 x 32: image a filled square 8..24, mask a square 4..28 (or `maskLeft` .. 28 for an off-centre one).
    private static byte[] IconList(int maskLeft = 4)
    {
        var list = new byte[256];
        for (int y = 0; y < 32; y++)
            for (int x = 0; x < 32; x++)
            {
                if (x >= 8 && x < 24 && y >= 8 && y < 24) list[y * 4 + x / 8] |= (byte)(0x80 >> (x & 7));
                if (x >= maskLeft && x < 28 && y >= 4 && y < 28) list[128 + y * 4 + x / 8] |= (byte)(0x80 >> (x & 7));
            }
        return list;
    }

    private static IconSuite Suite(params (string Type, byte[] Data)[] members)
    {
        var map = members.ToDictionary(m => m.Type, m => m.Data);
        return IconSuite.FromResources((type, id) => map.TryGetValue(type.ToString(), out var d) ? d : null, 128);
    }

    private static QuickDrawPort Port(int depth = 32, QuickDrawVersion version = QuickDrawVersion.MacOS9) =>
        new(new RgbaBitmap(48, 48), new QuickDrawOptions { ScreenDepth = depth, Version = version });

    [Fact]
    public void A_one_bit_icon_draws_its_image_inside_its_mask()
    {
        var port = Port();
        Assert.True(Suite(("ICN#", IconList())).Plot(port, R(0, 0, 32, 32)));
        Assert.Equal(new RgbaColor(0, 0, 0), port.Canvas[10, 10]);
        Assert.Equal(new RgbaColor(255, 255, 255), port.Canvas[5, 5]);
        Assert.Equal(0, port.Canvas[2, 2].A);
    }

    [Fact]
    public void Selected_darkens_the_background_and_disabled_greys_the_image()
    {
        var port = Port();
        Suite(("ICN#", IconList())).Plot(port, R(0, 0, 32, 32), transform: IconTransform.Selected);
        Assert.Equal(new RgbaColor(0x7F, 0x7F, 0x7F), port.Canvas[5, 5]);   // white $FFFF -> $7FFF
        Assert.Equal(new RgbaColor(0, 0, 0), port.Canvas[10, 10]);

        port = Port();
        Suite(("ICN#", IconList())).Plot(port, R(0, 0, 32, 32), transform: IconTransform.Disabled);
        Assert.Equal(new RgbaColor(0x80, 0x80, 0x80), port.Canvas[10, 10]); // GetGray's midpoint
    }

    [Fact]
    public void Colour_members_draw_through_the_system_table_with_the_label_baked_in()
    {
        var icl8 = Enumerable.Repeat((byte)5, 1024).ToArray();          // clut 8 entry 5: $FFFF, $FFFF, $0000
        var suite = Suite(("ICN#", IconList()), ("icl8", icl8));
        var port = Port();
        suite.Plot(port, R(0, 0, 32, 32));
        Assert.Equal(new RgbaColor(0xFF, 0xFF, 0x00), port.Canvas[10, 10]);

        port = Port();
        suite.Plot(port, R(0, 0, 32, 32), transform: IconTransform.Selected);
        Assert.Equal(new RgbaColor(0x7F, 0x7F, 0x00), port.Canvas[10, 10]); // Darken: halved; blue already smallest

        port = Port(depth: 4);                                              // 4 bits: no icl4, so the 1-bit member
        suite.Plot(port, R(0, 0, 32, 32));
        Assert.Equal(new RgbaColor(0, 0, 0), port.Canvas[10, 10]);
    }

    [Fact]
    public void No_one_bit_member_means_nothing_is_drawn()
    {
        var port = Port();
        Assert.False(Suite(("icl8", new byte[1024])).Plot(port, R(0, 0, 32, 32)));
        Assert.All(Enumerable.Range(0, 48 * 48), i => Assert.Equal(0, port.Canvas.Pixels[4 * i + 3]));
    }

    [Fact]
    public void Alignment_moves_the_rect_by_the_masks_bounding_box()
    {
        // The mask runs 12..28 across: centred in a 32-wide rect it moves 4 left (floor of half of -8).
        var port = Port();
        Suite(("ICN#", IconList(maskLeft: 12))).Plot(port, R(0, 8, 32, 40), IconAlignment.AbsoluteCenter);
        Assert.Equal(0, port.Canvas[8 + 12 - 5, 10].A);
        Assert.Equal(255, port.Canvas[8 + 12 - 4, 10].A);
        Assert.Equal(255, port.Canvas[8 + 27 - 4, 10].A);
        Assert.Equal(0, port.Canvas[8 + 28 - 4, 10].A);
    }

    [Fact]
    public void Offline_dots_follow_the_icon_on_the_bitmap_path_and_the_port_on_the_region_path()
    {
        var suite = Suite(("ICN#", IconList()));
        var bitmap = Port();
        suite.Plot(bitmap, R(0, 0, 32, 32), transform: IconTransform.Offline);
        Assert.Equal(new RgbaColor(0, 0, 0), bitmap.Canvas[4, 4]);          // row 4 even, x 4 % 4 == 0: a dot
        Assert.Equal(new RgbaColor(255, 255, 255), bitmap.Canvas[5, 4]);

        var region = Port();
        suite.Plot(region, R(0, 0, 36, 36), transform: IconTransform.Offline);   // not 32 x 32: regions (Mac OS 9)
        var pixels = Enumerable.Range(0, 36).Select(x => region.Canvas[x, 5]).ToList();
        Assert.Contains(new RgbaColor(0, 0, 0), pixels.Skip(4).Take(4));
    }

    [Fact]
    public void Colour_arithmetic_matches_the_Icon_Utilities()
    {
        Assert.Equal(new RgbColor(0x7FFF, 0x7FFF, 0x7FFF), IconSuite.Darken(RgbColor.White));
        Assert.Equal(new RgbColor(0x7FFF, 0x7FFF, 0x0000), IconSuite.Darken(new RgbColor(0xFFFF, 0xFFFF, 0x0000)));   // blue, the smallest, stays 0
        Assert.Equal(RgbColor.White, IconSuite.Brighten(RgbColor.Black));
    }
}
