using System.Text;
using ClassicMac.Core;
using ClassicMac.Files.Hfs;

namespace ClassicMac.Files.Tests;

// The HFS Plus B-tree writer (hfs-plus.md §3): a whole tree file built from its leaf records, read back by the reader,
// which checks every node, key order, index and map.
public class HfsPlusBTreeWriterTests
{
    internal static byte[] CatalogKey(uint parent, string name)
    {
        var writer = new BigEndianWriter();
        writer.WriteUInt16(6 + 2 * name.Length);
        writer.WriteUInt32(parent);
        writer.WriteUInt16(name.Length);
        foreach (char c in name)
        {
            writer.WriteUInt16(c);
        }

        return writer.ToArray();
    }

    internal static byte[] ExtentsKey(byte fork, uint fileId, uint startBlock)
    {
        var writer = new BigEndianWriter();
        writer.WriteUInt16(10);
        writer.WriteByte(fork);
        writer.WriteByte(0);
        writer.WriteUInt32(fileId);
        writer.WriteUInt32(startBlock);
        return writer.ToArray();
    }

    private static List<(byte[] Key, byte[] Data)> CatalogRecords(int count) =>
        [.. Enumerable.Range(0, count)
            .Select(i => (CatalogKey(16 + (uint)(i % 7), $"File number {i:D4} with a long name"), Encoding.ASCII.GetBytes($"record {i:D4}".PadRight(88))))
            .OrderBy(r => r.Item1, Comparer<byte[]>.Create((a, b) => HfsPlusBTree.CompareCatalogKeys(a, b, caseFolding: true)))];

    [Fact]
    public void A_catalog_of_many_records_reads_back_through_its_index()
    {
        var records = CatalogRecords(400);

        var tree = HfsPlusBTreeWriter.Build(HfsPlusBTreeWriter.Catalog, records, 4096, totalNodes: 64, clumpSize: 4096 * 64);

        var read = HfsPlusBTree.LeafRecords(tree, "catalog").ToList();
        Assert.Equal(records.Select(r => Convert.ToHexString(r.Key) + Convert.ToHexString(r.Data)),
            read.Select(r => Convert.ToHexString(r.Key) + Convert.ToHexString(r.Data)));
        var header = new BigEndianReader(tree);
        Assert.True(header.ReadUInt16At(14) >= 2);                                 // leaves under an index
        Assert.Equal(400u, header.ReadUInt32At(14 + 6));
        Assert.Equal((4096, 516, 64u), (header.ReadUInt16At(14 + 18), header.ReadUInt16At(14 + 20), header.ReadUInt32At(14 + 22)));
        Assert.Equal(6u, header.ReadUInt32At(14 + 38));                            // big keys, variable index keys
    }

    [Fact]
    public void An_extents_tree_has_fixed_index_keys()
    {
        var records = Enumerable.Range(0, 300)
            .Select(i => (ExtentsKey(0, 16 + (uint)i, 8), new byte[64]))
            .ToList();

        var tree = HfsPlusBTreeWriter.Build(HfsPlusBTreeWriter.Extents, records, 512, totalNodes: 100, clumpSize: 512 * 100);

        Assert.Equal(300, HfsPlusBTree.LeafRecords(tree, "extents-overflow").Count());
        Assert.Equal(2u, new BigEndianReader(tree).ReadUInt32At(14 + 38));
    }

    [Fact]
    public void An_empty_tree_has_no_root()
    {
        var tree = HfsPlusBTreeWriter.Build(HfsPlusBTreeWriter.Extents, [], 4096, totalNodes: 4, clumpSize: 4096 * 4);

        Assert.Empty(HfsPlusBTree.LeafRecords(tree, "extents-overflow"));
        var header = new BigEndianReader(tree);
        Assert.Equal((0, 0u, 3u), (header.ReadUInt16At(14), header.ReadUInt32At(14 + 2), header.ReadUInt32At(14 + 26)));
    }

    [Fact]
    public void A_tree_past_its_header_map_gets_map_nodes()
    {
        var records = Enumerable.Range(0, 10).Select(i => (ExtentsKey(0, 16 + (uint)i, 0), new byte[64])).ToList();

        var tree = HfsPlusBTreeWriter.Build(HfsPlusBTreeWriter.Extents, records, 512, totalNodes: 5000, clumpSize: 512 * 5000);

        Assert.Equal(10, HfsPlusBTree.LeafRecords(tree, "extents-overflow").Count());
        Assert.NotEqual(0u, new BigEndianReader(tree).ReadUInt32At(0));           // the header's link to a map node
    }

    [Fact]
    public void Too_few_nodes_are_refused()
    {
        Assert.Throws<InvalidDataException>(() =>
            HfsPlusBTreeWriter.Build(HfsPlusBTreeWriter.Catalog, CatalogRecords(400), 4096, totalNodes: 8, clumpSize: 4096 * 8));
    }
}
