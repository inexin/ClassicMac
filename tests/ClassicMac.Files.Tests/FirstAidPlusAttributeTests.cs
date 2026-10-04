using ClassicMac.Core;
using ClassicMac.Files.Containers;
using ClassicMac.Files.Hfs;
using static ClassicMac.Files.Tests.FirstAidPlusTests;

namespace ClassicMac.Files.Tests;

// The attributes tree's records on HFS Plus (hfs-plus.md §1.7, §2.6, §5.4): inline data within its record, fork-data
// attributes whose extents add up to their blocks and count as in use, extension records after their fork record, and
// every attribute a catalog file's or folder's; bad records are deleted by repair.
public class FirstAidPlusAttributeTests
{
    private const uint Docs = 16, Letter = 17;

    private static HfsPlusBuilder Builder()
    {
        var builder = new HfsPlusBuilder();
        uint docs = builder.Folder(HfsPlusBuilder.Root, "Docs");
        uint letter = builder.File(docs, "Letter", "dear sir"u8.ToArray(), []);
        builder.Attribute(docs, "com.example.tag", "red"u8.ToArray());
        builder.Attribute(letter, "com.example.big", new byte[2 * Block + 10], fork: true);
        return builder;
    }

    // The attribute records' (key, data) offsets, in order, by the header's attributes fork.
    private static List<(int Key, int Data)> Attributes(byte[] image)
    {
        var found = new List<(int, int)>();
        for (uint node = U32(image, Node(image, 352, 0) + 14 + 10); node != 0; node = U32(image, Node(image, 352, node)))
        {
            int at = Node(image, 352, node);
            for (var i = 0; i < U16(image, at + 10); i++)
            {
                int key = at + U16(image, at + Block - 2 * (i + 1));
                found.Add((key, key + 2 + U16(image, key)));
            }
        }

        return found;
    }

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

    [Fact]
    public void A_volume_with_inline_and_fork_attributes_appears_to_be_OK()
    {
        var image = Builder().Build("Attrs");
        var context = new ContainerContext();
        HfsReader.Instance.Read(ForkData.FromBytes(image), context);

        Assert.True(context.Diagnostics.Count == 0, string.Join("; ", context.Diagnostics.Select(d => d.Message)));
        var report = Verify(image);
        Assert.True(report.Problems.Count == 0, string.Join("; ", report.Problems));
        Assert.Contains(FirstAidMessages.CheckingAttributesBTree, report.Stages);
    }

    [Fact]
    public void An_attribute_of_no_file_or_folder_is_deleted()
    {
        var image = Builder().Build("Attrs");
        var (key, _) = Attributes(image).First(a => U32(image, a.Key + 4) == Docs);
        Put32(image, key + 4, 15);                                                 // still first in key order, no such CNID

        var report = Verify(image);
        Assert.Contains(report.Problems, p => p.Code == "firstaid.attribute-owner");

        var repaired = Repaired(image);
        Assert.DoesNotContain(Attributes(repaired), a => U32(repaired, a.Key + 4) == 15);
    }

    [Fact]
    public void An_inline_attribute_longer_than_its_record_is_deleted()
    {
        var image = Builder().Build("Attrs");
        var (_, data) = Attributes(image).First(a => U32(image, a.Key + 4) == Docs);
        Put32(image, data + 12, 500);                                              // attrSize past the record

        Assert.Contains(Verify(image).Problems, p => p.Code == "firstaid.attribute-record");
        Assert.Single(Attributes(Repaired(image)));
    }

    [Fact]
    public void A_fork_attribute_s_blocks_count_as_in_use()
    {
        var image = Builder().Build("Attrs");
        var (_, data) = Attributes(image).First(a => U32(image, a.Key + 4) == Letter);
        uint first = U32(image, data + 8 + 16);
        int bitmap = (int)(U32(image, Header + 112 + 16) * Block);
        image[bitmap + (int)(first / 8)] &= (byte)~(0x80 >> (int)(first % 8));

        Assert.Contains(Verify(image).Problems, p => p.Number == 60);
    }

    [Fact]
    public void A_fork_attribute_s_extents_short_of_its_blocks_cannot_be_repaired()
    {
        var image = Builder().Build("Attrs");
        var (_, data) = Attributes(image).First(a => U32(image, a.Key + 4) == Letter);
        Put32(image, data + 8 + 12, 9);                                            // totalBlocks past its extents

        var report = Verify(image);

        Assert.Contains(report.Problems, p => p.Code == "firstaid.attribute-extents" && !p.Repairable);
        Assert.Equal(FirstAidVerdict.CannotRepair, report.Verdict);
    }
}
