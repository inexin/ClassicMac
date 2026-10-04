using System;
using System.Linq;
using ClassicMac.Core;
using static ClassicMac.Files.Hfs.HfsRecords;

namespace ClassicMac.Files.Hfs;

// "Checking volume info." on HFS Plus (hfs.md §5.6): the volume header's file and folder counts, free blocks and next
// CNID against the catalog and the allocation the check built; any difference is #59 "Volume Header needs minor
// repair", which repair fixes by writing the computed header (and the alternate from it). Overflow extents records of
// files not in the catalog are reported here too.
internal static class PlusHeaderCompare
{
    private const uint CatalogNodeIdsReused = 0x1000;

    public static bool Run(FirstAidRun run)
    {
        run.Stage(FirstAidMessages.CheckingVolumeInfo);
        var computed = (byte[])run.Primary.Clone();
        var writer = new BigEndianWriter(computed);
        var header = new BigEndianReader(run.Primary);
        writer.WriteUInt32At(32, run.FileCount);
        writer.WriteUInt32At(36, run.DirCount);
        long used = 0;
        for (uint block = 0; block < run.BlockCount; block++)
        {
            used += (run.ComputedBitmap[block / 8] >> (7 - (int)(block % 8))) & 1;
        }

        writer.WriteUInt32At(48, run.BlockCount - used);
        if ((header.ReadUInt32At(4) & CatalogNodeIdsReused) == 0 && header.ReadUInt32At(64) < run.NextCnid)
        {
            writer.WriteUInt32At(64, run.NextCnid);
        }

        run.ComputedMdb = computed;
        if (!computed.AsSpan().SequenceEqual(run.Primary))
        {
            // Disk First Aid prints the compare group, 1 for the header's counts [Verified: Mac RE P1, P4, H2 "1, 0";
            // fitted, the other groups not seen].
            run.Flag(59, FirstAidRepairs.Mdb, 1);
        }

        if (run.Extents!.Records.Exists(r => PlusKeyFileId(r.Key) is var id && id >= 16 && !run.FileIds.Contains(id)))
        {
            run.Problem("Overflow extents records belong to a file not in the catalog", "firstaid.orphaned-extents", FirstAidRepairs.OrphanedExtents);
        }

        return true;
    }
}
