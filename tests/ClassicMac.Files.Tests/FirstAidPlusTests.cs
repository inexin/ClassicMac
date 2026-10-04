using ClassicMac.Core;
using ClassicMac.Files.Hfs;

namespace ClassicMac.Files.Tests;

// First Aid on HFS Plus volumes (hfs.md §5.6, hfs-plus.md §5.4): the volume header, the special files, the extents,
// catalog and attributes B-trees, the catalog's records and hierarchy, the allocation file, and the header's counts.
public class FirstAidPlusTests
{
    internal const int Header = 1024, Block = HfsPlusBuilder.Block;

    // A volume with a folder, a file in it, and a fragmented file with overflow extents.
    internal static byte[] Base()
    {
        var builder = new HfsPlusBuilder();
        uint docs = builder.Folder(HfsPlusBuilder.Root, "Docs");
        builder.File(docs, "Letter", "dear sir"u8.ToArray(), new byte[300]);
        builder.File(HfsPlusBuilder.Root, "Fragmented", new byte[12 * Block], [], fragments: 12);
        return builder.Build("Plus");
    }

    internal const uint Docs = 16, Letter = 17, Fragmented = 18;

    internal static uint U32(byte[] image, int offset) => new BigEndianReader(image).ReadUInt32At(offset);

    internal static ushort U16(byte[] image, int offset) => new BigEndianReader(image).ReadUInt16At(offset);

    internal static void Put32(byte[] image, int offset, long value) => new BigEndianWriter(image).WriteUInt32At(offset, (uint)value);

    internal static void Put16(byte[] image, int offset, int value) => new BigEndianWriter(image).WriteUInt16At(offset, (ushort)value);

    // A header field changed in both headers, so the alternate cannot stand in.
    internal static void PutBoth32(byte[] image, int field, long value)
    {
        Put32(image, Header + field, value);
        Put32(image, image.Length - 1024 + field, value);
    }

    internal static FirstAidReport Verify(byte[] image) => HfsFirstAid.Verify(ForkData.FromBytes(image));

    // A tree's node, by the header's fork data (the test trees are in one extent).
    internal static int Node(byte[] image, int forkData, uint node) => (int)(U32(image, Header + forkData + 16) * Block + node * Block);

    internal static int CatalogNode(byte[] image, uint node) => Node(image, 272, node);

    // The offset of the catalog record (parent, name)'s data, following the leaves.
    internal static int Record(byte[] image, uint parent, string name)
    {
        foreach (var (key, data) in Records(image))
        {
            if (U32(image, key + 2) == parent && U16(image, key + 6) == name.Length
                && Enumerable.Range(0, name.Length).All(i => U16(image, key + 8 + 2 * i) == name[i]))
            {
                return data;
            }
        }

        throw new InvalidOperationException($"No catalog record ({parent}, {name}).");
    }

    // Every catalog leaf record's key and data offsets, in order.
    internal static IEnumerable<(int Key, int Data)> Records(byte[] image)
    {
        for (uint node = U32(image, CatalogNode(image, 0) + 14 + 10); node != 0; node = U32(image, CatalogNode(image, node)))
        {
            int at = CatalogNode(image, node);
            for (var i = 0; i < U16(image, at + 10); i++)
            {
                int key = at + U16(image, at + Block - 2 * (i + 1));
                yield return (key, key + 2 + U16(image, key));
            }
        }
    }

    private static FirstAidProblem Single(FirstAidReport report, int number) => Assert.Single(report.Problems, p => p.Number == number);

    [Fact]
    public void A_sound_volume_appears_to_be_OK()
    {
        var report = Verify(Base());

        Assert.True(report.Problems.Count == 0, string.Join("; ", report.Problems));
        Assert.Equal(FirstAidVerdict.AppearsOk, report.Verdict);
        Assert.Equal("Plus", report.VolumeName);
        Assert.Contains(FirstAidMessages.CheckingExtendedVolume, report.Stages);
        Assert.Contains(FirstAidMessages.CheckingCatalogFile, report.Stages);
    }

    [Fact]
    public void An_HFSX_volume_whose_catalog_names_no_key_order_cannot_be_repaired()
    {
        var image = Base();                                                        // an HFS Plus catalog: keyCompareType 0
        PutBoth32(image, 0, 0x48580005);                                           // 'HX', version 5

        var report = Verify(image);

        Assert.Equal("firstaid.key-compare-type", Assert.Single(report.Problems).Code);
        Assert.Equal(FirstAidVerdict.CannotRepair, report.Verdict);
    }

    [Fact]
    public void A_bad_block_size_cannot_be_repaired()
    {
        var image = Base();
        PutBoth32(image, 40, 3000);

        Assert.Equal(7, Assert.Single(Verify(image).Problems).Number);
        Assert.Equal(FirstAidVerdict.CannotRepair, Verify(image).Verdict);
    }

