using System;
using System.Collections.Generic;
using System.Linq;
using ClassicMac.Core;

namespace ClassicMac.Files.Hfs;

// The checks after Disk First Aid's stages that it lacks (hfs.md §5.6): overflow extents records of files not in the
// catalog, an extent past the last allocation block, a fork's physical length short of its blocks, and the MDB's
// drNmFls and drFreeBks, which Disk First Aid does not compare.
internal static class ClassicMacChecks
{
    public static bool Run(FirstAidRun run)
    {
        foreach (var (key, _, _) in run.Extents!.Records)
        {
            uint fileId = new BigEndianReader(key).ReadUInt32At(2);
            if (fileId >= 16 && !run.FileIds.Contains(fileId))
            {
                run.Problem("Overflow extents records belong to a file not in the catalog", "firstaid.orphaned-extents", FirstAidRepairs.OrphanedExtents);
            }
        }

        if (run.ForkExtents.Exists(e => (long)e.Start + e.Count > run.BlockCount))
        {
            run.Problem("An extent runs past the volume's last allocation block", "firstaid.extent-past-end", FirstAidRepairs.None);
        }

        var allocated = Allocated(run);
        foreach (var (_, data, _) in run.Catalog!.Records.Where(r => r.Data.Length >= 102 && r.Data[0] == 2))
        {
            var reader = new BigEndianReader(data);
            uint id = reader.ReadUInt32At(0x14);
            if (reader.ReadUInt32At(0x1E) < allocated.GetValueOrDefault((id, (byte)0x00)) || reader.ReadUInt32At(0x28) < allocated.GetValueOrDefault((id, (byte)0xFF)))
            {
                run.Problem("A fork's physical length is short of its blocks", "firstaid.short-peof", FirstAidRepairs.ForkLengths);
            }
        }

        var computed = run.ComputedMdb;
        if (!computed.AsSpan(0x0C, 2).SequenceEqual(run.Primary.AsSpan(0x0C, 2)) || !computed.AsSpan(0x22, 2).SequenceEqual(run.Primary.AsSpan(0x22, 2)))
        {
            run.Problem("The MDB's root file count (drNmFls) or free block count (drFreeBks) is wrong", "firstaid.mdb-counts", FirstAidRepairs.Mdb);
        }

        return true;
    }

    /// <summary>The bytes of each fork's blocks, by (file ID, fork).</summary>
    public static Dictionary<(uint FileId, byte Fork), long> Allocated(FirstAidRun run)
    {
        var allocated = new Dictionary<(uint, byte), long>();
        foreach (var (fileId, fork, _, count) in run.ForkExtents)
        {
            allocated[(fileId, fork)] = allocated.GetValueOrDefault((fileId, fork)) + (long)count * run.BlockSize;
        }

        return allocated;
    }
}
