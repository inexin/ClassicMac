using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using ClassicMac.Core;

namespace ClassicMac.Files.Hfs;

/// <summary>Conservative copy-on-write edits to plain HFS volumes.</summary>
public static partial class HfsWriter
{
    private const int MdbOffset = 1024, MdbSize = 162, BlockSize = 512, NodeSize = 512;

    // The clock the writer's dates come from (the local time of the call), which a test may set for its own flow.
    private static readonly System.Threading.AsyncLocal<TimeProvider?> Clock = new();

    private static DateTime Now => (Clock.Value ?? TimeProvider.System).GetLocalNow().DateTime;

    /// <summary>Takes the writer's dates from <paramref name="clock"/> on this logical flow until disposed (reproducible edits).</summary>
    internal static IDisposable UseClock(TimeProvider clock)
    {
        Clock.Value = clock;
        return new ClockScope();
    }

    private sealed class ClockScope : IDisposable
    {
        public void Dispose() => Clock.Value = null;
    }

    /// <summary>
    /// Replaces one fork on a plain HFS volume and returns a new image. The source image is never modified.
    /// </summary>
    /// <exception cref="InvalidDataException">The image, target, or requested edit is not supported or valid.</exception>
    public static byte[] ReplaceFork(ForkData image, string macPath, HfsFork fork, ReadOnlyMemory<byte> data) =>
        ReplaceFork(new HfsVolume(image), macPath, fork, data).ToArray();

