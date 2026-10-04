using System.Buffers.Binary;
using ClassicMac.Core;
using ClassicMac.Files.Commands;
using ClassicMac.Files.Hfs;

namespace ClassicMac.Files.Tests;

// HfsReader.ReadFragmentation (docs/formats/file-systems/hfs.md §5.7): how many files lie in more than one extent, and
// how the free space lies, for telling whether a defragmentation is worth it.
public sealed class HfsFragmentationTests : IDisposable
{
    private readonly string directory = Directory.CreateTempSubdirectory("cm-fragmentation").FullName;

    public void Dispose() => Directory.Delete(directory, recursive: true);

    private static int FreeBlocks(byte[] image) => BinaryPrimitives.ReadUInt16BigEndian(image.AsSpan(1024 + 0x22));

    [Fact]
    public void A_fragmented_volume_counts_its_pieces_and_free_runs()
    {
        var image = HfsDefragmentTests.Fragmented();

        var fragmentation = HfsReader.Instance.ReadFragmentation(ForkData.FromBytes(image))!;

        Assert.Equal(21, fragmentation.Files);                                       // 20 pads and Spread
        Assert.Equal(1, fragmentation.FragmentedFiles);
        Assert.Equal(1, fragmentation.FragmentedForks);                               // Spread's data fork
        Assert.True(fragmentation.MostExtents > 3);                                   // past its catalog record's three
        Assert.True(fragmentation.FreeRuns > 1);
        Assert.True(fragmentation.LargestFreeRun < FreeBlocks(image));
    }

    [Fact]
    public void After_a_defragmentation_nothing_is_in_pieces()
    {
        var image = HfsWriter.Defragment(ForkData.FromBytes(HfsDefragmentTests.Fragmented()));

        var fragmentation = HfsReader.Instance.ReadFragmentation(ForkData.FromBytes(image))!;

        Assert.Equal((0, 0, 1, 1, (long)FreeBlocks(image)),
            (fragmentation.FragmentedFiles, fragmentation.FragmentedForks, fragmentation.MostExtents, fragmentation.FreeRuns, fragmentation.LargestFreeRun));
    }

    [Fact]
    public void An_empty_volume_has_one_free_run_and_other_input_none()
    {
        var image = HfsWriter.Format(800 * 1024, "Empty");

        var fragmentation = HfsReader.Instance.ReadFragmentation(ForkData.FromBytes(image))!;

        Assert.Equal((0, 0, 0, 1, (long)FreeBlocks(image)),
            (fragmentation.Files, fragmentation.FragmentedFiles, fragmentation.MostExtents, fragmentation.FreeRuns, fragmentation.LargestFreeRun));
        Assert.Null(HfsReader.Instance.ReadFragmentation(ForkData.FromBytes(new byte[4096])));
    }

    [Fact]
    public void Stat_gives_a_volume_its_fragmentation()
    {
        var path = Path.Combine(directory, "frag.img");
        File.WriteAllBytes(path, HfsDefragmentTests.Fragmented());
        using var tree = MacPathTree.Open(path);

        var volume = MacCommands.Stat(tree, tree.Root);
        var file = MacCommands.Stat(tree, tree.Resolve("Spread")!);

        Assert.Equal(1, volume.Fragmentation!.FragmentedFiles);
        Assert.Null(file.Fragmentation);
    }
}
