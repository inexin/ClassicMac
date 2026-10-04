using ClassicMac.Files.Hfs;
using static ClassicMac.Files.Tests.FirstAidImages;

namespace ClassicMac.Files.Tests;

// The checks and repairs beyond Disk First Aid's (hfs.md §5.6): problems like its own, in the same verdict.
public class FirstAidExtrasTests
{
    private static FirstAidProblem Extra(FirstAidReport report, string code)
    {
        return Assert.Single(report.Problems, p => p.Code == code);
    }

    private static byte[] Repaired(byte[] image)
    {
        var result = HfsFirstAid.Repair(ForkData.FromBytes(image));
        Assert.True(result.Written);
        Assert.True(result.After.Verdict == FirstAidVerdict.AppearsOk, string.Join("; ", result.After.Problems));
        Assert.Null(HfsWriter.Check(ForkData.FromBytes(result.Volume!)));
        return result.Volume!;
    }

    [Fact]
    public void A_missing_alternate_MDB_is_written_from_the_primary()
    {
        var image = Base();
        image.AsSpan(Alternate(image), Sector).Clear();

        var report = Verify(image);

        Assert.Equal(FirstAidVerdict.NeedsRepair, report.Verdict);                 // checked by the primary MDB
        Assert.Single(report.Problems);
        Extra(report, "firstaid.alternate-mdb-missing");
        Assert.True(report.Repairs.HasFlag(FirstAidRepairs.AlternateMdb));
        var repaired = Repaired(image);
        Assert.Equal(repaired.AsSpan(Primary, Sector).ToArray(), repaired.AsSpan(Alternate(repaired), Sector).ToArray());
    }

    [Fact]
    public void A_stale_alternate_MDB_is_written_from_the_primary()
    {
        var image = Base();
        Put16(image, Alternate(image) + 0x96, U16(image, Alternate(image) + 0x96) + 1);   // the catalog's first extent moved

        var report = Verify(image);

        Assert.Equal(FirstAidVerdict.NeedsRepair, report.Verdict);
        Assert.Single(report.Problems);
        Extra(report, "firstaid.alternate-mdb-stale");
        Repaired(image);
    }

    [Fact]
    public void A_wrong_root_file_count_is_reported_and_set()
    {
        var image = Base();
        ushort files = U16(image, Primary + 0x0C);
        Put16(image, Primary + 0x0C, files + 3);

        var report = Verify(image);

        Assert.Equal(FirstAidVerdict.NeedsRepair, report.Verdict);
        Extra(report, "firstaid.mdb-counts");
        Assert.Equal(files, U16(Repaired(image), Primary + 0x0C));
    }

    [Fact]
    public void A_wrong_free_block_count_is_reported_and_set()
    {
        var image = Base();
        ushort free = U16(image, Primary + 0x22);
        Put16(image, Primary + 0x22, free - 1);

        var report = Verify(image);

        Assert.Equal(FirstAidVerdict.NeedsRepair, report.Verdict);
        Extra(report, "firstaid.mdb-counts");
        Assert.Equal(free, U16(Repaired(image), Primary + 0x22));
    }

    [Fact]
    public void An_overflow_record_s_wrong_start_block_is_renumbered()
    {
        var builder = new HfsBuilder();
        builder.File(HfsBuilder.Root, "Fragmented", new byte[8 * HfsBuilder.Block], [], fragments: 8);
        var image = builder.Build("Disk");
        var keys = ExtentsKeys(image);
        Assert.Equal((3, 6), (U16(image, keys[0] + 6), U16(image, keys[1] + 6)));
        Put16(image, keys[1] + 6, 7);

        var report = Verify(image);

        Assert.Equal(FirstAidVerdict.NeedsRepair, report.Verdict);
        Extra(report, "firstaid.extent-start");
        var repaired = Repaired(image);
        Assert.Equal(6, U16(repaired, ExtentsKeys(repaired)[1] + 6));
    }

    [Fact]
    public void A_short_physical_length_is_set_to_the_fork_s_blocks()
    {
        var image = Base();
        int file = Record(image, 2, "T1");
        uint peof = U32(image, file + 0x1E);
        Put32(image, file + 0x1A, 100);                                            // LEOF
        Put32(image, file + 0x1E, 512);                                            // PEOF, short of its 10 blocks

        var report = Verify(image);

        Assert.Equal(FirstAidVerdict.NeedsRepair, report.Verdict);                 // MountCheck's minor finding
        Extra(report, "firstaid.short-peof");
        var repaired = Repaired(image);
        Assert.Equal(peof, U32(repaired, Record(repaired, 2, "T1") + 0x1E));
    }

    [Fact]
    public void An_extent_past_the_last_block_is_reported()
    {
        var image = Base();
        int file = Record(image, 2, "T1");
        uint blocks = U16(image, Primary + 0x12);
        Put16(image, file + 0x4A, (int)blocks - 2);                                 // its 10 blocks from N − 2

        var report = Verify(image);

        var problem = Extra(report, "firstaid.extent-past-end");
        Assert.False(problem.Repairable);
        Assert.Equal(FirstAidVerdict.CannotRepair, report.Verdict);
        Assert.Equal($"Problem:  {problem.Message}.", problem.ToString());
    }

    [Fact]
    public void A_fragmented_volume_ClassicMac_builds_has_no_problems()
    {
        var builder = new HfsBuilder();
        builder.File(HfsBuilder.Root, "Fragmented", new byte[8 * HfsBuilder.Block], new byte[3 * HfsBuilder.Block], fragments: 8, thread: true);
        var report = Verify(builder.Build("Disk"));

        Assert.Empty(report.Problems);
    }
}
