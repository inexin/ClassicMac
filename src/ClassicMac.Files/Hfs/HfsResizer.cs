using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using ClassicMac.Core;
using static ClassicMac.Files.Hfs.HfsWriter;
using static ClassicMac.Files.Hfs.HfsCatalogEditing;
using static ClassicMac.Files.Hfs.HfsAllocation;
using static ClassicMac.Files.Hfs.HfsBTreeWriting;
using static ClassicMac.Files.Hfs.HfsRecords;

namespace ClassicMac.Files.Hfs;

// HfsWriter.Resize and Defragment: a volume grown (past 65,535 blocks with a new block size), shrunk, or laid out again
// in its own geometry (hfs.md §3.2–§3.4).
internal static class HfsResizer
{
    internal static byte[] Resize(ForkData image, long size)
    {
        ArgumentNullException.ThrowIfNull(image);
        if (size % BlockSize != 0 || size <= 0 || size > MaximumFormatSize)
        {
            throw new ArgumentOutOfRangeException(nameof(size), "An HFS volume is whole 512-byte blocks, up to just under 2 GB.");
        }

        var state = OpenCatalog(image);
        var source = state.Source.ToArray();
        if (size < source.Length)
        {
            return Shrink(state, source, size);
        }

        if (size == source.Length)
        {
            throw new InvalidDataException($"The HFS volume is {size} bytes already.");
        }

        var mdb = new BigEndianReader(source.AsMemory(MdbOffset, MdbSize));
        long factor = state.BlockSize / BlockSize;
        long bitmapStart = mdb.ReadUInt16At(0x0E);
        long oldStart = mdb.ReadUInt16At(0x1C);
        long sectors = size / BlockSize;

        // The new block count, with the bitmap grown into the allocation area when its sectors cannot cover it.
        long start = oldStart, count;
        while (true)
        {
            count = (sectors - start - 2) / factor;
            if ((start - bitmapStart) * 4096 >= count)
            {
                break;
            }

            start++;
        }

        if (count > ushort.MaxValue)
        {
            // The layout Mac OS 9.0's initializer gives the new size (§3.1): its trees' sizes are the least they keep.
            var template = HfsFormatter.FormatVolume(size, "Untitled").ToArray();
            var layout = new BigEndianReader(template.AsMemory(MdbOffset, MdbSize));
            return Relayout(state, source, template, layout.ReadUInt16At(0x88), layout.ReadUInt16At(0x98));
        }

        long oldCount = state.BlockCount;
        var result = new byte[size];
        source.AsSpan(0, (int)(oldStart * BlockSize)).CopyTo(result);
        long areaBytes = oldCount * state.BlockSize;
        source.AsSpan((int)(oldStart * BlockSize), (int)areaBytes).CopyTo(result.AsSpan((int)(start * BlockSize)));
        var bitmapBytes = (int)((oldCount + 7) / 8);
        result.AsSpan((int)(bitmapStart * BlockSize + bitmapBytes), (int)((start - bitmapStart) * BlockSize - bitmapBytes)).Clear();
        source.AsSpan((int)(bitmapStart * BlockSize), bitmapBytes).CopyTo(result.AsSpan((int)(bitmapStart * BlockSize)));
        for (long block = oldCount; block < (bitmapBytes * 8L); block++)
        {
            result[bitmapStart * BlockSize + block / 8] &= (byte)~(0x80 >> (int)(block % 8));
        }

        var writer = new BigEndianWriter(result);
        writer.WriteUInt16At(MdbOffset + 0x12, count);                                // drNmAlBlks
        writer.WriteUInt16At(MdbOffset + 0x1C, start);                                // drAlBlSt
        writer.WriteUInt16At(MdbOffset + 0x22, mdb.ReadUInt16At(0x22) + (count - oldCount));   // drFreeBks
        writer.WriteUInt32At(MdbOffset + 0x06, MacDate.FromDateTime(Now).Seconds);   // drLsMod
        writer.WriteUInt32At(MdbOffset + 0x46, unchecked(mdb.ReadUInt32At(0x46) + 1));        // drWrCnt
        result.AsSpan(MdbOffset, BlockSize).CopyTo(result.AsSpan((int)(size - 2 * BlockSize)));

        // Checked as a deletion is: the result opens as the writer opens a volume, its records are the source's, and every
        // allocated block is the source's, read where the allocation area now starts.
        var after = OpenCatalog(ForkData.FromBytes(result), writable: false);
        if (after.Records.Count != state.Records.Count ||
            after.Records.Zip(state.Records).Any(pair => !pair.First.Key.AsSpan().SequenceEqual(pair.Second.Key) || !pair.First.Data.AsSpan().SequenceEqual(pair.Second.Data)))
        {
            throw new InvalidDataException("The grown HFS volume's catalog differs from the source's.");
        }

        for (uint block = 0; block < oldCount; block++)
        {
            if (IsAllocated(state.Bitmap, (ushort)block) &&
                !source.AsSpan((int)(oldStart * BlockSize + block * state.BlockSize), (int)state.BlockSize)
                    .SequenceEqual(result.AsSpan((int)(start * BlockSize + block * state.BlockSize), (int)state.BlockSize)))
            {
                throw new InvalidDataException("The grown HFS volume changed a block in use.");
            }
        }

        return result;
    }