    [Fact]
    public void A_missing_alternate_header_needs_repair()
    {
        var image = Base();
        image.AsSpan(image.Length - 1024, 512).Clear();

        var report = Verify(image);

        Assert.Equal("firstaid.alternate-volume-header", Assert.Single(report.Problems).Code);
        Assert.Equal(FirstAidVerdict.NeedsRepair, report.Verdict);
    }

    [Fact]
    public void A_catalog_fork_short_of_its_extents_cannot_be_repaired()
    {
        var image = Base();
        PutBoth32(image, 272 + 12, U32(image, Header + 272 + 12) + 1);             // totalBlocks

        Assert.Equal(46, Assert.Single(Verify(image).Problems).Number);
    }

    [Fact]
    public void A_damaged_header_is_checked_past_by_a_sound_alternate()
    {
        var image = Base();
        Put32(image, Header + 272 + 12, U32(image, Header + 272 + 12) + 1);          // the primary's catalog totalBlocks

        var report = Verify(image);

        Assert.Equal("firstaid.volume-header-damaged", Assert.Single(report.Problems).Code);
        Assert.Equal(FirstAidVerdict.NeedsRepair, report.Verdict);
    }

    [Fact]
    public void A_wrong_B_tree_header_needs_repair()
    {
        var image = Base();
        int header = CatalogNode(image, 0) + 14;
        Put32(image, header + 6, U32(image, header + 6) + 1);                       // leafRecords

        var report = Verify(image);

        Single(report, 54);
        Assert.Equal(FirstAidVerdict.NeedsRepair, report.Verdict);
    }

    [Fact]
    public void Keys_out_of_order_cannot_be_repaired()
    {
        var image = Base();
        int key = Records(image).First().Key;
        Put32(image, key + 2, 99);                                                 // parent 1 → 99: after its neighbours

        Assert.Equal(26, Single(Verify(image), 26).Number);
        Assert.Equal(FirstAidVerdict.CannotRepair, Verify(image).Verdict);
    }

    [Fact]
    public void A_wrong_valence_needs_repair()
    {
        var image = Base();
        int docs = Record(image, HfsPlusBuilder.Root, "Docs");
        Put32(image, docs + 4, U32(image, docs + 4) + 1);

        var problem = Single(Verify(image), 3);

        Assert.Equal((Docs, true), ((uint)problem.Arg2, problem.Repairable));
    }

    [Fact]
    public void A_missing_file_thread_needs_repair()
    {
        var image = Base();
        int thread = Record(image, Letter, "");
        Put16(image, thread, 3);                                                   // a folder thread for the file

        var report = Verify(image);

        Assert.Contains(report.Problems, p => p.Number is 36 or 6);
        Assert.Equal(FirstAidVerdict.NeedsRepair, report.Verdict);
    }

    [Fact]
    public void A_fork_longer_than_its_blocks_cannot_be_repaired()
    {
        var image = Base();
        int letter = Record(image, Docs, "Letter");
        new BigEndianWriter(image).WriteUInt64At(letter + 88, 10_000);           // dataFork.logicalSize past its block

        Assert.Equal(2, Single(Verify(image), 2).Number);
    }

    [Fact]
    public void An_extent_past_the_last_block_cannot_be_repaired()
    {
        var image = Base();
        int letter = Record(image, Docs, "Letter");
        Put32(image, letter + 88 + 16, U32(image, Header + 44) + 5);              // dataFork.extents[0].startBlock

        Assert.Equal(11, Single(Verify(image), 11).Number);
    }

    [Fact]
    public void Forks_that_share_blocks_need_repair()
    {
        var image = Base();
        int letter = Record(image, Docs, "Letter"), fragmented = Record(image, HfsPlusBuilder.Root, "Fragmented");
        Put32(image, letter + 88 + 16, U32(image, fragmented + 88 + 16));          // Letter's block is Fragmented's first

        Assert.True(Single(Verify(image), 12).Repairable);
    }

    [Fact]
    public void A_leaked_block_needs_the_allocation_file_written()
    {
        var image = Base();
        uint last = U32(image, Header + 44) - 2;                                   // a free block
        int bitmap = (int)(U32(image, Header + 112 + 16) * Block);
        image[bitmap + (int)(last / 8)] |= (byte)(0x80 >> (int)(last % 8));

        Assert.True(Single(Verify(image), 60).Repairable);
    }

    [Theory]
    [InlineData(32, 1)]                                                            // fileCount
    [InlineData(36, 1)]                                                            // folderCount
    [InlineData(48, -1)]                                                           // freeBlocks
    [InlineData(64, -1)]                                                           // nextCatalogID at the highest CNID
    public void Wrong_counts_in_the_header_need_repair(int offset, int delta)
    {
        var image = Base();
        Put32(image, Header + offset, U32(image, Header + offset) + delta);

        var report = Verify(image);

        Assert.True(Single(report, 59).Repairable);
        Assert.Equal(FirstAidVerdict.NeedsRepair, report.Verdict);
    }
}
