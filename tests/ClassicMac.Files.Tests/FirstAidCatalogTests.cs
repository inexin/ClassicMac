using ClassicMac.Files.Hfs;
using static ClassicMac.Files.Tests.FirstAidImages;

namespace ClassicMac.Files.Tests;

// "Checking catalog file." (hfs.md §5.6): each catalog record in key order, the threads paired with their records,
// each file's forks against their extents, and then MountCheck's view of the catalog and the bitmap. The cases are the
// spec's, most run live under Mac OS 9.0 [Verified: Disk First Aid 8.5.5].
public class FirstAidCatalogTests
{
    private static FirstAidProblem Single(byte[] image, int number) => Assert.Single(Verify(image).Problems, p => p.Number == number);

    private static string[] Lines(byte[] image) => [.. DiskFirstAid(Verify(image)).Select(p => p.ToString())];

    [Fact]
    public void Reserved_fields_in_a_file_record_are_repaired_and_the_scan_goes_on()
    {
        var image = Base();
        Put16(image, Record(image, 2, "T1") + 0x18, 5);                            // C1: filStBlk = 5

        var report = Verify(image);

        var problem = Single(image, 64);
        Assert.Equal((T1, true), (problem.Arg2, problem.Repairable));
        Assert.True(report.Repairs.HasFlag(FirstAidRepairs.ReservedFields));
        Assert.Equal(FirstAidVerdict.NeedsRepair, report.Verdict);
        Assert.Contains("Checking catalog file.", report.Stages);
    }

    [Theory]
    [InlineData(0x02)]   // a nonzero dirFlags
    public void Reserved_fields_in_a_folder_record_are_repaired(int flags)
    {
        var image = Base();
        Put16(image, Record(image, 2, "D1") + 2, flags);

        Assert.Equal(D1, Single(image, 64).Arg2);
    }

    [Fact]
    public void A_PEOF_past_the_file_s_blocks_cannot_be_repaired()
    {
        var image = Base();
        int file = Record(image, 2, "T1");
        Put32(image, file + 0x1E, U32(image, file + 0x1E) + Sector);               // C3 [Verified]

        var report = Verify(image);

        Assert.Equal((1, T1), (Single(image, 1).Number, Single(image, 1).Arg2));
        Assert.Equal(FirstAidVerdict.CannotRepair, report.Verdict);
    }

    [Fact]
    public void A_LEOF_past_the_PEOF_cannot_be_repaired()
    {
        var image = Base();
        int file = Record(image, 2, "T1");
        Put32(image, file + 0x1A, U32(image, file + 0x1E) + 1);                    // C5 [Verified]

        Assert.Equal(T1, Single(image, 2).Arg2);
        Assert.Equal(FirstAidVerdict.CannotRepair, Verify(image).Verdict);
    }

    [Fact]
    public void A_PEOF_short_of_the_file_s_blocks_is_only_MountCheck_s_minor_error()
    {
        var image = Base();
        int file = Record(image, 2, "T1");
        uint peof = U32(image, file + 0x1E) - Sector;                              // C4 [Verified]
        Put32(image, file + 0x1E, peof);
        Put32(image, file + 0x1A, Math.Min(U32(image, file + 0x1A), peof));

        Assert.Equal(["Problem:  MountCheck found minor errors."], Lines(image));
        Assert.Equal(FirstAidVerdict.NeedsRepair, Verify(image).Verdict);
    }

    [Fact]
    public void A_file_ID_below_16_cannot_be_repaired()
    {
        var image = Base();
        Put32(image, Record(image, 2, "T1") + 0x14, 7);

        Assert.Equal(FirstAidVerdict.CannotRepair, Verify(image).Verdict);
        Single(image, 65);
    }

    [Fact]
    public void A_file_thread_whose_file_is_missing_is_dangling()
    {
        var image = Base();
        int thread = Record(image, D2, "");
        image[thread] = 4;                                                         // D2's thread as a file thread
        image[thread + 0x0F] = (byte)'X';                                          // to (D1, "X2"): no such record

        var problem = Single(image, 6);
        Assert.Equal((D2, true), (problem.Arg2, problem.Repairable));
        Assert.True(Verify(image).Repairs.HasFlag(FirstAidRepairs.FileThreads));
    }

