using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using ClassicMac.Core;

namespace ClassicMac.Files.Hfs
{
    /// <summary>Conservative copy-on-write edits to plain HFS volumes.</summary>
    public static partial class HfsWriter
    {
        private const int MdbOffset = 1024, MdbSize = 162, BlockSize = 512, NodeSize = 512;

        /// <summary>
        /// Replaces one fork on a plain HFS volume and returns a new image. The source image is never modified.
        /// </summary>
        /// <exception cref="InvalidDataException">The image, target, or requested edit is not supported or valid.</exception>
        public static byte[] ReplaceFork(ForkData image, string macPath, HfsFork fork, ReadOnlyMemory<byte> data)
        {
            ArgumentNullException.ThrowIfNull(image);
            ArgumentException.ThrowIfNullOrEmpty(macPath);
            if (fork is not (HfsFork.Data or HfsFork.Resource)) throw new ArgumentOutOfRangeException(nameof(fork));
            if (macPath.StartsWith(':') || macPath.EndsWith(':') || macPath.Contains("::", StringComparison.Ordinal))
                throw new ArgumentException("Use a colon-separated HFS path without empty components.", nameof(macPath));

            byte[] source = image.ToArray();
            if (source.Length < MdbOffset + MdbSize || U16(source, MdbOffset) != 0x4244)
                throw new InvalidDataException("The input is not a plain HFS volume.");
            var mdb = source.AsSpan(MdbOffset, MdbSize);
            if (U16(mdb, 0x7C) == 0x482B)
                throw new InvalidDataException("Writing an HFS wrapper around HFS Plus is not supported.");
            if ((U16(mdb, 0x0A) & 0x8000) != 0)
                throw new InvalidDataException("The HFS volume is software-locked.");
            uint blockSize = U32(mdb, 0x14);
            uint blockCount = U16(mdb, 0x12);
            uint firstBlock = (uint)U16(mdb, 0x1C) * 512;
            if (blockSize < 512 || blockSize % 512 != 0 || firstBlock + (ulong)blockCount * blockSize > (ulong)source.Length)
                throw new InvalidDataException("The HFS allocation area is invalid or extends beyond the image.");

            var overflow = new Dictionary<(byte Fork, uint File), List<(ushort Start, byte[] Extents)>>();
            var extFileExtents = ParseExtents(mdb.Slice(0x86, 12));
            var extFile = ReadFork(source, firstBlock, blockSize, blockCount, extFileExtents, U32(mdb, 0x82), overflow, 0xFF, 3, false);
            ValidateExtentsTree(extFile);
            var extFileRecords = LeafRecords(extFile).ToArray();
            foreach (var record in extFileRecords)
            {
                if (record.Key.Length < 8 || record.Data.Length < 12) continue;
                var key = record.Key.AsSpan();
                var id = U32(key, 2);
                var start = U16(key, 6);
                var kind = key[1];
                if (!overflow.TryGetValue((kind, id), out var list)) overflow[(kind, id)] = list = [];
                list.Add((start, record.Data.AsSpan(0, 12).ToArray()));
            }

            var catalogExtents = ParseExtents(mdb.Slice(0x96, 12));
            var catalog = ReadFork(source, firstBlock, blockSize, blockCount, catalogExtents, U32(mdb, 0x92), overflow, 0, 4, true);
            ValidateCatalogTree(catalog);
            var catalogRecords = LeafRecords(catalog).ToArray();
            var folders = new Dictionary<uint, (uint Parent, string Name)>();
            foreach (var entry in catalogRecords)
            {
                if (entry.Key.Length >= 7 && entry.Data.Length >= 70 && entry.Data[0] == 1)
                    folders[U32(entry.Data, 6)] = (U32(entry.Key, 2), DecodeName(entry.Key));
            }

            string wanted = macPath;
            var match = catalogRecords.FirstOrDefault(entry => entry.Key.Length >= 7 && entry.Data.Length >= 102 && entry.Data[0] == 2 &&
                HfsPathEquals(PathOf(U32(entry.Key, 2), DecodeName(entry.Key), folders), wanted));
            if (match is null) throw new InvalidDataException($"The HFS file '{macPath}' was not found.");
            if ((match.Data[2] & 1) != 0) throw new InvalidDataException($"The HFS file '{macPath}' is locked.");
            string canonicalPath = PathOf(U32(match.Key, 2), DecodeName(match.Key), folders);

            int forkLengthOffset = fork == HfsFork.Data ? 26 : 36;
            int forkPhysicalLengthOffset = fork == HfsFork.Data ? 30 : 40;
            int forkExtentOffset = fork == HfsFork.Data ? 74 : 86;
            uint fileId = U32(match.Data, 20);
            uint logicalLength = U32(match.Data, forkLengthOffset);
            var primaryExtentRecord = match.Data.AsSpan(forkExtentOffset, 12).ToArray();
            LastExtentSlot(primaryExtentRecord);
            var extents = ParseExtents(primaryExtentRecord);
            var overflowKey = (fork == HfsFork.Data ? (byte)0 : (byte)0xFF, fileId);
            var targetOverflow = extFileRecords.Where(r => r.Key.Length >= 8 && r.Key[1] == overflowKey.Item1 && U32(r.Key, 2) == fileId)
                .OrderBy(r => U16(r.Key, 6)).ToArray();
            uint forkBlockNumber = (uint)extents.Aggregate(0, (count, extent) => count + extent.Count);
            foreach (var overflowRecord in targetOverflow)
            {
                if (U16(overflowRecord.Key, 6) != forkBlockNumber || overflowRecord.Data.Length < 12)
                    throw new InvalidDataException("The file's extents-overflow keys are not contiguous with its fork extents.");
                LastExtentSlot(overflowRecord.Data);
                var next = ParseExtents(overflowRecord.Data);
                extents.AddRange(next);
                forkBlockNumber += (uint)next.Aggregate(0, (count, extent) => count + extent.Count);
            }

            ulong capacity = extents.Aggregate<(ushort Start, ushort Count), ulong>(0, (total, e) => total + (ulong)e.Count * blockSize);
            if (logicalLength > capacity) throw new InvalidDataException("The existing fork length exceeds its allocated extents.");
            foreach (var (start, count) in extents)
            {
                if ((uint)start + count > blockCount || (ulong)firstBlock + (ulong)start * blockSize + (ulong)count * blockSize > (ulong)source.Length)
                    throw new InvalidDataException("A target fork extent lies outside the HFS allocation area or image.");
            }

            byte[] result = source.ToArray();
            var bitmapOffset = checked((int)U16(mdb, 0x0E) * 512);
            int bitmapLength = checked(((int)blockCount + 7) / 8);
            if (bitmapOffset < 0 || bitmapOffset + bitmapLength > source.Length)
                throw new InvalidDataException("The HFS volume bitmap lies outside the image.");
            var workingBitmap = source.AsSpan(bitmapOffset, bitmapLength).ToArray();
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
                throw new InvalidDataException("The HFS volume free-block count disagrees with the requested allocation change.");
            if (resize.AllocatedBlocks != 0 || resize.ReleasedBlocks != 0)
            {
                BinaryPrimitives.WriteUInt16BigEndian(result.AsSpan(MdbOffset + 0x22), checked((ushort)remainingFreeBlocks));
                workingBitmap.CopyTo(result.AsSpan(bitmapOffset, bitmapLength));
            }
            bool changedExtentsTree = resize.TreeChanged;
            uint newlyAllocatedBlocks = resize.AllocatedBlocks;
            uint releasedBlocks = resize.ReleasedBlocks;
            if (resizeContext.AllocatedTreeBlocks != 0)
            {
                BinaryPrimitives.WriteUInt32BigEndian(result.AsSpan(MdbOffset + 0x82), checked((uint)extFile.Length));
                result.AsSpan(MdbOffset + 0x86, 12).Clear();
                for (int index = 0; index < extFileExtents.Count; index++)
                    WriteExtent(result.AsSpan(MdbOffset + 0x86, 12), index,
                        extFileExtents[index].Start, extFileExtents[index].Count);
            }


            ulong physicalBytes = extents.Aggregate<(ushort Start, ushort Count), ulong>(0, (total, e) => total + (ulong)e.Count * blockSize);
            if (physicalBytes > uint.MaxValue) throw new InvalidDataException("The allocated fork exceeds HFS's 32-bit physical length field.");
            BinaryPrimitives.WriteUInt32BigEndian(match.Data.AsSpan(forkPhysicalLengthOffset), (uint)physicalBytes);
            BinaryPrimitives.WriteUInt32BigEndian(match.Data.AsSpan(forkLengthOffset), (uint)data.Length);
            DateTime writeTime = DateTime.Now;
            uint macWriteTime = MacDate.FromDateTime(writeTime).Seconds;
            BinaryPrimitives.WriteUInt32BigEndian(match.Data.AsSpan(48), macWriteTime);
            BinaryPrimitives.WriteUInt32BigEndian(result.AsSpan(MdbOffset + 0x06), macWriteTime);
            BinaryPrimitives.WriteUInt32BigEndian(result.AsSpan(MdbOffset + 0x46), unchecked(U32(mdb, 0x46) + 1));

            WriteFork(result, firstBlock, blockSize, extents, data.Span);
            // Clear the unused tail within the existing allocation, so shortening a fork does not leave stale bytes.
            ClearForkTail(result, firstBlock, blockSize, extents, data.Length);
            UpdateCatalogRecord(catalog, match);
            ValidateCatalogTree(catalog);
            WriteFork(result, firstBlock, blockSize, WithOverflow(catalogExtents, overflow, 0, 4), catalog);
            if (changedExtentsTree) WriteFork(result, firstBlock, blockSize, extFileExtents, extFile);
            int alternateMdbOffset = result.Length - 2 * BlockSize;
            if (resizeContext.AllocatedTreeBlocks != 0 && alternateMdbOffset >= 0 &&
                (ulong)alternateMdbOffset >= firstBlock + (ulong)blockCount * blockSize &&
                U16(source, alternateMdbOffset) == 0x4244)
                result.AsSpan(MdbOffset, BlockSize).CopyTo(result.AsSpan(alternateMdbOffset, BlockSize));
            ValidateExtentsTree(ReadFork(result, firstBlock, blockSize, blockCount, extFileExtents,
                U32(result, MdbOffset + 0x82), overflow, 0xFF, 3, false));
            Verify(source, result, canonicalPath, fork, data.Span, writeTime, newlyAllocatedBlocks, releasedBlocks);
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
            if (requiredBlocks > allocatedBlocks) return GrowFork(context, allocatedBlocks, requiredBlocks);
            if (requiredBlocks < allocatedBlocks) return ShrinkFork(context, allocatedBlocks - requiredBlocks);
            return default;
        }

