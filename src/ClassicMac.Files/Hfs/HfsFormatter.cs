using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using ClassicMac.Core;
using static ClassicMac.Files.Hfs.HfsWriter;
using static ClassicMac.Files.Hfs.HfsCatalogEditing;
using static ClassicMac.Files.Hfs.HfsBTreeWriting;

namespace ClassicMac.Files.Hfs;

// HfsWriter.Format: a new volume laid out as Mac OS 9.0's initializer lays it out (hfs.md §3.1).
internal static class HfsFormatter
{
    // The allocation block size Mac OS 9.0's initializer gives N sectors with no caller's value: ((N >> 16) + 1) × 512,
    // one sector more when that is a multiple of 65,536 [Code: Mac OS 9.0 ptch -20217 0x2C22].
    internal static uint AutomaticBlockSize(long size)
    {
        long blockBytes = ((size / BlockSize >> 16) + 1) * BlockSize;
        return (uint)(blockBytes % 65536 == 0 ? blockBytes + BlockSize : blockBytes);
    }

    internal static byte[] Format(long size, string volumeName, MacDate? created = null, uint? blockSize = null)
    {
        if (size > MaximumFormatSize)
        {
            throw new ArgumentOutOfRangeException(nameof(size), "An HFS volume made in memory is at most just under 2 GB; FormatTo writes larger ones.");
        }

        return FormatVolume(size, volumeName, created, blockSize).ToArray();
    }

