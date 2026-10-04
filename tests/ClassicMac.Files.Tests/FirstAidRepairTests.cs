using ClassicMac.Files.Containers;
using ClassicMac.Files.Hfs;
using static ClassicMac.Files.Tests.FirstAidImages;

namespace ClassicMac.Files.Tests;

// First Aid's repair (hfs.md §5.6): what verify found repairable is fixed in Disk First Aid's order, then the volume is
// verified again. The first five are the repairs Disk First Aid made live, with the bytes it wrote [Verified: Disk First
// Aid 8.5.5, RC1, RC6, RB6, RD1, RC7].
public class FirstAidRepairTests
{
    private static (byte[] Image, FirstAidRepairResult Result) Repaired(byte[] image)
    {
        var result = HfsFirstAid.Repair(ForkData.FromBytes(image));
        Assert.Equal(FirstAidVerdict.NeedsRepair, result.Before.Verdict);
        Assert.True(result.After.Verdict == FirstAidVerdict.AppearsOk, string.Join("; ", result.After.Problems));
        Assert.NotEmpty(result.Changes);
        Assert.Null(HfsWriter.Check(ForkData.FromBytes(result.Volume!)));
        return (result.Volume!, result);
    }

    [Fact]
    public void Reserved_fields_are_cleared()
    {
        var image = Base();
        Put16(image, Record(image, 2, "T1") + 0x18, 5);                            // RC1: filStBlk 5 → 0

        var (repaired, _) = Repaired(image);

        Assert.Equal(0, U16(repaired, Record(repaired, 2, "T1") + 0x18));
    }

    [Fact]
    public void A_wrong_valence_is_set_to_the_folder_s_items()
    {
        var image = Base();
        int folder = Record(image, 2, "D1");
        ushort valence = U16(image, folder + 4);
        Put16(image, folder + 4, valence + 1);                                     // RC6: 3 → 2 there

        var (repaired, _) = Repaired(image);

        Assert.Equal(valence, U16(repaired, Record(repaired, 2, "D1") + 4));
    }

    [Fact]
    public void The_MDB_s_counts_are_set_to_the_catalog_s()
    {
        var image = Base();
        uint files = U32(image, Primary + 0x54);
        Put32(image, Primary + 0x54, files + 1);                                   // RB6

        var (repaired, _) = Repaired(image);

        Assert.Equal(files, U32(repaired, Primary + 0x54));
    }

    [Fact]
    public void A_leaked_block_is_cleared_from_the_bitmap()
    {
        var image = Base();
        uint n = U16(image, Primary + 0x12);
        int bitmap = U16(image, Primary + 0x0E) * Sector;
        int at = bitmap + (int)((n - 1) / 8);
        byte clean = image[at];
        image[at] |= (byte)(0x80 >> (int)((n - 1) % 8));                           // RD1

        var (repaired, _) = Repaired(image);

        Assert.Equal(clean, repaired[at]);
    }

    [Fact]
    public void The_root_s_name_lock_is_cleared()
    {
        var image = Base();
        int root = Record(image, 1, "First Aid");
        Put16(image, root + 0x1E, U16(image, root + 0x1E) | 0x1000);               // RC7

        var (repaired, _) = Repaired(image);

        Assert.Equal(0, U16(repaired, Record(repaired, 1, "First Aid") + 0x1E) & 0x1000);
    }

    [Fact]
    public void A_custom_icon_flag_without_an_icon_is_cleared()
    {
        var image = Base();
        int folder = Record(image, 2, "D1");
        Put16(image, folder + 0x1E, U16(image, folder + 0x1E) | 0x0400);

        var (repaired, _) = Repaired(image);

        Assert.Equal(0, U16(repaired, Record(repaired, 2, "D1") + 0x1E) & 0x0400);
    }

    [Fact]
    public void A_used_block_marked_free_is_marked_used_again()
    {
        var image = Base();
        int bitmap = U16(image, Primary + 0x0E) * Sector;
        uint block = U16(image, Record(image, 2, "T1") + 0x4A);
        image[bitmap + (int)(block / 8)] &= (byte)~(0x80 >> (int)(block % 8));     // D2: MountCheck's serious error

        var (repaired, _) = Repaired(image);

        Assert.NotEqual(0, repaired[bitmap + (int)(block / 8)] & (0x80 >> (int)(block % 8)));
    }

    [Fact]
    public void A_dangling_file_thread_is_deleted()
    {
        var image = Base();
        int thread = Record(image, D2, "");
        image[thread] = 4;                                                         // D2's thread as a file thread
        image[thread + 0x0F] = (byte)'X';                                          // to a name not in D1

        var (repaired, _) = Repaired(image);

        Assert.Throws<InvalidOperationException>(() => Record(repaired, D2, "X2"));
        Record(repaired, D2, "");                                                  // D2's own folder thread, made again
    }

    [Fact]
    public void A_missing_folder_is_made_again_from_its_thread()
    {
        var image = Base();
        RemoveRecord(image, D1, "D2");                                             // its thread and T2 stay
        Assert.Contains(Verify(image).Problems, p => p.Number == 37);

        var (repaired, _) = Repaired(image);

        int folder = Record(repaired, D1, "D2");
        Assert.Equal((1, D2), (repaired[folder], U32(repaired, folder + 6)));
        Assert.Equal(1, U16(repaired, folder + 4));                                 // T2 in it
    }

    [Fact]
    public void A_file_s_missing_thread_is_made()
    {
        var image = Base();
        image[Record(image, 2, "T1") + 2] |= 0x02;                                 // C2: the thread flag, no thread

        var (repaired, _) = Repaired(image);

        Assert.Equal(4, repaired[Record(repaired, T1, "")]);
    }