    // A shrink (hfs.md §3.3): the allocation area keeps its start, so the bitmap keeps its sectors; every extent that
    // reaches past the new last block (a fork's, an overflow record's, the B-trees' own) moves whole to the first free run
    // below it that holds it, its descriptor rewritten where it lies (the catalog record, the extents record, the MDB).
    private static byte[] Shrink(CatalogEditState state, byte[] source, long size)
    {
        var mdb = new BigEndianReader(source.AsMemory(MdbOffset, MdbSize));
        if (mdb.ReadUInt16At(0x7C) == 0x482B)
        {
            throw new InvalidDataException("An HFS wrapper around an HFS Plus volume is not shrunk.");
        }

        long factor = state.BlockSize / BlockSize;
        long start = mdb.ReadUInt16At(0x1C);
        long count = (size / BlockSize - start - 2) / factor;
        if (count <= 0)
        {
            throw new InvalidDataException($"An HFS volume of {size} bytes has no room for its allocation blocks.");
        }

        long used = Enumerable.Range(0, (int)state.BlockCount).LongCount(b => IsAllocated(state.Bitmap, (ushort)b));
        if (used > count)
        {
            throw new InvalidDataException($"The HFS volume has {used} allocation blocks in use; {size} bytes hold {count}.");
        }

        // Every extent descriptor, where it lies: the MDB's two trees, each file record's two forks, each overflow record.
        var mdbBytes = source.AsSpan(MdbOffset, MdbSize).ToArray();
        var catalog = state.Catalog.ToArray();
        var extentsTree = state.ExtentsTree.ToArray();
        var descriptors = new List<(byte[] Bytes, int Offset)>();
        for (var slot = 0; slot < 3; slot++)
        {
            descriptors.Add((mdbBytes, 0x86 + slot * 4));
            descriptors.Add((mdbBytes, 0x96 + slot * 4));
        }

        foreach (var record in LeafRecords(catalog).Where(r => r.Data.Length >= 0x62 && r.Data[0] == 2))
        {
            int data = DataOffset(catalog, record);
            for (var slot = 0; slot < 3; slot++)
            {
                descriptors.Add((catalog, data + 0x4A + slot * 4));
                descriptors.Add((catalog, data + 0x56 + slot * 4));
            }
        }

        foreach (var record in LeafRecords(extentsTree))
        {
            int data = DataOffset(extentsTree, record);
            bool badBlocks = KeyId(record.Key) == 5;
            for (var slot = 0; slot < 3; slot++)
            {
                if (badBlocks && Extent(extentsTree, data + slot * 4) is var (s, c) && c > 0 && s + c > count)
                {
                    throw new InvalidDataException("Bad blocks past the new end cannot be moved.");
                }

                descriptors.Add((extentsTree, data + slot * 4));
            }
        }

        // The moves: each extent past the new end freed, then given the first free run below the end that holds it whole.
        var bitmap = state.Bitmap.ToArray();
        var moves = new List<(long From, long To, long Count)>();
        foreach (var (bytes, offset) in descriptors)
        {
            var (from, blocks) = Extent(bytes, offset);
            if (blocks == 0 || from + blocks <= count)
            {
                continue;
            }

            for (long b = from; b < from + blocks; b++)
            {
                SetBitmap(bitmap, (ushort)b, false);
            }

            long to = FreeRun(bitmap, count, blocks)
                ?? throw new InvalidDataException($"No run of {blocks} free allocation blocks below the new end holds an extent in use; defragment the volume first (defrag).");
            for (long b = to; b < to + blocks; b++)
            {
                SetBitmap(bitmap, (ushort)b, true);
            }

            var writer = new BigEndianWriter(bytes);
            writer.WriteUInt16At(offset, to);
            moves.Add((from, to, blocks));
        }

        // The image: the source up to the new end, each moved extent's blocks copied to their new place, the trees as
        // rewritten across their extents, the bitmap, the MDB and its copy.
        var result = new byte[size];
        long areaEnd = start * BlockSize + count * state.BlockSize;
        source.AsSpan(0, (int)areaEnd).CopyTo(result);
        foreach (var (from, to, blocks) in moves)
        {
            source.AsSpan((int)(start * BlockSize + from * state.BlockSize), (int)(blocks * state.BlockSize))
                .CopyTo(result.AsSpan((int)(start * BlockSize + to * state.BlockSize)));
        }

        var mdbWriter = new BigEndianWriter(mdbBytes);
        var overflow = LeafRecords(extentsTree).ToList();
        WriteTree(result, start, state.BlockSize, extentsTree, Extents(mdbBytes, 0x86, overflow, 3));
        WriteTree(result, start, state.BlockSize, catalog, Extents(mdbBytes, 0x96, overflow, 4));
        int bitmapOffset = mdb.ReadUInt16At(0x0E) * BlockSize;
        result.AsSpan(bitmapOffset, (int)(start * BlockSize - bitmapOffset)).Clear();
        bitmap.AsSpan(0, (int)((count + 7) / 8)).CopyTo(result.AsSpan(bitmapOffset));
        for (long block = count; block < (count + 7) / 8 * 8; block++)
        {
            result[bitmapOffset + block / 8] &= (byte)~(0x80 >> (int)(block % 8));
        }

        mdbWriter.WriteUInt16At(0x12, count);                                          // drNmAlBlks
        mdbWriter.WriteUInt16At(0x22, count - used);                                   // drFreeBks
        if (mdb.ReadUInt16At(0x10) >= count)
        {
            mdbWriter.WriteUInt16At(0x10, 0);                                          // drAllocPtr
        }

        mdbWriter.WriteUInt32At(0x06, MacDate.FromDateTime(Now).Seconds);              // drLsMod
        mdbWriter.WriteUInt32At(0x46, unchecked(mdb.ReadUInt32At(0x46) + 1));                  // drWrCnt
        mdbBytes.CopyTo(result.AsSpan(MdbOffset));
        result.AsSpan(MdbOffset, BlockSize).CopyTo(result.AsSpan((int)(size - 2 * BlockSize)));

        // Checked as a growth is, and by its files: the result opens as the writer opens a volume, its catalog is the
        // source's but for the extents moved, and every file reads back as the source's.
        if (HfsWriter.Check(ForkData.FromBytes(result)) is { } fault)
        {
            throw new InvalidDataException($"The shrunk HFS volume does not pass the writer's checks: {fault}");
        }

        var before = HfsReader.Instance.Read(state.Source.AsForkData(), new ContainerContext());
        var after = HfsReader.Instance.Read(ForkData.FromBytes(result), new ContainerContext());
        if (before.Count != after.Count || before.Zip(after).Any(pair => pair.First.MacPath != pair.Second.MacPath
                || !pair.First.DataFork.ToArray().AsSpan().SequenceEqual(pair.Second.DataFork.ToArray())
                || !pair.First.ResourceFork.ToArray().AsSpan().SequenceEqual(pair.Second.ResourceFork.ToArray())))
        {
            throw new InvalidDataException("The shrunk HFS volume's files differ from the source's.");
        }

        return result;
    }

