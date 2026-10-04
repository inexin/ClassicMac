using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using ClassicMac.Core;
using static ClassicMac.Files.Hfs.HfsWriter;
using static ClassicMac.Files.Hfs.HfsCatalogEditing;
using static ClassicMac.Files.Hfs.HfsCatalogKeys;
using static ClassicMac.Files.Hfs.HfsBTreeWriting;
using static ClassicMac.Files.Hfs.HfsAllocation;
using static ClassicMac.Files.Hfs.HfsRecords;

namespace ClassicMac.Files.Hfs;

// HfsWriter's fork replacement: growing and shrinking a fork's extents, and checking the result keeps everything else.
internal static class HfsForkWriting
{
    internal sealed class ForkResizeContext(byte[] extentsTree, TreeRecord catalogRecord, int extentOffset,
        TreeRecord[] overflowRecords, List<(ushort Start, ushort Count)> extents, byte[] bitmap,
        uint blockCount, byte forkType, uint fileId, List<(ushort Start, ushort Count)> extentsTreeExtents,
        uint allocationBlockSize)
    {
        public byte[] ExtentsTree { get; set; } = extentsTree;
        public TreeRecord CatalogRecord { get; } = catalogRecord;
        public int ExtentOffset { get; } = extentOffset;
        public TreeRecord[] OverflowRecords { get; } = overflowRecords;
        public List<(ushort Start, ushort Count)> Extents { get; } = extents;
        public byte[] Bitmap { get; } = bitmap;
        public uint BlockCount { get; } = blockCount;
        public byte ForkType { get; } = forkType;
        public uint FileId { get; } = fileId;
        public List<(ushort Start, ushort Count)> ExtentsTreeExtents { get; } = extentsTreeExtents;
        public uint AllocationBlockSize { get; } = allocationBlockSize;
        public uint AllocatedTreeBlocks { get; set; }
    }

    internal readonly record struct ForkResizeResult(bool TreeChanged, uint AllocatedBlocks, uint ReleasedBlocks);

    internal static ForkResizeResult ResizeFork(ForkResizeContext context, ulong requiredBlocks)
    {
        ulong allocatedBlocks = context.Extents.Aggregate<(ushort Start, ushort Count), ulong>(0,
            (total, extent) => total + extent.Count);
        if (requiredBlocks > allocatedBlocks)
        {
            return GrowFork(context, allocatedBlocks, requiredBlocks);
        }

        if (requiredBlocks < allocatedBlocks)
        {
            return ShrinkFork(context, allocatedBlocks - requiredBlocks);
        }

        return default;
    }

