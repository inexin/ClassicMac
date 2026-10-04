using ClassicMac.Core;
using ClassicMac.Files.Hfs;

namespace ClassicMac.Files.Tests;

// The extents overflow tree edited as the BTree manager edits it (hfs.md §1.8), as the catalog is: a record deleted or
// added changes its leaf (and the header, and an index key when a leaf's first key changes), not every node of a
// rebuilt tree.
public sealed class ExtentsTreeEditTests
{
    // 30 files of 5 fragments: each has one overflow record, 15 in each of two leaves.
    private static byte[] Volume()
    {
        var builder = new HfsBuilder { ExtentsTreeNodes = 8, CatalogLeaves = 16, FreeCatalogNodes = 8 };
        for (var i = 0; i < 30; i++)
        {
            builder.File(HfsBuilder.Root, $"F{i:D2}", new byte[5 * HfsBuilder.Block], [], fragments: 5);
        }

        return EditTreeVolume(builder.Build("Disk"));
    }

    // Free space for the edits to use.
    private static byte[] EditTreeVolume(byte[] image) => HfsWriter.Resize(ForkData.FromBytes(image), image.Length + 64 * 1024);

    private static byte[] ExtentsTree(byte[] image)
    {
        var mdb = new BigEndianReader(image.AsMemory(1024, 162));
        int firstBlock = mdb.ReadUInt16At(0x1C) * 512, blockSize = (int)mdb.ReadUInt32At(0x14);
        return image.AsSpan(firstBlock + mdb.ReadUInt16At(0x86) * blockSize, (int)mdb.ReadUInt32At(0x82)).ToArray();
    }

    private static int ChangedNodes(byte[] before, byte[] after) =>
        Enumerable.Range(0, before.Length / 512).Count(n => !before.AsSpan(n * 512, 512).SequenceEqual(after.AsSpan(n * 512, 512)));

    [Fact]
    public void Deleting_a_fragmented_file_changes_only_its_leaf_and_the_header()
    {
        var image = Volume();
        var before = ExtentsTree(image);

        var edited = HfsWriter.DeleteFile(ForkData.FromBytes(image), "F20");

        Assert.Equal(2, ChangedNodes(before, ExtentsTree(edited)));                  // the header and F20's leaf
        Assert.Null(HfsWriter.Check(ForkData.FromBytes(edited)));
        Assert.Equal(FirstAidVerdict.AppearsOk, HfsFirstAid.Verify(ForkData.FromBytes(edited)).Verdict);
    }

    [Fact]
    public void Deleting_a_leaf_s_first_record_also_updates_its_index_key()
    {
        var image = Volume();
        var before = ExtentsTree(image);

        var edited = HfsWriter.DeleteFile(ForkData.FromBytes(image), "F15");        // the second leaf's first record

        Assert.Equal(3, ChangedNodes(before, ExtentsTree(edited)));                  // the header, the leaf and the index
        Assert.Null(HfsWriter.Check(ForkData.FromBytes(edited)));
        Assert.Equal(FirstAidVerdict.AppearsOk, HfsFirstAid.Verify(ForkData.FromBytes(edited)).Verdict);
    }

    [Fact]
    public void Growing_a_fork_into_a_new_overflow_record_inserts_it_in_its_leaf()
    {
        var image = Volume();
        var before = ExtentsTree(image);
        var reader = HfsReader.Instance;

        // F05 grows past the 2 extents of its overflow record's 3: a fragment more makes a second record for it.
        var edited = HfsWriter.ReplaceFork(ForkData.FromBytes(image), "F05", HfsFork.Data, new byte[12 * HfsBuilder.Block]);

        Assert.True(ChangedNodes(before, ExtentsTree(edited)) <= 3);
        Assert.Null(HfsWriter.Check(ForkData.FromBytes(edited)));
        Assert.Equal(FirstAidVerdict.AppearsOk, HfsFirstAid.Verify(ForkData.FromBytes(edited)).Verdict);
        Assert.Equal(12 * HfsBuilder.Block, reader.Read(ForkData.FromBytes(edited), new ContainerContext())
            .Single(f => f.MacPath == "F05").DataFork.Length);
    }
}
