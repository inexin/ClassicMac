using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using ClassicMac.Core;
using ClassicMac.Files.Editing;
using static ClassicMac.Files.Hfs.HfsRecords;

namespace ClassicMac.Files.Hfs;

// First Aid's repair steps on HFS Plus (hfs-plus.md §5.4), as FirstAidRepairer's on HFS: the extents tree (without
// orphaned records, start blocks renumbered) and the catalog (with the repair list) written again by the HFS Plus
// B-tree writer through their extents, the allocation file from the blocks the extents use, and the volume header the
// check computed written with its alternate.
internal static class PlusRepair
{
    private const FirstAidRepairs CatalogRepairs = FirstAidRepairs.MissingFolder | FirstAidRepairs.MissingThreads | FirstAidRepairs.FileThreads
        | FirstAidRepairs.Valences | FirstAidRepairs.ForkLengths | FirstAidRepairs.LinkCounts | FirstAidRepairs.AttributeRecords;
    private const FirstAidRepairs ExtentsRepairs = FirstAidRepairs.OrphanedExtents | FirstAidRepairs.ExtentStarts;

    /// <summary>The trees a repair needs written again; true when either was.</summary>
    public static bool Trees(FirstAidRun run, HfsVolume volume, List<PlannedChange> changes)
    {
        bool written = false;
        // Forks that share blocks (#12) are copied first; their new extents go into both trees.
        var relocated = (run.Repairs & FirstAidRepairs.OverlappingExtents) != 0 ? OverlapRepair.Relocate(run, volume, changes) : [];
        if (run.Extents is { } extents && (run.TreesToRebuild.Contains(3) || relocated.Count > 0 || (run.Repairs & ExtentsRepairs) != 0))
        {
            var made = new List<PlannedChange>();
            var records = new List<(byte[] Key, byte[] Data)>();
            foreach (var (key, data, _) in extents.Records)
            {
                uint fileId = PlusKeyFileId(key);
                if (relocated.ContainsKey((fileId, key[2])))
                {
                    continue;                                                        // a moved fork's: made again below
                }

                if (fileId >= 16 && !run.FileIds.Contains(fileId))
                {
                    made.Add(new PlannedChange("repair", "", $"extents, file {fileId}: an overflow extents record of a file not in the catalog deleted"));
                    continue;
                }

                records.Add((key.ToArray(), data));
            }

            foreach (var ((fileId, fork), moved) in relocated)
            {
                records.AddRange(OverlapRepair.PlusRecords(fileId, fork, moved).Overflow);
            }

            records.Sort((a, b) => HfsPlusBTree.CompareExtentKeys(a.Key, b.Key));
            int renumbered = Renumber(records);
            if (renumbered > 0)
            {
                made.Add(new PlannedChange("repair", "", $"extents: {renumbered} overflow record{(renumbered == 1 ? "'s" : "s'")} start block renumbered"));
            }

            if (Write(run, volume, extents, HfsPlusBTreeWriter.Extents, records, changes))
            {
                changes.AddRange(made);
                written = true;
            }
        }

        if (run.Catalog is { } catalog && (run.TreesToRebuild.Contains(4) || relocated.Count > 0 || (run.Repairs & CatalogRepairs) != 0))
        {
            var made = new List<PlannedChange>();
            var records = PlusCatalogRepair.Repair(run, made, relocated);
            if ((made.Count > 0 || relocated.Count > 0 || run.TreesToRebuild.Contains(4)) && Write(run, volume, catalog, HfsPlusBTreeWriter.Catalog, records, changes))
            {
                changes.AddRange(made);
                written = true;
            }
        }

        if (run.AttributesTree is { } attributes
            && (run.TreesToRebuild.Contains(8) || (run.Repairs & FirstAidRepairs.AttributeRecords) != 0))
        {
            var bad = new HashSet<byte[]>(run.BadAttributes, HfsPlusBTree.ByteArrayEqualityComparer.Instance);
            var records = attributes.Records.Where(r => !bad.Contains(r.Key)).Select(r => (r.Key, r.Data)).ToList();
            if ((bad.Count > 0 || run.TreesToRebuild.Contains(8)) && Write(run, volume, attributes, HfsPlusBTreeWriter.Attributes, records, changes))
            {
                foreach (var key in bad)
                {
                    changes.Add(new PlannedChange("repair", "", $"attributes, CNID {PlusKeyFileId(key)}: a bad attribute record deleted"));
                }

                written = true;
            }
        }

        return written;
    }