        private static ForkResizeResult GrowFork(ForkResizeContext context, ulong allocatedBlocks, ulong requiredBlocks)
        {
            var terminalOverflow = context.OverflowRecords.LastOrDefault();
            byte[] terminal = terminalOverflow?.Data ?? context.CatalogRecord.Data.AsSpan(context.ExtentOffset, 12).ToArray();
            int lastSlot = LastExtentSlot(terminal);
            ulong remaining = requiredBlocks - allocatedBlocks;
            uint adjacentGrowth = 0;
            if (lastSlot >= 0)
            {
                ushort lastStart = U16(terminal, lastSlot * 4);
                ushort lastCount = U16(terminal, lastSlot * 4 + 2);
                uint adjacent = (uint)lastStart + lastCount;
                while (adjacentGrowth < remaining && adjacentGrowth < ushort.MaxValue - lastCount &&
                    adjacent + adjacentGrowth < context.BlockCount &&
                    !IsAllocated(context.Bitmap, (ushort)(adjacent + adjacentGrowth))) adjacentGrowth++;
                if (adjacentGrowth > 0)
                {
                    WriteExtent(terminal, lastSlot, lastStart, checked((ushort)(lastCount + adjacentGrowth)));
                    context.Extents[^1] = (lastStart, checked((ushort)(lastCount + adjacentGrowth)));
                    for (uint block = adjacent; block < adjacent + adjacentGrowth; block++)
                        SetBitmap(context.Bitmap, (ushort)block, allocated: true);
                    remaining -= adjacentGrowth;
                }
            }

            var additions = remaining == 0 ? [] : AllocateRuns(context.Bitmap,
                checked((ushort)context.BlockCount), remaining, checked((int)context.BlockCount));
            int slot = LastExtentSlot(terminal) + 1;
            ulong nextForkBlock = allocatedBlocks + adjacentGrowth;
            uint allocatedRuns = 0;
            bool changedTree = false;
            byte[]? pendingOverflowExtents = null;
            byte pendingOverflowSlots = 0;
            ulong pendingOverflowBlockStart = 0;
            void FlushOverflowExtents()
            {
                if (pendingOverflowExtents is null) return;
                if (pendingOverflowBlockStart > ushort.MaxValue)
                    throw new InvalidDataException("The fork's extent start exceeds HFS's 16-bit FABN field.");
                var key = new byte[8];
                key[0] = 7;
                key[1] = context.ForkType;
                BinaryPrimitives.WriteUInt32BigEndian(key.AsSpan(2), context.FileId);
                BinaryPrimitives.WriteUInt16BigEndian(key.AsSpan(6), (ushort)pendingOverflowBlockStart);
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
                    WriteExtent(terminal, slot++, start, count);
                else
                {
                    if (pendingOverflowExtents is null)
                    {
                        pendingOverflowExtents = new byte[12];
                        pendingOverflowBlockStart = nextForkBlock;
                    }
                    WriteExtent(pendingOverflowExtents, pendingOverflowSlots++, start, count);
                    if (pendingOverflowSlots == 3) FlushOverflowExtents();
                }
                for (uint block = start; block < (uint)start + count; block++)
                    SetBitmap(context.Bitmap, checked((ushort)block), allocated: true);
                context.Extents.Add((start, count));
                allocatedRuns += count;
                nextForkBlock += count;
            }
            FlushOverflowExtents();
            if (terminalOverflow is null)
                terminal.CopyTo(context.CatalogRecord.Data, context.ExtentOffset);
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
                throw new InvalidDataException("The extents-overflow B-tree cannot grow beyond its addressable node count.");
            uint oldNodeCount = (uint)(context.ExtentsTree.Length / NodeSize);
            uint addedNodes = context.AllocationBlockSize / NodeSize;
            uint newNodeCount = oldNodeCount + addedNodes;
            var runs = AllocateRuns(context.Bitmap, checked((ushort)context.BlockCount), 1, 1);
            ushort newBlock = runs[0].Start;
            var extents = context.ExtentsTreeExtents;
            var last = extents[^1];
            if ((uint)last.Start + last.Count == newBlock && last.Count < ushort.MaxValue)
                extents[^1] = (last.Start, checked((ushort)(last.Count + 1)));
            else
            {
                if (extents.Count == 3)
                    throw new InvalidDataException("The extents-overflow file has no free primary extent descriptor for tree growth.");
                extents.Add((newBlock, 1));
            }
            SetBitmap(context.Bitmap, newBlock, allocated: true);
            byte[] grown = new byte[checked(context.ExtentsTree.Length + (int)context.AllocationBlockSize)];
            context.ExtentsTree.CopyTo(grown, 0);
            uint newMapNodes = ExtendBTreeNodeMap(grown, oldNodeCount, newNodeCount);
            BinaryPrimitives.WriteUInt32BigEndian(grown.AsSpan(14 + 22), newNodeCount);
            BinaryPrimitives.WriteUInt32BigEndian(grown.AsSpan(14 + 26),
                checked(U32(grown, 14 + 26) + addedNodes - newMapNodes));
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
                int lastSlot = LastExtentSlot(overflowRecord.Data);
                while (lastSlot >= 0 && excess > 0)
                {
                    ushort start = U16(overflowRecord.Data, lastSlot * 4);
                    ushort count = U16(overflowRecord.Data, lastSlot * 4 + 2);
                    uint giveBack = (uint)Math.Min(excess, (ulong)count);
                    for (uint block = (uint)start + count - giveBack; block < (uint)start + count; block++)
                        SetBitmap(context.Bitmap, checked((ushort)block), allocated: false);
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
                    DeleteExtentsRecord(context.ExtentsTree, overflowRecord.Key);
                else
                    UpdateTreeRecord(context.ExtentsTree, overflowRecord.Key, overflowRecord.Data);
                changedTree = true;
            }

            byte[] primary = context.CatalogRecord.Data.AsSpan(context.ExtentOffset, 12).ToArray();
            int primarySlot = LastExtentSlot(primary);
            while (primarySlot >= 0 && excess > 0)
            {
                ushort start = U16(primary, primarySlot * 4);
                ushort count = U16(primary, primarySlot * 4 + 2);
                uint giveBack = (uint)Math.Min(excess, (ulong)count);
                for (uint block = (uint)start + count - giveBack; block < (uint)start + count; block++)
                    SetBitmap(context.Bitmap, checked((ushort)block), allocated: false);
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
            if (excess != 0) throw new InvalidDataException("The HFS fork extent records do not cover the blocks being reclaimed.");
            primary.CopyTo(context.CatalogRecord.Data, context.ExtentOffset);
            return new ForkResizeResult(changedTree, 0, releasedBlocks);
        }

        private static IEnumerable<TreeRecord> LeafRecords(byte[] tree)
        {
            if (tree.Length < NodeSize) throw new InvalidDataException("An HFS B-tree is shorter than one node.");
            uint node = U32(tree, 14 + 10);
            var seen = new HashSet<uint>();
            while (node != 0)
            {
                if (node >= tree.Length / NodeSize || !seen.Add(node)) throw new InvalidDataException("An HFS B-tree leaf link is invalid or loops.");
                int nodeOffset = checked((int)node * NodeSize);
                var bytes = tree.AsSpan(nodeOffset, NodeSize).ToArray();
                if ((sbyte)bytes[8] != -1) throw new InvalidDataException("An HFS B-tree leaf chain links to a non-leaf node.");
                int count = U16(bytes, 10);
                if (count > (NodeSize - 14) / 2 - 1)
                    throw new InvalidDataException("An HFS B-tree node has too many records for its offset table.");
                for (int i = 0; i < count; i++)
                {
                    int start = U16(bytes, NodeSize - 2 * (i + 1));
                    int end = U16(bytes, NodeSize - 2 * (i + 2));
                    if (start < 14 || end > NodeSize - 2 * (count + 1) || end <= start)
                        throw new InvalidDataException("An HFS B-tree record has invalid offsets.");
                    int keyEnd = start + 1 + bytes[start];
                    int dataStart = (keyEnd + 1) & ~1;
                    if (dataStart > end) throw new InvalidDataException("An HFS B-tree key exceeds its record.");
                    yield return new TreeRecord(bytes[start..keyEnd], bytes[dataStart..end], nodeOffset, start, end);
                }
                node = U32(bytes, 0);
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
                throw new InvalidDataException("The extents-overflow record to update was not found at its current location.");
            int dataStart = (target.RecordStart + target.Key.Length + 1) & ~1;
            data.CopyTo(tree, target.NodeOffset + dataStart);
        }

        private static void DeleteExtentsRecord(byte[] tree, byte[] key)
        {
            var records = LeafRecords(tree).ToList();
            int recordIndex = records.FindIndex(record => CompareExtentsKeys(record.Key, key) == 0);
            if (recordIndex < 0) throw new InvalidDataException("The extents-overflow record to remove was not found.");
            records.RemoveAt(recordIndex);
            RebuildBTree(tree, records.Select(record => (record.Key, record.Data)).ToList(), validateExtents: true);
        }

        private static void RebuildBTree(byte[] tree, List<(byte[] Key, byte[] Data)> records, bool validateExtents)
        {
            uint nodeCount = U32(tree, 14 + 22);
            var reserved = new HashSet<uint> { 0 };
            var maps = new List<(int Offset, int Length)>
            {
                (U16(tree, NodeSize - 6), U16(tree, NodeSize - 8) - U16(tree, NodeSize - 6)),
            };
            uint mapNode = U32(tree, 0);
            while (mapNode != 0)
            {
                if (mapNode >= nodeCount || !reserved.Add(mapNode))
                    throw new InvalidDataException("The extents-overflow B-tree map-node chain is invalid.");
                int offset = checked((int)mapNode * NodeSize);
                int start = U16(tree, offset + NodeSize - 2);
                int end = U16(tree, offset + NodeSize - 4);
                maps.Add((offset + start, end - start));
                mapNode = U32(tree, offset);
            }

            var available = Enumerable.Range(1, checked((int)nodeCount - 1))
                .Select(number => (uint)number).Where(number => !reserved.Contains(number)).ToArray();
            var built = new List<(uint Number, byte[] FirstKey, byte[] Bytes)>();
            int nextNode = 0;
            uint AllocateNode()
            {
                if (nextNode == available.Length)
                    throw new BTreeNeedsNodesException();
                return available[nextNode++];
            }

            List<List<(byte[] Key, byte[] Data)>> Partition(List<(byte[] Key, byte[] Data)> entries)
            {
                var groups = new List<List<(byte[] Key, byte[] Data)>>();
                var group = new List<(byte[] Key, byte[] Data)>();
                foreach (var entry in entries)
                {
                    group.Add(entry);
                    if (TryBuildLeafNode(new byte[NodeSize], group, out _)) continue;
                    group.RemoveAt(group.Count - 1);
                    if (group.Count == 0)
                        throw new InvalidDataException("An extents-overflow record cannot fit in a B-tree node.");
                    groups.Add(group);
                    group = [entry];
                    if (!TryBuildLeafNode(new byte[NodeSize], group, out _))
                        throw new InvalidDataException("An extents-overflow record cannot fit in a B-tree node.");
                }
                if (group.Count > 0) groups.Add(group);
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
                        throw new InvalidDataException("An extents-overflow node could not be rebuilt.");
                    var item = (AllocateNode(), group[0].Key, node);
                    level.Add(item);
                    built.Add(item);
                }
                for (int index = 0; index < level.Count; index++)
                {
                    BinaryPrimitives.WriteUInt32BigEndian(level[index].Bytes.AsSpan(0),
                        index + 1 < level.Count ? level[index + 1].Number : 0);
                    BinaryPrimitives.WriteUInt32BigEndian(level[index].Bytes.AsSpan(4),
                        index > 0 ? level[index - 1].Number : 0);
                }
                return level;
            }

            byte[] rebuilt = tree.ToArray();
            var leaves = BuildLevel(records, 1);
            var levelNodes = leaves;
            byte depth = records.Count == 0 ? (byte)0 : (byte)1;
            while (levelNodes.Count > 1)
            {
                if (depth == 127) throw new InvalidDataException("The extents-overflow B-tree is too deep.");
                depth++;
                var entries = levelNodes.Select(node => (node.FirstKey, ChildNode(node.Number))).ToList();
                levelNodes = BuildLevel(entries, depth);
            }

            foreach (uint number in available)
                Array.Clear(rebuilt, checked((int)number * NodeSize), NodeSize);
            foreach (var node in built)
                node.Bytes.CopyTo(rebuilt, checked((int)node.Number * NodeSize));

            var allocated = new HashSet<uint>(reserved);
            allocated.UnionWith(built.Select(node => node.Number));
            for (uint number = 0; number < nodeCount; number++)
            {
                int mapByte = checked((int)(number >> 3));
                int bit = (int)(number & 7);
                foreach (var (offset, length) in maps)
                {
                    if (mapByte >= length) { mapByte -= length; continue; }
                    if (allocated.Contains(number)) rebuilt[offset + mapByte] |= (byte)(0x80 >> bit);
                    else rebuilt[offset + mapByte] &= (byte)~(0x80 >> bit);
                    break;
                }
            }

            BinaryPrimitives.WriteUInt16BigEndian(rebuilt.AsSpan(14), depth);
            BinaryPrimitives.WriteUInt32BigEndian(rebuilt.AsSpan(14 + 2), levelNodes.Count == 0 ? 0 : levelNodes[0].Number);
            BinaryPrimitives.WriteUInt32BigEndian(rebuilt.AsSpan(14 + 6), checked((uint)records.Count));
            BinaryPrimitives.WriteUInt32BigEndian(rebuilt.AsSpan(14 + 10), leaves.Count == 0 ? 0 : leaves[0].Number);
            BinaryPrimitives.WriteUInt32BigEndian(rebuilt.AsSpan(14 + 14), leaves.Count == 0 ? 0 : leaves[^1].Number);
            BinaryPrimitives.WriteUInt32BigEndian(rebuilt.AsSpan(14 + 26), nodeCount - checked((uint)allocated.Count));
            if (validateExtents) ValidateExtentsTree(rebuilt);
            rebuilt.CopyTo(tree, 0);
        }

        private static uint ExtendBTreeNodeMap(byte[] tree, uint oldNodeCount, uint newNodeCount)
        {
            int headerStart = U16(tree, NodeSize - 6);
            int headerEnd = U16(tree, NodeSize - 8);
            if (headerStart < 14 || headerEnd <= headerStart || headerEnd > NodeSize - 8)
                throw new InvalidDataException("The HFS B-tree header node map has invalid offsets.");
            var maps = new List<(int Offset, int Length)> { (headerStart, headerEnd - headerStart) };
            uint lastMapNode = 0;
            var seen = new HashSet<uint>();
            uint current = U32(tree, 0);
            while (current != 0)
            {
                if (current >= oldNodeCount || !seen.Add(current))
                    throw new InvalidDataException("The HFS B-tree map-node chain is invalid.");
                int at = checked((int)current * NodeSize);
                int start = U16(tree, at + NodeSize - 2);
                int end = U16(tree, at + NodeSize - 4);
                if (tree[at + 8] != 2 || start < 14 || end <= start || end > NodeSize - 4)
                    throw new InvalidDataException("The HFS B-tree map node has invalid offsets.");
                maps.Add((at + start, end - start));
                lastMapNode = current;
                current = U32(tree, at);
            }

            uint mapCapacity = checked((uint)maps.Sum(map => (long)map.Length * 8));
            if (mapCapacity < oldNodeCount)
                throw new InvalidDataException("The HFS B-tree node map does not cover its existing nodes.");
            var newMapNodes = new List<uint>();
            while (mapCapacity < newNodeCount)
            {
                uint number = mapCapacity;
                int at = checked((int)number * NodeSize);
                if (number < oldNodeCount || at + NodeSize > tree.Length)
                    throw new InvalidDataException("The HFS B-tree has no node available for another map record.");
                BinaryPrimitives.WriteUInt32BigEndian(tree.AsSpan(lastMapNode == 0 ? 0 : checked((int)lastMapNode * NodeSize)), number);
                tree[at + 8] = 2;
                BinaryPrimitives.WriteUInt32BigEndian(tree.AsSpan(at + 4), lastMapNode);
                BinaryPrimitives.WriteUInt16BigEndian(tree.AsSpan(at + 10), 1);
                BinaryPrimitives.WriteUInt16BigEndian(tree.AsSpan(at + NodeSize - 2), 14);
                BinaryPrimitives.WriteUInt16BigEndian(tree.AsSpan(at + NodeSize - 4), NodeSize - 6);
                maps.Add((at + 14, NodeSize - 20));
                newMapNodes.Add(number);
                lastMapNode = number;
                mapCapacity = checked(mapCapacity + (NodeSize - 20) * 8u);
            }

            for (uint number = oldNodeCount; number < newNodeCount; number++)
            {
                int mapByte = checked((int)(number >> 3));
                foreach (var (offset, length) in maps)
                {
                    if (mapByte >= length) { mapByte -= length; continue; }
                    tree[offset + mapByte] &= (byte)~(0x80 >> (int)(number & 7));
                    break;
                }
            }
            foreach (uint number in newMapNodes)
            {
                int mapByte = checked((int)(number >> 3));
                foreach (var (offset, length) in maps)
                {
                    if (mapByte >= length) { mapByte -= length; continue; }
                    tree[offset + mapByte] |= (byte)(0x80 >> (int)(number & 7));
                    break;
                }
            }
            return checked((uint)newMapNodes.Count);
        }

        private static void InsertExtentsRecord(byte[] tree, byte[] key, byte[] data)
        {
            if (key.Length != 8 || key[0] != 7 || data.Length != 12)
                throw new ArgumentException("An HFS extents-overflow record must have an 8-byte key and a 12-byte extent record.");
            var records = LeafRecords(tree).Select(record => (record.Key, record.Data)).ToList();
            int insertion = records.FindIndex(record => CompareExtentsKeys(record.Key, key) >= 0);
            if (insertion >= 0 && CompareExtentsKeys(records[insertion].Key, key) == 0)
                throw new InvalidDataException("The extents-overflow B-tree already contains this file extent key.");
            if (insertion < 0) insertion = records.Count;
            records.Insert(insertion, (key, data));
            RebuildBTree(tree, records, validateExtents: true);
        }

        private static List<(byte[] Key, byte[] Data)> ReadNodeRecords(byte[] tree, uint nodeNumber)
        {
            if (nodeNumber >= tree.Length / NodeSize) throw new InvalidDataException("An HFS B-tree node lies outside the tree file.");
            int nodeOffset = checked((int)nodeNumber * NodeSize);
            var bytes = tree.AsSpan(nodeOffset, NodeSize);
            int count = U16(bytes, 10);
            if (count > (NodeSize - 14) / 2 - 1)
                throw new InvalidDataException("An HFS B-tree node has too many records for its offset table.");
            var records = new List<(byte[] Key, byte[] Data)>(count);
            for (int i = 0; i < count; i++)
            {
                int start = U16(bytes, NodeSize - 2 * (i + 1));
                int end = U16(bytes, NodeSize - 2 * (i + 2));
                if (start < 14 || end > NodeSize - 2 * (count + 1) || end <= start)
                    throw new InvalidDataException("An HFS B-tree node has invalid record offsets.");
                int keyEnd = start + 1 + bytes[start];
                int dataStart = (keyEnd + 1) & ~1;
                if (keyEnd > end || dataStart > end)
                    throw new InvalidDataException("An HFS B-tree node record has an invalid key or data offset.");
                records.Add((bytes[start..keyEnd].ToArray(), bytes[dataStart..end].ToArray()));
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
            for (int i = 0; i < offsets.Count; i++)
                BinaryPrimitives.WriteUInt16BigEndian(rebuilt.AsSpan(NodeSize - 2 * (i + 1)), offsets[i]);
            BinaryPrimitives.WriteUInt16BigEndian(rebuilt.AsSpan(NodeSize - 2 * (offsets.Count + 1)), checked((ushort)at));
            BinaryPrimitives.WriteUInt16BigEndian(rebuilt.AsSpan(10), checked((ushort)offsets.Count));
            return true;
        }

        private static byte[] ChildNode(uint node)
        {
            var child = new byte[4];
            BinaryPrimitives.WriteUInt32BigEndian(child, node);
            return child;
        }

        private static void ValidateExtentsTree(byte[] tree) => ValidateBTree(tree, catalog: false);

        private static void ValidateCatalogTree(byte[] tree) => ValidateBTree(tree, catalog: true);

        private static void ValidateBTree(byte[] tree, bool catalog)
        {
            string treeKind = catalog ? "catalog" : "extents-overflow";
            int CompareKeys(byte[] left, byte[] right) => catalog
                ? CompareCatalogKeys(left, right) : CompareExtentsKeys(left, right);
            if (tree.Length < NodeSize || tree.Length % NodeSize != 0 || tree[8] != 1 || U16(tree, 10) != 3)
                throw new InvalidDataException($"The HFS {treeKind} B-tree has an invalid header node or length.");
            uint nodeCount = U32(tree, 14 + 22);
            if (nodeCount != (uint)(tree.Length / NodeSize) || U16(tree, 14 + 18) != NodeSize)
                throw new InvalidDataException($"The HFS {treeKind} B-tree header disagrees with its node file.");
            int mapStart = U16(tree, NodeSize - 6);
            int mapEnd = U16(tree, NodeSize - 8);
            if (U16(tree, NodeSize - 2) != 14 || U16(tree, NodeSize - 4) != 14 + 106 ||
                mapStart != 14 + 106 + 128 || mapEnd < mapStart || mapEnd > NodeSize - 8)
                throw new InvalidDataException($"The HFS {treeKind} B-tree header records have invalid offsets.");

            var mapRecords = new List<(int Offset, int Length)> { (mapStart, mapEnd - mapStart) };
            var usedNodes = new HashSet<uint> { 0 };
            uint mapNode = U32(tree, 0);
            uint previousMapNode = 0;
            while (mapNode != 0)
            {
                if (mapNode >= nodeCount || !usedNodes.Add(mapNode))
                    throw new InvalidDataException($"The HFS {treeKind} B-tree map-node chain is invalid.");
                int offset = checked((int)mapNode * NodeSize);
                if (tree[offset + 8] != 2 || U16(tree, offset + 10) != 1 ||
                    U32(tree, offset + 4) != previousMapNode)
                    throw new InvalidDataException($"The HFS {treeKind} B-tree has an invalid map node.");
                int start = U16(tree, offset + NodeSize - 2);
                int end = U16(tree, offset + NodeSize - 4);
                if (start < 14 || end <= start || end > NodeSize - 4)
                    throw new InvalidDataException($"The HFS {treeKind} B-tree map node has invalid record offsets.");
                mapRecords.Add((offset + start, end - start));
                previousMapNode = mapNode;
                mapNode = U32(tree, offset);
            }
            if (mapRecords.Aggregate<(int Offset, int Length), ulong>(0, (total, record) => total + (ulong)record.Length * 8) < nodeCount)
                throw new InvalidDataException($"The HFS {treeKind} B-tree node map is truncated.");

            var nodesByHeight = new Dictionary<int, List<uint>>();
            uint leafRecordCount = 0;
            ushort depth = U16(tree, 14);
            uint root = U32(tree, 14 + 2);
            if (depth == 0)
            {
                if (root != 0 || U32(tree, 14 + 6) != 0 || U32(tree, 14 + 10) != 0 || U32(tree, 14 + 14) != 0)
                    throw new InvalidDataException($"The empty HFS {treeKind} B-tree has inconsistent header fields.");
            }
            else
            {
                if (depth > 127 || root == 0) throw new InvalidDataException($"The HFS {treeKind} B-tree has an invalid root or depth.");
                Visit(root, depth);
                var leaves = nodesByHeight[1];
                if (U32(tree, 14 + 10) != leaves[0] || U32(tree, 14 + 14) != leaves[^1] ||
                    U32(tree, 14 + 6) != leafRecordCount)
                    throw new InvalidDataException($"The HFS {treeKind} B-tree leaf header disagrees with its nodes.");
            }

            foreach (var (height, nodes) in nodesByHeight)
            {
                for (int i = 0; i < nodes.Count; i++)
                {
                    int offset = checked((int)nodes[i] * NodeSize);
                    uint previous = i == 0 ? 0 : nodes[i - 1];
                    uint next = i + 1 == nodes.Count ? 0 : nodes[i + 1];
                    if (U32(tree, offset) != next || U32(tree, offset + 4) != previous)
                        throw new InvalidDataException($"The HFS {treeKind} B-tree level {height} has inconsistent sibling links.");
                }
            }

            uint actualFreeNodes = 0;
            for (uint node = 0; node < nodeCount; node++)
            {
                int mapByte = checked((int)(node >> 3));
                int bit = (int)(node & 7);
                bool found = false;
                foreach (var (offset, length) in mapRecords)
                {
                    if (mapByte >= length) { mapByte -= length; continue; }
                    bool allocated = (tree[offset + mapByte] & (0x80 >> bit)) != 0;
                    if (allocated != usedNodes.Contains(node))
                        throw new InvalidDataException($"The HFS {treeKind} B-tree node map disagrees with its node graph.");
                    if (!allocated) actualFreeNodes++;
                    found = true;
                    break;
                }
                if (!found) throw new InvalidDataException($"The HFS {treeKind} B-tree node map is truncated.");
            }
            if (actualFreeNodes != U32(tree, 14 + 26))
                throw new InvalidDataException($"The HFS {treeKind} B-tree free-node count disagrees with its map.");

            (byte[] First, byte[] Last) Visit(uint number, int height)
            {
                if (number == 0 || number >= nodeCount || !usedNodes.Add(number))
                    throw new InvalidDataException($"The HFS {treeKind} B-tree index graph is invalid or cyclic.");
                int offset = checked((int)number * NodeSize);
                if (tree[offset + 8] != (height == 1 ? (byte)0xFF : (byte)0) || tree[offset + 9] != height)
                    throw new InvalidDataException($"The HFS {treeKind} B-tree index has an invalid child type or height.");
                var records = ReadNodeRecords(tree, number);
                if (records.Count == 0) throw new InvalidDataException($"An active HFS {treeKind} B-tree node is empty.");
                if (!nodesByHeight.TryGetValue(height, out var peers)) nodesByHeight[height] = peers = [];
                peers.Add(number);
                byte[]? first = null, last = null;
                foreach (var (key, data) in records)
                {
                    bool validKey = catalog
                        ? key.Length >= 7 && key[0] == key.Length - 1 &&
                          (key.Length == 7 + key[6] ||
                           (key.Length == ((8 + key[6]) & ~1) && key[^1] == 0))
                        : key.Length == 8 && key[0] == 7 && key[1] is 0 or 0xFF;
                    if (!validKey)
                        throw new InvalidDataException($"An HFS {treeKind} B-tree record has an invalid key.");
                    if (height == 1)
                    {
                        bool validData = catalog
                            ? data.Length >= 2 && data[0] switch
                            {
                                1 => data.Length >= 70,
                                2 => data.Length >= 102,
                                3 or 4 => data.Length >= 46,
                                _ => false,
                            }
                            : data.Length == 12;
                        if (!validData || (last is not null && CompareKeys(last, key) >= 0))
                            throw new InvalidDataException($"The HFS {treeKind} leaf records are invalid or out of order.");
                        first ??= key;
                        last = key;
                        leafRecordCount = checked(leafRecordCount + 1);
                    }
                    else
                    {
                        if (data.Length != 4) throw new InvalidDataException($"An HFS {treeKind} index record has an invalid child pointer.");
                        var child = Visit(U32(data, 0), height - 1);
                        if (CompareKeys(key, child.First) != 0 ||
                            (last is not null && CompareKeys(last, child.First) >= 0))
                            throw new InvalidDataException($"An HFS {treeKind} index key disagrees with its child.");
                        first ??= child.First;
                        last = child.Last;
                    }
                }
                return (first!, last!);
            }
        }

        private static int CompareExtentsKeys(byte[] left, byte[] right)
        {
            if (left.Length < 8 || right.Length < 8) throw new InvalidDataException("An extents-overflow key is shorter than eight bytes.");
            int order = U32(left, 2).CompareTo(U32(right, 2));
            if (order != 0) return order;
            order = left[1].CompareTo(right[1]);
            return order != 0 ? order : U16(left, 6).CompareTo(U16(right, 6));
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
                foreach (var record in extentsOverflow.OrderBy(r => r.Start)) Claim(ParseExtents(record.Extents), "extents-overflow file");

            foreach (var record in catalogRecords)
            {
                if (record.Key.Length < 7 || record.Data.Length < 102 || record.Data[0] != 2) continue;
                uint id = U32(record.Data, 20);
                foreach (var (kind, offset) in new[] { ((byte)0, 74), ((byte)0xFF, 86) })
                {
                    if (ReferenceEquals(record, target) && id == targetFileId && offset == targetForkOffset) continue;
                    var fileExtents = ParseExtents(record.Data.AsSpan(offset, 12));
                    Claim(fileExtents, "file fork");
                    if (overflow.TryGetValue((kind, id), out var fileOverflow))
                        foreach (var extra in fileOverflow.OrderBy(r => r.Start)) Claim(ParseExtents(extra.Extents), "file fork");
                }
            }
            Claim(targetExtents, "target fork");

            void Claim(IEnumerable<(ushort Start, ushort Count)> extents, string owner)
            {
                foreach (var (start, count) in extents)
                {
                    if ((uint)start + count > blockCount)
                        throw new InvalidDataException($"An extent of the {owner} lies outside the HFS allocation area.");
                    for (uint block = start; block < (uint)start + count; block++)
                    {
                        ushort index = (ushort)block;
                        if (!IsAllocated(bitmap, index)) throw new InvalidDataException($"An extent of the {owner} points to a free allocation block.");
                        if (!claimed.Add(index)) throw new InvalidDataException($"An allocation block is shared by multiple HFS extents ({owner}).");
                    }
                }
            }
        }

        private static byte[] ReadFork(byte[] image, uint firstBlock, uint blockSize, uint blockCount,
            List<(ushort Start, ushort Count)> firstExtents, uint logicalLength,
            Dictionary<(byte Fork, uint File), List<(ushort Start, byte[] Extents)>> overflow,
            byte overflowFork, uint fileId, bool includeOverflow)
        {
            var extents = firstExtents.ToList();
            if (includeOverflow && overflow.TryGetValue((overflowFork, fileId), out var extra))
                extents.AddRange(extra.OrderBy(x => x.Start).SelectMany(x => ParseExtents(x.Extents)));
            ulong capacity = extents.Aggregate<(ushort Start, ushort Count), ulong>(0, (n, e) => n + (ulong)e.Count * blockSize);
            if (logicalLength > capacity || logicalLength > int.MaxValue) throw new InvalidDataException("An HFS system fork has invalid length or extents.");
            var output = new byte[(int)logicalLength];
            int written = 0;
            foreach (var (start, count) in extents)
            {
                if ((uint)start + count > blockCount) throw new InvalidDataException("An HFS extent lies outside the allocation area.");
                int take = (int)Math.Min((ulong)(output.Length - written), (ulong)count * blockSize);
                long sourceOffset = (long)firstBlock + (long)start * blockSize;
                if (sourceOffset < 0 || sourceOffset + take > image.Length) throw new InvalidDataException("An HFS extent lies outside the image.");
                image.AsSpan((int)sourceOffset, take).CopyTo(output.AsSpan(written));
                written += take;
                if (written == output.Length) break;
            }
            return output;
        }

        private static List<(ushort Start, ushort Count)> ParseExtents(ReadOnlySpan<byte> bytes)
        {
            var result = new List<(ushort, ushort)>();
            for (int i = 0; i < 3; i++)
            {
                ushort start = U16(bytes, i * 4), count = U16(bytes, i * 4 + 2);
                if (count != 0) result.Add((start, count));
            }
            return result;
        }

        private static int LastExtentSlot(ReadOnlySpan<byte> record)
        {
            int last = -1;
            bool emptySeen = false;
            for (int slot = 0; slot < 3; slot++)
            {
                ushort start = U16(record, slot * 4), count = U16(record, slot * 4 + 2);
                if (count == 0)
                {
                    if (start != 0) throw new InvalidDataException("An empty HFS extent descriptor has a nonzero start block.");
                    emptySeen = true;
                }
                else
                {
                    if (emptySeen) throw new InvalidDataException("An HFS extent record has a populated descriptor after an empty slot.");
                    last = slot;
                }
            }
            return last;
        }

        private static void WriteExtent(Span<byte> record, int slot, ushort start, ushort count)
        {
            BinaryPrimitives.WriteUInt16BigEndian(record.Slice(slot * 4, 2), start);
            BinaryPrimitives.WriteUInt16BigEndian(record.Slice(slot * 4 + 2, 2), count);
        }

        private static List<(ushort Start, ushort Count)> AllocateRuns(byte[] bitmap, ushort blockCount, ulong required, int maxRuns)
        {
            var freeRuns = new List<(ushort Start, ushort Count)>();
            int runStart = -1;
            for (int block = 0; block <= blockCount; block++)
            {
                bool free = block < blockCount && !IsAllocated(bitmap, (ushort)block);
                if (free && runStart < 0) runStart = block;
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
                if (remaining == 0) break;
                if (chosen.Count == maxRuns) break;
                ushort take = (ushort)Math.Min((ulong)run.Count, remaining);
                chosen.Add((run.Start, take));
                remaining -= take;
            }
            if (remaining != 0)
                throw new InvalidDataException("There are not enough free HFS allocation blocks in the fork's available extent slots.");
            chosen.Sort((left, right) => left.Start.CompareTo(right.Start));
            return chosen;
        }

        private static bool IsAllocated(byte[] bitmap, ushort block)
        {
            int index = block >> 3;
            if (index >= bitmap.Length) throw new InvalidDataException("The HFS volume bitmap is too short.");
            return (bitmap[index] & (0x80 >> (block & 7))) != 0;
        }

        private static void SetBitmap(byte[] bitmap, ushort block, bool allocated)
        {
            int index = block >> 3;
            if (index >= bitmap.Length) throw new InvalidDataException("The HFS volume bitmap is too short.");
            byte mask = (byte)(0x80 >> (block & 7));
            if (allocated) bitmap[index] |= mask;
            else bitmap[index] &= (byte)~mask;
        }

        private static void WriteFork(byte[] image, uint firstBlock, uint blockSize, List<(ushort Start, ushort Count)> extents, ReadOnlySpan<byte> data)
        {
            int at = 0;
            foreach (var (start, count) in extents)
            {
                int take = Math.Min(data.Length - at, checked((int)((uint)count * blockSize)));
                if (take <= 0) break;
                int offset = checked((int)(firstBlock + (uint)start * blockSize));
                data.Slice(at, take).CopyTo(image.AsSpan(offset, take));
                at += take;
            }
            if (at != data.Length) throw new InvalidDataException("The HFS extents cannot hold the data to write.");
        }

        private static void ClearForkTail(byte[] image, uint firstBlock, uint blockSize, List<(ushort Start, ushort Count)> extents, int dataLength)
        {
            long skip = dataLength;
            foreach (var (start, count) in extents)
            {
                int bytes = checked((int)((uint)count * blockSize));
                int offset = checked((int)(firstBlock + (uint)start * blockSize));
                int clearFrom = (int)Math.Clamp(skip, 0, bytes);
                if (clearFrom < bytes) image.AsSpan(offset + clearFrom, bytes - clearFrom).Clear();
                skip -= bytes;
            }
        }

        private static List<(ushort Start, ushort Count)> WithOverflow(List<(ushort Start, ushort Count)> initial,
            Dictionary<(byte Fork, uint File), List<(ushort Start, byte[] Extents)>> overflow, byte fork, uint fileId)
        {
            var result = initial.ToList();
            if (overflow.TryGetValue((fork, fileId), out var values))
                result.AddRange(values.OrderBy(x => x.Start).SelectMany(x => ParseExtents(x.Extents)));
            return result;
        }

        private static void Verify(byte[] source, byte[] result, string targetPath, HfsFork changedFork, ReadOnlySpan<byte> expected,
            DateTime writeTime, uint newlyAllocatedBlocks, uint releasedBlocks)
        {
            uint macWriteTime = MacDate.FromDateTime(writeTime).Seconds;
            uint previousWriteCount = U32(source, MdbOffset + 0x46);
            ushort previousFreeBlocks = U16(source, MdbOffset + 0x22);
            ushort expectedFreeBlocks = checked((ushort)((uint)previousFreeBlocks - newlyAllocatedBlocks + releasedBlocks));
            if (U32(result, MdbOffset + 0x06) != macWriteTime || U32(result, MdbOffset + 0x46) != unchecked(previousWriteCount + 1) ||
                U16(result, MdbOffset + 0x22) != expectedFreeBlocks)
                throw new InvalidDataException("The rewritten HFS volume metadata did not record its modification and allocation changes.");
            var beforeDiagnostics = new List<Diagnostic>();
            var afterDiagnostics = new List<Diagnostic>();
            var before = HfsReader.Instance.Read(ForkData.FromBytes(source), new ContainerContext(diagnostics: beforeDiagnostics));
            var after = HfsReader.Instance.Read(ForkData.FromBytes(result), new ContainerContext(diagnostics: afterDiagnostics));
            if (beforeDiagnostics.Any(d => d.Severity == DiagnosticSeverity.Error) || afterDiagnostics.Any(d => d.Severity == DiagnosticSeverity.Error))
                throw new InvalidDataException("The original or rewritten HFS volume has structural errors; the edit was not verified.");
            var oldFiles = before.ToDictionary(f => f.MacPath, StringComparer.Ordinal);
            var newFiles = after.ToDictionary(f => f.MacPath, StringComparer.Ordinal);
            if (oldFiles.Count != newFiles.Count || oldFiles.Keys.Any(path => !newFiles.ContainsKey(path)))
                throw new InvalidDataException("The rewritten HFS volume did not preserve its file list.");
            foreach (var (path, oldFile) in oldFiles)
            {
                var newFile = newFiles[path];
                bool target = StringComparer.Ordinal.Equals(path, targetPath);
                if (!SameMetadata(oldFile, newFile, includeModified: !target) ||
                    (target && newFile.Modified != new MacDate(macWriteTime)))
                    throw new InvalidDataException($"The rewritten HFS volume changed metadata incorrectly for '{path}'.");
                if (target)
                {
                    var changed = changedFork == HfsFork.Data ? newFile.DataFork : newFile.ResourceFork;
                    var unchanged = changedFork == HfsFork.Data ? newFile.ResourceFork : newFile.DataFork;
                    var oldUnchanged = changedFork == HfsFork.Data ? oldFile.ResourceFork : oldFile.DataFork;
                    if (!changed.ToArray().AsSpan().SequenceEqual(expected) ||
                        !unchanged.ToArray().AsSpan().SequenceEqual(oldUnchanged.ToArray()))
                        throw new InvalidDataException($"The rewritten HFS volume did not preserve both forks for '{path}'.");
                }
                else if (!oldFile.DataFork.ToArray().AsSpan().SequenceEqual(newFile.DataFork.ToArray()) ||
                    !oldFile.ResourceFork.ToArray().AsSpan().SequenceEqual(newFile.ResourceFork.ToArray()))
                    throw new InvalidDataException($"The rewritten HFS volume changed a fork in unrelated file '{path}'.");
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
                if (!seen.Add(parent) || !folders.TryGetValue(parent, out var folder)) return "";
                parts.Insert(0, folder.Name);
                parent = folder.Parent;
            }
            return string.Join(':', parts);
        }

        private static void ValidateBitmapFreeCount(uint blockCount, byte[] bitmap, ushort expectedFree)
        {
            uint actualFree = 0;
            for (uint block = 0; block < blockCount; block++)
                if (!IsAllocated(bitmap, checked((ushort)block))) actualFree++;
            if (actualFree != expectedFree)
                throw new InvalidDataException("The HFS volume free-block count disagrees with its allocation bitmap.");
        }

        private static bool HfsPathEquals(string left, string right)
        {
            string[] leftParts = left.Split(':');
            string[] rightParts = right.Split(':');
            if (leftParts.Length != rightParts.Length) return false;
            for (int index = 0; index < leftParts.Length; index++)
            {
                if (CompareCatalogKeys(CatalogKey(0, leftParts[index]), CatalogKey(0, rightParts[index])) != 0)
                    return false;
            }
            return true;
        }

        private static string DecodeName(byte[] key) => key.Length >= 7 ? new MacString(key.AsSpan(7, Math.Min(key[6], key.Length - 7))).ToString() : "";
        private static ushort U16(ReadOnlySpan<byte> b, int o) => BinaryPrimitives.ReadUInt16BigEndian(b[o..]);
        private static uint U32(ReadOnlySpan<byte> b, int o) => BinaryPrimitives.ReadUInt32BigEndian(b[o..]);
    }

    /// <summary>The fork selected for an HFS file edit.</summary>
    public enum HfsFork
    {
        /// <summary>The data fork.</summary>
        Data,
        /// <summary>The resource fork.</summary>
        Resource,
    }
}