    internal static HfsVolume ReplaceFork(HfsVolume image, string macPath, HfsFork fork, ReadOnlyMemory<byte> data)
    {
        ArgumentNullException.ThrowIfNull(image);
        ArgumentException.ThrowIfNullOrEmpty(macPath);
        if (fork is not (HfsFork.Data or HfsFork.Resource))
        {
            throw new ArgumentOutOfRangeException(nameof(fork));
        }

        if (macPath.StartsWith(':') || macPath.EndsWith(':') || macPath.Contains("::", StringComparison.Ordinal))
        {
            throw new ArgumentException("Use a colon-separated HFS path without empty components.", nameof(macPath));
        }

        var source = image;
        if (source.Length < MdbOffset + MdbSize || ReadUInt16(source, MdbOffset) != 0x4244)
        {
            throw new InvalidDataException("The input is not a plain HFS volume.");
        }

        var mdbSector = new byte[BlockSize];
        source.Read(MdbOffset, mdbSector.AsSpan(0, (int)Math.Min(BlockSize, source.Length - MdbOffset)));
        var mdb = new BigEndianReader(mdbSector.AsMemory(0, MdbSize));
        if (U16(mdb, 0x7C) == 0x482B)
        {
            throw new InvalidDataException("Writing an HFS wrapper around HFS Plus is not supported.");
        }

        if ((U16(mdb, 0x0A) & 0x8000) != 0)
        {
            throw new InvalidDataException("The HFS volume is software-locked.");
        }

        uint blockSize = U32(mdb, 0x14);
        uint blockCount = U16(mdb, 0x12);
        uint firstBlock = (uint)U16(mdb, 0x1C) * 512;
        if (blockSize < 512 || blockSize % 512 != 0 || firstBlock + (ulong)blockCount * blockSize > (ulong)source.Length)
        {
            throw new InvalidDataException("The HFS allocation area is invalid or extends beyond the image.");
        }

        var overflow = new Dictionary<(byte Fork, uint File), List<(ushort Start, byte[] Extents)>>();
        var extFileExtents = ParseExtents(mdb, 0x86);
        var extFile = ReadFork(source, firstBlock, blockSize, blockCount, extFileExtents, U32(mdb, 0x82), overflow, 0xFF, 3, false);
        ValidateExtentsTree(extFile);
        var extFileRecords = LeafRecords(extFile).ToArray();
        foreach (var record in extFileRecords)
        {
            if (record.Key.Length < 8 || record.Data.Length < 12)
            {
                continue;
            }

            var key = new BigEndianReader(record.Key);
            var id = U32(key, 2);
            var start = U16(key, 6);
            var kind = record.Key[1];
            if (!overflow.TryGetValue((kind, id), out var list))
            {
                overflow[(kind, id)] = list = [];
            }

            list.Add((start, record.Data.AsSpan(0, 12).ToArray()));
        }

        var catalogExtents = ParseExtents(mdb, 0x96);
        var catalog = ReadFork(source, firstBlock, blockSize, blockCount, catalogExtents, U32(mdb, 0x92), overflow, 0, 4, true);
        ValidateCatalogTree(catalog);
        var catalogRecords = LeafRecords(catalog).ToArray();
        var catalogBefore = catalogRecords.Select(r => (r.Key, r.Data.ToArray())).ToList();
        var overflowBefore = extFileRecords.Select(r => (r.Key, r.Data.ToArray())).ToList();
        // Names as Mac OS Roman text to match the caller's path, and escaped as MacFile.MacPath shows them.
        var folders = new Dictionary<uint, (uint Parent, string Name)>();
        var shownFolders = new Dictionary<uint, (uint Parent, string Name)>();
        foreach (var entry in catalogRecords)
        {
            if (entry.Key.Length >= 7 && entry.Data.Length >= 70 && entry.Data[0] == 1)
            {
                uint folderId = U32(new BigEndianReader(entry.Data), 6), folderParent = U32(new BigEndianReader(entry.Key), 2);
                folders[folderId] = (folderParent, DecodeName(entry.Key));
                shownFolders[folderId] = (folderParent, ShownName(entry.Key));
            }
        }

        string wanted = macPath;
        var match = catalogRecords.FirstOrDefault(entry => entry.Key.Length >= 7 && entry.Data.Length >= 102 && entry.Data[0] == 2 &&
            HfsPathEquals(PathOf(U32(new BigEndianReader(entry.Key), 2), DecodeName(entry.Key), folders), wanted));
        if (match is null)
        {
            throw new InvalidDataException($"The HFS file '{macPath}' was not found.");
        }

        if ((match.Data[2] & 1) != 0)
        {
            throw new InvalidDataException($"The HFS file '{macPath}' is locked.");
        }

        string canonicalPath = PathOf(U32(new BigEndianReader(match.Key), 2), ShownName(match.Key), shownFolders);

        int forkLengthOffset = fork == HfsFork.Data ? 26 : 36;
        int forkPhysicalLengthOffset = fork == HfsFork.Data ? 30 : 40;
        int forkExtentOffset = fork == HfsFork.Data ? 74 : 86;
        var matchData = new BigEndianReader(match.Data);
        uint fileId = U32(matchData, 20);
        uint logicalLength = U32(matchData, forkLengthOffset);
        var primaryExtentRecord = new BigEndianReader(match.Data.AsSpan(forkExtentOffset, 12).ToArray());
        LastExtentSlot(primaryExtentRecord);
        var extents = ParseExtents(primaryExtentRecord);
        var overflowKey = (fork == HfsFork.Data ? (byte)0 : (byte)0xFF, fileId);
        var targetOverflow = extFileRecords.Where(r => r.Key.Length >= 8 && r.Key[1] == overflowKey.Item1 &&
                U32(new BigEndianReader(r.Key), 2) == fileId)
            .OrderBy(r => U16(new BigEndianReader(r.Key), 6)).ToArray();
        uint forkBlockNumber = (uint)extents.Aggregate(0, (count, extent) => count + extent.Count);
        foreach (var overflowRecord in targetOverflow)
        {
            if (U16(new BigEndianReader(overflowRecord.Key), 6) != forkBlockNumber ||
                overflowRecord.Data.Length < 12)
            {
                throw new InvalidDataException("The file's extents-overflow keys are not contiguous with its fork extents.");
            }

            var overflowData = new BigEndianReader(overflowRecord.Data);
            LastExtentSlot(overflowData);
            var next = ParseExtents(overflowData);
            extents.AddRange(next);
            forkBlockNumber += (uint)next.Aggregate(0, (count, extent) => count + extent.Count);
        }

        ulong capacity = extents.Aggregate<(ushort Start, ushort Count), ulong>(0, (total, e) => total + (ulong)e.Count * blockSize);
        if (logicalLength > capacity)
        {
            throw new InvalidDataException("The existing fork length exceeds its allocated extents.");
        }

        foreach (var (start, count) in extents)
        {
            if ((uint)start + count > blockCount || (ulong)firstBlock + (ulong)start * blockSize + (ulong)count * blockSize > (ulong)source.Length)
            {
                throw new InvalidDataException("A target fork extent lies outside the HFS allocation area or image.");
            }
        }

        var result = source.Fork();
        var mdbOut = mdbSector.ToArray();
        var volume = new BigEndianWriter(mdbOut);
        var bitmapOffset = checked((int)U16(mdb, 0x0E) * 512);
        int bitmapLength = checked(((int)blockCount + 7) / 8);
        if (bitmapOffset < 0 || bitmapOffset + bitmapLength > source.Length)
        {
            throw new InvalidDataException("The HFS volume bitmap lies outside the image.");
        }

        var workingBitmap = new byte[bitmapLength];
        source.Read(bitmapOffset, workingBitmap);
        ValidateBitmapFreeCount(blockCount, workingBitmap, U16(mdb, 0x22));
        ValidateExtentOwnership(blockCount, workingBitmap, extFileExtents,
            WithOverflow(catalogExtents, overflow, 0, 4), catalogRecords, overflow, match, fileId, forkExtentOffset, extents);
        var resizeContext = new ForkResizeContext(extFile, match, forkExtentOffset, targetOverflow,
            extents, workingBitmap, blockCount, overflowKey.Item1, fileId, extFileExtents, blockSize);
        var resize = ResizeFork(resizeContext,
            ((ulong)data.Length + blockSize - 1) / blockSize);
        extFile = resizeContext.ExtentsTree;
        ushort freeBlocks = U16(mdb, 0x22);
        long remainingFreeBlocks = (long)freeBlocks - resize.AllocatedBlocks + resize.ReleasedBlocks;
        if (remainingFreeBlocks < 0 || remainingFreeBlocks > blockCount)
        {
            throw new InvalidDataException("The HFS volume free-block count disagrees with the requested allocation change.");
        }

        if (resize.AllocatedBlocks != 0 || resize.ReleasedBlocks != 0)
        {
            volume.WriteUInt16At(0x22, remainingFreeBlocks);
            result.Write(bitmapOffset, workingBitmap);
        }
        bool changedExtentsTree = resize.TreeChanged;
        uint newlyAllocatedBlocks = resize.AllocatedBlocks;
        uint releasedBlocks = resize.ReleasedBlocks;
        if (resizeContext.AllocatedTreeBlocks != 0)
        {
            volume.WriteUInt32At(0x82, extFile.Length);
            mdbOut.AsSpan(0x86, 12).Clear();
            for (int index = 0; index < extFileExtents.Count; index++)
            {
                WriteExtent(mdbOut.AsSpan(0x86, 12), index,
                    extFileExtents[index].Start, extFileExtents[index].Count);
            }
        }


        ulong physicalBytes = extents.Aggregate<(ushort Start, ushort Count), ulong>(0, (total, e) => total + (ulong)e.Count * blockSize);
        if (physicalBytes > uint.MaxValue)
        {
            throw new InvalidDataException("The allocated fork exceeds HFS's 32-bit physical length field.");
        }

        var matchRecord = new BigEndianWriter(match.Data);
        matchRecord.WriteUInt32At(forkPhysicalLengthOffset, physicalBytes);
        matchRecord.WriteUInt32At(forkLengthOffset, data.Length);
        DateTime writeTime = Now;
        uint macWriteTime = MacDate.FromDateTime(writeTime).Seconds;
        matchRecord.WriteUInt32At(48, macWriteTime);
        volume.WriteUInt32At(0x06, macWriteTime);
        volume.WriteUInt32At(0x46, unchecked(U32(mdb, 0x46) + 1));

        WriteFork(result, firstBlock, blockSize, extents, data.Span);
        // Clear the unused tail within the existing allocation, so shortening a fork does not leave stale bytes.
        ClearForkTail(result, firstBlock, blockSize, extents, data.Length);
        UpdateCatalogRecord(catalog, match);
        ValidateCatalogTree(catalog);
        WriteFork(result, firstBlock, blockSize, WithOverflow(catalogExtents, overflow, 0, 4), catalog);
        if (changedExtentsTree)
        {
            WriteFork(result, firstBlock, blockSize, extFileExtents, extFile);
        }

        result.Write(MdbOffset, mdbOut);
        long alternateMdbOffset = result.Length - 2 * BlockSize;
        if (resizeContext.AllocatedTreeBlocks != 0 && alternateMdbOffset >= 0 &&
            (ulong)alternateMdbOffset >= firstBlock + (ulong)blockCount * blockSize &&
            ReadUInt16(source, alternateMdbOffset) == 0x4244)
        {
            result.Write(alternateMdbOffset, mdbOut);
        }

        ValidateExtentsTree(ReadFork(result, firstBlock, blockSize, blockCount, extFileExtents,
            U32(new BigEndianReader(mdbOut), 0x82), overflow, 0xFF, 3, false));
        Verify(source, result, canonicalPath, fork, data.Span, writeTime, newlyAllocatedBlocks, releasedBlocks);
        var targetBlocks = new HashSet<uint>(extents.SelectMany(e => Enumerable.Range(e.Start, e.Count).Select(b => (uint)b)));
        VerifyKept(result, catalogBefore, [Convert.ToHexString(match.Key)], 0, overflowBefore, [fileId], targetBlocks);
        return result;
    }