    internal static ForkResizeResult GrowFork(ForkResizeContext context, ulong allocatedBlocks, ulong requiredBlocks)
    {
        var terminalOverflow = context.OverflowRecords.LastOrDefault();
        byte[] terminal = terminalOverflow?.Data ?? context.CatalogRecord.Data.AsSpan(context.ExtentOffset, 12).ToArray();
        var terminalReader = new BigEndianReader(terminal);
        int lastSlot = LastExtentSlot(terminalReader);
        ulong remaining = requiredBlocks - allocatedBlocks;
        uint adjacentGrowth = 0;
        if (lastSlot >= 0)
        {
            ushort lastStart = terminalReader.ReadUInt16At(lastSlot * 4);
            ushort lastCount = terminalReader.ReadUInt16At(lastSlot * 4 + 2);
            uint adjacent = (uint)lastStart + lastCount;
            while (adjacentGrowth < remaining && adjacentGrowth < ushort.MaxValue - lastCount &&
                adjacent + adjacentGrowth < context.BlockCount &&
                !IsAllocated(context.Bitmap, (ushort)(adjacent + adjacentGrowth)))
            {
                adjacentGrowth++;
            }

            if (adjacentGrowth > 0)
            {
                WriteExtent(terminal, lastSlot, lastStart, checked((ushort)(lastCount + adjacentGrowth)));
                context.Extents[^1] = (lastStart, checked((ushort)(lastCount + adjacentGrowth)));
                for (uint block = adjacent; block < adjacent + adjacentGrowth; block++)
                {
                    SetBitmap(context.Bitmap, (ushort)block, allocated: true);
                }

                remaining -= adjacentGrowth;
            }
        }

        var additions = remaining == 0 ? [] : AllocateRuns(context.Bitmap,
            checked((ushort)context.BlockCount), remaining, checked((int)context.BlockCount));
        int slot = LastExtentSlot(terminalReader) + 1;
        ulong nextForkBlock = allocatedBlocks + adjacentGrowth;
        uint allocatedRuns = 0;
        bool changedTree = false;
        byte[]? pendingOverflowExtents = null;
        byte pendingOverflowSlots = 0;
        ulong pendingOverflowBlockStart = 0;
        void FlushOverflowExtents()
        {
            if (pendingOverflowExtents is null)
            {
                return;
            }

            if (pendingOverflowBlockStart > ushort.MaxValue)
            {
                throw new InvalidDataException("The fork's extent start exceeds HFS's 16-bit FABN field.");
            }

            var keyWriter = new BigEndianWriter(8);
            keyWriter.WriteByte(7);
            keyWriter.WriteByte(context.ForkType);
            keyWriter.WriteUInt32(context.FileId);
            keyWriter.WriteUInt16(pendingOverflowBlockStart);
            var key = keyWriter.ToArray();
            while (true)
            {
                try
                {
                    InsertExtentsRecord(context.ExtentsTree, key, pendingOverflowExtents);
                    break;
                }
                catch (BTreeNeedsNodesException)
                {
                    GrowExtentsTree(context);
                }
            }
            changedTree = true;
            pendingOverflowExtents = null;
            pendingOverflowSlots = 0;
        }
        foreach (var (start, count) in additions)
        {
            if (slot < 3)
            {
                WriteExtent(terminal, slot++, start, count);
            }
            else
            {
                if (pendingOverflowExtents is null)
                {
                    pendingOverflowExtents = new byte[12];
                    pendingOverflowBlockStart = nextForkBlock;
                }
                WriteExtent(pendingOverflowExtents, pendingOverflowSlots++, start, count);
                if (pendingOverflowSlots == 3)
                {
                    FlushOverflowExtents();
                }
            }
            for (uint block = start; block < (uint)start + count; block++)
            {
                SetBitmap(context.Bitmap, checked((ushort)block), allocated: true);
            }

            context.Extents.Add((start, count));
            allocatedRuns += count;
            nextForkBlock += count;
        }
        FlushOverflowExtents();
        if (terminalOverflow is null)
        {
            terminal.CopyTo(context.CatalogRecord.Data, context.ExtentOffset);
        }
        else
        {
            terminal.CopyTo(terminalOverflow.Data, 0);
            UpdateTreeRecord(context.ExtentsTree, terminalOverflow.Key, terminalOverflow.Data);
            changedTree = true;
        }
        return new ForkResizeResult(changedTree, checked(adjacentGrowth + allocatedRuns + context.AllocatedTreeBlocks), 0);
    }

    internal static void GrowExtentsTree(ForkResizeContext context)
    {
        if (context.ExtentsTree.Length > int.MaxValue - context.AllocationBlockSize ||
            context.ExtentsTree.Length / NodeSize > uint.MaxValue - context.AllocationBlockSize / NodeSize)
        {
            throw new InvalidDataException("The extents-overflow B-tree cannot grow beyond its addressable node count.");
        }

        var runs = AllocateRuns(context.Bitmap, checked((ushort)context.BlockCount), 1, 1);
        ushort newBlock = runs[0].Start;
        var extents = context.ExtentsTreeExtents;
        var last = extents[^1];
        if ((uint)last.Start + last.Count == newBlock && last.Count < ushort.MaxValue)
        {
            extents[^1] = (last.Start, checked((ushort)(last.Count + 1)));
        }
        else
        {
            if (extents.Count == 3)
            {
                throw new InvalidDataException("The extents-overflow file has no free primary extent descriptor for tree growth.");
            }

            extents.Add((newBlock, 1));
        }
        SetBitmap(context.Bitmap, newBlock, allocated: true);
        var grown = GrowBTree(context.ExtentsTree, checked(context.ExtentsTree.Length + (long)context.AllocationBlockSize));
        context.ExtentsTree = grown;
        context.AllocatedTreeBlocks++;
        ValidateExtentsTree(grown);
    }