    // Where a leaf record's data lies in its tree's bytes.
    private static int DataOffset(byte[] tree, TreeRecord record)
    {
        int at = record.NodeOffset + record.RecordEnd - record.Data.Length;
        if (!tree.AsSpan(at, record.Data.Length).SequenceEqual(record.Data))
        {
            throw new InvalidDataException("An HFS B-tree record is not where its offsets put it.");
        }

        return at;
    }

    private static (long Start, long Count) Extent(byte[] bytes, int offset)
    {
        var reader = new BigEndianReader(bytes);
        return (reader.ReadUInt16At(offset), reader.ReadUInt16At(offset + 2));
    }

    // The first run of free blocks below the end that holds count, or null.
    private static long? FreeRun(byte[] bitmap, long end, long count)
    {
        long run = 0;
        for (long block = 0; block < end; block++)
        {
            run = IsAllocated(bitmap, (ushort)block) ? 0 : run + 1;
            if (run == count)
            {
                return block - count + 1;
            }
        }

        return null;
    }

    // A tree file's extents: its three in the MDB, then its overflow records' in key order.
    private static List<(long Start, long Count)> Extents(byte[] mdb, int offset, List<TreeRecord> overflow, uint fileId)
    {
        var extents = new List<(long, long)>();
        for (var slot = 0; slot < 3; slot++)
        {
            extents.Add(Extent(mdb, offset + slot * 4));
        }

        foreach (var record in overflow.Where(r => r.Key[1] == 0 && KeyId(r.Key) == fileId))
        {
            for (var slot = 0; slot < 3; slot++)
            {
                extents.Add(Extent(record.Data, slot * 4));
            }
        }

        return extents;
    }