    [Fact]
    public void A_file_thread_to_a_folder_cannot_be_repaired()
    {
        var image = Base();
        image[Record(image, D2, "")] = 4;                                          // D2's thread as a file thread, to D2

        Assert.False(Single(image, 6).Repairable);
        Assert.Equal(FirstAidVerdict.CannotRepair, Verify(image).Verdict);
    }

    [Fact]
    public void A_folder_thread_to_a_missing_folder_is_repaired_by_making_the_folder()
    {
        var image = Base();
        Put32(image, Record(image, D1, "") + 0x0A, 999);                           // #37: thdParID → no such ID

        var problem = Single(image, 37);
        Assert.Equal((D1, true), (problem.Arg2, problem.Repairable));
        Assert.True(Verify(image).Repairs.HasFlag(FirstAidRepairs.MissingFolder));
    }

    [Fact]
    public void A_thread_name_longer_than_31_cannot_be_repaired()
    {
        var image = Base();
        image[Record(image, D1, "") + 0x0E] = 40;                                  // #39

        Single(image, 39);
        Assert.Equal(FirstAidVerdict.CannotRepair, Verify(image).Verdict);
    }

    [Fact]
    public void A_thread_s_reserved_bytes_are_repaired()
    {
        var image = Base();
        image[Record(image, D1, "") + 2] = 1;                                      // #64a

        Assert.Equal(D1, Single(image, 64).Arg2);
    }

    [Fact]
    public void A_custom_icon_flag_without_an_icon_file_is_repaired()
    {
        var image = Base();
        int folder = Record(image, 2, "D1");
        Put16(image, folder + 0x1E, U16(image, folder + 0x1E) | 0x0400);           // C8 [Verified]

        var problem = Single(image, 57);
        Assert.Equal((D1, true), (problem.Arg2, problem.Repairable));
    }

    [Fact]
    public void A_thread_flag_with_no_thread_is_MountCheck_s_minor_error()
    {
        var image = Base();
        image[Record(image, 2, "T1") + 2] |= 0x02;                                 // C2 [Verified]

        Assert.Equal(["Problem:  MountCheck found minor errors."], Lines(image));
    }

    [Fact]
    public void A_folder_s_wrong_valence_shows_first_as_MountCheck_s_minor_error()
    {
        var image = Base();
        int folder = Record(image, 2, "D1");
        Put16(image, folder + 4, U16(image, folder + 4) + 1);                      // C6 [Verified]

        Assert.Equal("Problem:  MountCheck found minor errors.", Lines(image)[0]);
    }

    [Fact]
    public void A_leaked_block_in_the_bitmap_is_MountCheck_s_minor_error_and_a_used_one_marked_free_a_serious_one()
    {
        var leaked = Base();
        uint n = U16(leaked, Primary + 0x12);
        int bitmap = U16(leaked, Primary + 0x0E) * Sector;
        leaked[bitmap + (int)((n - 1) / 8)] |= (byte)(0x80 >> (int)((n - 1) % 8)); // D1 [Verified]
        Assert.Contains("Problem:  MountCheck found minor errors.", Lines(leaked));

        var freed = Base();
        int file = Record(freed, 2, "T1");
        uint block = U16(freed, file + 0x4A);
        freed[bitmap + (int)(block / 8)] &= (byte)~(0x80 >> (int)(block % 8));     // D2 [Verified]
        Assert.Contains("Problem:  MountCheck found serious errors.", Lines(freed));
        Assert.Equal(FirstAidVerdict.NeedsRepair, Verify(freed).Verdict);
    }

    [Fact]
    public void A_wrong_free_block_count_is_not_Disk_First_Aid_s_concern()
    {
        var image = Base();
        Put16(image, Primary + 0x22, U16(image, Primary + 0x22) - 1);              // B4 [Verified]
        Put16(image, Primary + 0x0C, U16(image, Primary + 0x0C) + 1);              // B5: drNmFls [Verified]

        Assert.DoesNotContain(Verify(image).Problems, p => p.Origin == FirstAidOrigin.DiskFirstAid);
    }

    [Fact]
    public void An_extent_starting_past_the_last_block_cannot_be_repaired()
    {
        var image = Base();
        Put16(image, Record(image, 2, "T1") + 0x4A, 0xFFF0);

        Single(image, 11);
        Assert.Equal(FirstAidVerdict.CannotRepair, Verify(image).Verdict);
    }
}
