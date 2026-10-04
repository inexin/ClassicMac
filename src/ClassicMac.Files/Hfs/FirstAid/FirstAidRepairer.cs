using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using ClassicMac.Core;
using ClassicMac.Files.Editing;

namespace ClassicMac.Files.Hfs;

// First Aid's repair (hfs.md §5.6), in Disk First Aid's order: the B-trees written again from their records (the
// catalog with the repair list made, the extents tree without the records of files not in the catalog), then the
// bitmap and the MDB from what a verify of the result computes, then a verify; at most three passes, as Disk First Aid
// verifies again up to twice [Code: Disk First Aid 8.5.5, CODE 1 $1DF2A]. A volume Disk First Aid cannot repair is not
// written.
internal static class FirstAidRepairer
{
    private const int MaxPasses = 3, HeaderRecord = 14;

    public static FirstAidRepairResult Repair(HfsVolume volume)
    {
        var (run, before) = HfsFirstAid.Check(volume);
        if (before.Verdict != FirstAidVerdict.NeedsRepair)
        {
            return new FirstAidRepairResult(before, before, [], null);
        }

        var working = volume.Fork();
        var changes = new List<PlannedChange>();
        var after = before;
        for (var pass = 0; pass < MaxPasses && after.Verdict == FirstAidVerdict.NeedsRepair; pass++)
        {
            int made = changes.Count;
            if (Trees(run, working, changes))
            {
                (run, after) = HfsFirstAid.Check(working);
                if (run.Ended is not null)
                {
                    break;
                }
            }

            Bitmap(run, working, changes);
            Mdb(run, working, changes);
            (run, after) = HfsFirstAid.Check(working);
            if (changes.Count == made)
            {
                break;
            }
        }

        bool written = changes.Count > 0 && after.Verdict is FirstAidVerdict.AppearsOk or FirstAidVerdict.NeedsRepair;
        return new FirstAidRepairResult(before, after, changes, written ? working : null);
    }

    // The catalog and the extents tree, each written again when a repair needs it; true when either was.
    private static bool Trees(FirstAidRun run, HfsVolume volume, List<PlannedChange> changes)
    {
        const FirstAidRepairs catalogRepairs = FirstAidRepairs.ReservedFields | FirstAidRepairs.FinderFlags | FirstAidRepairs.FileThreads
            | FirstAidRepairs.MissingFolder | FirstAidRepairs.MissingThreads | FirstAidRepairs.Valences | FirstAidRepairs.MountCheck;
        bool written = false;
        if (run.Extents is { } extents && (run.TreesToRebuild.Contains(3) || (run.Repairs & FirstAidRepairs.OrphanedExtents) != 0))
        {
            var orphans = new List<PlannedChange>();
            var records = extents.Records.Where(r =>
            {
                uint fileId = new BigEndianReader(r.Key).ReadUInt32At(2);
                bool orphan = fileId >= 16 && !run.FileIds.Contains(fileId);
                if (orphan)
                {
                    orphans.Add(new PlannedChange("repair", "", $"extents, file {fileId}: an overflow extents record of a file not in the catalog deleted"));
                }

                return !orphan;
            }).Select(r => (r.Key, r.Data)).ToList();
            if (orphans.Count > 0 || run.TreesToRebuild.Contains(3))
            {
                written |= Write(run, volume, extents, records, changes);
                changes.AddRange(orphans);
            }
        }

        if (run.Catalog is { } catalog && (run.TreesToRebuild.Contains(4) || (run.Repairs & catalogRepairs) != 0))
        {
            var catalogChanges = new List<PlannedChange>();
            var records = CatalogRepair.Repair(catalog, catalogChanges);
            if (catalogChanges.Count > 0 || run.TreesToRebuild.Contains(4))
            {
                written |= Write(run, volume, catalog, records, changes);
                changes.AddRange(catalogChanges);
            }
        }

        return written;
    }

    // The tree written again from its records: header (node count from its PEOF, Disk First Aid's node size and maximum
    // key length, the reserved byte cleared), nodes and map; back through its extents.
    private static bool Write(FirstAidRun run, HfsVolume volume, FirstAidTree tree, List<(byte[] Key, byte[] Data)> records, List<PlannedChange> changes)
    {
        if (tree.NodeSize != HfsWriter.NodeSize)
        {
            return false;
        }

        var bytes = tree.File.Bytes.ToArray();
        var writer = new BigEndianWriter(bytes);
        writer.WriteUInt16At(HeaderRecord + 18, tree.NodeSize);
        writer.WriteUInt16At(HeaderRecord + 20, tree.MaxKeyLength);
        writer.WriteUInt32At(HeaderRecord + 22, tree.TotalNodes);
        bytes[0x32] = 0;
        if (!tree.File.TryReadMap(out _, out _) && tree.TotalNodes <= 8 * 256)
        {
            // The header node as Mac OS lays it out: header, user and map records, no map nodes.
            writer.WriteUInt32At(0, 0u);
            bytes[8] = 1;
            bytes[9] = 0;
            writer.WriteUInt16At(10, 3);
            writer.WriteUInt16At(tree.NodeSize - 2, HeaderRecord);
            writer.WriteUInt16At(tree.NodeSize - 4, 120);
            writer.WriteUInt16At(tree.NodeSize - 6, 248);
            writer.WriteUInt16At(tree.NodeSize - 8, tree.NodeSize - 8);
        }

        try
        {
            HfsBTreeWriting.RebuildBTree(bytes, records, validateExtents: false);
        }
        catch (Exception e) when (e is InvalidDataException or HfsBTreeWriting.BTreeNeedsNodesException)
        {
            return false;
        }

        long at = 0;
        foreach (var (start, count) in tree.Extents)
        {
            int length = (int)Math.Min((long)count * run.BlockSize, bytes.Length - at);
            if (length <= 0)
            {
                break;
            }

            volume.Write((long)run.AllocationStart * FirstAidRun.SectorSize + (long)start * run.BlockSize, bytes.AsSpan((int)at, length));
            at += length;
        }

        changes.Add(new PlannedChange("repair", "", $"{(tree.IsCatalog ? "catalog" : "extents")} B-tree written again ({records.Count} records)"));
        return true;
    }

    // The bitmap the verify built from the extents, when the volume's differs.
    private static void Bitmap(FirstAidRun run, HfsVolume volume, List<PlannedChange> changes)
    {
        if (run.ComputedBitmap.Length == 0)
        {
            return;
        }

        var onDisk = new byte[run.ComputedBitmap.Length];
        volume.Read((long)run.BitmapStart * FirstAidRun.SectorSize, onDisk);
        if (!onDisk.AsSpan().SequenceEqual(run.ComputedBitmap))
        {
            volume.Write((long)run.BitmapStart * FirstAidRun.SectorSize, run.ComputedBitmap);
            changes.Add(new PlannedChange("repair", "", "volume bitmap written from the blocks the files use"));
        }
    }

    // The MDB the verify computed, when the primary differs.
    private static void Mdb(FirstAidRun run, HfsVolume volume, List<PlannedChange> changes)
    {
        if (run.ComputedMdb.Length == 0 || run.ComputedMdb.AsSpan().SequenceEqual(run.Primary))
        {
            return;
        }

        volume.Write(2 * FirstAidRun.SectorSize, run.ComputedMdb);
        changes.Add(new PlannedChange("repair", "", "master directory block written from the values First Aid computed"));
    }
}