    /// <summary>The allocation file from the blocks the extents use, when it differs.</summary>
    public static void Bitmap(FirstAidRun run, HfsVolume volume, List<PlannedChange> changes)
    {
        if (run.AllocationFileExtents is not { } allocation || run.ComputedBitmap.Length == 0)
        {
            return;
        }

        if (!run.ReadExtents(allocation, run.ComputedBitmap.Length).AsSpan().SequenceEqual(run.ComputedBitmap))
        {
            WriteThrough(run, volume, allocation, run.ComputedBitmap);
            changes.Add(new PlannedChange("repair", "", "allocation file written from the blocks the files use"));
        }
    }

    /// <summary>The computed volume header, written over the header and the alternate when either differs.</summary>
    public static void Header(FirstAidRun run, HfsVolume volume, List<PlannedChange> changes)
    {
        if (run.ComputedMdb.Length == 0)
        {
            return;
        }

        long primary = run.VolumeOffset + 1024, alternate = run.VolumeOffset + run.BlockCount * (long)run.BlockSize - 1024;
        foreach (var (at, name) in new[] { (primary, "volume header"), (alternate, "alternate volume header") })
        {
            var onDisk = new byte[512];
            volume.Read(at, onDisk);
            if (!onDisk.AsSpan().SequenceEqual(run.ComputedMdb))
            {
                volume.Write(at, run.ComputedMdb);
                changes.Add(new PlannedChange("repair", "", $"{name} written from the values First Aid computed"));
            }
        }
    }

    // A tree written again from its records, at its node size and node count, keeping its clump size, type and key
    // comparison; false when they do not fit.
    private static bool Write(FirstAidRun run, HfsVolume volume, FirstAidTree tree, HfsPlusBTreeWriter.TreeKind kind,
        List<(byte[] Key, byte[] Data)> records, List<PlannedChange> changes)
    {
        var header = new BigEndianReader(tree.File.Bytes);
        byte[] bytes;
        try
        {
            bytes = HfsPlusBTreeWriter.Build(kind, records, tree.NodeSize, tree.TotalNodes, header.ReadUInt32At(14 + 32),
                tree.File.Bytes[14 + 37], tree.File.Bytes[14 + 36]);
        }
        catch (InvalidDataException)
        {
            return false;
        }

        WriteThrough(run, volume, tree.Extents, bytes);
        changes.Add(new PlannedChange("repair", "", $"{(tree.IsCatalog ? "catalog" : tree.IsAttributes ? "attributes" : "extents")} B-tree written again ({records.Count} records)"));
        return true;
    }

    // Bytes written from the start of a file's extents.
    private static void WriteThrough(FirstAidRun run, HfsVolume volume, List<(uint Start, uint Count)> extents, byte[] bytes)
    {
        long at = 0;
        foreach (var (start, count) in extents)
        {
            int length = (int)Math.Min((long)count * run.BlockSize, bytes.Length - at);
            if (length <= 0)
            {
                break;
            }

            volume.Write((long)run.AllocationStart * FirstAidRun.SectorSize + (long)start * run.BlockSize, bytes.AsSpan((int)at, length));
            at += length;
        }
    }

    // Each fork's overflow records after its first numbered from the first's start block and the blocks before.
    private static int Renumber(List<(byte[] Key, byte[] Data)> records)
    {
        int changed = 0;
        long next = -1;
        for (var i = 0; i < records.Count; i++)
        {
            var key = records[i].Key;
            bool sameFork = i > 0 && records[i - 1].Key.AsSpan(2, 6).SequenceEqual(key.AsSpan(2, 6));
            var reader = new BigEndianReader(key);
            if (sameFork && reader.ReadUInt32At(8) != next && next <= uint.MaxValue)
            {
                new BigEndianWriter(key).WriteUInt32At(8, (uint)next);
                changed++;
            }

            next = reader.ReadUInt32At(8) + PlusExtentRecords.Of(records[i].Data).Sum(e => (long)e.Count);
        }

        return changed;
    }
}
