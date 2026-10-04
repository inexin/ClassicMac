using ClassicMac.Core;
using ClassicMac.Files.Containers;
using ClassicMac.Files.Hfs;
using static ClassicMac.Files.Tests.FirstAidPlusTests;

namespace ClassicMac.Files.Tests;

// The journal of an HFS Plus volume (TN1150 "Journal"; hfs-plus.md §5.4): a check replays its transactions on a copy
// and checks the volume as they leave it; repair writes them to the volume and empties the journal.
public class FirstAidPlusJournalTests
{
    private const int Sector = HfsPlusBuilder.JournalSector, ListHeader = HfsPlusBuilder.BlockListHeader;

    private static byte[] Journaled()
    {
        var builder = new HfsPlusBuilder { JournalBlocks = 4 };
        uint docs = builder.Folder(HfsPlusBuilder.Root, "Docs");
        builder.File(docs, "Letter", "dear sir"u8.ToArray(), []);
        return builder.Build("Journaled");
    }

    // The journal's start, from the journal info block.
    private static int JournalStart(byte[] image) =>
        (int)new BigEndianReader(image).ReadUInt64At((int)(U32(image, Header + 12) * Block) + 36);

    // One transaction of one block list holding a copy of bytes for the volume's byte offset.
    private static void Journal(byte[] image, long target, byte[] bytes, bool badChecksum = false)
    {
        int journal = JournalStart(image), list = journal + Sector;
        image.AsSpan(list, ListHeader).Clear();
        var w = new BigEndianWriter(image);
        w.WriteUInt16At(list + 2, (ushort)2);                                      // binfo[0] and one block
        w.WriteUInt32At(list + 4, (uint)(ListHeader + bytes.Length));             // bytes_used
        w.WriteUInt64At(list + 32, (ulong)(target / Sector));                     // binfo[1].bnum, in journal sectors
        w.WriteUInt32At(list + 40, (uint)bytes.Length);                           // binfo[1].bsize
        uint checksum = HfsPlusBuilder.Checksum(image.AsSpan(list, 32), 8);
        w.WriteUInt32At(list + 8, badChecksum ? checksum + 1 : checksum);
        bytes.CopyTo(image, list + ListHeader);
        long size = (long)new BigEndianReader(image).ReadUInt64At(journal + 24);
        HfsPlusBuilder.WriteJournalHeader(image, journal, size, Sector, Sector + ListHeader + bytes.Length);
    }

    [Fact]
    public void A_journaled_volume_reads_and_appears_to_be_OK()
    {
        var image = Journaled();
        var context = new ContainerContext();
        HfsReader.Instance.Read(ForkData.FromBytes(image), context);

        var problems = context.Diagnostics.Where(d => d.Severity != ClassicMac.Core.DiagnosticSeverity.Info).ToList();
        Assert.True(problems.Count == 0, string.Join("; ", problems.Select(d => d.Message)));
        Assert.Empty(Verify(image).Problems);
    }

    // The journal holds the catalog node as it should be; on disk the folder's valence is wrong. The check replays the
    // journal first, so only the pending journal is a problem; repair writes it and empties the journal.
    [Fact]
    public void A_journal_with_transactions_is_replayed()
    {
        var image = Journaled();
        int docs = Record(image, HfsPlusBuilder.Root, "Docs");
        int node = CatalogNode(image, 0) + (docs - CatalogNode(image, 0)) / Block * Block;
        Journal(image, node, image.AsSpan(node, Block).ToArray());
        Put32(image, docs + 4, 9);
        var original = image.ToArray();

        var report = Verify(image);

        Assert.Equal("firstaid.journal-pending", Assert.Single(report.Problems).Code);
        Assert.Equal(FirstAidVerdict.NeedsRepair, report.Verdict);
        Assert.Contains(report.Stages, s => s.StartsWith("Replaying the journal", StringComparison.Ordinal));
        Assert.Equal(original, image);                                            // a check writes nothing
        var result = HfsFirstAid.Repair(ForkData.FromBytes(image));
        Assert.Equal(FirstAidVerdict.AppearsOk, result.After.Verdict);
        var repaired = result.Volume!;
        Assert.Equal(1u, U32(repaired, Record(repaired, HfsPlusBuilder.Root, "Docs") + 4));
        int journal = JournalStart(repaired);
        var header = new BigEndianReader(repaired);
        Assert.Equal(header.ReadUInt64At(journal + 8), header.ReadUInt64At(journal + 16));   // empty: start = end
        Assert.Contains(result.Changes, c => c.Detail.StartsWith("journal replayed", StringComparison.Ordinal));
    }

    [Fact]
    public void A_damaged_journal_cannot_be_repaired()
    {
        var image = Journaled();
        int docs = Record(image, HfsPlusBuilder.Root, "Docs");
        int node = CatalogNode(image, 0) + (docs - CatalogNode(image, 0)) / Block * Block;
        Journal(image, node, image.AsSpan(node, Block).ToArray(), badChecksum: true);

        var report = Verify(image);

        Assert.Contains(report.Problems, p => p.Code == "firstaid.journal-damaged" && !p.Repairable);
        Assert.Equal(FirstAidVerdict.CannotRepair, report.Verdict);
    }
}