    private sealed record TreeRecord(byte[] Key, byte[] Data, int NodeOffset, int RecordStart, int RecordEnd);

    private sealed class ForkResizeContext(byte[] extentsTree, TreeRecord catalogRecord, int extentOffset,
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

    private readonly record struct ForkResizeResult(bool TreeChanged, uint AllocatedBlocks, uint ReleasedBlocks);

    private sealed class BTreeNeedsNodesException : Exception
    {
        public BTreeNeedsNodesException() : base("The extents-overflow B-tree needs more nodes.") { }
    }

    private static ForkResizeResult ResizeFork(ForkResizeContext context, ulong requiredBlocks)
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

    private static ForkResizeResult GrowFork(ForkResizeContext context, ulong allocatedBlocks, ulong requiredBlocks)
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

    private static void GrowExtentsTree(ForkResizeContext context)
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

    private static ForkResizeResult ShrinkFork(ForkResizeContext context, ulong excess)
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

    private static IEnumerable<TreeRecord> LeafRecords(byte[] tree)
    {
        if (tree.Length < NodeSize)
        {
            throw new InvalidDataException("An HFS B-tree is shorter than one node.");
        }

        var file = new BTreeFile(tree, NodeSize, wordKeyLength: false);
        var seen = new HashSet<uint>();
        for (uint node = file.FirstLeaf; node != 0; node = file.Node(node).FLink)
        {
            if (node >= file.NodeCount || !seen.Add(node))
            {
                throw new InvalidDataException("An HFS B-tree leaf link is invalid or loops.");
            }

            var descriptor = file.Node(node);
            if (descriptor.Kind != BTreeNode.LeafKind)
            {
                throw new InvalidDataException("An HFS B-tree leaf chain links to a non-leaf node.");
            }

            if (descriptor.RecordCount > (NodeSize - 14) / 2 - 1)
            {
                throw new InvalidDataException("An HFS B-tree node has too many records for its offset table.");
            }

            for (int i = 0; i < descriptor.RecordCount; i++)
            {
                if (!file.TryRecord(node, i, out var key, out var data) || !file.TryRecordBounds(node, i, out int start, out int end))
                {
                    throw new InvalidDataException("An HFS B-tree record has invalid offsets.");
                }

                yield return new TreeRecord(key.ToArray(), data.ToArray(), file.Offset(node), start, end);
            }
        }
    }

    private static void UpdateCatalogRecord(byte[] tree, TreeRecord target)
    {
        // Locate the same leaf record from its key; its data starts at the even boundary after the key.
        int keyLength = target.Key.Length;
        int dataStart = (target.RecordStart + keyLength + 1) & ~1;
        target.Data.CopyTo(tree, target.NodeOffset + dataStart);
    }

    private static void UpdateTreeRecord(byte[] tree, byte[] key, byte[] data)
    {
        var target = LeafRecords(tree).FirstOrDefault(record => CompareExtentsKeys(record.Key, key) == 0);
        if (target is null || target.Data.Length != data.Length)
        {
            throw new InvalidDataException("The extents-overflow record to update was not found at its current location.");
        }

        int dataStart = (target.RecordStart + target.Key.Length + 1) & ~1;
        data.CopyTo(tree, target.NodeOffset + dataStart);
    }

    private static void DeleteExtentsRecord(byte[] tree, byte[] key)
    {
        var records = LeafRecords(tree).ToList();
        int recordIndex = records.FindIndex(record => CompareExtentsKeys(record.Key, key) == 0);
        if (recordIndex < 0)
        {
            throw new InvalidDataException("The extents-overflow record to remove was not found.");
        }

        records.RemoveAt(recordIndex);
        RebuildBTree(tree, records.Select(record => (record.Key, record.Data)).ToList(), validateExtents: true);
    }

    private static void RebuildBTree(byte[] tree, List<(byte[] Key, byte[] Data)> records, bool validateExtents)
    {
        var treeReader = new BigEndianReader(tree);
        uint nodeCount = U32(treeReader, 14 + 22);
        // Index keys are written at the tree's maximum key length, zero-padded, as Mac OS writes them: HFS B-trees do
        // not set kBTVariableIndexKeysMask [Code: Mac OS 9.0 ROM; Verified: Mac OS 9's catalog] (hfs.md §1.8).
        int maxKeyLength = U16(treeReader, 14 + 20);
        byte[] IndexKey(byte[] key)
        {
            if (key.Length == 0 || key[0] >= maxKeyLength)
            {
                return key;
            }

            var padded = new byte[maxKeyLength + 1];
            key.AsSpan(0, Math.Min(key.Length, key[0] + 1)).CopyTo(padded);
            padded[0] = (byte)maxKeyLength;
            return padded;
        }

        if (!new BTreeFile(tree, NodeSize, wordKeyLength: false).TryReadMap(out var map, out var mapProblem))
        {
            throw new InvalidDataException($"The B-tree's node map is invalid: {mapProblem}");
        }

        var reserved = new HashSet<uint>(map.MapNodes) { 0 };
        var available = Enumerable.Range(1, checked((int)nodeCount - 1))
            .Select(number => (uint)number).Where(number => !reserved.Contains(number)).ToArray();
        var built = new List<(uint Number, byte[] FirstKey, byte[] Bytes)>();
        int nextNode = 0;
        uint AllocateNode()
        {
            if (nextNode == available.Length)
            {
                throw new BTreeNeedsNodesException();
            }

            return available[nextNode++];
        }

        // The records in node-sized groups, each as full as it goes: a record joins the group while its key (padded to
        // even) and data end before the offset table, one entry longer (TryBuildLeafNode's layout).
        List<List<(byte[] Key, byte[] Data)>> Partition(List<(byte[] Key, byte[] Data)> entries)
        {
            static int After(int end, (byte[] Key, byte[] Data) entry) => checked(((end + entry.Key.Length + 1) & ~1) + entry.Data.Length);

            var groups = new List<List<(byte[] Key, byte[] Data)>>();
            var group = new List<(byte[] Key, byte[] Data)>();
            int end = 14;
            foreach (var entry in entries)
            {
                int next = After(end, entry);
                if (next <= NodeSize - 2 * (group.Count + 2))
                {
                    group.Add(entry);
                    end = next;
                    continue;
                }

                if (group.Count == 0)
                {
                    throw new InvalidDataException("An extents-overflow record cannot fit in a B-tree node.");
                }

                groups.Add(group);
                group = [entry];
                end = After(14, entry);
                if (end > NodeSize - 4)
                {
                    throw new InvalidDataException("An extents-overflow record cannot fit in a B-tree node.");
                }
            }
            if (group.Count > 0)
            {
                groups.Add(group);
            }

            return groups;
        }

        List<(uint Number, byte[] FirstKey, byte[] Bytes)> BuildLevel(
            List<(byte[] Key, byte[] Data)> entries, byte height)
        {
            var level = new List<(uint Number, byte[] FirstKey, byte[] Bytes)>();
            foreach (var group in Partition(entries))
            {
                var template = new byte[NodeSize];
                template[8] = height == 1 ? (byte)0xFF : (byte)0;
                template[9] = height;
                if (!TryBuildLeafNode(template, group, out var node))
                {
                    throw new InvalidDataException("An extents-overflow node could not be rebuilt.");
                }

                var item = (AllocateNode(), group[0].Key, node);
                level.Add(item);
                built.Add(item);
            }
            for (int index = 0; index < level.Count; index++)
            {
                var links = new BigEndianWriter(level[index].Bytes);
                links.WriteUInt32At(0, index + 1 < level.Count ? level[index + 1].Number : 0);
                links.WriteUInt32At(4, index > 0 ? level[index - 1].Number : 0);
            }
            return level;
        }

        byte[] rebuilt = tree.ToArray();
        var leaves = BuildLevel(records, 1);
        var levelNodes = leaves;
        byte depth = records.Count == 0 ? (byte)0 : (byte)1;
        while (levelNodes.Count > 1)
        {
            if (depth == 127)
            {
                throw new InvalidDataException("The extents-overflow B-tree is too deep.");
            }

            depth++;
            var entries = levelNodes.Select(node => (IndexKey(node.FirstKey), ChildNode(node.Number))).ToList();
            levelNodes = BuildLevel(entries, depth);
        }

        foreach (uint number in available)
        {
            Array.Clear(rebuilt, checked((int)number * NodeSize), NodeSize);
        }

        foreach (var node in built)
        {
            node.Bytes.CopyTo(rebuilt, checked((int)node.Number * NodeSize));
        }

        var allocated = new HashSet<uint>(reserved);
        allocated.UnionWith(built.Select(node => node.Number));
        if (!new BTreeFile(rebuilt, NodeSize, wordKeyLength: false).TryReadMap(out var rebuiltMap, out mapProblem))
        {
            throw new InvalidDataException($"The B-tree's node map is invalid: {mapProblem}");
        }

        for (uint number = 0; number < nodeCount; number++)
        {
            if (number < rebuiltMap.Capacity)
            {
                rebuiltMap.SetAllocated(number, allocated.Contains(number));
            }
        }

        var header = new BigEndianWriter(rebuilt);
        header.WriteUInt16At(14, depth);
        header.WriteUInt32At(14 + 2, levelNodes.Count == 0 ? 0 : levelNodes[0].Number);
        header.WriteUInt32At(14 + 6, records.Count);
        header.WriteUInt32At(14 + 10, leaves.Count == 0 ? 0 : leaves[0].Number);
        header.WriteUInt32At(14 + 14, leaves.Count == 0 ? 0 : leaves[^1].Number);
        header.WriteUInt32At(14 + 26, nodeCount - checked((uint)allocated.Count));
        if (validateExtents)
        {
            ValidateExtentsTree(rebuilt);
        }

        rebuilt.CopyTo(tree, 0);
    }

    // The node map of a tree file grown from oldNodeCount to newNodeCount nodes: map nodes added where the map cannot
    // cover them, the new nodes marked free and the new map nodes used; returns how many map nodes it added.
    internal static uint ExtendBTreeNodeMap(byte[] tree, uint oldNodeCount, uint newNodeCount)
    {
        var file = new BTreeFile(tree, NodeSize, wordKeyLength: false);
        if (!file.TryReadMap(out var map, out var problem) || map.MapNodes.Any(node => node >= oldNodeCount))
        {
            throw new InvalidDataException($"The HFS B-tree node map is invalid: {problem ?? "a map node lies past the tree"}");
        }

        if (map.Capacity < oldNodeCount)
        {
            throw new InvalidDataException("The HFS B-tree node map does not cover its existing nodes.");
        }

        var newMapNodes = new List<uint>();
        var writer = new BigEndianWriter(tree);
        uint lastMapNode = map.MapNodes.Count == 0 ? 0 : map.MapNodes[^1];
        for (long capacity = map.Capacity; capacity < newNodeCount; capacity += (NodeSize - 20) * 8)
        {
            // At the old end of the tree, one after another, as Mac OS places them [Verified: Mac OS 9.0]; a map node of one
            // record (offsets 14 and nodeSize - 6) whose backward link stays 0, as Apple's ExtendBTree leaves it [Doc:
            // Apple's hfs sources, BTreeAllocate.c].
            uint number = oldNodeCount + (uint)newMapNodes.Count;
            if (number >= newNodeCount)
            {
                throw new InvalidDataException("The HFS B-tree has no node available for another map record.");
            }

            int at = file.Offset(number);
            writer.WriteUInt32At(file.Offset(lastMapNode), number);
            tree[at + 8] = 2;
            writer.WriteUInt16At(at + 10, 1);
            writer.WriteUInt16At(at + NodeSize - 2, 14);
            writer.WriteUInt16At(at + NodeSize - 4, NodeSize - 6);
            newMapNodes.Add(number);
            lastMapNode = number;
        }

        if (!file.TryReadMap(out map, out problem))
        {
            throw new InvalidDataException($"The HFS B-tree node map is invalid: {problem}");
        }

        for (uint number = oldNodeCount; number < newNodeCount; number++)
        {
            map.SetAllocated(number, newMapNodes.Contains(number));
        }

        return checked((uint)newMapNodes.Count);
    }

    private static void InsertExtentsRecord(byte[] tree, byte[] key, byte[] data)
    {
        if (key.Length != 8 || key[0] != 7 || data.Length != 12)
        {
            throw new ArgumentException("An HFS extents-overflow record must have an 8-byte key and a 12-byte extent record.");
        }

        var records = LeafRecords(tree).Select(record => (record.Key, record.Data)).ToList();
        int insertion = records.FindIndex(record => CompareExtentsKeys(record.Key, key) >= 0);
        if (insertion >= 0 && CompareExtentsKeys(records[insertion].Key, key) == 0)
        {
            throw new InvalidDataException("The extents-overflow B-tree already contains this file extent key.");
        }

        if (insertion < 0)
        {
            insertion = records.Count;
        }

        records.Insert(insertion, (key, data));
        RebuildBTree(tree, records, validateExtents: true);
    }

    private static List<(byte[] Key, byte[] Data)> ReadNodeRecords(byte[] tree, uint nodeNumber)
    {
        if (nodeNumber >= tree.Length / NodeSize)
        {
            throw new InvalidDataException("An HFS B-tree node lies outside the tree file.");
        }

        var file = new BTreeFile(tree, NodeSize, wordKeyLength: false);
        int count = file.Node(nodeNumber).RecordCount;
        if (count > (NodeSize - 14) / 2 - 1)
        {
            throw new InvalidDataException("An HFS B-tree node has too many records for its offset table.");
        }

        var records = new List<(byte[] Key, byte[] Data)>(count);
        for (int i = 0; i < count; i++)
        {
            if (!file.TryRecord(nodeNumber, i, out var key, out var data))
            {
                throw new InvalidDataException("An HFS B-tree node has invalid record offsets.");
            }

            records.Add((key.ToArray(), data.ToArray()));
        }
        return records;
    }

    private static bool TryBuildLeafNode(byte[] original, List<(byte[] Key, byte[] Data)> records, out byte[] rebuilt)
    {
        int requiredEnd = 14;
        foreach (var record in records)
        {
            requiredEnd = checked((requiredEnd + record.Key.Length + 1) & ~1);
            requiredEnd = checked(requiredEnd + record.Data.Length);
        }
        int tableStart = NodeSize - 2 * (records.Count + 1);
        if (requiredEnd > tableStart)
        {
            rebuilt = [];
            return false;
        }

        rebuilt = original.ToArray();
        Array.Clear(rebuilt, 14, NodeSize - 14);
        int at = 14;
        var offsets = new List<ushort>(records.Count + 1);
        foreach (var record in records)
        {
            offsets.Add(checked((ushort)at));
            record.Key.CopyTo(rebuilt, at);
            at += record.Key.Length;
            at = (at + 1) & ~1;
            record.Data.CopyTo(rebuilt, at);
            at += record.Data.Length;
        }
        var writer = new BigEndianWriter(rebuilt);
        for (int i = 0; i < offsets.Count; i++)
        {
            writer.WriteUInt16At(NodeSize - 2 * (i + 1), offsets[i]);
        }

        writer.WriteUInt16At(NodeSize - 2 * (offsets.Count + 1), at);
        writer.WriteUInt16At(10, offsets.Count);
        return true;
    }

    private static byte[] ChildNode(uint node)
    {
        var child = new BigEndianWriter(4);
        child.WriteUInt32(node);
        return child.ToArray();
    }

    private static void ValidateExtentsTree(byte[] tree) => ValidateBTree(tree, catalog: false);

    private static void ValidateCatalogTree(byte[] tree) => ValidateBTree(tree, catalog: true);

    private static void ValidateBTree(byte[] tree, bool catalog)
    {
        string treeKind = catalog ? "catalog" : "extents-overflow";
        int CompareKeys(byte[] left, byte[] right) => catalog
            ? CompareCatalogKeys(left, right) : CompareExtentsKeys(left, right);
        var treeReader = new BigEndianReader(tree);
        if (tree.Length < NodeSize || tree.Length % NodeSize != 0 || tree[8] != 1 || U16(treeReader, 10) != 3)
        {
            throw new InvalidDataException($"The HFS {treeKind} B-tree has an invalid header node or length.");
        }

        uint nodeCount = U32(treeReader, 14 + 22);
        if (nodeCount != (uint)(tree.Length / NodeSize) || U16(treeReader, 14 + 18) != NodeSize)
        {
            throw new InvalidDataException($"The HFS {treeKind} B-tree header disagrees with its node file.");
        }

        int mapStart = U16(treeReader, NodeSize - 6);
        int mapEnd = U16(treeReader, NodeSize - 8);
        if (U16(treeReader, NodeSize - 2) != 14 || U16(treeReader, NodeSize - 4) != 14 + 106 ||
            mapStart != 14 + 106 + 128 || mapEnd < mapStart || mapEnd > NodeSize - 8)
        {
            throw new InvalidDataException($"The HFS {treeKind} B-tree header records have invalid offsets.");
        }

        // The node map; a map node's backward link is not read (Apple's ExtendBTree leaves it 0; DFA's BTMapChk ignores
        // it) (hfs.md §1.8).
        if (!new BTreeFile(tree, NodeSize, wordKeyLength: false).TryReadMap(out var map, out var mapProblem))
        {
            throw new InvalidDataException($"The HFS {treeKind} B-tree node map is invalid: {mapProblem}");
        }

        var usedNodes = new HashSet<uint>(map.MapNodes) { 0 };
        if (map.Capacity < nodeCount)
        {
            throw new InvalidDataException($"The HFS {treeKind} B-tree node map is truncated.");
        }

        var nodesByHeight = new Dictionary<int, List<uint>>();
        uint leafRecordCount = 0;
        ushort depth = U16(treeReader, 14);
        uint root = U32(treeReader, 14 + 2);
        if (depth == 0)
        {
            if (root != 0 || U32(treeReader, 14 + 6) != 0 || U32(treeReader, 14 + 10) != 0 ||
                U32(treeReader, 14 + 14) != 0)
            {
                throw new InvalidDataException($"The empty HFS {treeKind} B-tree has inconsistent header fields.");
            }
        }
        else
        {
            if (depth > 127 || root == 0)
            {
                throw new InvalidDataException($"The HFS {treeKind} B-tree has an invalid root or depth.");
            }

            Visit(root, depth);
            var leaves = nodesByHeight[1];
            if (U32(treeReader, 14 + 10) != leaves[0] || U32(treeReader, 14 + 14) != leaves[^1] ||
                U32(treeReader, 14 + 6) != leafRecordCount)
            {
                throw new InvalidDataException($"The HFS {treeKind} B-tree leaf header disagrees with its nodes.");
            }
        }

        foreach (var (height, nodes) in nodesByHeight)
        {
            for (int i = 0; i < nodes.Count; i++)
            {
                int offset = checked((int)nodes[i] * NodeSize);
                uint previous = i == 0 ? 0 : nodes[i - 1];
                uint next = i + 1 == nodes.Count ? 0 : nodes[i + 1];
                if (U32(treeReader, offset) != next || U32(treeReader, offset + 4) != previous)
                {
                    throw new InvalidDataException($"The HFS {treeKind} B-tree level {height} has inconsistent sibling links.");
                }
            }
        }

        uint actualFreeNodes = 0;
        for (uint node = 0; node < nodeCount; node++)
        {
            bool allocated = map.IsAllocated(node);
            if (allocated != usedNodes.Contains(node))
            {
                throw new InvalidDataException($"The HFS {treeKind} B-tree node map disagrees with its node graph.");
            }

            if (!allocated)
            {
                actualFreeNodes++;
            }
        }
        if (actualFreeNodes != U32(treeReader, 14 + 26))
        {
            throw new InvalidDataException($"The HFS {treeKind} B-tree free-node count disagrees with its map.");
        }

        (byte[] First, byte[] Last) Visit(uint number, int height)
        {
            if (number == 0 || number >= nodeCount || !usedNodes.Add(number))
            {
                throw new InvalidDataException($"The HFS {treeKind} B-tree index graph is invalid or cyclic.");
            }

            int offset = checked((int)number * NodeSize);
            if (tree[offset + 8] != (height == 1 ? (byte)0xFF : (byte)0) || tree[offset + 9] != height)
            {
                throw new InvalidDataException($"The HFS {treeKind} B-tree index has an invalid child type or height.");
            }

            var records = ReadNodeRecords(tree, number);
            if (records.Count == 0)
            {
                throw new InvalidDataException($"An active HFS {treeKind} B-tree node is empty.");
            }

            if (!nodesByHeight.TryGetValue(height, out var peers))
            {
                nodesByHeight[height] = peers = [];
            }

            peers.Add(number);
            byte[]? first = null, last = null;
            foreach (var (key, data) in records)
            {
                // A catalog key is as long as its name (padded to even), or in an index node the tree's maximum key
                // length, zero-padded after the name, as Mac OS writes index keys (hfs.md §1.8).
                bool validKey = catalog
                    ? key.Length >= 7 && key[0] == key.Length - 1 &&
                      (key.Length == 7 + key[6] ||
                       (key.Length == ((8 + key[6]) & ~1) && key[^1] == 0) ||
                       (height > 1 && key.Length == 38 && 7 + key[6] <= 38 && key.AsSpan(7 + key[6]).IndexOfAnyExcept((byte)0) < 0))
                    : key.Length == 8 && key[0] == 7 && key[1] is 0 or 0xFF;
                if (!validKey)
                {
                    throw new InvalidDataException($"An HFS {treeKind} B-tree record has an invalid key.");
                }

                if (height == 1)
                {
                    bool validData = catalog
                        ? data.Length >= 2 && data[0] switch
                        {
                            1 => data.Length >= 70,
                            2 => data.Length >= 102,
                            // A thread as long as its name, as hfsutils writes it and Mac OS keeps it, or Mac OS's 46
                            // bytes (hfs.md §1.9) [Verified: Mac OS 9 used such a volume].
                            3 or 4 => data.Length >= 15 && data[14] <= 31 && data.Length >= 15 + data[14],
                            _ => false,
                        }
                        : data.Length == 12;
                    if (!validData || (last is not null && CompareKeys(last, key) >= 0))
                    {
                        throw new InvalidDataException($"The HFS {treeKind} leaf records are invalid or out of order.");
                    }

                    first ??= key;
                    last = key;
                    leafRecordCount = checked(leafRecordCount + 1);
                }
                else
                {
                    if (data.Length != 4)
                    {
                        throw new InvalidDataException($"An HFS {treeKind} index record has an invalid child pointer.");
                    }

                    var child = Visit(U32(new BigEndianReader(data), 0), height - 1);
                    if (CompareKeys(key, child.First) != 0 ||
                        (last is not null && CompareKeys(last, child.First) >= 0))
                    {
                        throw new InvalidDataException($"An HFS {treeKind} index key disagrees with its child.");
                    }

                    first ??= child.First;
                    last = child.Last;
                }
            }
            return (first!, last!);
        }
    }

    private static int CompareExtentsKeys(byte[] left, byte[] right)
    {
        if (left.Length < 8 || right.Length < 8)
        {
            throw new InvalidDataException("An extents-overflow key is shorter than eight bytes.");
        }

        var leftReader = new BigEndianReader(left);
        var rightReader = new BigEndianReader(right);
        int order = U32(leftReader, 2).CompareTo(U32(rightReader, 2));
        if (order != 0)
        {
            return order;
        }

        order = left[1].CompareTo(right[1]);
        return order != 0 ? order : U16(leftReader, 6).CompareTo(U16(rightReader, 6));
    }

    private static void ValidateExtentOwnership(uint blockCount, byte[] bitmap,
        List<(ushort Start, ushort Count)> extentsFile, List<(ushort Start, ushort Count)> catalogFile,
        TreeRecord[] catalogRecords, Dictionary<(byte Fork, uint File), List<(ushort Start, byte[] Extents)>> overflow,
        TreeRecord target, uint targetFileId, int targetForkOffset, List<(ushort Start, ushort Count)> targetExtents)
    {
        var claimed = new HashSet<ushort>();
        Claim(extentsFile, "extents-overflow file");
        Claim(catalogFile, "catalog file");
        if (overflow.TryGetValue((0x00, 3), out var extentsOverflow))
        {
            foreach (var record in extentsOverflow.OrderBy(r => r.Start))
            {
                Claim(ParseExtents(new BigEndianReader(record.Extents)), "extents-overflow file");
            }
        }

        foreach (var record in catalogRecords)
        {
            if (record.Key.Length < 7 || record.Data.Length < 102 || record.Data[0] != 2)
            {
                continue;
            }

            var data = new BigEndianReader(record.Data);
            uint id = U32(data, 20);
            foreach (var (kind, offset) in new[] { ((byte)0, 74), ((byte)0xFF, 86) })
            {
                if (ReferenceEquals(record, target) && id == targetFileId && offset == targetForkOffset)
                {
                    continue;
                }

                var fileExtents = ParseExtents(data, offset);
                Claim(fileExtents, "file fork");
                if (overflow.TryGetValue((kind, id), out var fileOverflow))
                {
                    foreach (var extra in fileOverflow.OrderBy(r => r.Start))
                    {
                        Claim(ParseExtents(new BigEndianReader(extra.Extents)), "file fork");
                    }
                }
            }
        }
        Claim(targetExtents, "target fork");

        void Claim(IEnumerable<(ushort Start, ushort Count)> extents, string owner)
        {
            foreach (var (start, count) in extents)
            {
                if ((uint)start + count > blockCount)
                {
                    throw new InvalidDataException($"An extent of the {owner} lies outside the HFS allocation area.");
                }

                for (uint block = start; block < (uint)start + count; block++)
                {
                    ushort index = (ushort)block;
                    if (!IsAllocated(bitmap, index))
                    {
                        throw new InvalidDataException($"An extent of the {owner} points to a free allocation block.");
                    }

                    if (!claimed.Add(index))
                    {
                        throw new InvalidDataException($"An allocation block is shared by multiple HFS extents ({owner}).");
                    }
                }
            }
        }
    }

    private static byte[] ReadFork(HfsVolume image, uint firstBlock, uint blockSize, uint blockCount,
        List<(ushort Start, ushort Count)> firstExtents, uint logicalLength,
        Dictionary<(byte Fork, uint File), List<(ushort Start, byte[] Extents)>> overflow,
        byte overflowFork, uint fileId, bool includeOverflow)
    {
        var extents = firstExtents.ToList();
        if (includeOverflow && overflow.TryGetValue((overflowFork, fileId), out var extra))
        {
            extents.AddRange(extra.OrderBy(x => x.Start)
                .SelectMany(x => ParseExtents(new BigEndianReader(x.Extents))));
        }

        ulong capacity = extents.Aggregate<(ushort Start, ushort Count), ulong>(0, (n, e) => n + (ulong)e.Count * blockSize);
        if (logicalLength > capacity || logicalLength > int.MaxValue)
        {
            throw new InvalidDataException("An HFS system fork has invalid length or extents.");
        }

        var output = new byte[(int)logicalLength];
        int written = 0;
        foreach (var (start, count) in extents)
        {
            if ((uint)start + count > blockCount)
            {
                throw new InvalidDataException("An HFS extent lies outside the allocation area.");
            }

            int take = (int)Math.Min((ulong)(output.Length - written), (ulong)count * blockSize);
            long sourceOffset = (long)firstBlock + (long)start * blockSize;
            if (sourceOffset < 0 || sourceOffset + take > image.Length)
            {
                throw new InvalidDataException("An HFS extent lies outside the image.");
            }

            image.Read(sourceOffset, output.AsSpan(written, take));
            written += take;
            if (written == output.Length)
            {
                break;
            }
        }
        return output;
    }

    private static List<(ushort Start, ushort Count)> ParseExtents(BigEndianReader bytes, int offset = 0)
    {
        var result = new List<(ushort, ushort)>();
        for (int i = 0; i < 3; i++)
        {
            ushort start = U16(bytes, offset + i * 4), count = U16(bytes, offset + i * 4 + 2);
            if (count != 0)
            {
                result.Add((start, count));
            }
        }
        return result;
    }

    private static int LastExtentSlot(BigEndianReader record)
    {
        int last = -1;
        bool emptySeen = false;
        for (int slot = 0; slot < 3; slot++)
        {
            ushort start = U16(record, slot * 4), count = U16(record, slot * 4 + 2);
            if (count == 0)
            {
                if (start != 0)
                {
                    throw new InvalidDataException("An empty HFS extent descriptor has a nonzero start block.");
                }

                emptySeen = true;
            }
            else
            {
                if (emptySeen)
                {
                    throw new InvalidDataException("An HFS extent record has a populated descriptor after an empty slot.");
                }

                last = slot;
            }
        }
        return last;
    }

    private static void WriteExtent(Span<byte> record, int slot, ushort start, ushort count)
    {
        var extent = new BigEndianWriter(4);
        extent.WriteUInt16(start);
        extent.WriteUInt16(count);
        extent.WrittenSpan.CopyTo(record.Slice(slot * 4, 4));
    }

    private static List<(ushort Start, ushort Count)> AllocateRuns(byte[] bitmap, ushort blockCount, ulong required, int maxRuns)
    {
        var freeRuns = new List<(ushort Start, ushort Count)>();
        int runStart = -1;
        for (int block = 0; block <= blockCount; block++)
        {
            bool free = block < blockCount && !IsAllocated(bitmap, (ushort)block);
            if (free && runStart < 0)
            {
                runStart = block;
            }

            if (!free && runStart >= 0)
            {
                freeRuns.Add((checked((ushort)runStart), checked((ushort)(block - runStart))));
                runStart = -1;
            }
        }
        freeRuns.Sort((left, right) => right.Count.CompareTo(left.Count));
        var chosen = new List<(ushort Start, ushort Count)>();
        ulong remaining = required;
        foreach (var run in freeRuns)
        {
            if (remaining == 0)
            {
                break;
            }

            if (chosen.Count == maxRuns)
            {
                break;
            }

            ushort take = (ushort)Math.Min((ulong)run.Count, remaining);
            chosen.Add((run.Start, take));
            remaining -= take;
        }
        if (remaining != 0)
        {
            throw new InvalidDataException("There are not enough free HFS allocation blocks in the fork's available extent slots.");
        }

        chosen.Sort((left, right) => left.Start.CompareTo(right.Start));
        return chosen;
    }

    private static bool IsAllocated(byte[] bitmap, ushort block)
    {
        int index = block >> 3;
        if (index >= bitmap.Length)
        {
            throw new InvalidDataException("The HFS volume bitmap is too short.");
        }

        return (bitmap[index] & (0x80 >> (block & 7))) != 0;
    }

    private static void SetBitmap(byte[] bitmap, ushort block, bool allocated)
    {
        int index = block >> 3;
        if (index >= bitmap.Length)
        {
            throw new InvalidDataException("The HFS volume bitmap is too short.");
        }

        byte mask = (byte)(0x80 >> (block & 7));
        if (allocated)
        {
            bitmap[index] |= mask;
        }
        else
        {
            bitmap[index] &= (byte)~mask;
        }
    }

    private static void WriteFork(HfsVolume image, uint firstBlock, uint blockSize, List<(ushort Start, ushort Count)> extents, ReadOnlySpan<byte> data)
    {
        int at = 0;
        foreach (var (start, count) in extents)
        {
            int take = Math.Min(data.Length - at, checked((int)((uint)count * blockSize)));
            if (take <= 0)
            {
                break;
            }

            long offset = firstBlock + (long)start * blockSize;
            image.Write(offset, data.Slice(at, take));
            at += take;
        }
        if (at != data.Length)
        {
            throw new InvalidDataException("The HFS extents cannot hold the data to write.");
        }
    }

    private static void ClearForkTail(HfsVolume image, uint firstBlock, uint blockSize, List<(ushort Start, ushort Count)> extents, int dataLength)
    {
        long skip = dataLength;
        foreach (var (start, count) in extents)
        {
            int bytes = checked((int)((uint)count * blockSize));
            long offset = firstBlock + (long)start * blockSize;
            int clearFrom = (int)Math.Clamp(skip, 0, bytes);
            if (clearFrom < bytes)
            {
                image.Write(offset + clearFrom, new byte[bytes - clearFrom]);
            }

            skip -= bytes;
        }
    }

    private static List<(ushort Start, ushort Count)> WithOverflow(List<(ushort Start, ushort Count)> initial,
        Dictionary<(byte Fork, uint File), List<(ushort Start, byte[] Extents)>> overflow, byte fork, uint fileId)
    {
        var result = initial.ToList();
        if (overflow.TryGetValue((fork, fileId), out var values))
        {
            result.AddRange(values.OrderBy(x => x.Start)
                .SelectMany(x => ParseExtents(new BigEndianReader(x.Extents))));
        }

        return result;
    }

    private static void Verify(HfsVolume source, HfsVolume result, string targetPath, HfsFork changedFork, ReadOnlySpan<byte> expected,
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

    private static bool SameMetadata(MacFile left, MacFile right, bool includeModified) =>
        left.Name == right.Name && left.FolderPath.SequenceEqual(right.FolderPath) &&
        left.FinderInfo.Type == right.FinderInfo.Type && left.FinderInfo.Creator == right.FinderInfo.Creator &&
        left.FinderInfo.Flags == right.FinderInfo.Flags && left.FinderInfo.Location == right.FinderInfo.Location &&
        left.FinderInfo.Folder == right.FinderInfo.Folder && left.FinderInfo.Extended.Span.SequenceEqual(right.FinderInfo.Extended.Span) &&
        left.Created == right.Created && (!includeModified || left.Modified == right.Modified);

    private static string PathOf(uint parent, string name, Dictionary<uint, (uint Parent, string Name)> folders)
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

    private static void ValidateBitmapFreeCount(uint blockCount, byte[] bitmap, ushort expectedFree)
    {
        uint actualFree = 0;
        for (uint block = 0; block < blockCount; block++)
        {
            if (!IsAllocated(bitmap, checked((ushort)block)))
            {
                actualFree++;
            }
        }

        if (actualFree != expectedFree)
        {
            throw new InvalidDataException("The HFS volume free-block count disagrees with its allocation bitmap.");
        }
    }

    private static bool HfsPathEquals(string left, string right)
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
    private static string DecodeName(byte[] key) => key.Length >= 7 ? new MacString(key.AsSpan(7, Math.Min(key[6], key.Length - 7))).ToMacRoman() : "";

    // A key's name as MacFile.MacPath shows it (control characters escaped).
    private static string ShownName(byte[] key) => key.Length >= 7 ? new MacString(key.AsSpan(7, Math.Min(key[6], key.Length - 7))).ToString() : "";
    // An offset outside the data throws ArgumentOutOfRangeException.
    private static ushort U16(BigEndianReader b, int o) =>
        b.TryReadUInt16At(o, out ushort value) ? value : throw new ArgumentOutOfRangeException(nameof(o));
    private static uint U32(BigEndianReader b, int o) =>
        b.TryReadUInt32At(o, out uint value) ? value : throw new ArgumentOutOfRangeException(nameof(o));
}

/// <summary>The fork selected for an HFS file edit.</summary>
public enum HfsFork
{
    /// <summary>The data fork.</summary>
    Data,
    /// <summary>The resource fork.</summary>
    Resource,
}
