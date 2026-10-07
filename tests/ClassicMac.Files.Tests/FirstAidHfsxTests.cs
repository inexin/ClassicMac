using ClassicMac.Core;
using ClassicMac.Files.Containers;
using ClassicMac.Files.Hfs;
using static ClassicMac.Files.Tests.FirstAidPlusTests;

namespace ClassicMac.Files.Tests;

// First Aid on HFSX (hfs-plus.md §4, §5.4): checked and repaired as HFS Plus, its catalog in the order its header names
// (case folding, or binary so names that differ in case are distinct), each folder's folder count its folders.
public class FirstAidHfsxTests
{
    private static byte[] Volume(bool caseSensitive)
    {
        var builder = new HfsPlusBuilder { Hfsx = true, CaseSensitive = caseSensitive };
        uint docs = builder.Folder(HfsPlusBuilder.Root, "Docs");
        builder.Folder(docs, "Inner");
        builder.File(docs, "Read Me", "one"u8.ToArray(), []);
        if (caseSensitive)
        {
            builder.File(docs, "read me", "two"u8.ToArray(), []);              // distinct only when names keep their case
        }

        return builder.Build("Plus X");
    }

    private static void ReadsClean(byte[] image)
    {
        var context = new ContainerContext();
        HfsReader.Instance.Read(ForkData.FromBytes(image), context);
        Assert.True(context.Diagnostics.Count == 0, string.Join("; ", context.Diagnostics.Select(d => d.Message)));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void A_sound_HFSX_volume_appears_to_be_OK(bool caseSensitive)
    {
        var image = Volume(caseSensitive);
        ReadsClean(image);

        var report = Verify(image);

        Assert.True(report.Problems.Count == 0, string.Join("; ", report.Problems));
        Assert.Equal(FirstAidVerdict.AppearsOk, report.Verdict);
        Assert.True(report.HfsPlus);
    }

    [Fact]
    public void A_wrong_folder_count_is_repaired()
    {
        var image = Volume(caseSensitive: false);
        int docs = Record(image, HfsPlusBuilder.Root, "Docs");
        Put32(image, docs + 84, 7);

        var report = Verify(image);
        Assert.Equal("firstaid.folder-count", Assert.Single(report.Problems).Code);

        var result = HfsFirstAid.Repair(ForkData.FromBytes(image));
        Assert.Equal(FirstAidVerdict.AppearsOk, result.After.Verdict);
        Assert.Equal(1u, U32(result.Volume!, Record(result.Volume!, HfsPlusBuilder.Root, "Docs") + 84));
        ReadsClean(result.Volume!);
    }

    // Mac OS X 10.4's HFSX: no folder has the has-folder-count flag, every count 0 [Verified: Mac OS X 10.4.6 newfs_hfs -s,
    // its fsck_hfs finding such volumes OK]. They verify clean, and a repair of something else leaves the flags clear, as
    // 10.4 does not keep the counts up.
    [Fact]
    public void Folders_without_the_folder_count_flag_are_not_counted_and_stay_so()
    {
        var image = Volume(caseSensitive: true);
        foreach (var (_, data) in Records(image).Where(r => U16(image, r.Data) == 1).ToList())
        {
            image[data + 3] &= unchecked((byte)~0x10);
            Put32(image, data + 84, 0);
        }

        Assert.Equal(FirstAidVerdict.AppearsOk, Verify(image).Verdict);

        Put32(image, Record(image, HfsPlusBuilder.Root, "Docs") + 4, 9);         // valence
        var result = HfsFirstAid.Repair(ForkData.FromBytes(image));

        Assert.Equal(FirstAidVerdict.AppearsOk, result.After.Verdict);
        Assert.All(Records(result.Volume!).Where(r => U16(result.Volume!, r.Data) == 1),
            r => Assert.Equal((0, 0u), (result.Volume![r.Data + 3] & 0x10, U32(result.Volume!, r.Data + 84))));
    }

    // A case-sensitive catalog repaired keeps its binary order: both names stay, each with its own data.
    [Fact]
    public void A_case_sensitive_catalog_is_repaired_in_its_own_order()
    {
        var image = Volume(caseSensitive: true);
        int docs = Record(image, HfsPlusBuilder.Root, "Docs");
        Put32(image, docs + 4, 9);                                                 // valence

        var result = HfsFirstAid.Repair(ForkData.FromBytes(image));

        Assert.Equal(FirstAidVerdict.AppearsOk, result.After.Verdict);
        ReadsClean(result.Volume!);
        var files = HfsReader.Instance.Read(ForkData.FromBytes(result.Volume!), new ContainerContext());
        Assert.Equal("one"u8.ToArray(), files.Single(f => f.MacPath == "Docs:Read Me").DataFork.ToArray());
        Assert.Equal("two"u8.ToArray(), files.Single(f => f.MacPath == "Docs:read me").DataFork.ToArray());
    }
}
