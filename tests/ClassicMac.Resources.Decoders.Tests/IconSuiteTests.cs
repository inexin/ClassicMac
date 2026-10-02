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

    // An icon family (icns) of the given members, read back as a suite (IconFamilyToIconSuite).
    private static IconSuite Family(params (string Type, byte[] Data)[] members)
    {
        var writer = new BigEndianWriter();
        writer.WriteFourCC(FourCC.FromString("icns"));
        writer.WriteUInt32(0u);
        foreach (var (type, data) in members)
        {
            writer.WriteFourCC(FourCC.FromString(type));
            writer.WriteUInt32(data.Length + 8);
            writer.WriteBytes(data);
        }
        writer.WriteUInt32At(4, writer.WrittenSpan.Length);
        return IconSuite.FromFamily(IconFamily.ReadIcns(writer.ToArray()));
    }

    private static byte[] Fill(int length, byte value) => Enumerable.Repeat(value, length).ToArray();

    // Raw il32 (ARGB) of one colour.
    private static byte[] Il32(byte r, byte g, byte b, int pixels = 1024)
    {
        var data = new byte[4 * pixels];
        for (int i = 0; i < pixels; i++) (data[4 * i + 1], data[4 * i + 2], data[4 * i + 3]) = (r, g, b);
        return data;
    }

    // A port whose canvas is painted one colour first.
    private static QuickDrawPort Background(int depth, RgbColor colour, QuickDrawVersion version = QuickDrawVersion.MacOS9)
    {
        var port = Port(depth, version);
        port.ForeColor = colour;
        port.PaintRect(R(0, 0, 48, 48));
        port.ForeColor = RgbColor.Black;
        return port;
    }

    private static readonly RgbColor Blue = new(0, 0, 0xFFFF);

    [Fact]
    public void An_eight_bit_mask_blends_the_data_over_the_screen_by_a_floor_of_m_over_256()
    {
        // il32 red over blue, l8mk $80: d + ((s - d) * m >> 8): red 0 + (255 * 128 >> 8) = 127, blue 255 + (-255 * 128 >> 8) = 127.
        var suite = Family(("ICN#", IconList()), ("il32", Il32(255, 0, 0)), ("l8mk", Fill(1024, 0x80)));
        var port = Background(32, Blue);
        suite.Plot(port, R(0, 0, 32, 32));
        Assert.Equal(new RgbaColor(127, 0, 127), port.Canvas[10, 10]);
        Assert.Equal(new RgbaColor(127, 0, 127), port.Canvas[1, 1]);          // outside the 1-bit mask: the 8-bit one replaces it
        Assert.Equal(new RgbaColor(0, 0, 255), port.Canvas[40, 40]);

        // m $FF: the source exactly; m 1: (-255 * 1) >> 8 = -1, an arithmetic floor.
        suite = Family(("ICN#", IconList()), ("il32", Il32(255, 0, 0)), ("l8mk", [.. Fill(512, 0xFF), .. Fill(512, 0x01)]));
        port = Background(32, Blue);
        suite.Plot(port, R(0, 0, 32, 32));
        Assert.Equal(new RgbaColor(255, 0, 0), port.Canvas[10, 10]);
        Assert.Equal(new RgbaColor(0, 0, 254), port.Canvas[10, 20]);
    }

    [Fact]
    public void An_all_zero_eight_bit_mask_draws_nothing_even_inside_the_one_bit_mask()
    {
        var port = Background(32, Blue);
        Family(("ICN#", IconList()), ("il32", Il32(255, 0, 0)), ("l8mk", new byte[1024])).Plot(port, R(0, 0, 32, 32));
        Assert.Equal(new RgbaColor(0, 0, 255), port.Canvas[10, 10]);
    }

    [Fact]
    public void The_eight_bit_mask_needs_eight_bits_a_colour_member_and_Mac_OS_9()
    {
        var mask = Fill(1024, 0xFF);
        // 4 bits: the 1-bit mask (icl4 entry 3 is red); 1-bit data (no colour member): the 1-bit mask; the ROM: the 1-bit mask.
        var port = Background(4, Blue);
        var before = port.Canvas[1, 1];
        Family(("ICN#", IconList()), ("icl4", Fill(512, 0x33)), ("l8mk", mask)).Plot(port, R(0, 0, 32, 32));
        Assert.Equal(before, port.Canvas[1, 1]);
        Assert.NotEqual(before, port.Canvas[10, 10]);

        port = Background(32, Blue);
        Family(("ICN#", IconList()), ("l8mk", mask)).Plot(port, R(0, 0, 32, 32));
        Assert.Equal(new RgbaColor(0, 0, 255), port.Canvas[1, 1]);

        port = Background(32, Blue, QuickDrawVersion.MacRom);
        Family(("ICN#", IconList()), ("icl8", Fill(1024, 5)), ("l8mk", mask)).Plot(port, R(0, 0, 32, 32));
        Assert.Equal(new RgbaColor(0, 0, 255), port.Canvas[1, 1]);
        Assert.Equal(new RgbaColor(255, 255, 0), port.Canvas[10, 10]);
    }

    [Fact]
    public void The_rect_size_picks_the_eight_bit_mask_and_a_size_mismatch_falls_back_to_the_one_bit_one()
    {
        // A 16 x 16 rect picks s8mk; with only l8mk present it is the first of the list, and its size does not match ics8.
        var ics = new byte[64];
        for (int i = 0; i < 32; i++) ics[32 + i] = (byte)(i >= 8 && i < 24 ? 0xFF : 0);   // mask rows 4..12
        var port = Background(32, Blue);
        Family(("ics#", ics), ("is32", Il32(255, 0, 0, 256)), ("s8mk", Fill(256, 0xFF))).Plot(port, R(0, 0, 16, 16));
        Assert.Equal(new RgbaColor(255, 0, 0), port.Canvas[1, 1]);

        port = Background(32, Blue);
        Family(("ics#", ics), ("is32", Il32(255, 0, 0, 256)), ("l8mk", Fill(1024, 0xFF))).Plot(port, R(0, 0, 16, 16));
        Assert.Equal(new RgbaColor(0, 0, 255), port.Canvas[1, 1]);
        Assert.Equal(new RgbaColor(255, 0, 0), port.Canvas[1, 5]);
    }

    [Fact]
    public void Sixteen_bits_truncate_the_blend_and_eight_bits_take_the_nearest_entry()
    {
        var suite = Family(("ICN#", IconList()), ("il32", Il32(255, 0, 0)), ("icl8", Fill(1024, 5)), ("l8mk", Fill(1024, 0x80)));
        var port = Background(16, Blue);
        suite.Plot(port, R(0, 0, 32, 32));
        Assert.Equal(new RgbaColor(0x7B, 0, 0x7B), port.Canvas[10, 10]);      // 127 >> 3 = 15, expanded

        // 8 bits: icl8 yellow (255, 255, 0) over blue at $80 -> (127, 127, 127): the nearest grey of the 8-bit table.
        port = Background(8, Blue);
        suite.Plot(port, R(0, 0, 32, 32));
        var c = port.Canvas[10, 10];
        Assert.True(c.R == c.G && c.G == c.B && Math.Abs(c.R - 127) <= 8, c.ToString());
    }

    [Fact]
    public void Transforms_recolour_the_source_before_the_blend()
    {
        var suite = Family(("ICN#", IconList()), ("il32", Il32(200, 100, 0)), ("l8mk", Fill(1024, 0xFF)));
        var port = Background(32, Blue);
        suite.Plot(port, R(0, 0, 32, 32), transform: IconTransform.Selected);
        Assert.Equal(new RgbaColor(100, 50, 0), port.Canvas[1, 1]);

        port = Background(32, Blue);
        suite.Plot(port, R(0, 0, 32, 32), transform: IconTransform.Disabled);
        Assert.Equal(new RgbaColor(227, 177, 127), port.Canvas[1, 1]);

        // Open draws the 1-bit outline: the 8-bit mask is not used, so outside the 1-bit mask stays blue.
        port = Background(32, Blue);
        suite.Plot(port, R(0, 0, 32, 32), transform: IconTransform.Open);
        Assert.Equal(new RgbaColor(0, 0, 255), port.Canvas[1, 1]);
    }

    [Fact]
    public void A_scaled_eight_bit_mask_is_stretched_nearest_neighbour_like_the_data()
    {
        // l8mk: left half $FF, right half 0; in a 48 x 48 rect (no h8mk, ich#) ICN# is the group, so l8mk matches icl8.
        var mask = new byte[1024];
        for (int y = 0; y < 32; y++)
            for (int x = 0; x < 16; x++) mask[y * 32 + x] = 0xFF;
        var port = Background(32, Blue);
        Family(("ICN#", IconList()), ("il32", Il32(255, 0, 0)), ("l8mk", mask)).Plot(port, R(0, 0, 48, 48));
        Assert.Equal(new RgbaColor(255, 0, 0), port.Canvas[23, 1]);
        Assert.Equal(new RgbaColor(0, 0, 255), port.Canvas[24, 1]);
        Assert.Equal(new RgbaColor(255, 0, 0), port.Canvas[0, 47]);
    }

    [Fact]
    public void Colour_arithmetic_matches_the_Icon_Utilities()
    {
        Assert.Equal(new RgbColor(0x7FFF, 0x7FFF, 0x7FFF), IconSuite.Darken(RgbColor.White));
        Assert.Equal(new RgbColor(0x7FFF, 0x7FFF, 0x0000), IconSuite.Darken(new RgbColor(0xFFFF, 0xFFFF, 0x0000)));   // blue, the smallest, stays 0
        Assert.Equal(RgbColor.White, IconSuite.Brighten(RgbColor.Black));
    }
}
