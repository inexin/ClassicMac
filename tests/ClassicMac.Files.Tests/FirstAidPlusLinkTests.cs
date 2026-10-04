using ClassicMac.Files.Containers;
using ClassicMac.Files.Hfs;
using static ClassicMac.Files.Tests.FirstAidPlusTests;

namespace ClassicMac.Files.Tests;

// Hard links on HFS Plus (hfs-plus.md §1.8, §5.4): each link's indirect file or folder must exist, and its link count
// is the number of its links, repaired when it is not.
public class FirstAidPlusLinkTests
{
    private static (byte[] Image, uint Inode, uint Folder) Linked()
    {
        var builder = new HfsPlusBuilder();
        uint docs = builder.Folder(HfsPlusBuilder.Root, "Docs");
        uint inode = builder.HardLinks("shared"u8.ToArray(), (HfsPlusBuilder.Root, "Link A"), (docs, "Link B"));
        uint folder = builder.DirectoryHardLinks((HfsPlusBuilder.Root, "Alias A"), (docs, "Alias B"), (docs, "Alias C"));
        return (builder.Build("Links"), inode, folder);
    }



    // The parent of a record named by its thread.
    private static uint Parent(byte[] image, uint id) => U32(image, Record(image, id, "") + 4);

    [Fact]
    public void A_volume_with_hard_links_appears_to_be_OK()
    {
        var (image, _, _) = Linked();
        var context = new ContainerContext();
        var files = HfsPlusReader.Read(ForkData.FromBytes(image), context);

        Assert.Contains(files, f => f.MacPath == "Docs:Link B" && f.DataFork.Length == 6);   // the reader resolves the link
        var report = Verify(image);
        Assert.True(report.Problems.Count == 0, string.Join("; ", report.Problems));
    }

    [Fact]
    public void A_wrong_file_link_count_is_repaired()
    {
        var (image, inode, _) = Linked();
        int at = Record(image, Parent(image, inode), $"iNode{inode}");
        Put32(image, at + 44, 5);

        var report = Verify(image);
        Assert.Equal("firstaid.link-count", Assert.Single(report.Problems).Code);
        var result = HfsFirstAid.Repair(ForkData.FromBytes(image));

        Assert.Equal(FirstAidVerdict.AppearsOk, result.After.Verdict);
        Assert.Equal(2u, U32(result.Volume!, Record(result.Volume!, Parent(result.Volume!, inode), $"iNode{inode}") + 44));
    }

    [Fact]
    public void A_wrong_folder_link_count_is_repaired()
    {
        var (image, _, folder) = Linked();
        int at = Record(image, Parent(image, folder), $"dir_{folder}");
        Put32(image, at + 44, 1);

        Assert.Equal("firstaid.link-count", Assert.Single(Verify(image).Problems).Code);
        var result = HfsFirstAid.Repair(ForkData.FromBytes(image));

        Assert.Equal(FirstAidVerdict.AppearsOk, result.After.Verdict);
        Assert.Equal(3u, U32(result.Volume!, Record(result.Volume!, Parent(result.Volume!, folder), $"dir_{folder}") + 44));
    }

    [Fact]
    public void A_link_to_a_missing_indirect_file_cannot_be_repaired()
    {
        var (image, inode, _) = Linked();
        int link = Record(image, HfsPlusBuilder.Root, "Link A");
        Put32(image, link + 44, inode + 100);                                      // special: no such iNode

        var report = Verify(image);

        Assert.Contains(report.Problems, p => p.Code == "firstaid.link-target-missing" && !p.Repairable);
        Assert.Equal(FirstAidVerdict.CannotRepair, report.Verdict);
    }
}
