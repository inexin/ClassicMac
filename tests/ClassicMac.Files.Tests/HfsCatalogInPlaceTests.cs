using ClassicMac.Core;
using ClassicMac.Files.Hfs;

namespace ClassicMac.Files.Tests;

// The catalog edited record by record as Apple's BTree manager edits it (hfs.md §1.8, §5.5): a record goes into its
// leaf; a full leaf rotates records into its left sibling, or splits to the left into the first free node; a leaf's new
// first key goes up the index; an emptied leaf is freed and zeroed, and a root left with one child collapses; a tree
// with fewer free nodes than its depth + 1 grows its file first. Only the nodes that change are written.
public sealed class HfsCatalogInPlaceTests
{
    private const int Leaves = 22;

    // 60 files in Docs and Read Me at the top: 65 records over 22 leaves, 3 each (2 in the last), room for one more in
    // each; the catalog file has free nodes after them unless freeNodes is 0.
    private static byte[] Volume(bool fixedIndexKeys = true, int freeNodes = 10)
    {
        var builder = new HfsBuilder { CatalogLeaves = Leaves, FixedIndexKeys = fixedIndexKeys, FreeCatalogNodes = freeNodes };
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

    // The catalog file, from its first extent (the volumes here have one).
    private sealed class Tree(byte[] bytes)
    {
        public byte[] Bytes { get; } = bytes;

        private BigEndianReader Reader => new(Bytes);

        public int Depth => Reader.ReadUInt16At(14);

        public uint Free => Reader.ReadUInt32At(40);

        public uint Nodes => Reader.ReadUInt32At(36);

        public uint FLink(uint node) => Reader.ReadUInt32At((int)node * 512);

        public uint BLink(uint node) => Reader.ReadUInt32At((int)node * 512 + 4);

        public int Count(uint node) => Reader.ReadUInt16At((int)node * 512 + 10);

        public bool InMap(uint node) => (Bytes[248 + (int)(node >> 3)] & (0x80 >> (int)(node & 7))) != 0;

        // The leaf holding a record whose key's name is the given one.
        public uint LeafOf(string name)
        {
            var wanted = MacRoman.Encode(name);
            for (uint node = Reader.ReadUInt32At(24); node != 0; node = FLink(node))
            {
                for (var r = 0; r < Count(node); r++)
                {
                    int offset = (int)node * 512 + Reader.ReadUInt16At((int)node * 512 + 510 - 2 * r);
                    if (Bytes.AsSpan(offset + 7, Bytes[offset + 6]).SequenceEqual(wanted))
                    {
                        return node;
                    }
                }
            }

            throw new InvalidOperationException($"No record named {name}.");
        }

        public static Tree Of(HfsVolume volume)
        {
            var mdb = new byte[162];
            volume.Read(1024, mdb);
            var reader = new BigEndianReader(mdb);
            var bytes = new byte[reader.ReadUInt32At(0x92)];
            volume.Read(reader.ReadUInt16At(0x1C) * 512L + reader.ReadUInt16At(0x96) * (long)reader.ReadUInt32At(0x14), bytes);
            return new Tree(bytes);
        }
    }

    private static HfsVolume Open(byte[] image) => new(ForkData.FromBytes(image));

    // The MDB and the two leaves its records go in (the folder's, and the last for its thread), plus the header.
    [Fact]
    public void A_record_added_to_a_leaf_with_room_writes_that_leaf()
    {
        var edited = HfsWriter.CreateFolder(Open(Volume()), "Docs:New");

        Assert.InRange(edited.ChangedSectors.Count, 3, 1 + 3);
        Assert.Null(HfsWriter.Check(edited.AsForkData()));
        Assert.Contains(HfsReader.Instance.ReadFolders(edited.AsForkData(), new ContainerContext()), f => f.MacPath == "Docs:New");
    }

    // Two records into a leaf with room for one: records rotate into its left sibling, which has room, and no node is
    // taken.
    [Fact]
    public void A_full_leaf_rotates_records_into_its_left_sibling()
    {
        var volume = Open(Volume());
        var before = Tree.Of(volume);
        uint leaf = before.LeafOf("File 30"), left = before.BLink(leaf);

        volume = HfsWriter.CreateFolder(volume, "Docs:File 30 a");
        volume = HfsWriter.CreateFolder(volume, "Docs:File 30 b");

        var after = Tree.Of(volume);
        Assert.Equal(before.Free, after.Free);
        Assert.Equal(left, after.BLink(leaf));
        Assert.Equal(before.Count(left) + 1, after.Count(left));
        Assert.Null(HfsWriter.Check(volume.AsForkData()));
    }

    // With its left sibling full too, the leaf splits to the left: the first free node becomes its left sibling.
    [Fact]
    public void A_full_leaf_with_a_full_left_sibling_splits_to_the_left()
    {
        var volume = Open(Volume());
        var start = Tree.Of(volume);
        uint leaf = start.LeafOf("File 30"), left = start.BLink(leaf);
        volume = HfsWriter.CreateFolder(volume, $"Docs:File {(start.LeafOf("File 27") == left ? 27 : 28)} a");
        var before = Tree.Of(volume);
        uint firstFree = Enumerable.Range(1, (int)before.Nodes - 1).Select(n => (uint)n).First(n => !before.InMap(n));
        Assert.Equal(left, before.BLink(leaf));

        volume = HfsWriter.CreateFolder(volume, "Docs:File 30 a");
        volume = HfsWriter.CreateFolder(volume, "Docs:File 30 b");

        var after = Tree.Of(volume);
        Assert.Equal(firstFree, after.BLink(leaf));
        Assert.Equal((leaf, left), (after.FLink(firstFree), after.BLink(firstFree)));
        Assert.Equal(firstFree, after.FLink(left));
        Assert.InRange(after.Free, before.Free - 2, before.Free - 1);                      // its parent may split too
        Assert.Null(HfsWriter.Check(volume.AsForkData()));
    }

    // Every file deleted on its own, the first in its leaf included (its index key changes): only the leaves its records
    // are in, the index above and the header are written, and the volume is sound with the rest kept.
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void Each_file_deleted_writes_its_leaf(bool fixedIndexKeys)
    {
        var image = Volume(fixedIndexKeys);
        var before = Paths(Open(image));
        foreach (var path in before.Where(p => p.StartsWith("Docs:", StringComparison.Ordinal)))
        {
            var edited = HfsWriter.Delete(Open(image), path, recursive: false);

            Assert.True(HfsWriter.Check(edited.AsForkData()) is null, $"{path}: {HfsWriter.Check(edited.AsForkData())}");
            Assert.Equal(before.Where(p => p != path), Paths(edited));
            // The MDB, the bitmap, its leaf, Docs's leaf (the valence), the index (two levels), the header.
            Assert.True(edited.ChangedSectors.Count <= 2 + 5, $"{path}: {edited.ChangedSectors.Count} sectors");
        }
    }

    // A leaf whose records are all deleted is unlinked, zeroed and freed, and its index record goes.
    [Fact]
    public void An_emptied_leaf_is_freed()
    {
        var volume = Open(Volume());
        var before = Tree.Of(volume);
        uint leaf = before.LeafOf("File 30"), left = before.BLink(leaf), right = before.FLink(leaf);
        var names = Enumerable.Range(26, 10).Select(i => $"File {i:D2}").Where(n => before.LeafOf(n) == leaf).ToList();

        foreach (var name in names)
        {
            volume = HfsWriter.Delete(volume, $"Docs:{name}", recursive: false);
        }

        var after = Tree.Of(volume);
        Assert.Equal(before.Free + 1, after.Free);
        Assert.False(after.InMap(leaf));
        Assert.True(after.Bytes.AsSpan((int)leaf * 512, 512).IndexOfAnyExcept((byte)0) < 0);
        Assert.Equal((right, left), (after.FLink(left), after.BLink(right)));
        Assert.Null(HfsWriter.Check(volume.AsForkData()));
    }

    // Folders added to a new volume until its root splits, then deleted: the emptied nodes are freed and the root
    // collapses back to one leaf.
    [Fact]
    public void A_root_left_with_one_child_collapses()
    {
        var volume = HfsWriter.FormatVolume(20 * 1024 * 1024, "Collapse");
        var start = Tree.Of(volume);
        for (var i = 0; i < 40; i++)
        {
            volume = HfsWriter.CreateFolder(volume, $"Folder {i:D2}");
        }

        Assert.True(Tree.Of(volume).Depth >= 2);
        for (var i = 0; i < 40; i++)
        {
            volume = HfsWriter.Delete(volume, $"Folder {i:D2}", recursive: false);
        }

        var after = Tree.Of(volume);
        Assert.Equal(1, after.Depth);
        Assert.Equal(start.Free, after.Free);
        Assert.Null(HfsWriter.Check(volume.AsForkData()));
    }

    // Folders added one at a time to a new volume: leaves, index nodes and the root split (depth 1 to 3 or more), and the
    // catalog file grows when its free nodes run short.
    [Fact]
    public void Leaves_index_nodes_and_the_root_split_into_free_nodes()
    {
        var volume = HfsWriter.FormatVolume(20 * 1024 * 1024, "Splits");
        Assert.Equal(1, Tree.Of(volume).Depth);
        for (var i = 0; i < 600; i++)
        {
            volume = HfsWriter.CreateFolder(volume, $"Folder {i * 7919 % 600:D3}");
            if (i % 100 == 99)
            {
                Assert.True(HfsWriter.Check(volume.AsForkData()) is null, $"{i}: {HfsWriter.Check(volume.AsForkData())}");
            }
        }

        Assert.True(Tree.Of(volume).Depth >= 3);
        Assert.Equal(600, HfsReader.Instance.ReadFolders(volume.AsForkData(), new ContainerContext()).Count(f => f.MacPath.StartsWith("Folder ", StringComparison.Ordinal)));
    }

    // A catalog with no free node grows its file before an insert (fewer free nodes than its depth + 1).
    [Fact]
    public void A_tree_without_free_nodes_grows_its_file_first()
    {
        var volume = Open(Volume(freeNodes: 0));
        var before = Tree.Of(volume);

        volume = HfsWriter.CreateFolder(volume, "Docs:New");

        var after = Tree.Of(volume);
        Assert.True(after.Nodes > before.Nodes);
        Assert.Null(HfsWriter.Check(volume.AsForkData()));
    }
}
