using ClassicMac.Core;
using ClassicMac.Files.Containers;
using ClassicMac.Files.Hfs;
using static ClassicMac.Files.Tests.FirstAidPlusTests;

namespace ClassicMac.Files.Tests;

// First Aid's repair of HFS Plus volumes (hfs-plus.md §5.4): the catalog and extents trees written again with the
// repairs made, the allocation file, then the volume header and its alternate; the repaired volume checks out and the
// reader reads it with nothing to report.
public class FirstAidPlusRepairTests
{
    private static byte[] Repaired(byte[] image)
    {
        var result = HfsFirstAid.Repair(ForkData.FromBytes(image));
        Assert.Equal(FirstAidVerdict.NeedsRepair, result.Before.Verdict);
        Assert.True(result.After.Verdict == FirstAidVerdict.AppearsOk, string.Join("; ", result.After.Problems));
        var context = new ContainerContext();
        HfsReader.Instance.Read(ForkData.FromBytes(result.Volume!), context);
        Assert.True(context.Diagnostics.Count == 0, string.Join("; ", context.Diagnostics.Select(d => d.Message)));
        return result.Volume!;
    }

    // The catalog written again with its records changed, as a fault.
    private static void RewriteCatalog(byte[] image, Func<List<(byte[] Key, byte[] Data)>, IEnumerable<(byte[] Key, byte[] Data)>> change)
    {
        int start = CatalogNode(image, 0);
        uint nodes = U32(image, Header + 272 + 12);
        var tree = image.AsSpan(start, (int)nodes * Block).ToArray();
        var records = change(HfsPlusBTree.LeafRecords(tree, "catalog").ToList()).ToList();
        records.Sort((a, b) => HfsPlusBTree.CompareCatalogKeys(a.Key, b.Key, caseFolding: true));
        HfsPlusBTreeWriter.Build(HfsPlusBTreeWriter.Catalog, records, Block, nodes, nodes * Block).CopyTo(image, start);
    }

    private static uint Parent(byte[] key) => new BigEndianReader(key).ReadUInt32At(2);

    private static bool IsThreadOf(byte[] key, uint id) => Parent(key) == id && key[7] == 0 && key[6] == 0;

    [Fact]
    public void A_wrong_valence_is_set()
    {
        var image = Base();
        int docs = Record(image, HfsPlusBuilder.Root, "Docs");
        uint valence = U32(image, docs + 4);
        Put32(image, docs + 4, valence + 2);

        var repaired = Repaired(image);

        Assert.Equal(valence, U32(repaired, Record(repaired, HfsPlusBuilder.Root, "Docs") + 4));
    }

    [Theory]
    [InlineData(32, 1)]
    [InlineData(36, 1)]
    [InlineData(48, -3)]
    [InlineData(64, -2)]
    public void Wrong_header_counts_are_set_in_both_headers(int offset, int delta)
    {
        var image = Base();
        uint right = U32(image, Header + offset);
        Put32(image, Header + offset, right + delta);

        var repaired = Repaired(image);

        Assert.Equal(right, U32(repaired, Header + offset));
        Assert.Equal(right, U32(repaired, repaired.Length - 1024 + offset));
    }

    [Fact]
    public void A_leaked_block_is_cleared_from_the_allocation_file()
    {
        var image = Base();
        uint free = U32(image, Header + 44) - 2;
        int bitmap = (int)(U32(image, Header + 112 + 16) * Block);
        byte clean = image[bitmap + (int)(free / 8)];
        image[bitmap + (int)(free / 8)] |= (byte)(0x80 >> (int)(free % 8));

        Assert.Equal(clean, Repaired(image)[bitmap + (int)(free / 8)]);
    }

    [Fact]
    public void A_wrong_B_tree_header_is_written_again()
    {
        var image = Base();
        int header = CatalogNode(image, 0) + 14;
        uint records = U32(image, header + 6);
        Put32(image, header + 6, records + 3);

        var repaired = Repaired(image);

        Assert.Equal(records, U32(repaired, CatalogNode(repaired, 0) + 14 + 6));
    }

    [Fact]
    public void A_missing_alternate_header_is_written()
    {
        var image = Base();
        image.AsSpan(image.Length - 1024, 512).Clear();

        var repaired = Repaired(image);

        Assert.Equal(repaired.AsSpan(Header, 512).ToArray(), repaired.AsSpan(repaired.Length - 1024, 512).ToArray());
    }

    [Fact]
    public void A_damaged_header_is_written_from_the_alternate()
    {
        var image = Base();
        Put32(image, Header + 272 + 12, U32(image, Header + 272 + 12) + 1);

        var repaired = Repaired(image);

        Assert.Equal(repaired.AsSpan(repaired.Length - 1024, 512).ToArray(), repaired.AsSpan(Header, 512).ToArray());
    }

    [Fact]
    public void A_missing_file_thread_is_made()
    {
        var image = Base();
        RewriteCatalog(image, records => records.Where(r => !IsThreadOf(r.Key, Letter)));

        var repaired = Repaired(image);

        Assert.Equal(4, U16(repaired, Record(repaired, Letter, "")));
    }

    [Fact]
    public void A_missing_folder_is_made_again_from_its_thread()
    {
        var image = Base();
        RewriteCatalog(image, records => records.Where(r => !(Parent(r.Key) == HfsPlusBuilder.Root && U16(r.Data, 0) == 1)));

        var repaired = Repaired(image);

        int docs = Record(repaired, HfsPlusBuilder.Root, "Docs");
        Assert.Equal((1, Docs, 1u), (U16(repaired, docs), U32(repaired, docs + 8), U32(repaired, docs + 4)));
    }

