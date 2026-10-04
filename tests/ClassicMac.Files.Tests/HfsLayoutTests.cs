using System.Buffers.Binary;
using ClassicMac.Core;
using ClassicMac.Files.Commands;
using ClassicMac.Files.Hfs;

namespace ClassicMac.Files.Tests;

// HfsReader.ReadLayout (docs/formats/file-systems/hfs.md §5.7): where a volume's free space and split files lie, as
// block ranges, and how small it can be made now and after a defragmentation; one summary for the Volume card,
// Defragment and Resize.
public sealed class HfsLayoutTests : IDisposable
{
    private const int Mdb = 1024;
    private readonly string directory = Directory.CreateTempSubdirectory("cm-layout").FullName;

    public void Dispose() => Directory.Delete(directory, recursive: true);

    private static int FreeBlocks(byte[] image) => BinaryPrimitives.ReadUInt16BigEndian(image.AsSpan(Mdb + 0x22));

    private static VolumeLayout Layout(byte[] image) => HfsReader.Instance.ReadLayout(ForkData.FromBytes(image))!;

    [Fact]
    public void A_fragmented_volume_gives_its_free_runs_and_split_extents_as_block_ranges()
    {
        var image = HfsDefragmentTests.Fragmented();
        var spread = HfsReader.Instance.Read(ForkData.FromBytes(image), new ContainerContext()).Single(f => f.MacPath == "Spread");

        var layout = Layout(image);

        Assert.Equal((21, 1, 1), (layout.Files, layout.SplitFiles, layout.SplitForks));         // 20 pads and Spread's data fork
        Assert.Equal(BinaryPrimitives.ReadUInt16BigEndian(image.AsSpan(Mdb + 0x12)), layout.BlockCount);
        Assert.Equal(512, layout.BlockSize);
        Assert.Equal(layout.SplitExtents.Count, layout.MostExtents);
        Assert.True(layout.MostExtents > 3);                                              // past its catalog record's three
        Assert.Equal((spread.DataFork.Length + 511) / 512, layout.SplitExtents.Sum(e => e.Count));
        Assert.Equal(FreeBlocks(image), layout.FreeBlocks);
        Assert.Equal(layout.FreeRuns.Max(r => r.Count), layout.LargestFreeRun);
        Assert.True(layout.FreeRuns.Count > 1);
        Assert.True(layout.FreeRuns.Zip(layout.FreeRuns.Skip(1)).All(p => p.First.End < p.Second.Start));   // in order, apart
        Assert.All(layout.SplitExtents, e => Assert.DoesNotContain(layout.FreeRuns, r => r.Start < e.End && e.Start < r.End));
    }

    [Fact]
    public void The_smallest_sizes_are_what_a_shrink_takes_now_and_after_a_defragmentation()
    {
        var image = HfsDefragmentTests.Fragmented();
        for (var i = 1; i < 40; i += 2)
        {
            image = HfsWriter.DeleteFile(ForkData.FromBytes(image), $"Pad {i:D2}");      // free space in small gaps
        }

        var layout = Layout(image);

        Assert.Equal(HfsWriter.SmallestSize(ForkData.FromBytes(image)), layout.SmallestSizeDefragmented);
        Assert.True(layout.SmallestSize > layout.SmallestSizeDefragmented);
        Assert.Equal(FirstAidVerdict.AppearsOk, HfsFirstAid.Verify(ForkData.FromBytes(HfsWriter.Resize(ForkData.FromBytes(image), layout.SmallestSize))).Verdict);
        Assert.Throws<InvalidDataException>(() => HfsWriter.Resize(ForkData.FromBytes(image), layout.SmallestSize - 512));
    }

    [Fact]
    public void After_a_defragmentation_nothing_is_in_pieces_and_both_smallest_sizes_meet()
    {
        var image = HfsWriter.Defragment(ForkData.FromBytes(HfsDefragmentTests.Fragmented()));

        var layout = Layout(image);

        Assert.Equal((0, 0, 1, (long)FreeBlocks(image)), (layout.SplitFiles, layout.SplitForks, layout.FreeRuns.Count, layout.LargestFreeRun));
        Assert.Empty(layout.SplitExtents);
        Assert.Equal(layout.SmallestSizeDefragmented, layout.SmallestSize);
        Assert.Equal(layout.BlockCount, layout.FreeRuns.Single().End);                   // the free space at the end
    }

    [Fact]
    public void An_empty_volume_has_one_free_run_and_other_input_none()
    {
        var image = HfsWriter.Format(800 * 1024, "Empty");

        var layout = Layout(image);

        Assert.Equal((0, 0, 0, 1, (long)FreeBlocks(image)), (layout.Files, layout.SplitFiles, layout.MostExtents, layout.FreeRuns.Count, layout.LargestFreeRun));
        Assert.Null(HfsReader.Instance.ReadLayout(ForkData.FromBytes(new byte[4096])));
    }

    [Fact]
    public void Stat_gives_a_volume_its_layout()
    {
        var path = Path.Combine(directory, "frag.img");
        File.WriteAllBytes(path, HfsDefragmentTests.Fragmented());
        using var tree = MacPathTree.Open(path);

        var volume = MacCommands.Stat(tree, tree.Root);
        var file = MacCommands.Stat(tree, tree.Resolve("Spread")!);

        Assert.Equal(1, volume.Layout!.SplitFiles);
        Assert.Null(file.Layout);
    }
}
