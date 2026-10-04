using System.Collections.Generic;
using System.Linq;
using ClassicMac.Core;
using static ClassicMac.Files.Hfs.HfsRecords;

namespace ClassicMac.Files.Hfs;

// Disk First Aid's B-tree file setup, before the B-tree stages and with no line of its own (hfs.md §5.6): each tree's
// file from the alternate MDB's extents, which must add up to its PEOF exactly, and its header node's node size and
// first record [Code: Disk First Aid 8.5.5, CODE 1 $1A560, $1AA86, $1A918, $25BB6].
internal static class BTreeSetupCheck
{
    private const int ExtentsFile = 3, CatalogFile = 4;

    public static bool Run(FirstAidRun run)
    {
        var mdb = new BigEndianReader(run.Alternate);

        // #47: the extents file's three extents (to the first empty one) are its PEOF; it has no overflow extents.
        var extentsExtents = Extents(mdb, 0x86);
        if (Blocks(extentsExtents) * run.BlockSize != mdb.ReadUInt32At(0x82))
        {
            return run.Fatal(47, ExtentsFile);
        }

        if (Tree(run, ExtentsFile, extentsExtents, mdb.ReadUInt32At(0x82)) is not { } extents)
        {
            return false;
        }

        run.Extents = extents;
        run.ForkExtents.AddRange(extentsExtents.Select(e => ((uint)ExtentsFile, (byte)0, e.Start, e.Count)));

        // #46: the catalog's MDB extents and its overflow extents (fork $00, file 4) are its PEOF.
        var catalogExtents = Extents(mdb, 0x96);
        catalogExtents.AddRange(OverflowExtents(extents, CatalogFile, 0x00));
        if (Blocks(catalogExtents) * run.BlockSize != mdb.ReadUInt32At(0x92))
        {
            return run.Fatal(46, CatalogFile);
        }

        run.ForkExtents.AddRange(catalogExtents.Select(e => ((uint)CatalogFile, (byte)0, e.Start, e.Count)));
        run.Catalog = Tree(run, CatalogFile, catalogExtents, mdb.ReadUInt32At(0x92));
        return run.Catalog is not null;
    }

    // An MDB extent record's extents, to the first with no blocks.
    private static List<(uint Start, uint Count)> Extents(BigEndianReader record, int at)
    {
        var extents = new List<(uint, uint)>();
        for (var i = 0; i < 3; i++)
        {
            uint count = record.ReadUInt16At(at + 4 * i + 2);
            if (count == 0)
            {
                break;
            }

            extents.Add((record.ReadUInt16At(at + 4 * i), count));
        }

        return extents;
    }

    private static long Blocks(List<(uint Start, uint Count)> extents)
    {
        long blocks = 0;
        foreach (var (_, count) in extents)
        {
            blocks += count;
        }

        return blocks;
    }

    // The tree in its file, if its header node's node size and first record offset are sound (#61).
    private static FirstAidTree? Tree(FirstAidRun run, int fileId, List<(uint Start, uint Count)> extents, uint peof)
    {
        var bytes = run.ReadExtents(extents, peof);
        if (bytes.Length < 512)
        {
            run.Fatal(61, fileId);
            return null;
        }

        var header = new BigEndianReader(bytes);
        int nodeSize = header.ReadUInt16At(14 + 18);
        if (nodeSize is not (512 or 1024 or 2048 or 4096 or 8192 or 16384 or 32768) || bytes.Length < nodeSize)
        {
            run.Fatal(61, fileId);
            return null;
        }

        int firstRecord = header.ReadUInt16At(nodeSize - 2);
        if (firstRecord < 14 || firstRecord % 2 != 0 || firstRecord >= nodeSize)
        {
            run.Fatal(61, fileId);
            return null;
        }

        // A reserved header byte that is not zero is cleared by repair, without a problem line.
        if (bytes[0x32] != 0)
        {
            run.Silent(FirstAidRepairs.BTreeHeader);
            run.TreesToRebuild.Add(fileId);
        }

        return new FirstAidTree(fileId, bytes, nodeSize, extents);
    }

    // A file's overflow extents from the extents tree's leaves, in key order; none when the tree cannot be followed.
    private static List<(uint Start, uint Count)> OverflowExtents(FirstAidTree extents, int fileId, byte fork)
    {
        var tree = extents.File;
        var found = new List<(uint, uint)>();
        var seen = new HashSet<uint>();
        if (tree.Bytes.Length < 14 + 30)
        {
            return found;
        }

        for (uint node = tree.FirstLeaf; node != 0 && node < extents.TotalNodes && seen.Add(node); node = tree.Node(node).FLink)
        {
            var descriptor = tree.Node(node);
            for (var i = 0; i < descriptor.RecordCount; i++)
            {
                if (tree.TryRecord(node, i, out var key, out var data) && key.Length >= 8 && data.Length >= 12
                    && key.Span[1] == fork && KeyId(key) == fileId)
                {
                    found.AddRange(Extents(new BigEndianReader(data), 0));
                }
            }
        }

        return found;
    }
}
