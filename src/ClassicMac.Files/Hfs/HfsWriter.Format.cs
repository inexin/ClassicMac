using System;
using System.Collections.Generic;
using System.IO;
using ClassicMac.Core;

namespace ClassicMac.Files.Hfs;

public static partial class HfsWriter
{
    /// <summary>The smallest volume <see cref="Format"/> makes: 400 KB, a single-sided floppy.</summary>
    public const long MinimumFormatSize = 400 * 1024;

    /// <summary>The largest volume <see cref="Format"/> makes, as one in-memory image: just under 2 GB.</summary>
    public const long MaximumFormatSize = int.MaxValue / BlockSize * BlockSize;

    /// <summary>
    /// A new, empty HFS volume of <paramref name="size"/> bytes named <paramref name="volumeName"/>, laid out as Mac OS 9.0's
    /// initializer lays it out (hfs.md §3.1): boot
    /// blocks zero, the MDB, the bitmap, the extents overflow file and the catalog (with the root folder and its thread)
    /// at the start of the allocation area, and the alternate MDB. <paramref name="created"/> is the creation and
    /// modification date (the local time now when omitted). The result passes <see cref="Check"/>.
    /// </summary>
    /// <exception cref="ArgumentOutOfRangeException">The size is not whole 512-byte blocks, or outside 400 KB to 2 GB.</exception>
    /// <exception cref="ArgumentException">The name is empty, over 27 bytes, has a colon, or a character Mac OS Roman has not.</exception>
    public static byte[] Format(long size, string volumeName, MacDate? created = null)
    {
        ArgumentNullException.ThrowIfNull(volumeName);
        if (size % BlockSize != 0 || size < MinimumFormatSize || size > MaximumFormatSize)
        {
            throw new ArgumentOutOfRangeException(nameof(size), "An HFS volume is whole 512-byte blocks, from 400 KB to just under 2 GB.");
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
        long blockBytes = ((sectors >> 16) + 1) * BlockSize;
        if (blockBytes % 65536 == 0)
        {
            blockBytes += BlockSize;
        }

        long factor = blockBytes / BlockSize;
        long bitmapSectors = (sectors / factor + 4095) / 4096;
        long count = (sectors - 3 - bitmapSectors - 2) / factor;
        uint blockSize = checked((uint)blockBytes);

        // Each B-tree file: min(N ÷ 128, 2,048) sectors rounded down to whole allocation blocks; one block when a block
        // is 1 MB or more, four when N ≤ 128. It is the files' size and their clump size [Code: 0x2F7A].
        long treeBlocks = blockBytes >= 1024 * 1024 ? 1 : sectors <= 128 ? 4 : Math.Min(sectors >> 7, 2048) / factor;
        long treeBytes = treeBlocks * blockSize;
        if (treeBytes < 2 * NodeSize || 2 * treeBlocks >= count)
        {
            throw new ArgumentOutOfRangeException(nameof(size), "The volume is too small for its catalog.");
        }

        // The default clump size: four blocks, or one when four would pass 1 MB [Code: 0x2C12].
        uint defaultClump = 4 * blockSize > 1024 * 1024 ? blockSize : 4 * blockSize;

        uint date = (created ?? MacDate.FromDateTime(DateTime.Now)).Seconds;
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

        var image = new byte[size];
        long allocationStart = (3 + bitmapSectors) * BlockSize;
        extents.CopyTo(image, allocationStart);
        catalog.CopyTo(image, allocationStart + treeBytes);
        for (long block = 0; block < 2 * treeBlocks; block++)
        {
            image[3 * BlockSize + block / 8] |= (byte)(0x80 >> (int)(block % 8));
        }

        var mdb = new BigEndianWriter(image);
        mdb.WriteUInt16At(MdbOffset + 0x00, 0x4244);                                  // drSigWord 'BD'
        mdb.WriteUInt32At(MdbOffset + 0x02, date);                                    // drCrDate
        mdb.WriteUInt32At(MdbOffset + 0x06, date);                                    // drLsMod
        mdb.WriteUInt16At(MdbOffset + 0x0A, 0x0100);                                  // drAtrb: unmounted cleanly [Code]
        mdb.WriteUInt16At(MdbOffset + 0x0E, 3);                                       // drVBMSt
        mdb.WriteUInt16At(MdbOffset + 0x12, count);                                   // drNmAlBlks
        mdb.WriteUInt32At(MdbOffset + 0x14, blockSize);                               // drAlBlkSiz
        mdb.WriteUInt32At(MdbOffset + 0x18, defaultClump);                            // drClpSiz
        mdb.WriteUInt16At(MdbOffset + 0x1C, 3 + bitmapSectors);                       // drAlBlSt
        mdb.WriteUInt32At(MdbOffset + 0x1E, 16u);                                     // drNxtCNID
        mdb.WriteUInt16At(MdbOffset + 0x22, count - 2 * treeBlocks);                  // drFreeBks
        mdb.WriteUInt32At(MdbOffset + 0x46, 2u);                                      // drWrCnt: 2 from the initializer [Code]
        image[MdbOffset + 0x24] = (byte)nameBytes.Length;                             // drVN
        nameBytes.CopyTo(image, MdbOffset + 0x25);
        mdb.WriteUInt32At(MdbOffset + 0x4A, treeBytes);                               // drXTClpSiz
        mdb.WriteUInt32At(MdbOffset + 0x4E, treeBytes);                               // drCTClpSiz
        mdb.WriteUInt32At(MdbOffset + 0x82, treeBytes);                               // drXTFlSize
        mdb.WriteUInt16At(MdbOffset + 0x86, 0);                                       // drXTExtRec: allocation block 0
        mdb.WriteUInt16At(MdbOffset + 0x88, treeBlocks);
        mdb.WriteUInt32At(MdbOffset + 0x92, treeBytes);                               // drCTFlSize
        mdb.WriteUInt16At(MdbOffset + 0x96, treeBlocks);                              // drCTExtRec: after the extents file
        mdb.WriteUInt16At(MdbOffset + 0x98, treeBlocks);
        image.AsSpan(MdbOffset, BlockSize).CopyTo(image.AsSpan((int)(size - 2 * BlockSize)));   // the alternate MDB

        if (Check(ForkData.FromBytes(image)) is { } fault)
        {
            throw new InvalidOperationException($"The new HFS volume failed its own check: {fault}");
        }

        return image;
    }

    // An empty B-tree file of 512-byte nodes: the header node (its header record, 128 bytes of user data and a map
    // record marking node 0 used) and free nodes after it (hfs.md §1.6, §1.7).
    private static byte[] EmptyTree(long bytes, int maxKeyLength)
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
