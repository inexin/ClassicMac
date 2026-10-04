using ClassicMac.App.Controls;
using ClassicMac.Files;

namespace ClassicMac.App.Tests;

// The allocation map (volume-tools.md §6): equal shares of the volume's blocks, one segment per 2 DIP (one block per
// segment on small volumes); small things win, so scattered free space looks scattered.
public sealed class AllocationMapTests
{
    private static VolumeLayout Layout(long blocks, BlockRange[] free, BlockRange[] split) =>
        new(blocks, 512, 3, split.Length > 0 ? 1 : 0, split.Length > 0 ? 1 : 0, split.Length, free, split, 0, 0);

    [Fact]
    public void A_wide_strip_has_one_segment_per_2_DIP_each_an_equal_share()
    {
        var segments = AllocationMap.Segments(Layout(4000, [new(0, 40)], []), width: 200);

        Assert.Equal(100, segments.Count);
        Assert.All(segments, s => Assert.Equal(40, s.Blocks.Count));
        Assert.Equal(4000, segments[^1].Blocks.End);
        Assert.Equal(AllocationMapKind.Free, segments[0].Kind);
        Assert.Equal(AllocationMapKind.Used, segments[1].Kind);
    }

    [Fact]
    public void A_small_volume_gets_one_block_per_segment()
    {
        var segments = AllocationMap.Segments(Layout(10, [new(2, 3)], [new(7, 1), new(9, 1)]), width: 200);

        Assert.Equal(10, segments.Count);
        Assert.Equal(
            [AllocationMapKind.Used, AllocationMapKind.Used, AllocationMapKind.Free, AllocationMapKind.Free, AllocationMapKind.Free,
             AllocationMapKind.Used, AllocationMapKind.Used, AllocationMapKind.Split, AllocationMapKind.Used, AllocationMapKind.Split],
            segments.Select(s => s.Kind));
    }

    [Fact]
    public void Small_things_win()
    {
        // 100 blocks in 10 segments of 10.
        var segments = AllocationMap.Segments(Layout(100, [new(5, 20), new(35, 1), new(41, 2)], [new(44, 30)]), width: 20);

        Assert.Equal(AllocationMapKind.Partial, segments[0].Kind);                       // 5 free of 10
        Assert.Equal(AllocationMapKind.Free, segments[1].Kind);                          // all 10 free
        Assert.Equal(AllocationMapKind.Partial, segments[2].Kind);
        Assert.Equal(AllocationMapKind.Partial, segments[3].Kind);                       // one free block
        Assert.Equal(AllocationMapKind.Split, segments[4].Kind);                         // a split file's block beats free ones
        Assert.Equal(AllocationMapKind.Split, segments[7].Kind);
        Assert.Equal(AllocationMapKind.Used, segments[8].Kind);
        Assert.Equal((2, 4), (segments[4].Free, segments[3].Free + 3));
    }

    [Fact]
    public void Tooltips_and_the_summary_say_what_the_colours_say()
    {
        var layout = Layout(100, [new(5, 20), new(41, 2)], [new(44, 30)]);
        var segments = AllocationMap.Segments(layout, width: 20);

        Assert.Equal("Blocks 0–9: 5 free, the rest used", AllocationMap.Describe(segments[0]));
        Assert.Equal("Blocks 10–19: free", AllocationMap.Describe(segments[1]));
        Assert.Equal("Blocks 40–49: part of a file in pieces, 2 free", AllocationMap.Describe(segments[4]));
        Assert.Equal("Blocks 80–89: used", AllocationMap.Describe(segments[8]));
        Assert.Equal("Block 7: used", AllocationMap.Describe(AllocationMap.Segments(Layout(10, [], []), width: 200)[7]));
        Assert.Equal("100 blocks; 1 file in pieces; free space in 2 runs, the largest 20 blocks", AllocationMap.Summary(layout));
    }
}
