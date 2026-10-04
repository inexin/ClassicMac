using ClassicMac.Files.Hfs;
using static ClassicMac.Files.Tests.FirstAidImages;

namespace ClassicMac.Files.Tests;

// Disk First Aid's first stage, "Checking disk volume" (hfs.md §5.6): the alternate MDB decides whether the disk is
// HFS, and its geometry is checked; a bad geometry ends the check, which cannot repair it.
public class FirstAidVolumeInfoTests
{
    [Fact]
    public void A_volume_ClassicMac_makes_appears_to_be_OK()
    {
        var report = Verify(Base());

        Assert.Equal(FirstAidVerdict.AppearsOk, report.Verdict);
        Assert.Empty(report.Problems);
        Assert.Equal("Checking disk volume.", report.Stages[0]);
    }

    [Fact]
    public void Without_the_alternate_MDB_it_is_not_an_HFS_disk()
    {
        var image = Base();
        Put16(image, Alternate(image), 0);                                         // A1 [Verified]

        var report = Verify(image);

        Assert.Equal(FirstAidVerdict.NotHfs, report.Verdict);
        Assert.Equal("This is not an HFS disk.", report.Summary);
    }

    [Theory]
    [InlineData(0x14, 4, 0x300, 7)]       // drAlBlkSiz not a multiple of 512
    [InlineData(0x14, 4, 0x7FFFFF00, 7)]  // too large
    [InlineData(0x12, 2, 0xFFFF, 8)]      // more allocation blocks than the volume holds
    [InlineData(0x0E, 2, 2, 9)]           // drVBMSt at or before the MDB
    [InlineData(0x1C, 2, 3, 10)]          // drAlBlSt before the bitmap's end
    public void A_bad_geometry_in_the_alternate_MDB_cannot_be_repaired(int offset, int size, long value, int number)
    {
        var image = Base();
        if (size == 2)
        {
            Put16(image, Alternate(image) + offset, (int)value);
        }
        else
        {
            Put32(image, Alternate(image) + offset, value);
        }

        var report = Verify(image);

        var problem = Assert.Single(DiskFirstAid(report));
        Assert.Equal(number, problem.Number);
        Assert.Contains(report.Problems, p => p.Code == "firstaid.alternate-mdb-stale");     // ClassicMac's, besides
        Assert.False(problem.Repairable);
        Assert.Equal(FirstAidVerdict.CannotRepair, report.Verdict);
        Assert.Equal("Test done. Problems were found, but Disk First Aid cannot repair them.", report.Summary);
    }

    [Fact]
    public void The_allocation_block_size_must_keep_the_block_count_under_65536()
    {
        // 64 MB in 512-byte blocks would be 131,068 blocks: the smallest size that fits is 1,024.
        var image = HfsWriter.Format(64L * 1024 * 1024, "Big");
        Put32(image, Alternate(image) + 0x14, 512);

        Assert.Equal(7, Assert.Single(DiskFirstAid(Verify(image))).Number);
    }

    [Fact]
    public void Only_the_alternate_MDB_s_geometry_is_checked_here()
    {
        var image = Base();
        Put32(image, Primary + 0x14, 0x300);                                       // the primary's: the MDB compare's

        Assert.DoesNotContain(Verify(image).Problems, p => p.Number == 7);
    }

    [Fact]
    public void Problems_carry_Disk_First_Aid_s_numbers_and_words()
    {
        Assert.Equal("Invalid PEOF", FirstAidMessages.Text(1));
        Assert.Equal("Invalid allocation block size", FirstAidMessages.Text(7));
        Assert.Equal("Volume Bit Map needs minor repair", FirstAidMessages.Text(60));
        Assert.Null(FirstAidMessages.Text(17));                                    // the blank entries
        Assert.Equal("firstaid.invalid-allocation-block-size", FirstAidMessages.Code(7));
    }
}