    [Fact]
    public void A_lost_file_s_thread_overflow_extents_and_blocks_are_released()
    {
        var image = Base();
        uint free = U32(image, Header + 48);
        RewriteCatalog(image, records => records.Where(r => !(Parent(r.Key) == HfsPlusBuilder.Root && U16(r.Data, 0) == 2)));

        var repaired = Repaired(image);

        Assert.Equal(free + 12, U32(repaired, Header + 48));
        Assert.Equal(1u, U32(repaired, Header + 32));
        Assert.Throws<InvalidOperationException>(() => Record(repaired, Fragmented, ""));
    }

    [Fact]
    public void A_short_block_count_is_set_to_the_fork_s_extents()
    {
        var image = Base();
        int letter = Record(image, Docs, "Letter");
        Put32(image, letter + 168 + 12, 0);                                        // resource fork: 300 bytes in 0 blocks
        new BigEndianWriter(image).WriteUInt64At(letter + 168, 0);

        var repaired = Repaired(image);

        Assert.Equal(1u, U32(repaired, Record(repaired, Docs, "Letter") + 168 + 12));
    }

    // A volume in its HFS wrapper: checked and repaired inside the embedded extent; the wrapper is left as it was.
    [Fact]
    public void A_wrapped_volume_is_checked_and_repaired_in_place()
    {
        var builder = new HfsPlusBuilder();
        uint docs = builder.Folder(HfsPlusBuilder.Root, "Docs");
        builder.File(docs, "Letter", "dear sir"u8.ToArray(), []);
        var image = builder.BuildWrapped("Wrapped");
        Assert.Equal(FirstAidVerdict.AppearsOk, Verify(image).Verdict);
        Assert.Equal("Wrapped", Verify(image).VolumeName);
        const int Embedded = 6 * 512;
        var plain = image.AsSpan(Embedded, image.Length - Embedded - 1024).ToArray();
        int folder = Record(plain, HfsPlusBuilder.Root, "Docs");
        Put32(image, Embedded + folder + 4, 7);                                    // the valence, inside the wrapper

        var repaired = Repaired(image);

        Assert.Equal(image.AsSpan(0, Embedded).ToArray(), repaired.AsSpan(0, Embedded).ToArray());
        var inner = repaired.AsSpan(Embedded, image.Length - Embedded - 1024).ToArray();
        Assert.Equal(1u, U32(inner, Record(inner, HfsPlusBuilder.Root, "Docs") + 4));
    }

    // #12: the fork found later gets its own copy, in free blocks; both read as before.
    [Fact]
    public void A_fork_sharing_blocks_gets_its_own_copy()
    {
        var image = Base();
        int letter = Record(image, Docs, "Letter"), fragmented = Record(image, HfsPlusBuilder.Root, "Fragmented");
        uint shared = U32(image, fragmented + 88 + 16);
        "MARK"u8.CopyTo(image.AsSpan((int)(shared * Block)));
        Put32(image, letter + 88 + 16, shared);                                    // Letter's one block is Fragmented's first

        var repaired = Repaired(image);

        uint moved = U32(repaired, Record(repaired, Docs, "Letter") + 88 + 16);
        Assert.NotEqual(shared, moved);
        Assert.Equal(shared, U32(repaired, Record(repaired, HfsPlusBuilder.Root, "Fragmented") + 88 + 16));
        Assert.Equal("MARK"u8.ToArray(), repaired.AsSpan((int)(moved * Block), 4).ToArray());
    }

    [Fact]
    public void A_fragmented_fork_sharing_blocks_is_copied_whole_and_loses_its_overflow_records()
    {
        var builder = new HfsPlusBuilder();
        uint docs = builder.Folder(HfsPlusBuilder.Root, "Docs");
        builder.File(HfsPlusBuilder.Root, "A", Enumerable.Range(0, Block).Select(i => (byte)i).ToArray(), []);
        builder.File(docs, "B", Enumerable.Range(0, 12 * Block).Select(i => (byte)(i * 7)).ToArray(), [], fragments: 12);
        var image = builder.Build("Plus");
        Assert.NotEmpty(OverflowRecords(image));
        Put32(image, Record(image, docs, "B") + 88 + 16, U32(image, Record(image, HfsPlusBuilder.Root, "A") + 88 + 16));
        var before = Enumerable.Range(0, Block).Select(i => (byte)i)                      // its first block is A's now
            .Concat(Enumerable.Range(Block, 11 * Block).Select(i => (byte)(i * 7))).ToArray();

        var repaired = Repaired(image);

        Assert.Equal(before, Data(repaired, "Docs:B"));
        Assert.Equal(Enumerable.Range(0, Block).Select(i => (byte)i), Data(repaired, "A"));
        Assert.Empty(OverflowRecords(repaired));                                   // B in free blocks, in one piece
    }

    private static byte[] Data(byte[] image, string path) =>
        HfsReader.Instance.Read(ForkData.FromBytes(image), new ContainerContext()).Single(f => f.MacPath == path).DataFork.ToArray();

    private static List<(byte[] Key, byte[] Data)> OverflowRecords(byte[] image)
    {
        int start = Node(image, 192, 0);
        var tree = image.AsSpan(start, (int)U32(image, Header + 192 + 12) * Block).ToArray();
        return HfsPlusBTree.LeafRecords(tree, "extents-overflow").ToList();
    }

    [Fact]
    public void A_volume_that_appears_to_be_OK_is_left_alone()
    {
        var result = HfsFirstAid.Repair(ForkData.FromBytes(Base()));

        Assert.Equal(FirstAidVerdict.AppearsOk, result.Before.Verdict);
        Assert.False(result.Written);
    }

    private static ushort U16(byte[] bytes, int offset) => new BigEndianReader(bytes).ReadUInt16At(offset);
}