    internal static void FormatTo(string path, long size, string volumeName, MacDate? created = null, uint? blockSize = null)
    {
        ArgumentNullException.ThrowIfNull(path);
        var volume = FormatVolume(size, volumeName, created, blockSize);
        using var stream = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None);
        stream.SetLength(size);
        var sector = new byte[BlockSize];
        foreach (var number in volume.Sectors.Order())
        {
            volume.Read(number * BlockSize, sector);
            stream.Position = number * BlockSize;
            stream.Write(sector);
        }
    }

    // The new volume over zeros, only its MDB, bitmap, B-trees and alternate MDB written.
    internal static HfsVolume FormatVolume(long size, string volumeName, MacDate? created = null, uint? requestedBlockSize = null)
    {
        ArgumentNullException.ThrowIfNull(volumeName);
        if (size % BlockSize != 0 || size < MinimumFormatSize || size > MaximumFormatToSize)
        {
            throw new ArgumentOutOfRangeException(nameof(size), "An HFS volume is whole 512-byte blocks, from 400 KB to 2 TB.");
        }

        if (volumeName.Length == 0 || volumeName.Contains(':', StringComparison.Ordinal) || !MacRoman.TryEncode(volumeName, out var nameBytes) || nameBytes.Length > 27)
        {
            throw new ArgumentException("A volume name is 1 to 27 Mac OS Roman characters, with no colon.", nameof(volumeName));
        }

        // The layout Mac OS 9.0's HFS initializer computes for N sectors with no caller's values (hfs.md §3.1)
        // [Code: Mac OS 9.0 ptch -20217 0x2C12]. The allocation block size: ((N >> 16) + 1) × 512, one sector more when
        // that is a multiple of 65,536 [Code: 0x2C22]; the bitmap from sector 3, one sector per 4,096 blocks of
        // ⌊N ÷ a⌋; the allocation area after it, up to the alternate MDB and the spare sector at the end.
        long sectors = size / BlockSize;
        // A caller's block size, as the initializer takes one: a multiple of 512 that keeps the volume within 65,535
        // blocks [ClassicMac: the caller's path of the initializer not traced].
        long blockBytes = requestedBlockSize ?? AutomaticBlockSize(size);
        if (blockBytes < BlockSize || blockBytes % BlockSize != 0)
        {
            throw new ArgumentOutOfRangeException(nameof(requestedBlockSize), "An allocation block is a multiple of 512 bytes.");
        }

        long factor = blockBytes / BlockSize;
        long bitmapSectors = (sectors / factor + 4095) / 4096;
        long count = (sectors - 3 - bitmapSectors - 2) / factor;
        if (count > ushort.MaxValue)
        {
            throw new ArgumentOutOfRangeException(nameof(requestedBlockSize),
                $"{size} bytes in blocks of {blockBytes} bytes is {count} blocks; HFS has at most 65,535 (blocks of {AutomaticBlockSize(size)} bytes or more fit).");
        }

        uint blockSize = checked((uint)blockBytes);

        // Each B-tree file: min(N ÷ 128, 2,048) sectors rounded down to whole allocation blocks; one block when a block
        // is 1 MB or more, four when N ≤ 128. It is the files' size and their clump size [Code: 0x2F7A].
        long treeBlocks = blockBytes >= 1024 * 1024 ? 1 : sectors <= 128 ? 4 : Math.Min(sectors >> 7, 2048) / factor;
        treeBlocks = Math.Max(treeBlocks, (2 * NodeSize + blockBytes - 1) / blockBytes);   // a header and one node, with large blocks
        long treeBytes = treeBlocks * blockSize;
        if (treeBytes < 2 * NodeSize || 2 * treeBlocks >= count)
        {
            throw new ArgumentOutOfRangeException(nameof(size), "The volume is too small for its catalog.");
        }

        // The default clump size: four blocks, or one when four would pass 1 MB [Code: 0x2C12].
        uint defaultClump = 4 * blockSize > 1024 * 1024 ? blockSize : 4 * blockSize;

        uint date = (created ?? MacDate.FromDateTime(Now)).Seconds;
        var extents = EmptyTree(treeBytes, 7);
        var catalog = EmptyTree(treeBytes, 37);
        new BigEndianWriter(extents).WriteUInt32At(0x2E, treeBytes);                  // the header's clump size [Code: 0x1D254]
        new BigEndianWriter(catalog).WriteUInt32At(0x2E, treeBytes);
        var root = new byte[70];
        root[0] = 1;
        var rootWriter = new BigEndianWriter(root);
        rootWriter.WriteUInt32At(6, 2u);
        rootWriter.WriteUInt32At(10, date);
        rootWriter.WriteUInt32At(14, date);
        var thread = new byte[46];
        thread[0] = 3;
        new BigEndianWriter(thread).WriteUInt32At(10, 1u);
        thread[14] = (byte)nameBytes.Length;
        nameBytes.CopyTo(thread, 15);
        RebuildBTree(extents, [], validateExtents: true);
        // The root's thread key is written with key length 6: no name, and no alignment byte counted [Code: 0x1D254].
        RebuildBTree(catalog, [(CatalogKey(1, volumeName), root), (new byte[] { 6, 0, 0, 0, 0, 2, 0 }, thread)], validateExtents: false);

        var bitmap = new byte[bitmapSectors * BlockSize];
        for (long block = 0; block < 2 * treeBlocks; block++)
        {
            bitmap[block / 8] |= (byte)(0x80 >> (int)(block % 8));
        }

        var sector = new byte[BlockSize];
        var mdb = new BigEndianWriter(sector);
        mdb.WriteUInt16At(0x00, 0x4244);                                              // drSigWord 'BD'
        mdb.WriteUInt32At(0x02, date);                                                // drCrDate
        mdb.WriteUInt32At(0x06, date);                                                // drLsMod
        mdb.WriteUInt16At(0x0A, 0x0100);                                              // drAtrb: unmounted cleanly [Code]
        mdb.WriteUInt16At(0x0E, 3);                                                   // drVBMSt
        mdb.WriteUInt16At(0x12, count);                                               // drNmAlBlks
        mdb.WriteUInt32At(0x14, blockSize);                                           // drAlBlkSiz
        mdb.WriteUInt32At(0x18, defaultClump);                                        // drClpSiz
        mdb.WriteUInt16At(0x1C, 3 + bitmapSectors);                                   // drAlBlSt
        mdb.WriteUInt32At(0x1E, 16u);                                                 // drNxtCNID
        mdb.WriteUInt16At(0x22, count - 2 * treeBlocks);                              // drFreeBks
        mdb.WriteUInt32At(0x46, 2u);                                                  // drWrCnt: 2 from the initializer [Code]
        sector[0x24] = (byte)nameBytes.Length;                                        // drVN
        nameBytes.CopyTo(sector, 0x25);
        mdb.WriteUInt32At(0x4A, treeBytes);                                           // drXTClpSiz
        mdb.WriteUInt32At(0x4E, treeBytes);                                           // drCTClpSiz
        mdb.WriteUInt32At(0x82, treeBytes);                                           // drXTFlSize
        mdb.WriteUInt16At(0x86, 0);                                                   // drXTExtRec: allocation block 0
        mdb.WriteUInt16At(0x88, treeBlocks);
        mdb.WriteUInt32At(0x92, treeBytes);                                           // drCTFlSize
        mdb.WriteUInt16At(0x96, treeBlocks);                                          // drCTExtRec: after the extents file
        mdb.WriteUInt16At(0x98, treeBlocks);

        var volume = new HfsVolume(ForkData.Zeros(size));
        long allocationStart = (3 + bitmapSectors) * BlockSize;
        volume.Write(MdbOffset, sector);
        volume.Write(3 * BlockSize, bitmap);
        volume.Write(allocationStart, extents);
        volume.Write(allocationStart + treeBytes, catalog);
        volume.Write(size - 2 * BlockSize, sector);                                   // the alternate MDB
        if (Check(volume.AsForkData()) is { } fault)
        {
            throw new InvalidOperationException($"The new HFS volume failed its own check: {fault}");
        }

        return volume;
    }

    // An empty B-tree file of 512-byte nodes: the header node (its header record, 128 bytes of user data and a map
    // record marking node 0 used) and free nodes after it (hfs.md §1.6, §1.7).
    internal static byte[] EmptyTree(long bytes, int maxKeyLength)
    {
        var tree = new byte[bytes];
        var writer = new BigEndianWriter(tree);
        tree[8] = 1;                                                                  // ndType: header node
        writer.WriteUInt16At(10, 3);                                                  // ndNRecs
        writer.WriteUInt16At(14 + 18, NodeSize);                                      // bthNodeSize
        writer.WriteUInt16At(14 + 20, maxKeyLength);                                  // bthKeyLen
        writer.WriteUInt32At(14 + 22, bytes / NodeSize);                              // bthNNodes
        writer.WriteUInt32At(14 + 26, bytes / NodeSize - 1);                          // bthFree
        tree[248] = 0x80;                                                             // the map: node 0 in use
        writer.WriteUInt16At(NodeSize - 2, 14);
        writer.WriteUInt16At(NodeSize - 4, 120);
        writer.WriteUInt16At(NodeSize - 6, 248);
        writer.WriteUInt16At(NodeSize - 8, NodeSize - 8);
        return tree;
    }
}
