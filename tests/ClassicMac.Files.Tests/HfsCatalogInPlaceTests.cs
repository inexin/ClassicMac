using ClassicMac.Core;
using ClassicMac.Files.Hfs;

namespace ClassicMac.Files.Tests;

// An edit changes the catalog nodes its records are in, as Mac OS does, not the whole catalog (hfs.md §5.5): a record
// added to or removed from a leaf with room is written into that leaf, a leaf's new first key is carried up its index,
// and a leaf that would overflow or empty makes the tree built again.
public sealed class HfsCatalogInPlaceTests
{
    private const int Leaves = 22;

    // 60 files in Docs and Read Me at the top: 65 records over 22 leaves, 3 each (2 in the last), room for one more in each.
    private static byte[] Volume(bool fixedIndexKeys)
    {
        var builder = new HfsBuilder { CatalogLeaves = Leaves, FixedIndexKeys = fixedIndexKeys };
        var docs = builder.Folder(HfsBuilder.Root, "Docs");
        for (var i = 0; i < 60; i++)
        {
            builder.File(docs, $"File {i:D2}", [(byte)i], []);
        }

        builder.File(HfsBuilder.Root, "Read Me", "hello"u8.ToArray(), []);
        return builder.Build("Disk");
    }

    private static List<string> Paths(HfsVolume volume) =>
        [.. HfsReader.Instance.Read(volume.AsForkData(), new ContainerContext()).Select(f => f.MacPath).Order(StringComparer.Ordinal)];

    // The MDB and bitmap, plus the catalog nodes written: the header and each leaf changed.
    [Fact]
    public void A_new_folder_writes_two_leaves_and_the_header()
    {
        var volume = new HfsVolume(ForkData.FromBytes(Volume(fixedIndexKeys: true)));

        var edited = HfsWriter.CreateFolder(volume, "Docs:New");

        Assert.InRange(edited.ChangedSectors.Count, 3, 2 + 3);                   // its record's leaf, its thread's leaf
        Assert.Null(HfsWriter.Check(edited.AsForkData()));
        Assert.Contains(HfsReader.Instance.ReadFolders(edited.AsForkData(), new ContainerContext()), f => f.MacPath == "Docs:New");
    }

    // Every file deleted on its own, the first in its leaf included (its leaf's index key changes): only the leaves its
    // records are in, the index above and the header are written, and the volume is sound with the rest kept.
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void Each_file_deleted_writes_its_leaf(bool fixedIndexKeys)
    {
        var image = Volume(fixedIndexKeys);
        var before = Paths(new HfsVolume(ForkData.FromBytes(image)));
        foreach (var path in before.Where(p => p.StartsWith("Docs:", StringComparison.Ordinal)))
        {
            var edited = HfsWriter.Delete(new HfsVolume(ForkData.FromBytes(image)), path, recursive: false);

            Assert.True(HfsWriter.Check(edited.AsForkData()) is null, $"{path}: {HfsWriter.Check(edited.AsForkData())}");
            Assert.Equal(before.Where(p => p != path), Paths(edited));
            if (fixedIndexKeys)
            {
                // Its leaf, Docs's leaf (the valence), the index (two levels), the header.
                Assert.True(edited.ChangedSectors.Count <= 2 + 5, $"{path}: {edited.ChangedSectors.Count} sectors");
            }
        }
    }

    // The catalog's depth, from its header node.
    private static int Depth(HfsVolume volume)
    {
        var mdb = new byte[162];
        volume.Read(1024, mdb);
        var reader = new BigEndianReader(mdb);
        long header = reader.ReadUInt16At(0x1C) * 512L + reader.ReadUInt16At(0x96) * (long)reader.ReadUInt32At(0x14);
        var node = new byte[16];
        volume.Read(header, node);
        return new BigEndianReader(node).ReadUInt16At(14);
    }

    // Folders added one at a time to a new volume, whose catalog has free nodes: full leaves split into them, their
    // parents take records and split in turn, and the root splits (depth 1 to 3 or more), each edit writing a few nodes.
    [Fact]
    public void Leaves_index_nodes_and_the_root_split_into_free_nodes()
    {
        var volume = HfsWriter.FormatVolume(20 * 1024 * 1024, "Splits");
        Assert.Equal(1, Depth(volume));
        var small = 0;
        for (var i = 0; i < 600; i++)
        {
            volume = HfsWriter.CreateFolder(volume, $"Folder {i * 7919 % 600:D3}");
            // The MDB, and at most: a leaf and the last leaf (the thread) split, each one's next leaf, their parents split,
            // the header.
            small += volume.ChangedSectors.Count <= 1 + 15 ? 1 : 0;
            if (i % 100 == 99)
            {
                Assert.True(HfsWriter.Check(volume.AsForkData()) is null, $"{i}: {HfsWriter.Check(volume.AsForkData())}");
            }
        }

        Assert.True(Depth(volume) >= 3);
        Assert.InRange(small, 599, 600);                                                // the tree built again once, its free nodes used up
        Assert.Equal(600, HfsReader.Instance.ReadFolders(volume.AsForkData(), new ContainerContext()).Count(f => f.MacPath.StartsWith("Folder ", StringComparison.Ordinal)));
    }

    // A leaf that cannot take its new records, with no free node to split into: the tree is built again, as before.
    [Fact]
    public void A_full_leaf_has_the_tree_built_again()
    {
        var volume = new HfsVolume(ForkData.FromBytes(Volume(fixedIndexKeys: true)));
        for (var i = 0; i < 30; i++)
        {
            volume = HfsWriter.CreateFolder(volume, $"Docs:File 05 {i:D2}");
        }

        Assert.Null(HfsWriter.Check(volume.AsForkData()));
        Assert.Equal(30, HfsReader.Instance.ReadFolders(volume.AsForkData(), new ContainerContext()).Count(f => f.MacPath.StartsWith("Docs:File 05 ", StringComparison.Ordinal)));
    }

    // Deleting the only records of a leaf would empty it: the tree is built again.
    [Fact]
    public void An_emptied_leaf_has_the_tree_built_again()
    {
        var volume = new HfsVolume(ForkData.FromBytes(Volume(fixedIndexKeys: true)));
        for (var i = 0; i < 60; i++)
        {
            volume = HfsWriter.Delete(volume, $"Docs:File {i:D2}", recursive: false);
        }

        Assert.Null(HfsWriter.Check(volume.AsForkData()));
        Assert.Equal(["Read Me"], Paths(volume));
    }
}