    internal static ForkResizeResult ShrinkFork(ForkResizeContext context, ulong excess)
    {
        uint releasedBlocks = 0;
        bool changedTree = false;
        for (int recordIndex = context.OverflowRecords.Length - 1; recordIndex >= 0 && excess > 0; recordIndex--)
        {
            var overflowRecord = context.OverflowRecords[recordIndex];
            var overflowData = new BigEndianReader(overflowRecord.Data);
            int lastSlot = LastExtentSlot(overflowData);
            while (lastSlot >= 0 && excess > 0)
            {
                ushort start = overflowData.ReadUInt16At(lastSlot * 4);
                ushort count = overflowData.ReadUInt16At(lastSlot * 4 + 2);
                uint giveBack = (uint)Math.Min(excess, (ulong)count);
                for (uint block = (uint)start + count - giveBack; block < (uint)start + count; block++)
                {
                    SetBitmap(context.Bitmap, checked((ushort)block), allocated: false);
                }

                excess -= giveBack;
                releasedBlocks += giveBack;
                if (giveBack == count)
                {
                    WriteExtent(overflowRecord.Data, lastSlot, 0, 0);
                    context.Extents.RemoveAt(context.Extents.Count - 1);
                    lastSlot--;
                }
                else
                {
                    WriteExtent(overflowRecord.Data, lastSlot, start, checked((ushort)(count - giveBack)));
                    context.Extents[^1] = (start, checked((ushort)(count - giveBack)));
                }
            }
            if (lastSlot < 0)
            {
                DeleteExtentsRecord(context.ExtentsTree, overflowRecord.Key);
            }
            else
            {
                UpdateTreeRecord(context.ExtentsTree, overflowRecord.Key, overflowRecord.Data);
            }

            changedTree = true;
        }

        byte[] primary = context.CatalogRecord.Data.AsSpan(context.ExtentOffset, 12).ToArray();
        var primaryReader = new BigEndianReader(primary);
        int primarySlot = LastExtentSlot(primaryReader);
        while (primarySlot >= 0 && excess > 0)
        {
            ushort start = primaryReader.ReadUInt16At(primarySlot * 4);
            ushort count = primaryReader.ReadUInt16At(primarySlot * 4 + 2);
            uint giveBack = (uint)Math.Min(excess, (ulong)count);
            for (uint block = (uint)start + count - giveBack; block < (uint)start + count; block++)
            {
                SetBitmap(context.Bitmap, checked((ushort)block), allocated: false);
            }

            excess -= giveBack;
            releasedBlocks += giveBack;
            if (giveBack == count)
            {
                WriteExtent(primary, primarySlot, 0, 0);
                context.Extents.RemoveAt(context.Extents.Count - 1);
                primarySlot--;
            }
            else
            {
                WriteExtent(primary, primarySlot, start, checked((ushort)(count - giveBack)));
                context.Extents[^1] = (start, checked((ushort)(count - giveBack)));
            }
        }
        if (excess != 0)
        {
            throw new InvalidDataException("The HFS fork extent records do not cover the blocks being reclaimed.");
        }

        primary.CopyTo(context.CatalogRecord.Data, context.ExtentOffset);
        return new ForkResizeResult(changedTree, 0, releasedBlocks);
    }

    // The edited file: its catalog record the source's but for the changed fork's lengths and extents and the
    // modification date, the changed fork the bytes given, the other fork as it was. The MDB records the write and the
    // blocks allocated or freed. Every other file was checked by VerifyKept.
    internal static void Verify(HfsVolume source, HfsVolume result, CatalogEditState after, (byte[] Key, byte[] Data) before,
        List<(byte[] Key, byte[] Data)> overflowBefore, (uint FirstBlock, uint BlockSize, uint BlockCount) sourceGeometry, string targetPath,
        HfsFork changedFork, ReadOnlySpan<byte> expected, DateTime writeTime, uint newlyAllocatedBlocks, uint releasedBlocks)
    {
        uint macWriteTime = MacDate.FromDateTime(writeTime).Seconds;
        var sourceMdb = new byte[MdbSize];
        var resultMdb = new byte[MdbSize];
        source.Read(MdbOffset, sourceMdb);
        result.Read(MdbOffset, resultMdb);
        var sourceReader = new BigEndianReader(sourceMdb);
        var resultReader = new BigEndianReader(resultMdb);
        uint previousWriteCount = sourceReader.ReadUInt32At(0x46);
        ushort previousFreeBlocks = sourceReader.ReadUInt16At(0x22);
        ushort expectedFreeBlocks = checked((ushort)((uint)previousFreeBlocks - newlyAllocatedBlocks + releasedBlocks));
        if (resultReader.ReadUInt32At(0x06) != macWriteTime ||
            resultReader.ReadUInt32At(0x46) != unchecked(previousWriteCount + 1) ||
            resultReader.ReadUInt16At(0x22) != expectedFreeBlocks)
        {
            throw new InvalidDataException("The rewritten HFS volume metadata did not record its modification and allocation changes.");
        }

        var record = after.Records.Find(r => r.Key.AsSpan().SequenceEqual(before.Key));
        if (record.Data is null || record.Data.Length != before.Data.Length || before.Data.Length < 102)
        {
            throw new InvalidDataException($"The rewritten HFS volume did not keep the catalog record of '{targetPath}'.");
        }

        // The fields the edit may change: the changed fork's logical and physical lengths and extents, and the
        // modification date (+$30); every other byte is the source's.
        var (lengths, extentRecord) = changedFork == HfsFork.Data ? (0x1A, 0x4A) : (0x24, 0x56);
        bool May(int at) => at is >= 0x30 and < 0x34 || at >= lengths && at < lengths + 8 || at >= extentRecord && at < extentRecord + 12;
        var now = new BigEndianReader(record.Data);
        for (var at = 0; at < before.Data.Length; at++)
        {
            if (record.Data[at] != before.Data[at] && !May(at))
            {
                throw new InvalidDataException($"The rewritten HFS volume changed metadata incorrectly for '{targetPath}'.");
            }
        }

        if (now.ReadUInt32At(0x30) != macWriteTime || now.ReadUInt32At(lengths) != expected.Length)
        {
            throw new InvalidDataException($"The rewritten HFS volume changed metadata incorrectly for '{targetPath}'.");
        }

        var afterOverflow = LeafRecords(after.ExtentsTree).Select(r => (r.Key, r.Data)).ToList();
        var unchangedFork = changedFork == HfsFork.Data ? HfsFork.Resource : HfsFork.Data;
        var changed = RecordFork(result, (after.FirstBlock, after.BlockSize, after.BlockCount), record.Data, changedFork, afterOverflow);
        var kept = RecordFork(result, (after.FirstBlock, after.BlockSize, after.BlockCount), record.Data, unchangedFork, afterOverflow);
        var keptBefore = RecordFork(source, sourceGeometry, before.Data, unchangedFork, overflowBefore);
        if (!changed.AsSpan().SequenceEqual(expected) || !kept.AsSpan().SequenceEqual(keptBefore))
        {
            throw new InvalidDataException($"The rewritten HFS volume did not preserve both forks for '{targetPath}'.");
        }
    }

