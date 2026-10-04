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
            ushort lastStart = U16(terminalReader, lastSlot * 4);
            ushort lastCount = U16(terminalReader, lastSlot * 4 + 2);
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

        uint oldNodeCount = (uint)(context.ExtentsTree.Length / NodeSize);
        uint addedNodes = context.AllocationBlockSize / NodeSize;
        uint newNodeCount = oldNodeCount + addedNodes;
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
        byte[] grown = new byte[checked(context.ExtentsTree.Length + (int)context.AllocationBlockSize)];
        context.ExtentsTree.CopyTo(grown, 0);
        uint newMapNodes = ExtendBTreeNodeMap(grown, oldNodeCount, newNodeCount);
        var header = new BigEndianWriter(grown);
        header.WriteUInt32At(14 + 22, newNodeCount);
        header.WriteUInt32At(14 + 26, checked(U32(new BigEndianReader(grown), 14 + 26) + addedNodes - newMapNodes));
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
                ushort start = U16(overflowData, lastSlot * 4);
                ushort count = U16(overflowData, lastSlot * 4 + 2);
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
            ushort start = U16(primaryReader, primarySlot * 4);
            ushort count = U16(primaryReader, primarySlot * 4 + 2);
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

    internal static void Verify(HfsVolume source, HfsVolume result, string targetPath, HfsFork changedFork, ReadOnlySpan<byte> expected,
        DateTime writeTime, uint newlyAllocatedBlocks, uint releasedBlocks)
    {
        uint macWriteTime = MacDate.FromDateTime(writeTime).Seconds;
        var sourceMdb = new byte[MdbSize];
        var resultMdb = new byte[MdbSize];
        source.Read(MdbOffset, sourceMdb);
        result.Read(MdbOffset, resultMdb);
        var sourceReader = new BigEndianReader(sourceMdb);
        var resultReader = new BigEndianReader(resultMdb);
        uint previousWriteCount = U32(sourceReader, 0x46);
        ushort previousFreeBlocks = U16(sourceReader, 0x22);
        ushort expectedFreeBlocks = checked((ushort)((uint)previousFreeBlocks - newlyAllocatedBlocks + releasedBlocks));
        if (U32(resultReader, 0x06) != macWriteTime ||
            U32(resultReader, 0x46) != unchecked(previousWriteCount + 1) ||
            U16(resultReader, 0x22) != expectedFreeBlocks)
        {
            throw new InvalidDataException("The rewritten HFS volume metadata did not record its modification and allocation changes.");
        }

        var beforeDiagnostics = new List<Diagnostic>();
        var afterDiagnostics = new List<Diagnostic>();
        var before = HfsReader.Instance.Read(source.AsForkData(), new ContainerContext(diagnostics: beforeDiagnostics));
        var after = HfsReader.Instance.Read(result.AsForkData(), new ContainerContext(diagnostics: afterDiagnostics));
        if (beforeDiagnostics.Any(d => d.Severity == DiagnosticSeverity.Error) || afterDiagnostics.Any(d => d.Severity == DiagnosticSeverity.Error))
        {
            throw new InvalidDataException("The original or rewritten HFS volume has structural errors; the edit was not verified.");
        }

        // The target only: its metadata, the changed fork and the other one. Every other file is checked by its records
        // and blocks (VerifyKept), without reading its forks.
        var oldFile = before.FirstOrDefault(f => StringComparer.Ordinal.Equals(f.MacPath, targetPath));
        var newFile = after.FirstOrDefault(f => StringComparer.Ordinal.Equals(f.MacPath, targetPath));
        if (oldFile is null || newFile is null || before.Count != after.Count)
        {
            throw new InvalidDataException("The rewritten HFS volume did not preserve its file list.");
        }

        if (!SameMetadata(oldFile, newFile, includeModified: false) || newFile.Modified != new MacDate(macWriteTime))
        {
            throw new InvalidDataException($"The rewritten HFS volume changed metadata incorrectly for '{targetPath}'.");
        }

        var changed = changedFork == HfsFork.Data ? newFile.DataFork : newFile.ResourceFork;
        var unchanged = changedFork == HfsFork.Data ? newFile.ResourceFork : newFile.DataFork;
        var oldUnchanged = changedFork == HfsFork.Data ? oldFile.ResourceFork : oldFile.DataFork;
        if (!changed.ToArray().AsSpan().SequenceEqual(expected) ||
            !unchanged.ToArray().AsSpan().SequenceEqual(oldUnchanged.ToArray()))
        {
            throw new InvalidDataException($"The rewritten HFS volume did not preserve both forks for '{targetPath}'.");
        }
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
