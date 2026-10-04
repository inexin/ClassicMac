using ClassicMac.Files.Hfs;
using static ClassicMac.Files.Tests.FirstAidImages;

namespace ClassicMac.Files.Tests;

// Disk First Aid's B-tree stages (hfs.md §5.6): the B-tree files' setup from the alternate MDB, then each tree's
// header, walk, map and header compare.
public class FirstAidBTreeTests
{
    private static FirstAidProblem Only(byte[] image) => Assert.Single(Verify(image).Problems);

    [Fact]
    public void A_catalog_PEOF_other_than_its_extents_cannot_be_repaired()
    {
        var image = Base();
        Put32(image, Alternate(image) + 0x92, U32(image, Alternate(image) + 0x92) + Sector);    // A4 [Verified]

        var report = Verify(image);

        Assert.Equal((46, 4L, 0L), (Only(image).Number, Only(image).Arg2, Only(image).Arg3));
        Assert.Equal(FirstAidVerdict.CannotRepair, report.Verdict);
    }

    [Fact]
    public void An_extents_file_PEOF_other_than_its_extents_cannot_be_repaired()
    {
        var image = Base();
        Put32(image, Alternate(image) + 0x82, U32(image, Alternate(image) + 0x82) + Sector);

        Assert.Equal((47, 3L), (Only(image).Number, Only(image).Arg2));
    }

    [Fact]
    public void A_node_size_that_is_not_a_power_of_two_from_512_cannot_be_repaired()
    {
        var image = Base();
        Put16(image, CatalogNode(image, 0) + 14 + 18, 1000);

        Assert.Equal(61, Only(image).Number);
    }

    [Theory]
    [InlineData(10, 2, 28)]          // the header node's record count
    [InlineData(14, 9, 29)]          // the header record's tree depth (+$0E)
    public void A_bad_header_node_cannot_be_repaired(int offset, int value, int number)
    {
        var image = Base();
        Put16(image, CatalogNode(image, 0) + offset, value);

        Assert.Equal(number, Only(image).Number);
        Assert.Equal(FirstAidVerdict.CannotRepair, Verify(image).Verdict);
    }

    [Fact]
    public void A_root_past_the_last_node_cannot_be_repaired()
    {
        var image = Base();
        int header = CatalogNode(image, 0) + 14;
        Put32(image, header + 2, U32(image, header + 22));                          // bthRoot = bthNNodes

        Assert.Equal(15, Only(image).Number);
    }

    [Fact]
    public void A_wrong_backward_link_cannot_be_repaired()
    {
        var image = Base();
        Put32(image, CatalogNode(image, FirstLeaf(image)) + 4, 5);

        Assert.Equal(21, Only(image).Number);
    }

    [Fact]
    public void Keys_out_of_order_cannot_be_repaired()
    {
        var image = Base();
        int leaf = CatalogNode(image, FirstLeaf(image));
        Put32(image, leaf + U16(image, leaf + Sector - 4) + 2, 0);                // record 1's parent ID: before the root's

        Assert.Equal(26, Only(image).Number);
    }

    [Fact]
    public void A_wrong_node_height_is_repaired_by_rebuilding_the_tree()
    {
        var image = Base();
        image[CatalogNode(image, FirstLeaf(image)) + 9] = 2;

        var report = Verify(image);

        var problem = Assert.Single(report.Problems);
        Assert.Equal((5, true), (problem.Number, problem.Repairable));
        Assert.True(report.Repairs.HasFlag(FirstAidRepairs.RebuildBTree));
        Assert.Equal(FirstAidVerdict.NeedsRepair, report.Verdict);
    }

    [Fact]
    public void A_wrong_header_record_is_repaired_by_writing_the_computed_one()
    {
        var image = Base();
        int header = CatalogNode(image, 0) + 14;
        Put32(image, header + 6, U32(image, header + 6) + 1);                       // C9: bthNRecs + 1 [Verified]

        var report = Verify(image);

        var problem = Assert.Single(report.Problems);
        Assert.Equal((54, 0L, 0L, true), (problem.Number, problem.Arg2, problem.Arg3, problem.Repairable));
        Assert.Equal("Checking catalog BTree.", problem.Stage);
        Assert.True(report.Repairs.HasFlag(FirstAidRepairs.BTreeHeader));
    }

    [Fact]
    public void A_catalog_with_index_levels_ClassicMac_writes_appears_to_be_OK()
    {
        var image = Base();
        for (var i = 0; i < 120; i++)
        {
            image = HfsWriter.CreateFolder(ForkData.FromBytes(image), $"D1:Folder {i:D3}");
        }

        var report = Verify(image);

        Assert.Empty(report.Problems);
        Assert.Equal(FirstAidVerdict.AppearsOk, report.Verdict);
    }

    [Fact]
    public void The_stages_run_in_Disk_First_Aid_s_order()
    {
        Assert.Equal(
        [
            "Checking disk volume.", "Checking \"Mac OS Standard\" volume structures.", "Checking for locked volume name.",
            "Checking extent BTree.", "Checking extent file.", "Checking catalog BTree.",
        ], Verify(Base()).Stages.Take(6));
    }

    private static uint FirstLeaf(byte[] image) => U32(image, CatalogNode(image, 0) + 14 + 10);
}
