using System;
using System.Collections.Generic;
using System.Linq;
using ClassicMac.Core;
using ClassicMac.Files.Editing;

namespace ClassicMac.Files.Hfs;

// #12's repair (hfs.md §5.6): a fork that shares blocks with one found before it (the B-tree files first, then the
// catalog's files in key order) is copied whole into free blocks, in one piece where a run of free blocks is long
// enough, so it reads as before and no longer shares. Disk First Aid likewise gives the files their own copies; the
// choice of which fork moves, and where, is ClassicMac's. The new extents go into the trees as they are written again.
internal static class OverlapRepair
{
    /// <summary>The forks moved, by (file ID, fork), with their new extents; their blocks are copied in <paramref name="volume"/>.</summary>
    public static Dictionary<(uint FileId, byte Fork), List<(uint Start, uint Count)>> Relocate(FirstAidRun run, HfsVolume volume,
        List<PlannedChange> changes)
    {
        var claimed = new bool[run.BlockCount];
        var used = new bool[run.BlockCount];
        var sharing = new List<((uint FileId, byte Fork) Fork, List<(uint Start, uint Count)> Extents)>();
        foreach (var fork in run.ForkExtents.GroupBy(e => (e.FileId, e.Fork)))
        {
            var extents = fork.Select(e => (e.Start, e.Count)).ToList();
            var blocks = Blocks(extents, run.BlockCount).ToList();
            foreach (long block in blocks)
            {
                used[block] = true;
            }

            if (fork.Key.FileId >= 16 && blocks.Exists(b => claimed[b]))
            {
                sharing.Add((fork.Key, extents));
                continue;
            }

            foreach (long block in blocks)
            {
                claimed[block] = true;
            }
        }

        var moved = new Dictionary<(uint, byte), List<(uint, uint)>>();
        foreach (var (key, extents) in sharing)
        {
            long total = extents.Sum(e => (long)e.Count);
            if (Allocate(used, total) is not { } target)
            {
                continue;                                                        // no room: the problem stays
            }

            var bytes = run.ReadExtents(extents, total * run.BlockSize);
            long at = 0;
            foreach (var (start, count) in target)
            {
                long length = (long)count * run.BlockSize;
                volume.Write((long)run.AllocationStart * FirstAidRun.SectorSize + (long)start * run.BlockSize, bytes.AsSpan(checked((int)at), checked((int)length)));
                at += length;
            }

            moved[key] = target;
            changes.Add(new PlannedChange("repair", "",
                $"file {key.FileId}: its {(key.Fork == 0 ? "data" : "resource")} fork, which shared blocks with another, given its own copy ({total} blocks)"));
        }

        return moved;
    }

    /// <summary>A fork's extents as an extent record of three (zeros after the last) and the overflow records for the rest.</summary>
    public static (byte[] First, List<(byte[] Key, byte[] Data)> Overflow) Records(uint fileId, byte fork, List<(uint Start, uint Count)> extents)
    {
        var first = Record(extents.Take(3));
        var overflow = new List<(byte[], byte[])>();
        long blocks = extents.Take(3).Sum(e => (long)e.Count);
        for (var i = 3; i < extents.Count; i += 3)
        {
            var key = new byte[8];
            key[0] = 7;
            key[1] = fork;
            var writer = new BigEndianWriter(key);
            writer.WriteUInt32At(2, fileId);
            writer.WriteUInt16At(6, blocks);
            overflow.Add((key, Record(extents.Skip(i).Take(3))));
            blocks += extents.Skip(i).Take(3).Sum(e => (long)e.Count);
        }

        return (first, overflow);
    }

    private static byte[] Record(IEnumerable<(uint Start, uint Count)> extents)
    {
        var record = new byte[12];
        var writer = new BigEndianWriter(record);
        var at = 0;
        foreach (var (start, count) in extents)
        {
            writer.WriteUInt16At(at, start);
            writer.WriteUInt16At(at + 2, count);
            at += 4;
        }

        return record;
    }

    private static IEnumerable<long> Blocks(List<(uint Start, uint Count)> extents, uint blockCount)
    {
        foreach (var (start, count) in extents)
        {
            for (long block = start; block < (long)start + count && block < blockCount; block++)
            {
                yield return block;
            }
        }
    }

    // Free blocks for a fork of total blocks: one run long enough if there is one, else the free runs in order; the
    // blocks taken are marked used. Null when the volume has too few.
    private static List<(uint Start, uint Count)>? Allocate(bool[] used, long total)
    {
        var runs = new List<(uint Start, uint Count)>();
        for (long block = 0; block < used.Length;)
        {
            if (used[block])
            {
                block++;
                continue;
            }

            long start = block;
            while (block < used.Length && !used[block] && block - start < ushort.MaxValue)
            {
                block++;
            }

            runs.Add(((uint)start, (uint)(block - start)));
        }

        if (runs.Sum(r => (long)r.Count) < total)
        {
            return null;
        }

        var taken = runs.FirstOrDefault(r => r.Count >= total) is { Count: > 0 } whole
            ? [(whole.Start, (uint)total)]
            : TakeInOrder(runs, total);
        foreach (var (start, count) in taken)
        {
            Array.Fill(used, true, (int)start, (int)count);
        }

        return taken;
    }

    private static List<(uint Start, uint Count)> TakeInOrder(List<(uint Start, uint Count)> runs, long total)
    {
        var taken = new List<(uint, uint)>();
        foreach (var (start, count) in runs)
        {
            if (total == 0)
            {
                break;
            }

            uint part = (uint)Math.Min(count, total);
            taken.Add((start, part));
            total -= part;
        }

        return taken;
    }
}
