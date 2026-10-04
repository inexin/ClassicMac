using ClassicMac.Files.Hfs;
using static ClassicMac.Files.Tests.FirstAidImages;

namespace ClassicMac.Files.Tests;

// The last stages (hfs.md §5.6): "Checking catalog hierarchy.", "Checking volume bit map." and "Checking volume info.".
// Most cases were run live under Mac OS 9.0 [Verified: Disk First Aid 8.5.5].
public class FirstAidVolumeTests
{
    private static FirstAidProblem Single(byte[] image, int number) => Assert.Single(Verify(image).Problems, p => p.Number == number);

    [Fact]
    public void A_wrong_valence_is_repaired_to_the_count_of_the_folder_s_items()
    {
        var image = Base();
        int folder = Record(image, 2, "D1");
        Put16(image, folder + 4, U16(image, folder + 4) + 1);                      // C6 [Verified]

        var problem = Single(image, 3);
        Assert.Equal((D1, true, "Checking catalog hierarchy."), (problem.Arg2, problem.Repairable, problem.Stage));
        Assert.True(Verify(image).Repairs.HasFlag(FirstAidRepairs.Valences));
    }

    [Fact]
    public void Folders_nested_past_100_are_reported_and_the_rest_of_the_hierarchy_is_not_checked()
    {
        var image = HfsWriter.Format(4L * 1024 * 1024, "Deep");
        var path = "";
        for (var i = 0; i < 101; i++)
        {
            path = i == 0 ? "F" : path + ":F";
            image = HfsWriter.CreateFolder(ForkData.FromBytes(image), path);
        }

        var report = Verify(image);

        Assert.Equal(48, Assert.Single(report.Problems).Number);
        Assert.Equal(FirstAidVerdict.AppearsOk, report.Verdict);                   // it sets no repair
    }

    [Theory]
    [InlineData(0)]   // D1: a free block's bit set (a leak) [Verified]
    [InlineData(1)]   // D3: a byte after the bitmap, in its last sector [Verified]
    [InlineData(2)]   // D4: the bit of block N, after the last block [Verified]
    public void A_bitmap_other_than_the_blocks_in_use_is_written_again(int edit)
    {
        var image = Base();
        uint n = U16(image, Primary + 0x12);
        int bitmap = U16(image, Primary + 0x0E) * Sector;
        switch (edit)
        {
            case 0:
                image[bitmap + (int)((n - 1) / 8)] |= (byte)(0x80 >> (int)((n - 1) % 8));
                break;
            case 1:
                image[bitmap + (int)((n + 7) / 8) + 4] = 0x80;
                break;
            default:
                image[bitmap + (int)(n / 8)] |= (byte)(0x80 >> (int)(n % 8));
                break;
        }

        var report = Verify(image);

        Assert.True(Single(image, 60).Repairable);
        Assert.Equal("Checking volume bit map.", Single(image, 60).Stage);
        Assert.Equal((4L, 0L), (Single(image, 60).Arg2, Single(image, 60).Arg3));
        Assert.True(report.Repairs.HasFlag(FirstAidRepairs.Bitmap));
    }

    [Fact]
    public void Files_sharing_blocks_are_overlapped_extents()
    {
        var image = Base();
        Put16(image, Record(image, D2, "T2") + 0x4A, U16(image, Record(image, 2, "T1") + 0x4A));

        var report = Verify(image);

        Assert.True(Single(image, 12).Repairable);
        Assert.True(report.Repairs.HasFlag(FirstAidRepairs.OverlappingExtents));
        Assert.Equal(FirstAidVerdict.NeedsRepair, report.Verdict);
    }

    [Theory]
    [InlineData("alternate", 0x02, 4, 1, 1)]           // A2: ALT.drCrDate + 1
    [InlineData("primary", 0x86 + 2, 2, 1, 4)]         // A3: PRI.drXTExtRec[0].count + 1
    [InlineData("primary", 0x1E, 4, 5000, 1)]          // B2: drNxtCNID 5,000 past the next
    [InlineData("primary", 0x1E, 4, -1, 1)]            // B3: drNxtCNID = the highest CNID
    [InlineData("primary", 0x54, 4, 1, 2)]             // B6: drFilCnt + 1
    [InlineData("primary", 0x52, 2, 1, 2)]             // drNmRtDirs + 1
    public void An_MDB_other_than_the_volume_s_is_written_again(string which, int offset, int size, int delta, int detail)
    {
        var image = Base();
        int at = (which == "alternate" ? Alternate(image) : Primary) + offset;
        if (size == 2)
        {
            Put16(image, at, U16(image, at) + delta);
        }
        else
        {
            Put32(image, at, U32(image, at) + delta);
        }

        var problem = Single(image, 58);

        Assert.Equal((detail, 0L, true), (problem.Arg2, problem.Arg3, problem.Repairable));
        Assert.Equal("Checking volume info.", problem.Stage);
        Assert.True(Verify(image).Repairs.HasFlag(FirstAidRepairs.Mdb));
    }

    [Fact]
    public void An_MDB_s_attributes_and_clump_size_are_checked()
    {
        var locked = Base();
        Put16(locked, Primary + 0x0A, U16(locked, Primary + 0x0A) | 0x0001);       // B7 [Verified]
        Assert.Equal(1, Single(locked, 58).Arg2);

        var clump = Base();
        Put32(clump, Primary + 0x18, U32(clump, Primary + 0x14) + 1);               // B8: drClpSiz = A + 1 [Verified]
        Assert.Equal(1, Single(clump, 58).Arg2);
    }

    [Fact]
    public void A_next_CNID_a_little_past_the_highest_is_kept()
    {
        var image = Base();
        Put32(image, Primary + 0x1E, U32(image, Primary + 0x1E) + 100);            // B1 [Verified]

        Assert.Equal(FirstAidVerdict.AppearsOk, Verify(image).Verdict);
    }

    [Fact]
    public void A_volume_name_other_than_the_root_folder_s_is_written_again()
    {
        var image = Base();
        image[Primary + 0x25] = (byte)'f';                                         // "first Aid"

        Assert.Equal(1, Single(image, 58).Arg2);
    }

    [Fact]
    public void All_ten_stages_run_on_a_sound_volume()
    {
        Assert.Equal(
        [
            "Checking disk volume.", "Checking \"Mac OS Standard\" volume structures.", "Checking for locked volume name.",
            "Checking extent BTree.", "Checking extent file.", "Checking catalog BTree.", "Checking catalog file.",
            "Checking catalog hierarchy.", "Checking volume bit map.", "Checking volume info.",
        ], Verify(Base()).Stages);
    }
}