    [Fact]
    public void A_wrong_header_record_is_written_again()
    {
        var image = Base();
        int header = CatalogNode(image, 0) + 14;
        uint records = U32(image, header + 6);
        Put32(image, header + 6, records + 1);                                     // C9: bthNRecs + 1

        var (repaired, result) = Repaired(image);

        Assert.Equal(records, U32(repaired, CatalogNode(repaired, 0) + 14 + 6));
        Assert.Contains(result.Changes, c => c.Detail.StartsWith("catalog B-tree written again", StringComparison.Ordinal));
    }

    // A file whose catalog record is lost: its thread is deleted, its overflow extents records purged, its blocks freed,
    // and the root's valence and the MDB's counts follow.
    [Fact]
    public void A_lost_file_s_thread_overflow_extents_and_blocks_are_released()
    {
        var builder = new HfsBuilder();
        builder.File(HfsBuilder.Root, "Fragmented", new byte[5 * HfsBuilder.Block], [], fragments: 5, thread: true);
        builder.File(HfsBuilder.Root, "Survivor", "keep"u8.ToArray(), []);
        var image = builder.Build("Disk");
        Assert.Equal(FirstAidVerdict.AppearsOk, Verify(image).Verdict);
        ushort free = U16(image, Primary + 0x22);
        RemoveRecord(image, 2, "Fragmented");

        var (repaired, result) = Repaired(image);

        Assert.Contains(result.Changes, c => c.Detail.Contains("overflow extents record of a file not in the catalog", StringComparison.Ordinal));
        Assert.Equal(free + 5, U16(repaired, Primary + 0x22));
        Assert.Equal((1, 1u), (U16(repaired, Primary + 0x0C), U32(repaired, Primary + 0x54)));
        Assert.Equal(1, U16(repaired, Record(repaired, 1, "Disk") + 4));
        Assert.Equal(["Survivor"], HfsReader.Instance.Read(ForkData.FromBytes(repaired), new ContainerContext()).Select(f => f.MacPath));
    }

    // #12: the fork found second gets its own copy of the blocks it shares, in free blocks; its content reads the same.
    [Fact]
    public void A_fork_sharing_blocks_gets_its_own_copy()
    {
        var image = Base();
        int t1 = Record(image, 2, "T1"), t2 = Record(image, D2, "T2");
        int blockSize = (int)U32(image, Primary + 0x14), firstBlock = U16(image, Primary + 0x1C) * Sector;
        int shared = U16(image, t1 + 0x4A);
        "MARK"u8.CopyTo(image.AsSpan(firstBlock + shared * blockSize));
        Put16(image, t2 + 0x4A, shared);                                           // T2's one block is T1's first
        Assert.Contains(Verify(image).Problems, p => p.Number == 12);

        var (repaired, result) = Repaired(image);

        int moved = U16(repaired, Record(repaired, D2, "T2") + 0x4A);
        Assert.NotEqual(shared, moved);
        Assert.Equal(shared, U16(repaired, Record(repaired, 2, "T1") + 0x4A));
        Assert.Equal("MARK"u8.ToArray(), repaired.AsSpan(firstBlock + moved * blockSize, 4).ToArray());
        Assert.Equal("MARK"u8.ToArray(), repaired.AsSpan(firstBlock + shared * blockSize, 4).ToArray());
        Assert.Contains(result.Changes, c => c.Detail.Contains("own copy", StringComparison.Ordinal));
    }

    [Fact]
    public void A_fragmented_fork_sharing_blocks_is_copied_whole_and_loses_its_overflow_records()
    {
        var builder = new HfsBuilder();
        builder.File(HfsBuilder.Root, "A", Enumerable.Range(0, 2 * HfsBuilder.Block).Select(i => (byte)i).ToArray(), []);
        builder.File(HfsBuilder.Root, "B", Enumerable.Range(0, 5 * HfsBuilder.Block).Select(i => (byte)(i * 7)).ToArray(), [], fragments: 5);
        var image = builder.Build("Disk");
        Assert.NotEmpty(ExtentsKeys(image));
        Put16(image, Record(image, 2, "B") + 0x4A, U16(image, Record(image, 2, "A") + 0x4A));
        var before = ReadData(image, "B");

        var (repaired, _) = Repaired(image);

        Assert.Equal(before, ReadData(repaired, "B"));
        Assert.Equal(Enumerable.Range(0, 2 * HfsBuilder.Block).Select(i => (byte)i), ReadData(repaired, "A"));
        Assert.Empty(ExtentsKeys(repaired));                                       // B in free blocks, in one piece
    }

    private static byte[] ReadData(byte[] image, string name) =>
        HfsReader.Instance.Read(ForkData.FromBytes(image), new ContainerContext()).Single(f => f.MacPath == name).DataFork.ToArray();

    [Fact]
    public void A_volume_that_appears_to_be_OK_is_left_alone()
    {
        var result = HfsFirstAid.Repair(ForkData.FromBytes(Base()));

        Assert.Equal(FirstAidVerdict.AppearsOk, result.Before.Verdict);
        Assert.Empty(result.Changes);
        Assert.Null(result.Volume);
    }

    [Fact]
    public void A_volume_Disk_First_Aid_cannot_repair_is_not_written()
    {
        var image = Base();
        int file = Record(image, 2, "T1");
        Put32(image, file + 0x1E, U32(image, file + 0x1E) + Sector);               // C3: #1, cannot repair

        var result = HfsFirstAid.Repair(ForkData.FromBytes(image));

        Assert.Equal(FirstAidVerdict.CannotRepair, result.After.Verdict);
        Assert.Null(result.Volume);
    }
}