    // A tree file's bytes written across its extents.
    private static void WriteTree(byte[] image, long start, uint blockSize, byte[] tree, List<(long Start, long Count)> extents)
    {
        long written = 0;
        foreach (var (first, blocks) in extents)
        {
            if (written >= tree.Length)
            {
                break;
            }

            var length = (int)Math.Min(blocks * blockSize, tree.Length - written);
            tree.AsSpan((int)written, length).CopyTo(image.AsSpan((int)(start * BlockSize + first * blockSize)));
            written += length;
        }

        if (written < tree.Length)
        {
            throw new InvalidDataException("An HFS B-tree file is longer than its extents.");
        }
    }

    // SmallestSize (hfs.md §3.3): the allocation area's start, the blocks in use, the alternate MDB and the spare sector.
    internal static long SmallestSize(ForkData image)
    {
        ArgumentNullException.ThrowIfNull(image);
        var state = OpenCatalog(image, writable: false);
        long used = 0;
        for (uint block = 0; block < state.BlockCount; block++)
        {
            used += IsAllocated(state.Bitmap, (ushort)block) ? 1 : 0;
        }

        return state.FirstBlock + used * state.BlockSize + 2 * BlockSize;
    }

    // Defragment (hfs.md §3.4): the volume laid out again in its own size and geometry, its extents tree as long as before
    // but empty, from block 0.
    internal static byte[] Defragment(ForkData image)
    {
        ArgumentNullException.ThrowIfNull(image);
        var state = OpenCatalog(image);
        var source = state.Source.ToArray();
        var mdb = new BigEndianReader(source.AsMemory(MdbOffset, MdbSize));
        long start = mdb.ReadUInt16At(0x1C);
        long extentsBytes = mdb.ReadUInt32At(0x82);
        var template = new byte[source.Length];
        source.AsSpan(0, (int)(start * BlockSize)).CopyTo(template);
        var writer = new BigEndianWriter(template);
        template.AsSpan(MdbOffset + 0x86, 12).Clear();
        writer.WriteUInt16At(MdbOffset + 0x86, 0);                                      // drXTExtRec: block 0
        writer.WriteUInt16At(MdbOffset + 0x88, extentsBytes / state.BlockSize);
        var tree = EmptyTree(extentsBytes);
        state.ExtentsTree.AsSpan(0x2E, 4).CopyTo(tree.AsSpan(0x2E));                    // the header's clump size
        tree.CopyTo(template, start * BlockSize);
        return Relayout(state, source, template, extentsBytes / state.BlockSize, state.Catalog.Length / state.BlockSize);
    }

