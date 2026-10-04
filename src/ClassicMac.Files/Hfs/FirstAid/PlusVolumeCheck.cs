using System;
using System.Collections.Generic;
using System.Linq;
using ClassicMac.Core;

namespace ClassicMac.Files.Hfs;

// An HFS Plus volume's first stages (hfs.md §5.6, hfs-plus.md §1.1–§1.4): where the volume is (the image, or inside an
// HFS wrapper), its volume header, chosen as for HFS's MDBs (the primary when it is sound, else a sound alternate, and a
// missing or differing alternate written from it by repair), the geometry, and the special files: each fork's extents
// add up to its block count, and the extents, catalog and attributes trees are set up [Doc: TN1150].
internal static class PlusVolumeCheck
{
    private const ushort Hfs = 0x4244, HfsPlus = 0x482B, Hfsx = 0x4858;
    private const int HeaderOffset = 1024;
    private const int AllocationFile = 112, ExtentsFile = 192, CatalogFile = 272, AttributesFile = 352, StartupFile = 432;

    /// <summary>What the image holds: an HFS Plus volume (and where), HFSX, or neither.</summary>
    public static (bool Plus, bool Hfsx, long Offset, long Length) Find(HfsVolume volume)
    {
        if (volume.Length < 3 * 512)
        {
            return (false, false, 0, 0);
        }

        var sector = new byte[512];
        volume.Read(HeaderOffset, sector);
        var mdb = new BigEndianReader(sector);
        ushort signature = mdb.ReadUInt16At(0);
        if (signature is HfsPlus or Hfsx)
        {
            return (signature == HfsPlus, signature == Hfsx, 0, volume.Length);
        }

        // An HFS wrapper: the embedded volume is drEmbedExtent's blocks of the wrapper's allocation area.
        if (signature == Hfs && mdb.ReadUInt16At(0x7C) == HfsPlus)
        {
            long blockSize = mdb.ReadUInt32At(0x14);
            long offset = mdb.ReadUInt16At(0x1C) * 512L + mdb.ReadUInt16At(0x7E) * blockSize;
            long length = mdb.ReadUInt16At(0x80) * blockSize;
            return (true, false, offset, Math.Min(length, volume.Length - offset));
        }

        return (false, false, 0, 0);
    }

    public static bool Run(FirstAidRun run, long offset, long length)
    {
        run.Stage(FirstAidMessages.CheckingDiskVolume);
        run.Plus = true;
        run.VolumeOffset = offset;
        run.AllocationStart = (int)(offset / FirstAidRun.SectorSize);
        if (length < 3 * 512)
        {
            return run.FatalProblem("The HFS Plus volume is too small to hold its headers", "firstaid.invalid-volume-header");
        }

        var primary = Read(run, offset + HeaderOffset);
        var alternate = Read(run, offset + length - 1024);
        run.Stage(FirstAidMessages.CheckingExtendedVolume);
        var header = primary;
        int number = Problem(length, primary);
        if (number != 0 && Problem(length, alternate) == 0)
        {
            run.Problem("The volume header is damaged; the alternate header was used", "firstaid.volume-header-damaged", FirstAidRepairs.Mdb);
            header = alternate;
        }
        else if (number != 0)
        {
            return number == 69 ? run.FatalProblem("The volume header is not an HFS Plus one", "firstaid.invalid-volume-header") : run.Fatal(number);
        }

        var reader = new BigEndianReader(header);
        run.Primary = header;
        run.BlockSize = reader.ReadUInt32At(40);
        run.BlockCount = reader.ReadUInt32At(44);
        long end = run.BlockCount * (long)run.BlockSize;
        run.Alternate = Read(run, offset + end - 1024);
        if (header == primary && Differs(primary, run.Alternate))
        {
            run.Problem("The alternate volume header is missing or differs from the volume header", "firstaid.alternate-volume-header", FirstAidRepairs.AlternateMdb);
        }

        // The blocks that hold the boot blocks and header, and the alternate header.
        AddBlocks(run, 0, 0, (uint)(1535 / run.BlockSize));
        AddBlocks(run, 0, (uint)((end - 1024) / run.BlockSize), (uint)((end - 1) / run.BlockSize));
        return SpecialFiles(run, reader);
    }

