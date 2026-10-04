using ClassicMac.Core;
using ClassicMac.Graphics.QuickDraw;

namespace ClassicMac.Graphics.Tests;

// ScrollRect (docs/formats/graphics/quickdraw.md §2.25): the rect's pixels moved by srcCopy in black and white through
// the rect, the clip and the screen, and the part left behind erased only when there is an update region; the same in
// both QuickDraws.
public class ScrollRectTests
{
    private const int Width = 20, Height = 10;

    private static MacRect R(int top, int left, int bottom, int right) => new((short)top, (short)left, (short)bottom, (short)right);

    // Ten columns 2 pixels wide, each its own grey, so a move shows.
    private static QuickDrawPort Striped(QuickDrawVersion version)
    {
        var port = new QuickDrawPort(new RgbaBitmap(Width, Height), new QuickDrawOptions { Version = version });
        for (var i = 0; i < 10; i++)
        {
            var grey = (ushort)(i * 0x1111);
            port.ForeColor = new RgbColor(grey, grey, grey);
            port.PaintRect(R(0, 2 * i, Height, 2 * i + 2));
        }

        port.ForeColor = RgbColor.Black;
        return port;
    }

    private static byte Grey(QuickDrawPort port, int x, int y) => port.Canvas[x, y].R;

    public static TheoryData<QuickDrawVersion> Versions => [QuickDrawVersion.MacOS9, QuickDrawVersion.MacRom];

    [Theory]
    [MemberData(nameof(Versions))]
    public void The_rect_moves_and_what_it_leaves_is_erased_and_returned(QuickDrawVersion version)
    {
        var port = Striped(version);
        port.BackColor = new RgbColor(0xFFFF, 0, 0);
        var before = Enumerable.Range(0, Width).Select(x => Grey(port, x, 5)).ToArray();

        var update = port.ScrollRect(R(2, 4, 8, 16), 4, 0, updateRegion: true);

        Assert.Equal(R(2, 4, 8, 8), update!.BoundingBox);
        Assert.Equal(before[4..12], Enumerable.Range(8, 8).Select(x => Grey(port, x, 5)));          // moved right by 4
        Assert.Equal(new RgbaColor(255, 0, 0), port.Canvas[5, 5]);                                   // erased in the back colour
        Assert.Equal(before[2], Grey(port, 2, 5));                                                  // outside the rect: kept
        Assert.Equal(before[17], Grey(port, 17, 5));                                                // the part moved past the rect: not drawn
        Assert.Equal(before[5], Grey(port, 5, 1));                                                  // above the rect: kept
        Assert.Equal(RgbColor.Black, port.ForeColor);                                               // colours restored
    }

    [Theory]
    [MemberData(nameof(Versions))]
    public void Without_an_update_region_nothing_is_erased(QuickDrawVersion version)
    {
        var port = Striped(version);
        var before = Grey(port, 5, 5);

        Assert.Null(port.ScrollRect(R(2, 4, 8, 16), 4, 0, updateRegion: false));

        Assert.Equal(before, Grey(port, 5, 5));
        Assert.Equal(Grey(port, 4, 5), Grey(port, 8, 5));
    }

    [Theory]
    [MemberData(nameof(Versions))]
    public void The_copy_is_not_colorized_and_the_clip_limits_it(QuickDrawVersion version)
    {
        var port = Striped(version);
        var before = Enumerable.Range(0, Width).Select(x => Grey(port, x, 5)).ToArray();
        port.ForeColor = new RgbColor(0, 0xFFFF, 0);
        port.BackColor = new RgbColor(0, 0, 0xFFFF);
        port.Clip = Region.FromRect(R(0, 0, Height, 10));

        var update = port.ScrollRect(R(0, 0, Height, 16), 2, 0, updateRegion: true);

        Assert.Equal(R(0, 0, Height, 2), update!.BoundingBox);
        Assert.Equal(before[0..8], Enumerable.Range(2, 8).Select(x => Grey(port, x, 5)));            // greys kept: srcCopy in black and white
        Assert.Equal(before[10], Grey(port, 10, 5));                                                 // outside the clip: untouched
        Assert.Equal(new RgbaColor(0, 0, 255), port.Canvas[0, 5]);
    }

    [Theory]
    [MemberData(nameof(Versions))]
    public void A_hidden_pen_or_no_move_does_nothing_and_gives_an_empty_region(QuickDrawVersion version)
    {
        var port = Striped(version);
        var before = port.Canvas.Pixels.ToArray();

        Assert.True(port.ScrollRect(R(0, 0, Height, Width), 0, 0, updateRegion: true)!.IsEmpty);
        port.HidePen();
        Assert.True(port.ScrollRect(R(0, 0, Height, Width), 3, 1, updateRegion: true)!.IsEmpty);

        Assert.Equal(before, port.Canvas.Pixels.ToArray());
    }

    [Theory]
    [MemberData(nameof(Versions))]
    public void Scrolling_by_the_whole_rect_erases_all_of_it(QuickDrawVersion version)
    {
        var port = Striped(version);

        var update = port.ScrollRect(R(2, 4, 8, 16), 0, 6, updateRegion: true);

        Assert.Equal(R(2, 4, 8, 16), update!.BoundingBox);
        Assert.All(Enumerable.Range(4, 12), x => Assert.Equal(new RgbaColor(255, 255, 255), port.Canvas[x, 5]));
    }
}