    // An empty extents tree of this many bytes, its node map extended past the header's when it is that long.
    private static byte[] EmptyTree(long bytes)
    {
        long first = Math.Min(bytes, 2048L * NodeSize);
        var tree = HfsFormatter.EmptyTree(first, 7);
        RebuildBTree(tree, [], validateExtents: true);
        if (first == bytes)
        {
            return tree;
        }

        return GrowBTree(tree, bytes);
    }

    // The volume laid out again over a template (hfs.md §3.2, §3.4): the template's MDB gives the geometry, clump sizes
    // and the extents tree's place (an empty tree written at block 0); the catalog's nodes are kept (grown to whole blocks
    // and at least catalogBlocks) after it, each file's forks given one extent each in turn after that, and the source
    // MDB's other fields kept.
    private static byte[] Relayout(CatalogEditState state, byte[] source, byte[] result, long extentsBlocks, long leastCatalogBlocks)
    {
        long size = result.Length;
        var mdb = new BigEndianReader(source.AsMemory(MdbOffset, MdbSize));
        if (mdb.ReadUInt16At(0x7C) == 0x482B)
        {
            throw new InvalidDataException("An HFS wrapper around an HFS Plus volume is not resized past 65,535 blocks.");
        }

        if (LeafRecords(state.ExtentsTree).Any(r => KeyId(r.Key) == 5))
        {
            throw new InvalidDataException("A volume with bad blocks is not laid out again.");
        }

        var layout = new BigEndianReader(result.AsMemory(MdbOffset, MdbSize));
        uint blockSize = layout.ReadUInt32At(0x14);
        long start = layout.ReadUInt16At(0x1C), count = layout.ReadUInt16At(0x12), bitmapStart = layout.ReadUInt16At(0x0E);

        // The catalog: its nodes, grown to whole new blocks and at least the initializer's catalog, the new nodes free.
        var catalog = state.Catalog;
        long catalogBlocks = Math.Max(leastCatalogBlocks, (catalog.Length + blockSize - 1) / blockSize);
        catalog = catalogBlocks * blockSize > catalog.Length ? GrowBTree(catalog, catalogBlocks * blockSize) : catalog.ToArray();

        // Each file's forks in one extent each, in catalog order, after the trees.
        var files = HfsReader.Instance.Read(state.Source.AsForkData(), new ContainerContext()).ToDictionary(f => f.CatalogId!.Value);
        long next = extentsBlocks + catalogBlocks;
        long areaStart = start * BlockSize;
        var catalogReader = new BigEndianReader(catalog);
        var writer = new BigEndianWriter(catalog);
        foreach (var record in LeafRecords(catalog).Where(r => r.Data.Length >= 0x62 && r.Data[0] == 2))
        {
            int data = DataOffset(catalog, record);
            uint id = FileId(record.Data);
            if (!files.TryGetValue(id, out var file))
            {
                throw new InvalidDataException($"File {id} could not be read to lay it out again.");
            }

            foreach (var (fork, logical, physical, extents) in new[] { (file.DataFork, 0x1A, 0x1E, 0x4A), (file.ResourceFork, 0x24, 0x28, 0x56) })
            {
                var bytes = fork.ToArray();
                if (catalogReader.ReadUInt32At(data + logical) != bytes.Length)
                {
                    throw new InvalidDataException($"File {id}'s fork reads back shorter than its length.");
                }

                long blocks = (bytes.Length + blockSize - 1) / blockSize;
                if (next + blocks > count)
                {
                    throw new InvalidDataException("The volume's files do not fit the new size.");
                }

                catalog.AsSpan(data + extents, 12).Clear();
                if (blocks > 0)
                {
                    writer.WriteUInt16At(data + extents, next);
                    writer.WriteUInt16At(data + extents + 2, blocks);
                    bytes.CopyTo(result, areaStart + next * blockSize);
                }

                writer.WriteUInt32At(data + physical, blocks * blockSize);
                next += blocks;
            }
        }

        catalog.CopyTo(result, areaStart + extentsBlocks * blockSize);
        int bitmapOffset = (int)(bitmapStart * BlockSize);
        result.AsSpan(bitmapOffset, (int)(areaStart - bitmapOffset)).Clear();
        for (long block = 0; block < next; block++)
        {
            result[bitmapOffset + block / 8] |= (byte)(0x80 >> (int)(block % 8));
        }

        // The MDB: the source's, with the new layout's geometry, clump sizes and trees' places.
        var sector = source.AsSpan(MdbOffset, BlockSize).ToArray();
        var writerMdb = new BigEndianWriter(sector);
        foreach (var (offset, length) in new[] { (0x0E, 2), (0x12, 2), (0x14, 4), (0x18, 4), (0x1C, 2), (0x4A, 4), (0x4E, 4), (0x82, 4), (0x86, 12) })
        {
            result.AsSpan(MdbOffset + offset, length).CopyTo(sector.AsSpan(offset));
        }

        writerMdb.WriteUInt16At(0x10, next);                                            // drAllocPtr
        writerMdb.WriteUInt16At(0x22, count - next);                                    // drFreeBks
        writerMdb.WriteUInt32At(0x92, catalogBlocks * blockSize);                       // drCTFlSize
        sector.AsSpan(0x96, 12).Clear();
        writerMdb.WriteUInt16At(0x96, extentsBlocks);                                   // drCTExtRec
        writerMdb.WriteUInt16At(0x98, catalogBlocks);
        writerMdb.WriteUInt32At(0x06, MacDate.FromDateTime(Now).Seconds);               // drLsMod
        writerMdb.WriteUInt32At(0x46, unchecked(mdb.ReadUInt32At(0x46) + 1));                   // drWrCnt
        sector.CopyTo(result, MdbOffset);
        sector.CopyTo(result, size - 2 * BlockSize);
        source.AsSpan(0, MdbOffset).CopyTo(result);                                     // the boot blocks

        // Checked as a shrink is: the writer's checks, the folders, and every file read back as the source's.
        if (HfsWriter.Check(ForkData.FromBytes(result)) is { } fault)
        {
            throw new InvalidDataException($"The volume laid out again does not pass the writer's checks: {fault}");
        }

        var after = HfsReader.Instance.Read(ForkData.FromBytes(result), new ContainerContext());
        if (after.Count != files.Count || after.Any(f => !files.TryGetValue(f.CatalogId!.Value, out var was) || was.MacPath != f.MacPath
                || !was.DataFork.ToArray().AsSpan().SequenceEqual(f.DataFork.ToArray())
                || !was.ResourceFork.ToArray().AsSpan().SequenceEqual(f.ResourceFork.ToArray())))
        {
            throw new InvalidDataException("The volume laid out again has files that differ from the source's.");
        }

        return result;
    }
}