    private static byte[] Read(FirstAidRun run, long at)
    {
        var bytes = new byte[512];
        if (at >= 0 && at + 512 <= run.Volume.Length)
        {
            run.Volume.Read(at, bytes);
        }

        return bytes;
    }

    // A header's first problem: not HFS Plus (69), a version other than 4 (66), the block size (7), the block count (8),
    // the extents file's blocks (47) or the catalog's (46); 0 when it is sound.
    private static int Problem(long length, byte[] header)
    {
        var reader = new BigEndianReader(header);
        if (reader.ReadUInt16At(0) != HfsPlus)
        {
            return 69;
        }

        if (reader.ReadUInt16At(2) != 4)
        {
            return 66;
        }

        uint blockSize = reader.ReadUInt32At(40), blocks = reader.ReadUInt32At(44);
        if (blockSize < 512 || (blockSize & (blockSize - 1)) != 0)
        {
            return 7;
        }

        if (blocks == 0 || blocks * (long)blockSize > length)
        {
            return 8;
        }

        if (Blocks(reader, ExtentsFile) != reader.ReadUInt32At(ExtentsFile + 12) || Outside(reader, ExtentsFile, blocks))
        {
            return 47;
        }

        // The catalog continues in overflow extents only when its own eight are all in use.
        return Short(reader, CatalogFile) || Outside(reader, CatalogFile, blocks) ? 46 : 0;
    }

    // A fork whose own extents cannot be its block count: more blocks than it has, or fewer with an extent left unused.
    private static bool Short(BigEndianReader header, int fork)
    {
        long blocks = Blocks(header, fork), declared = header.ReadUInt32At(fork + 12);
        int used = PlusExtentRecords.Of(header.Source.Slice(fork + 16, PlusExtentRecords.RecordLength)).Count();
        return blocks > declared || used < 8 && blocks != declared;
    }

    private static long Blocks(BigEndianReader header, int fork) =>
        PlusExtentRecords.Of(header.Source.Slice(fork + 16, PlusExtentRecords.RecordLength)).Sum(e => (long)e.Count);

    private static bool Outside(BigEndianReader header, int fork, uint blocks) =>
        PlusExtentRecords.Of(header.Source.Slice(fork + 16, PlusExtentRecords.RecordLength)).Any(e => (long)e.Start + e.Count > blocks);

    // The alternate against the header: the signature, version, block size and count, and the special files.
    private static bool Differs(byte[] header, byte[] alternate)
    {
        foreach (var (at, size) in new[] { (0, 4), (40, 8), (112, 400) })
        {
            if (!header.AsSpan(at, size).SequenceEqual(alternate.AsSpan(at, size)))
            {
                return true;
            }
        }

        return false;
    }

    private static void AddBlocks(FirstAidRun run, uint fileId, uint first, uint last) =>
        run.ForkExtents.Add((fileId, 0, first, last - first + 1));

    // The special files: the extents tree first (the others' overflow extents are in it), then the catalog, the
    // allocation file, and the attributes and startup files when present.
    private static bool SpecialFiles(FirstAidRun run, BigEndianReader header)
    {
        var extents = Fork(run, header, ExtentsFile, 3, overflow: null);
        if (extents is null)
        {
            return false;
        }

        if (Tree(run, 3, extents, header.ReadUInt64At(ExtentsFile), 47) is not { } extentsTree)
        {
            return false;
        }

        run.Extents = extentsTree;
        var overflow = LeafRecords(extentsTree);
        if (Fork(run, header, CatalogFile, 4, overflow) is not { } catalog)
        {
            return false;
        }

        if (Tree(run, 4, catalog, header.ReadUInt64At(CatalogFile), 46) is not { } catalogTree)
        {
            return false;
        }

        run.Catalog = catalogTree;
        if (Fork(run, header, AllocationFile, 6, overflow) is not { } allocation)
        {
            return false;
        }

        if (header.ReadUInt64At(AllocationFile) * 8 < run.BlockCount)
        {
            return run.FatalProblem("The allocation file is too short for the volume's blocks", "firstaid.invalid-allocation-file");
        }

        run.AllocationFileExtents = allocation;
        if (header.ReadUInt32At(AttributesFile + 12) != 0)
        {
            if (Fork(run, header, AttributesFile, 8, overflow) is not { } attributes
                || Tree(run, 8, attributes, header.ReadUInt64At(AttributesFile), 0) is not { } attributesTree)
            {
                return false;
            }

            run.AttributesTree = attributesTree;
        }

        return Fork(run, header, StartupFile, 7, overflow) is not null;
    }