    // A file's fork from its catalog record: the record's three extents, then its overflow records in order.
    private static byte[] RecordFork(HfsVolume image, (uint FirstBlock, uint BlockSize, uint BlockCount) geometry, byte[] record, HfsFork fork,
        List<(byte[] Key, byte[] Data)> overflowRecords)
    {
        var (length, extentRecord, forkType) = fork == HfsFork.Data ? (0x1A, 0x4A, (byte)0x00) : (0x24, 0x56, (byte)0xFF);
        var reader = new BigEndianReader(record);
        uint fileId = reader.ReadUInt32At(0x14);
        var overflow = new Dictionary<(byte Fork, uint File), List<(ushort Start, byte[] Extents)>>
        {
            [(forkType, fileId)] = [.. overflowRecords
                .Where(r => r.Key.Length >= 8 && r.Data.Length >= 12 && r.Key[1] == forkType && KeyId(r.Key) == fileId)
                .Select(r => (ExtentsStart(r.Key), r.Data.AsSpan(0, 12).ToArray()))],
        };
        return ReadFork(image, geometry.FirstBlock, geometry.BlockSize, geometry.BlockCount,
            ParseExtents(new BigEndianReader(record.AsMemory(extentRecord, 12))), reader.ReadUInt32At(length), overflow, forkType, fileId, includeOverflow: true);
    }

    internal static bool SameMetadata(MacFile left, MacFile right, bool includeModified) =>
        left.Name == right.Name && left.FolderPath.SequenceEqual(right.FolderPath) &&
        left.FinderInfo.Type == right.FinderInfo.Type && left.FinderInfo.Creator == right.FinderInfo.Creator &&
        left.FinderInfo.Flags == right.FinderInfo.Flags && left.FinderInfo.Location == right.FinderInfo.Location &&
        left.FinderInfo.Folder == right.FinderInfo.Folder && left.FinderInfo.Extended.Span.SequenceEqual(right.FinderInfo.Extended.Span) &&
        left.Created == right.Created && (!includeModified || left.Modified == right.Modified);

    internal static string PathOf(uint parent, string name, Dictionary<uint, (uint Parent, string Name)> folders)
    {
        var parts = new List<string> { name };
        var seen = new HashSet<uint>();
        while (parent is not (1 or 2))
        {
            if (!seen.Add(parent) || !folders.TryGetValue(parent, out var folder))
            {
                return "";
            }

            parts.Insert(0, folder.Name);
            parent = folder.Parent;
        }
        return string.Join(':', parts);
    }

    internal static bool HfsPathEquals(string left, string right)
    {
        string[] leftParts = left.Split(':');
        string[] rightParts = right.Split(':');
        if (leftParts.Length != rightParts.Length)
        {
            return false;
        }

        for (int index = 0; index < leftParts.Length; index++)
        {
            if (CompareCatalogKeys(CatalogKey(0, leftParts[index]), CatalogKey(0, rightParts[index])) != 0)
            {
                return false;
            }
        }
        return true;
    }

    // A key's name as Mac OS Roman text, which paths given by callers match (control characters included).
    internal static string DecodeName(byte[] key) => key.Length >= 7 ? new MacString(key.AsSpan(7, Math.Min(key[6], key.Length - 7))).ToMacRoman() : "";

    // A key's name as MacFile.MacPath shows it (control characters escaped).
    internal static string ShownName(byte[] key) => key.Length >= 7 ? new MacString(key.AsSpan(7, Math.Min(key[6], key.Length - 7))).ToString() : "";
}
