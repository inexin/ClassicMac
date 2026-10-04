using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using ClassicMac.Core;
using ClassicMac.Files.Editing;
using static ClassicMac.Files.Hfs.HfsRecords;

namespace ClassicMac.Files.Hfs;

// First Aid's repair (hfs.md §5.6), in Disk First Aid's order: the B-trees written again from their records (the
// catalog with the repair list made, the extents tree without the records of files not in the catalog), then the
// bitmap and the MDB from what a verify of the result computes, then a verify; at most three passes, as Disk First Aid
// verifies again up to twice [Code: Disk First Aid 8.5.5, CODE 1 $1DF2A]. A volume Disk First Aid cannot repair is not
// written. Beyond Disk First Aid: first the alternate MDB from the primary, then with the trees the overflow records'
// start blocks and short physical lengths, and with the MDB its drNmFls and drFreeBks. Forks that share blocks (#12)
// are copied first, and their new extents written with the trees.
internal static class FirstAidRepairer
{
    private const int MaxPasses = 3, HeaderRecord = 14;

    public static FirstAidRepairResult Repair(HfsVolume volume, IProgress<VolumeProgress>? progress = null,
        System.Threading.CancellationToken cancellationToken = default)
    {
        var (run, before) = HfsFirstAid.Check(volume, progress, cancellationToken);
        if (before.Verdict != FirstAidVerdict.NeedsRepair)
        {
            return new FirstAidRepairResult(before, before, [], null);
        }

        var working = volume.Fork();
        var changes = new List<PlannedChange>();
        var after = before;
        for (var pass = 0; pass < MaxPasses && after.Verdict == FirstAidVerdict.NeedsRepair; pass++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            progress?.Report(new VolumeProgress(pass + 1, MaxPasses, "Repairing."));
            int made = changes.Count;
            if (!run.Plus && (run.Repairs & FirstAidRepairs.AlternateMdb) != 0)
            {
                working.Write((run.Sectors - 2) * FirstAidRun.SectorSize, run.Primary);
                changes.Add(new PlannedChange("repair", "", "alternate master directory block written from the primary"));
            }

            if (run.Plus && (run.Repairs & FirstAidRepairs.Journal) != 0 && PlusJournal.Replay(working, run.VolumeOffset) is { Damage: null } replay)
            {
                changes.Add(new PlannedChange("repair", "",
                    $"journal replayed ({replay.Transactions} transaction{(replay.Transactions == 1 ? "" : "s")}, {replay.Blocks} block{(replay.Blocks == 1 ? "" : "s")}) and emptied"));
            }

            if (run.Plus ? PlusRepair.Trees(run, working, changes) : Trees(run, working, changes))
            {
                (run, after) = HfsFirstAid.Check(working, progress, cancellationToken);
                if (run.Ended is not null)
                {
                    break;
                }
            }

            if (run.Plus)
            {
                PlusRepair.Bitmap(run, working, changes);
                PlusRepair.Header(run, working, changes);
            }
            else
            {
                Bitmap(run, working, changes);
                Mdb(run, working, changes);
            }

            (run, after) = HfsFirstAid.Check(working, progress, cancellationToken);
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
            | FirstAidRepairs.MissingFolder | FirstAidRepairs.MissingThreads | FirstAidRepairs.Valences | FirstAidRepairs.MountCheck
            | FirstAidRepairs.ForkLengths;
        bool written = false;
        var wanted = run.Repairs;
        var relocated = (wanted & FirstAidRepairs.OverlappingExtents) != 0
            ? OverlapRepair.Relocate(run, volume, changes)
            : [];
        if (run.Extents is { } extents
            && (run.TreesToRebuild.Contains(3) || relocated.Count > 0 || (wanted & (FirstAidRepairs.OrphanedExtents | FirstAidRepairs.ExtentStarts)) != 0))
        {
            var orphans = new List<PlannedChange>();
            var records = extents.Records.Where(r =>
            {
                uint fileId = KeyId(r.Key);
                if (relocated.ContainsKey((fileId, r.Key[1])))
                {
                    return false;                                                // a moved fork's: made again below
                }

                bool orphan = fileId >= 16 && !run.FileIds.Contains(fileId);
                if (orphan)
                {
                    orphans.Add(new PlannedChange("repair", "", $"extents, file {fileId}: an overflow extents record of a file not in the catalog deleted"));
                }

                return !orphan;
            }).Select(r => (r.Key.ToArray(), r.Data)).ToList();
            foreach (var ((fileId, fork), moved) in relocated)
            {
                records.AddRange(OverlapRepair.Records(fileId, fork, moved).Overflow);
            }

            records.Sort((a, b) => HfsBTreeWriting.CompareExtentsKeys(a.Item1, b.Item1));
            int renumbered = Renumber(records);
            if (renumbered > 0)
            {
                orphans.Add(new PlannedChange("repair", "", $"extents: {renumbered} overflow record{(renumbered == 1 ? "'s" : "s'")} start block renumbered"));
            }

            if (orphans.Count > 0 || relocated.Count > 0 || run.TreesToRebuild.Contains(3))
            {
                written |= Write(run, volume, extents, records, changes);
                changes.AddRange(orphans);
            }
        }

        if (run.Catalog is { } catalog && (run.TreesToRebuild.Contains(4) || relocated.Count > 0 || (wanted & catalogRepairs) != 0))
        {
            var catalogChanges = new List<PlannedChange>();
            var records = CatalogRepair.Repair(run, catalogChanges, relocated);
            if (catalogChanges.Count > 0 || relocated.Count > 0 || run.TreesToRebuild.Contains(4))
            {
                written |= Write(run, volume, catalog, records, changes);
                changes.AddRange(catalogChanges);
            }
        }

        return written;
    }

    // Beyond Disk First Aid: each fork's overflow records after its first numbered from the first's start block and the blocks of
    // those before (a record Disk First Aid's walk does not find first is left); returns how many changed.
    private static int Renumber(List<(byte[] Key, byte[] Data)> records)
    {
        int changed = 0;
        long next = -1;
        for (var i = 0; i < records.Count; i++)
        {
            var key = records[i].Key;
            bool sameFork = i > 0 && records[i - 1].Key.AsSpan(1, 5).SequenceEqual(key.AsSpan(1, 5));
            var reader = new BigEndianReader(key);
            if (sameFork && reader.ReadUInt16At(6) != next && next <= ushort.MaxValue)
            {
                new BigEndianWriter(key).WriteUInt16At(6, (ushort)next);
                changed++;
            }

            next = reader.ReadUInt16At(6) + ExtentRecords.Of(records[i].Data).Sum(e => (long)e.Count);
        }

        return changed;
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