    // A special file's extents (its fork data's and its overflow records'), which must add up to its block count, with
    // its logical size within them; added to the blocks in use.
    private static List<(uint Start, uint Count)>? Fork(FirstAidRun run, BigEndianReader header, int at, uint fileId,
        List<(byte[] Key, byte[] Data)>? overflow)
    {
        var record = header.Source.Slice(at + 16, PlusExtentRecords.RecordLength);
        if (!PlusExtentRecords.Check(run, record, fileId, 0))
        {
            return null;
        }

        var extents = PlusExtentRecords.Of(record).ToList();
        long blocks = extents.Sum(e => (long)e.Count);
        foreach (var (key, data) in overflow ?? [])
        {
            var reader = new BigEndianReader(key);
            if (key[2] == 0 && reader.ReadUInt32At(4) == fileId && reader.ReadUInt32At(8) == blocks)
            {
                foreach (var extent in PlusExtentRecords.Of(data))
                {
                    extents.Add(extent);
                    blocks += extent.Count;
                }
            }
        }

        if (blocks != header.ReadUInt32At(at + 12) || header.ReadUInt64At(at) > (ulong)(blocks * run.BlockSize))
        {
            return fileId switch
            {
                3 => Fail(run.Fatal(47, fileId)),
                4 => Fail(run.Fatal(46, fileId)),
                _ => Fail(run.FatalProblem($"Special file {fileId}'s extents are not its block count", "firstaid.invalid-special-file")),
            };
        }

        run.ForkExtents.AddRange(extents.Select(e => (fileId, (byte)0, e.Start, e.Count)));
        return extents;
    }

    private static List<(uint Start, uint Count)>? Fail(bool _) => null;

    // The tree in its file, if its header node's node size is sound (#61); the catalog and attributes nodes are at least
    // 4 KB.
    private static FirstAidTree? Tree(FirstAidRun run, int fileId, List<(uint Start, uint Count)> extents, ulong logicalSize, int peofProblem)
    {
        if (logicalSize < 512 || logicalSize > int.MaxValue)
        {
            run.Fatal(peofProblem == 0 ? 61 : peofProblem, fileId);
            return null;
        }

        var bytes = run.ReadExtents(extents, (long)logicalSize);
        var reader = new BigEndianReader(bytes);
        int nodeSize = reader.ReadUInt16At(14 + 18);
        if (nodeSize is not (512 or 1024 or 2048 or 4096 or 8192 or 16384 or 32768) || (fileId != 3 && nodeSize < 4096)
            || bytes.Length < nodeSize || reader.ReadUInt16At(nodeSize - 2) != 14)
        {
            run.Fatal(61, fileId);
            return null;
        }

        return new FirstAidTree(fileId, bytes, nodeSize, extents, plus: true);
    }

    // The extents tree's leaf records, by its leaf chain, for the other special files' overflow extents.
    private static List<(byte[] Key, byte[] Data)> LeafRecords(FirstAidTree tree)
    {
        var records = new List<(byte[], byte[])>();
        var file = tree.File;
        var seen = new HashSet<uint>();
        for (uint node = file.FirstLeaf; node != 0 && node < tree.TotalNodes && seen.Add(node); node = file.Node(node).FLink)
        {
            for (var i = 0; i < file.Node(node).RecordCount; i++)
            {
                if (file.TryRecord(node, i, out var key, out var data) && key.Length >= 12 && data.Length >= PlusExtentRecords.RecordLength)
                {
                    records.Add((key.ToArray(), data.ToArray()));
                }
            }
        }

        return records;
    }
}
